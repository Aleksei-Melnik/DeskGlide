using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SdrCapture;

// DXGI permits only one duplication of a given output in a process. Capture in
// a short-lived ordinary child instead of interrupting the live replay/NDI
// capture. Pixels travel only through its inherited anonymous stdout pipe:
// no desktop files, named endpoint, elevated command or network listener.
static class ScreenshotCaptureWorker
{
    internal const string Argument="--screenshot-worker";
    internal const string FixtureArgument="--screenshot-worker-fixture";
    internal const string WarmArgument="--screenshot-worker-ready",WarmFixtureArgument="--screenshot-worker-ready-fixture";
    const int ReadyMagic=0x33524744;
    const int Magic=0x33475344,MaximumTiles=64,MaximumErrorBytes=4096;
    internal static ScreenshotFrame Capture(CancellationToken cancel=default,string? fixture=null)
    {
        cancel.ThrowIfCancellationRequested();
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancel);lifetime.CancelAfter(TimeSpan.FromSeconds(20));
        using var child=Start(fixture,false);
        using var stopped=lifetime.Token.Register(()=>Stop(child));
        ScreenshotFrame? frame=null;
        try
        {
            using var reader=new BinaryReader(child.StandardOutput.BaseStream,Encoding.UTF8,leaveOpen:true);
            frame=Read(reader);
            child.WaitForExitAsync(lifetime.Token).GetAwaiter().GetResult();
            if(child.ExitCode!=0)throw new IOException("Screenshot capture did not finish successfully.");
            lifetime.Token.ThrowIfCancellationRequested();
            var result=frame;frame=null;return result;
        }
        catch(Exception) when(lifetime.IsCancellationRequested)
        {
            cancel.ThrowIfCancellationRequested();
            throw new IOException(UiStrings.T("Screen capture timed out. Try again."));
        }
        finally{frame?.Dispose();Stop(child);child.WaitForExit(2000);}
    }
    static Process Start(string? fixture,bool warm)
    {
        using var current=Process.GetCurrentProcess();
        var start=new ProcessStartInfo(Environment.ProcessPath??throw new IOException("Application path is unavailable."))
        {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardInput=warm,WorkingDirectory=AppContext.BaseDirectory};
        string mode=warm?(fixture==null?WarmArgument:WarmFixtureArgument):(fixture==null?Argument:FixtureArgument);
        foreach(string value in new[]{mode,current.Id.ToString(CultureInfo.InvariantCulture),current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)})start.ArgumentList.Add(value);
        if(fixture!=null)start.ArgumentList.Add(fixture);
        return Process.Start(start)??throw new IOException("Could not start screenshot capture.");
    }
    // Waits with initialized DXGI objects, not a saved screenshot. EOF on the
    // private stdin pipe exits the child if the parent closes/crashes.
    internal sealed class Ready:IDisposable
    {
        readonly Process child;bool disposed;
        internal Ready(CancellationToken cancel,string? fixture=null)
        {
            child=Start(fixture,true);
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var stopped=timeout.Token.Register(()=>Stop(child));
                using var reader=new BinaryReader(child.StandardOutput.BaseStream,Encoding.UTF8,leaveOpen:true);
                if(reader.ReadInt32()!=ReadyMagic)throw new IOException("Screenshot warm-up failed.");
                timeout.Token.ThrowIfCancellationRequested();
            }
            catch{Dispose();throw;}
        }
        internal ScreenshotFrame Capture(CancellationToken cancel)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var stopped=timeout.Token.Register(()=>Stop(child));ScreenshotFrame? frame=null;
            try
            {
                child.StandardInput.BaseStream.WriteByte(1);child.StandardInput.BaseStream.Flush();child.StandardInput.Close();
                using var reader=new BinaryReader(child.StandardOutput.BaseStream,Encoding.UTF8,leaveOpen:true);frame=Read(reader);
                child.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();timeout.Token.ThrowIfCancellationRequested();
                if(child.ExitCode!=0)throw new IOException("Screenshot capture did not finish successfully.");
                var result=frame;frame=null;return result;
            }
            catch(Exception) when(timeout.IsCancellationRequested)
            {cancel.ThrowIfCancellationRequested();throw new IOException(UiStrings.T("Screen capture timed out. Try again."));}
            finally{frame?.Dispose();}
        }
        internal bool TestPipeClosure()
        {child.StandardInput.Close();return child.WaitForExit(2000)&&child.ExitCode==0;}
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            try{child.StandardInput.Close();}catch(InvalidOperationException){}catch(IOException){}
            Stop(child);child.WaitForExit(2000);child.Dispose();
        }
    }
    static void Stop(Process child)
    {try{if(!child.HasExited)child.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
    internal static void Write(BinaryWriter writer,IReadOnlyList<ScreenshotTile> tiles)
    {
        if(tiles.Count<1||tiles.Count>MaximumTiles)throw new IOException("Invalid screenshot display count.");
        writer.Write(Magic);writer.Write(true);writer.Write(tiles.Count);
        foreach(var tile in tiles)tile.Write(writer);
        writer.Flush();
    }
    internal static ScreenshotFrame Read(BinaryReader reader)
    {
        var watch=Stopwatch.StartNew();
        if(reader.ReadInt32()!=Magic)throw new IOException("Invalid screenshot response.");
        if(!reader.ReadBoolean())
        {
            int length=reader.ReadInt32();if(length<0||length>MaximumErrorBytes)throw new IOException("Invalid screenshot error.");
            byte[] error=new byte[length];reader.BaseStream.ReadExactly(error);throw new IOException(Encoding.UTF8.GetString(error));
        }
        int count=reader.ReadInt32();if(count<1||count>MaximumTiles)throw new IOException("Invalid screenshot display count.");
        var tiles=new List<ScreenshotTile>();long pixels=0;Rectangle bounds=default;
        for(int i=0;i<count;i++)
        {
            var tile=ScreenshotTile.Read(reader,ref pixels);bounds=i==0?tile.Bounds:Rectangle.Union(bounds,tile.Bounds);ScreenshotFrame.CheckSize(bounds);tiles.Add(tile);
        }
        if(reader.BaseStream.ReadByte()!=-1)throw new IOException("Unexpected screenshot data.");
        double wireMs=watch.Elapsed.TotalMilliseconds;var frame=new ScreenshotFrame(tiles);
        Log.Write($"Screenshot response: pipe={wireMs:F1} ms, preview={watch.Elapsed.TotalMilliseconds-wireMs:F1} ms.");return frame;
    }
    internal static int Run(string[] args)
    {
        bool fixture=args.FirstOrDefault() is FixtureArgument or WarmFixtureArgument;
        bool warm=args.FirstOrDefault() is WarmArgument or WarmFixtureArgument;
        if(args.Length!=(fixture?4:3)||!int.TryParse(args[1],out int pid)||pid<=0||!long.TryParse(args[2],out long started)||!Console.IsOutputRedirected||warm&&!Console.IsInputRedirected)
            throw new IOException("Invalid screenshot worker request.");
        using var parent=Process.GetProcessById(pid);using var current=Process.GetCurrentProcess();
        if(parent.StartTime.ToUniversalTime().Ticks!=started||parent.SessionId!=current.SessionId||
            !string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Screenshot parent changed.");
        using var output=Console.OpenStandardOutput();using var writer=new BinaryWriter(output,Encoding.UTF8,leaveOpen:true);
        IReadOnlyList<ScreenshotTile> tiles;
        try
        {
            using var prepared=!fixture&&warm?new ScreenshotFrame.PreparedCapture():null;
            if(warm)
            {
                writer.Write(ReadyMagic);writer.Flush();using var input=Console.OpenStandardInput();
                if(input.ReadByte()!=1)return 0;
            }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var watch=Stopwatch.StartNew();
            tiles=fixture?ScreenshotTests.WorkerFixture(args[3],timeout.Token):prepared!=null?prepared.Capture(timeout.Token):ScreenshotFrame.CaptureTiles(timeout.Token);
            Log.Write($"Screenshot native capture: {watch.Elapsed.TotalMilliseconds:F1} ms.");
        }
        catch(Exception e)
        {
            Log.Write("Screenshot worker: "+e);
            byte[] error=Encoding.UTF8.GetBytes(e.Message);int length=Math.Min(error.Length,MaximumErrorBytes);
            writer.Write(Magic);writer.Write(false);writer.Write(length);writer.Write(error,0,length);writer.Flush();return 1;
        }
        Write(writer,tiles);return 0;
    }
}

// One waiting worker per tray, prepared off the UI thread. A request consumes it;
// a replacement is warmed only after the editor closes. No background readback,
// file cache, window, continuous polling or extra copy of the desktop is kept.
sealed class ScreenshotCaptureCache:IDisposable
{
    readonly object gate=new();readonly CancellationTokenSource lifetime=new();
    Task<ScreenshotCaptureWorker.Ready?>? pending;bool disposed;
    internal void Warm()
    {
        lock(gate)
        {
            if(disposed||pending!=null)return;
            pending=Task.Run(()=>
            {
                try{return new ScreenshotCaptureWorker.Ready(lifetime.Token);}
                catch(Exception e){if(!lifetime.IsCancellationRequested)Log.Write("Screenshot warm-up: "+e.Message);return null;}
            });
        }
    }
    internal ScreenshotFrame Capture(CancellationToken cancel)
    {
        Task<ScreenshotCaptureWorker.Ready?>? task;
        lock(gate){ObjectDisposedException.ThrowIf(disposed,this);task=pending;pending=null;}
        using var ready=task?.GetAwaiter().GetResult();cancel.ThrowIfCancellationRequested();
        if(ready!=null)
        {
            try{return ready.Capture(cancel);}
            catch(Exception e) when(!cancel.IsCancellationRequested){ready.Dispose();Log.Write("Screenshot warm capture reset: "+e.Message);}
        }
        // Display/driver changes invalidate the waiting duplication. Rebuild it
        // for this same request instead of returning a stale image or giving up.
        return ScreenshotCaptureWorker.Capture(cancel);
    }
    public void Dispose()
    {
        Task<ScreenshotCaptureWorker.Ready?>? task;
        lock(gate){if(disposed)return;disposed=true;lifetime.Cancel();task=pending;pending=null;}
        task?.GetAwaiter().GetResult()?.Dispose();lifetime.Dispose();
    }
}
