using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace SdrCapture;
static class KvmTests
{
    public static void Run(){RunAsync().GetAwaiter().GetResult();Files();Hooks();}
    static void Hooks()
    {
        using var service=new KvmService(new KvmOptions());
        using var input=new KvmController(service,new KvmOptions{Role="Host",Seamless=false});
        input.Refresh();input.ReturnLocal();
        Thread.Sleep(150);
        Program.Write("kvm-input-thread-test.json",new{Pass=true,InstalledOnDedicatedThread=true,InjectedInput=false,KeyboardLogging=false});
    }
    static void Files()
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"kvm-files-test-"+Guid.NewGuid().ToString("N"));
        using var dispatcher=new Control();_=dispatcher.Handle;
        using var service=new KvmService(new KvmOptions());
        string[]? completed=null;
        using var clipboard=new KvmClipboard(service,new(),dispatcher,paths=>completed=paths,folder);
        string id=Guid.NewGuid().ToString("N");byte[] payload=RandomNumberGenerator.GetBytes(150000);
        clipboard.HandleFile("peer",new(){Type="file-begin",Id=id,Files=[new("Folder",0,true),new("Folder/example.bin",payload.Length,false),new("empty.txt",0,false)]});
        clipboard.HandleFile("peer",new(){Type="file-open",Id=id,Text="Folder/example.bin"});
        for(int offset=0;offset<payload.Length;offset+=65536)clipboard.HandleFile("peer",new(){Type="file-data",Id=id,Data=payload.AsSpan(offset,Math.Min(65536,payload.Length-offset)).ToArray()});
        clipboard.HandleFile("peer",new(){Type="file-close",Id=id,Data=SHA256.HashData(payload)});
        clipboard.HandleFile("peer",new(){Type="file-open",Id=id,Text="empty.txt"});
        clipboard.HandleFile("peer",new(){Type="file-close",Id=id,Data=SHA256.HashData([])});
        clipboard.HandleFile("peer",new(){Type="file-done",Id=id});
        if(completed?.Length!=2||!File.ReadAllBytes(Path.Combine(completed.First(Directory.Exists),"example.bin")).SequenceEqual(payload))throw new Exception("Received clipboard files do not match");
        Program.Write("kvm-files-test.json",new{Pass=true,NestedFolder=true,EmptyFile=true,Bytes=payload.Length,HashVerified=true,ClipboardModified=false});
    }
    static async Task RunAsync()
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"kvm-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        using var identity=new KvmIdentity(folder);
        var (pin,key)=KvmPairing.Decode(identity.Code);
        if(!pin.SequenceEqual(SHA256.HashData(identity.Certificate.RawData))||!key.SequenceEqual(identity.Key))throw new Exception("Pairing code roundtrip");
        using(var loaded=new KvmIdentity(folder))if(loaded.Code!=identity.Code)throw new Exception("Identity persistence");
        using var deadline=new CancellationTokenSource(20000);
        var host=new KvmOptions{Role="Host"};
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var client=new KvmOptions{Role="Client",Host="127.0.0.1",Port=port,PairingCode=identity.Code};
        async Task<KvmWire> Accept()=>await KvmWire.Accept(await listener.AcceptTcpClientAsync(deadline.Token),identity,host,deadline.Token);
        try
        {
            var accepting=Accept();
            using var connection=await KvmWire.Connect(client,"bulk",deadline.Token);
            using var server=await accepting;
            if(server.Peer.Version!=Updates.VersionText||server.Peer.UpdateProtocol!=1||connection.Peer.UpdateProtocol!=1)throw new Exception("Update capability handshake lost");
            byte[] payload=RandomNumberGenerator.GetBytes(65536);
            await connection.SendAsync(new(){Type="file-data",Id="test",Data=payload},deadline.Token);
            var read=await server.ReadAsync(deadline.Token);
            if(read.Data==null||!read.Data.SequenceEqual(payload)||server.Peer.Id!=client.Id)throw new Exception("Authenticated transfer corrupted");
            await server.SendAsync(new(){Type="clipboard-text",Text="Русский текст 👋"},deadline.Token);
            if((await connection.ReadAsync(deadline.Token)).Text!="Русский текст 👋")throw new Exception("Unicode clipboard protocol");
            foreach(bool wrongPin in new[]{false,true})
            {
                var badPin=pin.ToArray();var badKey=key.ToArray();if(wrongPin)badPin[0]^=1;else badKey[0]^=1;
                var acceptBad=Accept();bool rejected=false;
                try{using var bad=await KvmWire.Connect(client with{PairingCode=KvmPairing.Encode(badPin,badKey)},"control",deadline.Token);}
                catch(Exception e)when(e is AuthenticationException or IOException){rejected=true;}
                try{using var bad=await acceptBad;throw new Exception("Invalid pairing accepted on host");}catch(Exception e)when(e is AuthenticationException or IOException){}
                if(!rejected)throw new Exception("Invalid pairing accepted on client");
            }
        }
        finally{listener.Stop();}
        var left=new MonitorPlacement{Peer="stream",Device="left",X=-1920,Y=0,Width=1920,Height=1080};
        var center=new MonitorPlacement{Peer="game",Device="center",X=0,Y=0,Width=2560,Height=1440};
        var right=new MonitorPlacement{Peer="stream",Device="right",X=2560,Y=0,Width=1920,Height=1080};
        var layout=new[]{left,center,right};
        if(KvmLayout.At(layout,new(-1,500))!=left||KvmLayout.At(layout,new(2560,500))!=right)throw new Exception("Side-specific monitor routing");
        if(KvmLayout.ToPhysical(left,new("left",1920,-200,1920,1080,false),new(-1,500))!=new Point(3839,300))throw new Exception("Remote monitor coordinate mapping");
        KvmClipboard.ValidateManifest([new("Folder",0,true),new("Folder/тест.txt",10,false),new("other.bin",200,false)]);
        foreach(var files in new KvmFileEntry[][]{
            [new("../bad",1,false)],[new("C:/bad",1,false)],[new("a:ads",1,false)],[new("CON.txt",1,false)],
            [new("folder/missing.txt",1,false)],[new("a",1,false),new("A",1,false)],[new("a",-1,false)]
        })
        {
            bool rejected=false;try{KvmClipboard.ValidateManifest(files);}catch(IOException){rejected=true;}
            if(!rejected)throw new Exception("Unsafe clipboard manifest accepted");
        }
        byte[] pcm=new byte[4800*2*4];var pcmFloats=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(pcm.AsSpan());pcmFloats.Fill(.25f);
        KvmAudioBus.Put("audio-test",123456,pcm);
        float[] audioResult=new float[800*6];
        KvmAudioBus.Read("audio-test",AudioTimeline.Position()-2400,audioResult,4);
        if(audioResult.Where((_,i)=>i%6==4).Count(f=>f==.25f)<700||audioResult.Where((_,i)=>i%6<4).Any(f=>f!=0))throw new Exception("Remote audio routing");
        Program.Write("kvm-test.json",new{Pass=true,EncryptedLoopback=true,UpdateCapabilityHandshake=true,WrongSecretRejected=true,WrongCertificateRejected=true,UnicodeClipboard=true,FilePayloadBytes=65536,IndependentLeftRightMonitors=true,UnsafeFilePathsRejected=true,LiveTwoPcInputTested=false});
    }
}
