using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace SdrCapture;

sealed record KvmHelperMessage
{
    public string Type {get;set;}="";
    public string Peer {get;set;}="";
    public string Key {get;set;}="";
    public bool Flag {get;set;}
    public Point? Point {get;set;}
    public KvmMessage? Input {get;set;}
    public KvmOptions? Options {get;set;}
    public KvmPeerInfo[]? Peers {get;set;}
}

// Private, bounded, bidirectional IPC. Neither endpoint listens on a network port.
sealed class KvmHelperPipe:IDisposable
{
    internal const int MaximumFrame=128*1024;
    readonly Stream stream;
    readonly CancellationTokenSource stop=new();
    readonly Channel<KvmHelperMessage> outgoing=Channel.CreateBounded<KvmHelperMessage>(new BoundedChannelOptions(512){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    readonly Task writer;
    public KvmHelperPipe(Stream stream){this.stream=stream;writer=Task.Run(WriteLoop);}
    public bool Post(KvmHelperMessage message)
    {
        if(!stop.IsCancellationRequested&&outgoing.Writer.TryWrite(message))return true;
        Abort();return false;
    }
    async Task WriteLoop()
    {
        try
        {
            await foreach(var message in outgoing.Reader.ReadAllAsync(stop.Token))
            {
                byte[] body=JsonSerializer.SerializeToUtf8Bytes(message);
                if(body.Length>MaximumFrame)throw new IOException("Input helper frame is too large.");
                byte[] size=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(size,body.Length);
                await stream.WriteAsync(size,stop.Token);await stream.WriteAsync(body,stop.Token);
            }
        }
        catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){Abort();}
    }
    public async Task<KvmHelperMessage?> Read(CancellationToken token)
    {
        byte[] size=new byte[4];int first=await stream.ReadAsync(size.AsMemory(0,1),token);if(first==0)return null;
        await stream.ReadExactlyAsync(size.AsMemory(1),token);
        int length=BinaryPrimitives.ReadInt32LittleEndian(size);
        if(length<2||length>MaximumFrame)throw new IOException("Invalid input helper frame size.");
        byte[] body=new byte[length];await stream.ReadExactlyAsync(body,token);
        try{return JsonSerializer.Deserialize<KvmHelperMessage>(body)??throw new IOException("Invalid input helper message.");}
        catch(JsonException e){throw new IOException("Invalid input helper JSON.",e);}
    }
    void Abort(){if(!stop.IsCancellationRequested){stop.Cancel();outgoing.Writer.TryComplete();stream.Dispose();}}
    public void Dispose()=>Abort();
}

static class KvmInputHelper
{
    public const string Argument="--input-helper";
    internal static KvmOptions SafeOptions(KvmOptions value)=>new()
    {
        Id=value.Id,Role=value.Role,Seamless=value.Seamless,BlockScreenCorners=value.BlockScreenCorners,
        OpenHotkeyModifiers=value.OpenHotkeyModifiers,OpenHotkeyKey=value.OpenHotkeyKey,
        ToggleHotkeyModifiers=value.ToggleHotkeyModifiers,ToggleHotkeyKey=value.ToggleHotkeyKey,
        Layout=value.Layout.Select(m=>m with{}).ToList(),RemoteOnlyPeers=value.RemoteOnlyPeers.ToList(),ExcludedMonitors=value.ExcludedMonitors.ToList()
    };
    internal static void ValidatePlan(KvmHelperMessage message)
    {
        if(message.Options is not {} options||options.Role is not ("Host" or "Client")||message.Peers is not {Length:<=16})throw new IOException("Invalid input helper plan.");
        if(options.Layout==null||options.Layout.Any(m=>m==null||m.Peer==null||m.Device==null||m.Peer.Length>100||m.Device.Length>256)||
            options.RemoteOnlyPeers==null||options.ExcludedMonitors==null||options.ExcludedMonitors.Any(k=>k==null))throw new IOException("Invalid input helper layout.");
        (SafeOptions(options) with{Role="Host"}).Validate();
        foreach(var peer in message.Peers)
        {
            if(peer==null||!Guid.TryParseExact(peer.Id,"N",out _)||peer.Name is not {Length:<=100})throw new IOException("Invalid input helper peer.");
            KvmWire.ValidateScreens(peer.Screens);
        }
        if(message.Peers.Select(p=>p.Id).Distinct().Count()!=message.Peers.Length)throw new IOException("Duplicate input helper peers.");
    }
    public static async Task<(KvmHelperPipe Pipe,Process Process)> Start(CancellationToken token)
    {
        string name="DeskGlide.Input."+Environment.ProcessId+"."+Guid.NewGuid().ToString("N");
        var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly|PipeOptions.FirstPipeInstance);
        Process? child=null;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var current=Process.GetCurrentProcess();
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory,WindowStyle=ProcessWindowStyle.Hidden};
            foreach(string argument in new[]{Argument,name,Environment.ProcessId.ToString(),current.StartTime.ToUniversalTime().Ticks.ToString()})start.ArgumentList.Add(argument);
            child=Process.Start(start)??throw new IOException("Could not start the input helper.");
            await pipe.WaitForConnectionAsync(timeout.Token);
            VerifyClient(pipe,child.Id);
            return(new KvmHelperPipe(pipe),child);
        }
        catch{pipe.Dispose();child?.Dispose();throw;}
    }
    public static int Run(string[] args)
    {
        if(args.Length!=4||args[0]!=Argument||!args[1].StartsWith("DeskGlide.Input.",StringComparison.Ordinal)||args[1].Length>150||
            !int.TryParse(args[2],out int pid)||pid<=0||!long.TryParse(args[3],out long started))throw new IOException("Invalid input helper request.");
        if(!KvmInputAccess.Elevated)throw new IOException("The input helper needs Windows administrator approval.");
        using var parent=Process.GetProcessById(pid);using var current=Process.GetCurrentProcess();
        if(parent.StartTime.ToUniversalTime().Ticks!=started||parent.SessionId!=current.SessionId||
            !string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Input helper parent mismatch.");
        using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        pipe.Connect(10000);
        VerifyServer(pipe,pid);
        using var link=new KvmHelperPipe(pipe);
        RunLoop(link,parent).GetAwaiter().GetResult();return 0;
    }
    internal static void VerifyClient(NamedPipeServerStream pipe,int expected)
    {if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint pid)||pid!=expected)throw new IOException("Input helper identity mismatch.");}
    internal static void VerifyServer(NamedPipeClientStream pipe,int expected)
    {if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint pid)||pid!=expected)throw new IOException("Input helper server mismatch.");}
    sealed class Transport(KvmHelperPipe link):IKvmInputTransport
    {
        public KvmPeerInfo[] Peers {get;set;}=[];
        public bool Send(string peer,KvmMessage message)=>KvmInput.Valid(message)&&link.Post(new(){Type="send",Peer=peer,Input=message});
    }
    internal static async Task RunLoop(KvmHelperPipe link,Process parent,bool installHooks=true)
    {
        KvmController? controller=null;string id="",role="",configuration="";var transport=new Transport(link);
        try
        {
            while(!parent.HasExited)
            {
                // A dead/stalled parent must never leave privileged input capture running.
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var message=await link.Read(deadline.Token);if(message==null)break;
                switch(message.Type)
                {
                    case "ping":link.Post(new(){Type="pong"});break;
                    case "configure":
                        ValidatePlan(message);var options=SafeOptions(message.Options!);
                        string signature=JsonSerializer.Serialize(options with{Seamless=true});
                        if(controller!=null&&(options.Id!=id||options.Role!=role))throw new IOException("Input helper session changed.");
                        transport.Peers=message.Peers!;
                        if(controller==null)
                        {
                            id=options.Id;role=options.Role;controller=new(transport,options,installHooks);
                            controller.OpenRequested+=()=>link.Post(new(){Type="open"});
                            controller.EmergencyReturn+=()=>link.Post(new(){Type="emergency"});
                            controller.SeamlessChanged+=value=>link.Post(new(){Type="toggle",Flag=value});
                            controller.Notification+=text=>link.Post(new(){Type="notice",Key=text});
                            controller.StateChanged+=()=>link.Post(new(){Type="state",Key=controller.ActiveMonitorKey});
                        }
                        else if(signature!=configuration)controller.UpdateLayout(options);
                        else{controller.Seamless=options.Seamless;controller.Refresh();}
                        configuration=signature;
                        link.Post(new(){Type="ready"});break;
                    case "activate":
                        if(controller?.Monitors.FirstOrDefault(m=>m.Key==message.Key) is {} target)controller.Activate(target,message.Point);
                        break;
                    case "local":controller?.ReturnLocal();break;
                    case "seamless":if(controller!=null)controller.Seamless=message.Flag;break;
                    case "viewer":if(controller!=null)controller.ViewerActive=message.Flag;break;
                    case "input":
                        if(role!="Client"||message.Input==null||!KvmInput.Valid(message.Input))throw new IOException("Invalid input helper command.");
                        if(!KvmInput.Inject(message.Input))link.Post(new(){Type="blocked"});break;
                    case "stop":return;
                    default:throw new IOException("Unsupported input helper command.");
                }
            }
        }
        catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException or ArgumentException){Log.Write("KVM input helper stopped: "+e.GetType().Name);}
        finally{controller?.Dispose();KvmInput.ReleaseAll();}
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
}
