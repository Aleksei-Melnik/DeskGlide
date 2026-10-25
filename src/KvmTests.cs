using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace SdrCapture;
static class KvmTests
{
    public static void Run(){RunAsync().GetAwaiter().GetResult();Files();Hooks();}
    static void Hooks()
    {
        var mousePacket=new byte[48];mousePacket[8]=1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(mousePacket.AsSpan(36),-47);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(mousePacket.AsSpan(40),13);
        if(KvmRawMouse.RelativeDelta(mousePacket,8)!=new Point(-47,13))throw new Exception("Physical raw mouse deltas parsed incorrectly");
        mousePacket[8]=0;if(KvmRawMouse.RelativeDelta(mousePacket,8)!=null)throw new Exception("Synthetic game warp accepted as physical input");
        mousePacket[8]=1;mousePacket[24]=1;if(KvmRawMouse.RelativeDelta(mousePacket,8)!=null)throw new Exception("Absolute input interpreted as relative movement");
        KvmInputAccess.TestProbe=()=>KvmInputBlock.None;
        try
        {
        using var service=new KvmService(new KvmOptions());
        using var input=new KvmController(service,new KvmOptions{Role="Host",Seamless=false});
        input.Refresh();input.ReturnLocal();
        Thread.Sleep(150);
        // No injected OS input or cursor movement: only launcher shortcut dispatch is exercised.
        int opens=0;
        using var shortcuts=new KvmController(service,new KvmOptions{Role="Off"});shortcuts.OpenRequested+=()=>opens++;
        foreach(bool injected in new[]{false,true})
        {
            shortcuts.TestKey(162,false,injected);shortcuts.TestKey(164,false,injected);
            if(shortcuts.TestKey((int)Keys.K,false,injected)!=(IntPtr)1)throw new Exception("KVM shortcut not consumed");
            shortcuts.TestKey((int)Keys.K,false,injected);shortcuts.TestKey((int)Keys.K,true,injected);
            shortcuts.TestKey(164,true,injected);shortcuts.TestKey(162,true,injected);
        }
        if(opens!=2)throw new Exception("Physical/Stream Deck shortcut or repeat guard failed");
        var layoutOptions=new KvmOptions{Layout=[new(){Peer="local",Device="center",Width=100,Height=100,Hotkey=1}]};
        layoutOptions.Validate();bool conflict=false;try{(layoutOptions with{OpenHotkeyKey=(uint)Keys.F1}).Validate();}catch(ArgumentException){conflict=true;}if(!conflict)throw new Exception("Duplicate monitor/hub shortcut allowed");
        Program.Write("kvm-input-thread-test.json",new{Pass=true,InstalledOnDedicatedThread=true,InjectedInput=false,KeyboardLogging=false});
        }
        finally{KvmInputAccess.TestProbe=null;}
    }
    public static void Files()
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"kvm-files-test-"+Guid.NewGuid().ToString("N"));
        using var dispatcher=new Control();_=dispatcher.Handle;
        using var service=new KvmService(new KvmOptions());
        string[]? completed=null;
        using var clipboard=new KvmClipboard(service,new(),dispatcher,paths=>completed=paths,folder);
        int notifications=0;clipboard.Notification+=_=>notifications++;
        string id=Guid.NewGuid().ToString("N");byte[] payload=RandomNumberGenerator.GetBytes(150000);
        clipboard.HandleFile("peer",new(){Type="file-begin",Id=id,Files=[new("Folder",0,true),new("Folder/example.bin",payload.Length,false),new("empty.txt",0,false)]});
        clipboard.HandleFile("peer",new(){Type="file-open",Id=id,Text="Folder/example.bin"});
        for(int offset=0;offset<payload.Length;offset+=65536)clipboard.HandleFile("peer",new(){Type="file-data",Id=id,Data=payload.AsSpan(offset,Math.Min(65536,payload.Length-offset)).ToArray()});
        clipboard.HandleFile("peer",new(){Type="file-close",Id=id,Data=SHA256.HashData(payload)});
        clipboard.HandleFile("peer",new(){Type="file-open",Id=id,Text="empty.txt"});
        clipboard.HandleFile("peer",new(){Type="file-close",Id=id,Data=SHA256.HashData([])});
        clipboard.HandleFile("peer",new(){Type="file-done",Id=id});
        if(completed?.Length!=2||!File.ReadAllBytes(Path.Combine(completed.First(Directory.Exists),"example.bin")).SequenceEqual(payload))throw new Exception("Received clipboard files do not match");
        if(notifications!=0)throw new Exception("Routine clipboard transfer raised a notification");
        ClipboardImages(folder,service,dispatcher);
        Program.Write("kvm-files-test.json",new{Pass=true,NestedFolder=true,EmptyFile=true,Bytes=payload.Length,HashVerified=true,ClipboardImages=true,LargePngChunked=true,BitmapAndPngFormats=true,ClipboardModified=false});
    }
    static void ClipboardImages(string folder,KvmService service,Control dispatcher)
    {
        ClipboardImageFormats();
        using var source=new Bitmap(1200,1200,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var pixels=source.LockBits(new(0,0,source.Width,source.Height),System.Drawing.Imaging.ImageLockMode.WriteOnly,source.PixelFormat);
        var random=new byte[pixels.Stride*source.Height];new Random(1234).NextBytes(random);for(int i=3;i<random.Length;i+=4)random[i]=255;
        System.Runtime.InteropServices.Marshal.Copy(random,0,pixels.Scan0,random.Length);source.UnlockBits(pixels);
        var original=new DataObject();original.SetImage(source);
        using var captured=KvmClipboardImage.Capture(original)??throw new Exception("Native screenshot bitmap was not captured");
        string sourceFile=Path.Combine(folder,"screenshot.png");KvmClipboardImage.Save(captured,sourceFile);byte[] png=File.ReadAllBytes(sourceFile);
        if(png.Length<=4*1024*1024)throw new Exception("Screenshot fixture does not exercise the packet size limit");
        using var pngStream=new MemoryStream(png,false);pngStream.Position=5;var pngOnly=new DataObject();pngOnly.SetData("PNG",false,pngStream);
        using var pngCapture=KvmClipboardImage.Capture(pngOnly)??throw new Exception("PNG-only clipboard image was not captured");
        if(pngStream.Position!=5||pngCapture.Size!=source.Size||pngCapture.GetPixel(117,233)!=source.GetPixel(117,233))throw new Exception("PNG clipboard decoding changed pixels or consumed the source stream");
        int received=0;
        using var clipboard=new KvmClipboard(service,new(),dispatcher,paths=>{},Path.Combine(folder,"images"),data=>
        {
            if(data.GetImage() is not Bitmap image||image.Size!=source.Size||image.GetPixel(117,233)!=source.GetPixel(117,233))throw new Exception("Received image cannot be pasted as a native bitmap");
            if(((IDataObject)data).GetData("PNG",false) is not MemoryStream stream||!stream.ToArray().SequenceEqual(png))throw new Exception("Original PNG was not preserved");
            var paths=data.GetFileDropList();if(paths.Count!=1||!File.ReadAllBytes(paths[0]!).SequenceEqual(png))throw new Exception("Screenshot file fallback was not preserved");
            received++;
        });
        string id=Guid.NewGuid().ToString("N");
        clipboard.HandleFile("peer",new(){Type="file-begin",Id=id,Flags=2,Files=[new("Скриншот.png",png.Length,false)]});
        clipboard.HandleFile("peer",new(){Type="file-open",Id=id,Text="Скриншот.png"});
        for(int offset=0;offset<png.Length;offset+=65536)clipboard.HandleFile("peer",new(){Type="file-data",Id=id,Data=png.AsSpan(offset,Math.Min(65536,png.Length-offset)).ToArray()});
        clipboard.HandleFile("peer",new(){Type="file-close",Id=id,Data=SHA256.HashData(png)});
        clipboard.HandleFile("peer",new(){Type="file-done",Id=id});
        if(received!=1)throw new Exception("Screenshot clipboard result was not delivered exactly once");
        foreach(var invalid in new[]{new KvmFileEntry[]{new("x.txt",10,false)},[new("x.png",0,true)],[new("x.png",KvmClipboardImage.MaxBytes+1,false)],new[]{new KvmFileEntry("x.png",10,false),new("y.png",10,false)}})
        {
            bool rejected=false;try{KvmClipboardImage.ValidateManifest(invalid);}catch(IOException){rejected=true;}
            if(!rejected)throw new Exception("Invalid image manifest accepted");
        }
        string corrupt=Path.Combine(folder,"invalid.png");File.WriteAllBytes(corrupt,new byte[24]);
        bool badPng=false;try{using var invalid=KvmClipboardImage.Open(corrupt);}catch(IOException){badPng=true;}
        if(!badPng)throw new Exception("Non-PNG screenshot payload was accepted");
        Program.Write("kvm-image-tests.json",new{Pass=true,PixelsPreserved=true,PngOnlyClipboard=true,NativeBitmapAndPng=true,FileDropFallback=true,ImageBytes=png.Length,PacketLimitRespected=true,InvalidImageManifestsRejected=true,ClipboardModified=false});
    }
    static void ClipboardImageFormats()
    {
        using var source=new Bitmap(3,2,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++)source.SetPixel(x,y,Color.FromArgb(255,30+x*40,40+y*50,150+x+y));
        string dibV5=DataFormats.GetFormat(17).Name;
        foreach(bool v5 in new[]{false,true})foreach(bool topDown in new[]{false,true})
        {
            byte[] dib=ClipboardDib(source,v5,topDown);using var stream=new MemoryStream(dib,false);stream.Position=7;
            var data=new DataObject();data.SetData(v5?dibV5:DataFormats.Dib,false,stream);
            using var actual=KvmClipboardImage.Capture(data);
            RequireImage(actual,source,"DIB clipboard pixels/orientation");
            if(stream.Position!=7)throw new Exception("DIB clipboard stream position changed");
        }
        using var transparent=new Bitmap(2,1,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        transparent.SetPixel(0,0,Color.FromArgb(128,40,80,120));transparent.SetPixel(1,0,Color.FromArgb(255,20,60,100));
        var transparentData=new DataObject();transparentData.SetData(dibV5,false,ClipboardDib(transparent,true,false));
        using(var actual=KvmClipboardImage.Capture(transparentData))RequireImage(actual,transparent,"DIBV5 transparency changed");
        using var badPng=new MemoryStream(new byte[32],false);badPng.Position=5;
        var fallback=new DataObject();fallback.SetData("PNG",false,badPng);fallback.SetImage(source);
        using(var actual=KvmClipboardImage.Capture(fallback))RequireImage(actual,source,"Invalid PNG prevented a valid bitmap fallback");
        if(badPng.Position!=5)throw new Exception("Invalid PNG stream position changed");
        using var validDib=new MemoryStream(ClipboardDib(source,false,false),false);
        var dibFallback=new DataObject();dibFallback.SetData("PNG",false,new byte[32]);dibFallback.SetData(DataFormats.Dib,false,validDib);
        using(var actual=KvmClipboardImage.Capture(dibFallback))RequireImage(actual,source,"Invalid PNG prevented a valid DIB fallback");
        byte[] oversized=ClipboardDib(source,false,false);System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(4),int.MaxValue);
        var invalid=new DataObject();invalid.SetData(DataFormats.Dib,false,oversized);
        bool rejected=false;try{using var ignored=KvmClipboardImage.Capture(invalid);}catch(IOException){rejected=true;}
        if(!rejected)throw new Exception("Oversized DIB clipboard dimensions accepted");
        var text=new DataObject();text.SetText("clipboard image test");
        if(KvmClipboardImage.Capture(text)!=null)throw new Exception("Text clipboard was treated as an image");
        Program.Write("kvm-image-format-tests.json",new{Pass=true,DibAndDibV5=true,TopDownAndBottomUp=true,Padded24BitRows=true,TransparencyPreserved=true,InvalidPngFallsBack=true,StreamPositionsPreserved=true,BoundedDimensions=true,ClipboardModified=false});
    }
    static void RequireImage(Bitmap? actual,Bitmap expected,string message)
    {
        if(actual?.Size!=expected.Size)throw new Exception(message);
        for(int y=0;y<expected.Height;y++)for(int x=0;x<expected.Width;x++)if(actual.GetPixel(x,y)!=expected.GetPixel(x,y))throw new Exception(message);
    }
    static byte[] ClipboardDib(Bitmap source,bool v5,bool topDown)
    {
        int header=v5?124:40,bpp=v5?32:24,stride=((source.Width*bpp+31)/32)*4;
        byte[] result=new byte[header+stride*source.Height];var bytes=result.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes,header);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[4..],source.Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[8..],topDown?-source.Height:source.Height);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes[12..],1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes[14..],(short)bpp);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[20..],stride*source.Height);
        if(v5)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[16..],3);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..],0x00ff0000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..],0x0000ff00);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[48..],0x000000ff);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[52..],0xff000000);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[56..],0x73524742);
        }
        for(int y=0;y<source.Height;y++)for(int x=0;x<source.Width;x++)
        {
            var color=source.GetPixel(x,y);int offset=header+(topDown?y:source.Height-1-y)*stride+x*(bpp/8);
            result[offset]=color.B;result[offset+1]=color.G;result[offset+2]=color.R;if(v5)result[offset+3]=color.A;
        }
        return result;
    }
    public static void NativeClipboardImage()
    {
        if(EventWaitHandle.TryOpenExisting("Local\\SdrCapture.OpenKvm",out var running))
        {running.Dispose();throw new InvalidOperationException("This test changes the Windows clipboard. Run it only without a live DeskGlide instance.");}
        string folder=Path.Combine(Path.GetTempPath(),"DeskGlide-image-clipboard-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"screenshot.png");using var source=new Bitmap(80,60);
        using(var graphics=Graphics.FromImage(source))graphics.Clear(Color.FromArgb(255,31,97,173));
        KvmClipboardImage.Save(source,path);byte[] expected=File.ReadAllBytes(path);
        try
        {
            using var owner=new Control();_=owner.Handle;
            foreach(bool v5 in new[]{false,true})foreach(bool topDown in new[]{false,true})
            {
                PublishNativeDib(owner.Handle,v5?17u:8u,ClipboardDib(source,v5,topDown));
                using var dibImage=KvmClipboardImage.Capture(Clipboard.GetDataObject()??throw new Exception("Empty native DIB clipboard"));
                RequireImage(dibImage,source,"Native DIB/DIBV5 screenshot pixels changed");
            }
            using(var payload=KvmClipboardImage.Open(path))Clipboard.SetDataObject(payload.Data,true,0,0);
            GC.Collect();GC.WaitForPendingFinalizers();
            using var pasted=Clipboard.GetImage();
            if(pasted is not Bitmap image||image.Size!=source.Size||image.GetPixel(21,17)!=source.GetPixel(21,17))throw new Exception("Persisted native image was lost after disposal of the transfer payload");
            var data=Clipboard.GetDataObject()??throw new Exception("Empty native clipboard");
            if(data.GetData("PNG",false) is not MemoryStream stream||!stream.ToArray().AsSpan(0,expected.Length).SequenceEqual(expected))throw new Exception("PNG was not published as a native clipboard format");
            using var captured=KvmClipboardImage.Capture(data);
            if(captured==null||captured.Size!=source.Size||captured.GetPixel(21,17)!=source.GetPixel(21,17))throw new Exception("Windows clipboard screenshot could not be captured again");
            if(!Clipboard.ContainsFileDropList())throw new Exception("File-drop image fallback missing");
            Program.Write("kvm-native-image-tests.json",new{Pass=true,NativeClipboardBitmap=true,NativeClipboardPng=true,NativeDibAndDibV5=true,TopDownAndBottomUp=true,PayloadDisposedBeforePaste=true,PngCaptureFromWindows=true});
        }
        finally{Clipboard.Clear();}
    }
    static void PublishNativeDib(nint owner,uint format,byte[] dib)
    {
        nint memory=GlobalAlloc(2,(nuint)dib.Length);if(memory==0)throw new System.ComponentModel.Win32Exception();
        try
        {
            nint address=GlobalLock(memory);if(address==0)throw new System.ComponentModel.Win32Exception();
            try{Marshal.Copy(dib,0,address,dib.Length);}finally{GlobalUnlock(memory);}
            if(!OpenClipboard(owner))throw new System.ComponentModel.Win32Exception();
            try
            {
                if(!EmptyClipboard()||SetClipboardData(format,memory)==0)throw new System.ComponentModel.Win32Exception();
                memory=0;
            }
            finally{CloseClipboard();}
        }
        finally{if(memory!=0)GlobalFree(memory);}
    }
    [DllImport("user32.dll",SetLastError=true)]static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll",SetLastError=true)]static extern bool EmptyClipboard();
    [DllImport("user32.dll")]static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)]static extern nint SetClipboardData(uint format,nint memory);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")]static extern nint GlobalFree(nint memory);
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
        var client=new KvmOptions{Role="Client",Host="127.0.0.1",Port=port,PairingCode=identity.Code,RemoteViewOnly=true};
        async Task<KvmWire> Accept()=>await KvmWire.Accept(await listener.AcceptTcpClientAsync(deadline.Token),identity,host,deadline.Token);
        try
        {
            var accepting=Accept();
            using var connection=await KvmWire.Connect(client,"bulk",deadline.Token);
            using var server=await accepting;
            if(!server.Peer.RemoteViewOnly)throw new Exception("Server-only capability lost in handshake");
            if(server.Peer.Version!=Updates.VersionText||server.Peer.UpdateProtocol!=1||connection.Peer.UpdateProtocol!=1)throw new Exception("Update capability handshake lost");
            byte[] payload=RandomNumberGenerator.GetBytes(65536);
            await connection.SendAsync(new(){Type="file-data",Id="test",Data=payload},deadline.Token);
            var read=await server.ReadAsync(deadline.Token);
            if(read.Data==null||!read.Data.SequenceEqual(payload)||server.Peer.Id!=client.Id)throw new Exception("Authenticated transfer corrupted");
            await server.SendAsync(new(){Type="clipboard-text",Text="Русский текст 👋"},deadline.Token);
            if((await connection.ReadAsync(deadline.Token)).Text!="Русский текст 👋")throw new Exception("Unicode clipboard protocol");
            for(int i=0;i<100;i++)connection.Post(new(){Type="mouse",X=i,Y=i});
            connection.Post(new(){Type="mouse",X=99,Y=99,Flags=2});
            for(int i=100;i<200;i++)connection.Post(new(){Type="mouse",X=i,Y=i});
            connection.Post(new(){Type="mouse",X=199,Y=199,Flags=4});connection.Post(new(){Type="key",Code=65});connection.Post(new(){Type="key",Code=65,Flags=2});connection.Post(new(){Type="fixture-end"});
            var events=new List<KvmMessage>();while(true){var item=await server.ReadAsync(deadline.Token);if(item.Type=="fixture-end")break;events.Add(item);}
            var boundaries=events.Where(m=>m.Type=="key"||m.Flags!=0).Select(m=>(m.Type,m.Flags)).ToArray();
            if(!boundaries.SequenceEqual(new[]{("mouse",2),("mouse",4),("key",0),("key",2)})||events.TakeWhile(m=>m.Flags!=2).Last().X!=99||events.TakeWhile(m=>m.Flags!=4).Last().X!=199)throw new Exception("Mouse coalescing crossed a button/key boundary");
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
        var peers=new[]{new KvmPeerInfo("game","Gaming PC",[new("center",0,0,2560,1440,true)]),new KvmPeerInfo("stream","Streaming PC",[new("left",0,0,1920,1080,true),new("right",1920,0,1920,1080,false)]),new KvmPeerInfo("server","Server",[new("server-screen",0,0,1920,1080,true)],RemoteViewOnly:true)};
        var layoutOptions=new KvmOptions{Id="game",Layout=layout.ToList()};
        var merged=KvmLayout.Merge(layoutOptions,peers);
        if(merged.Count!=3||merged.Any(m=>m.Peer=="server")||KvmLayout.At(merged,new(-1,500))?.Device!="left"||KvmLayout.At(merged,new(2560,500))?.Device!="right")throw new Exception("Remote-only server changed edge routing");
        layoutOptions.RemoteOnlyPeers.Add("stream");
        if(KvmLayout.Merge(layoutOptions,peers).Any(m=>m.Peer!="game"))throw new Exception("Host exclusion ignored");
        layoutOptions.RemoteOnlyPeers.Clear();
        layoutOptions.ExcludedMonitors.Add(left.Key);
        var individual=KvmLayout.Merge(layoutOptions,peers);if(individual.Any(m=>m.Key==left.Key)||!individual.Any(m=>m.Key==right.Key))throw new Exception("Per-monitor exclusion removed the other screen");
        layoutOptions.ExcludedMonitors.Clear();
        if(KvmLayout.Merge(layoutOptions,peers).First(m=>m.Device=="left").X!=-1920)throw new Exception("Restoring a PC lost its layout");
        foreach(var invalid in new KvmScreen[][]{[peers[0].Screens[0],peers[0].Screens[0]],[new(null!,0,0,1,1,true)],[null!]})
        {bool rejected=false;try{KvmWire.ValidateScreens(invalid);}catch(IOException){rejected=true;}if(!rejected)throw new Exception("Malformed screens accepted");}
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
