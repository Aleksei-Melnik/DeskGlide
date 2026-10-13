using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SdrCapture;

static class StreamDeckTests
{
    internal static DeckSnapshot Fixture()=>new(new("0.7.20","Test PC","Host",true,120,300,true,true,null,false,"idle",0,[new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Test Server","0.7.20",true)],
        [new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|DISPLAY1","Test Server · DISPLAY1",1920,1080,false),new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|DISPLAY1","Test PC · DISPLAY1",2560,1440,true)]));
    public static void Run()=>RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        string package=@"C:\Packages with spaces\clip & 100%\com.yami.deskglide.streamDeckPlugin";
        var install=StreamDeckInstallation.InstallCommand(@"C:\Program Files\Elgato\StreamDeck\StreamDeck.exe",package);
        Require(!install.UseShellExecute&&install.ArgumentList.Count==1&&install.ArgumentList[0]==package,"Plugin installation must pass the original package directly to Stream Deck, without file-association redirection");
        string folder=Path.Combine(Path.GetTempPath(),"DeskGlide-Deck-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string discovery=Path.Combine(folder,"bridge.json"),name="DeskGlide.StreamDeck.Test."+Guid.NewGuid().ToString("N");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));var token=timeout.Token;
        int executions=0;var saving=new TaskCompletionSource<DeckReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server=new StreamDeckBridge(r=>
        {
            Interlocked.Increment(ref executions);
            return r.Command=="saveReplay"?saving.Task:Task.FromResult(new DeckReply(r.Id,true,State:Fixture()));
        },discovery,name);
        server.Publish(Fixture());
        await using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);await client.ConnectAsync(token);
        async Task Send(DeckRequest r)=>await client.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r,DeckProtocol.Json)+"\n"),token);
        async Task<JsonElement> Read()=>JsonDocument.Parse((await DeckProtocol.ReadLine(client,token))!).RootElement.Clone();
        async Task<DeckReply> Reply(string id)
        {
            while(true){var m=await Read();if(m.GetProperty("type").GetString()=="reply"){var r=m.GetProperty("reply").Deserialize<DeckReply>(DeckProtocol.Json)!;if(r.Id==id)return r;}}
        }
        var first=await Read();Require(first.GetProperty("type").GetString()=="status","Initial state missing");
        var save=new DeckRequest(Guid.NewGuid().ToString("N"),"saveReplay");await Send(save);
        var status=new DeckRequest(Guid.NewGuid().ToString("N"),"status");await Send(status);Require((await Reply(status.Id)).Ok,"Pending save blocked status/other commands");
        await Send(save);Require((await Reply(save.Id)).Code=="duplicate","Duplicate save was executed");Require(executions==2,"Duplicate command dispatched");
        saving.SetResult(new(save.Id,true,State:Fixture()));Require((await Reply(save.Id)).Ok,"Save reply missing");
        foreach(string command in new[]{"status","saveReplay","openKvm","toggleEdges","toggleReplay","toggleStreaming"})Require(DeckProtocol.Valid(new(Guid.NewGuid().ToString("N"),command)),"Valid command rejected");
        Require(!DeckProtocol.Valid(new("bad","status")),"Bad id accepted");Require(!DeckProtocol.Valid(new(save.Id,"exec")),"Arbitrary command accepted");
        Require(!DeckProtocol.RemoteAllowed(new(save.Id,"openKvm","host")),"Remote UI request accepted");
        Require(!DeckProtocol.Valid(new(save.Id,"status","other")),"Arbitrary target accepted");
        Require(!DeckProtocol.Valid(new(save.Id,"saveReplay","local","aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")),"Peer accepted for unsupported command");
        var monitor=new DeckRequest(Guid.NewGuid().ToString("N"),"switchMonitor","host",Monitor:"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|DISPLAY1");
        Require(DeckProtocol.Valid(monitor)&&DeckProtocol.RemoteAllowed(monitor),"Typed monitor command rejected");
        Require(!DeckProtocol.Valid(monitor with{Monitor=""})&&!DeckProtocol.Valid(monitor with{Monitor=new string('x',257)})&&!DeckProtocol.Valid(monitor with{Monitor="display\n"})&&!DeckProtocol.Valid(monitor with{Command="saveReplay"}),"Invalid monitor selector accepted");
        await Send(monitor);Require((await Reply(monitor.Id)).State?.Local.Monitors?.Length==2,"Monitor command/status did not roundtrip");
        using(var oversized=new MemoryStream(Encoding.UTF8.GetBytes(new string('x',DeckProtocol.MaxLine+1)+"\n")))
        {bool rejected=false;try{await DeckProtocol.ReadLine(oversized,token);}catch(IOException){rejected=true;}Require(rejected,"Oversized input accepted");}
        // Invalid commands close only their connection; a second client remains usable.
        await client.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"bad\",\"command\":\"shell\"}\n"),token);
        await using var next=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);await next.ConnectAsync(token);
        Require(JsonDocument.Parse((await DeckProtocol.ReadLine(next,token))!).RootElement.GetProperty("type").GetString()=="status","Malformed client killed listener");
        server.Dispose();Require(!File.Exists(discovery),"Stale discovery retained after shutdown");
        Program.Write("streamdeck-tests.json",new{Pass=true,DirectPluginInstallation=true,ConcurrentSaveAndStatus=true,DuplicateSuppressed=true,CommandsWhitelisted=true,BoundedInput=true,LocalNamedPipeRoundtrip=true,LiveInputInjected=false});
        Directory.Delete(folder);
    }
    static void Require(bool condition,string error){if(!condition)throw new Exception(error);}
    public static void Network()=>NetworkAsync().GetAwaiter().GetResult();
    static async Task NetworkAsync()
    {
        if(EventWaitHandle.TryOpenExisting("Local\\SdrCapture.OpenKvm",out var live))
        {live.Dispose();throw new InvalidOperationException("Run Stream Deck network tests on isolated CI, without the installed DeskGlide running.");}
        var socket=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);socket.Start();int port=((System.Net.IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        using var host=new KvmService(new(){Role="Host",Port=port});
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));var token=deadline.Token;
        while(host.PairingCode=="")await Task.Delay(50,token);
        int calls=0;var waiting=new TaskCompletionSource<DeckReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hostDeck=new DeckRemote(host,r=>{Interlocked.Increment(ref calls);return r.Command=="toggleStreaming"?waiting.Task:Task.FromResult(new DeckReply(r.Id,true,State:Fixture()));});
        using var client=new KvmService(new(){Role="Client",Port=port,Host="127.0.0.1",PairingCode=host.PairingCode});
        using var clientDeck=new DeckRemote(client,_=>throw new Exception("Host tried to execute a client command"));
        while(client.Peers.Length!=1||host.Peers.Length!=1)await Task.Delay(50,token);
        Require(client.Peers[0].DeckProtocol==1&&host.Peers[0].DeckProtocol==1,"Stream Deck protocol capability not negotiated");
        var request=new DeckRequest(Guid.NewGuid().ToString("N"),"saveReplay","host");
        var result=await clientDeck.Execute(request).WaitAsync(token);Require(result.Ok&&result.State?.Local.Name=="Test PC","Paired command failed");
        Require((await clientDeck.Execute(request)).Code=="duplicate"&&calls==1,"Paired command was repeated");
        Require((await clientDeck.Execute(new(Guid.NewGuid().ToString("N"),"openKvm","host"))).Code=="unsupported"&&calls==1,"Remote UI command executed");
        var monitorResult=await clientDeck.Execute(new(Guid.NewGuid().ToString("N"),"switchMonitor","host",Monitor:"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|DISPLAY1")).WaitAsync(token);Require(monitorResult.Ok&&monitorResult.State?.Local.Monitors?.Length==2&&calls==2,"Paired monitor selection did not roundtrip");
        var pending=clientDeck.Execute(new(Guid.NewGuid().ToString("N"),"toggleStreaming","host"));
        while(calls<3)await Task.Delay(20,token);
        host.Dispose();Require(!(await pending.WaitAsync(token)).Ok,"Disconnected command reported success");
        Program.Write("streamdeck-network-tests.json",new{Pass=true,TlsPairedRoundtrip=true,CapabilityNegotiated=true,DuplicateSuppressed=true,RemoteUiDenied=true,DisconnectFailsPendingCommand=true,ReplayFixtureOnly=true});
    }
    // Isolated protocol fixture for Node/official SDK integration tests. No tray,
    // recorder, capture, network KVM, clipboard, hotkeys or input hooks are started.
    public static void FixtureServer(string descriptor,string transcript)
    {
        var state=Fixture();using var server=new StreamDeckBridge(r=>
        {
            File.AppendAllText(transcript,JsonSerializer.Serialize(r,DeckProtocol.Json)+"\n");
            return Task.FromResult(new DeckReply(r.Id,true,State:state));
        },descriptor,"DeskGlide.StreamDeck.Fixture."+Guid.NewGuid().ToString("N"));server.Publish(state);
        Thread.Sleep(TimeSpan.FromSeconds(90));
    }
}
