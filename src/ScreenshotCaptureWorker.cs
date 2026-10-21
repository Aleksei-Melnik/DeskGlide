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
    const int Magic=0x31475344,MaximumTiles=64,MaximumErrorBytes=4096;
    internal static ScreenshotFrame Capture(CancellationToken cancel=default,string? fixture=null)
    {
        cancel.ThrowIfCancellationRequested();
        using var current=Process.GetCurrentProcess();
        var start=new ProcessStartInfo(Environment.ProcessPath??throw new IOException("Application path is unavailable."))
        {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,WorkingDirectory=AppContext.BaseDirectory};
        foreach(string value in new[]{fixture==null?Argument:FixtureArgument,current.Id.ToString(CultureInfo.InvariantCulture),current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)})start.ArgumentList.Add(value);
        if(fixture!=null)start.ArgumentList.Add(fixture);
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancel);lifetime.CancelAfter(TimeSpan.FromSeconds(20));
        using var child=Process.Start(start)??throw new IOException("Could not start screenshot capture.");
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
        return new(tiles);
    }
    internal static int Run(string[] args)
    {
        bool fixture=args.FirstOrDefault()==FixtureArgument;
        if(args.Length!=(fixture?4:3)||!int.TryParse(args[1],out int pid)||pid<=0||!long.TryParse(args[2],out long started)||!Console.IsOutputRedirected)
            throw new IOException("Invalid screenshot worker request.");
        using var parent=Process.GetProcessById(pid);using var current=Process.GetCurrentProcess();
        if(parent.StartTime.ToUniversalTime().Ticks!=started||parent.SessionId!=current.SessionId||
            !string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Screenshot parent changed.");
        using var output=Console.OpenStandardOutput();using var writer=new BinaryWriter(output,Encoding.UTF8,leaveOpen:true);
        IReadOnlyList<ScreenshotTile> tiles;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            tiles=fixture?ScreenshotTests.WorkerFixture(args[3],timeout.Token):ScreenshotFrame.CaptureTiles(timeout.Token);
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
