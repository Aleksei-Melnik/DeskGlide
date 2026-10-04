using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
namespace SdrCapture;
static class KvmVideoTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    public static void Codec()
    {
        GpuPixels();
        using var codec=new KvmJpeg();using var image=new Bitmap(1920,1080,PixelFormat.Format32bppRgb);
        using(var g=Graphics.FromImage(image)){g.Clear(Color.FromArgb(13,90,187));g.FillRectangle(Brushes.Red,0,0,1920,32);g.FillRectangle(Brushes.Blue,0,1048,1920,32);g.DrawString("ScreenCapture KVM · 60 FPS",SystemFonts.CaptionFont!,Brushes.White,50,50);}
        byte[] bytes=codec.Encode(image,95);using var decoded=codec.Decode(bytes);
        Require(decoded.Width==1920&&decoded.Height==1080,"JPEG geometry");
        Require(decoded.GetPixel(5,5).R>245&&decoded.GetPixel(5,1070).B>245,"JPEG orientation/channels");
        var sample=decoded.GetPixel(5,200);Require(Math.Abs(sample.R-13)<3&&Math.Abs(sample.G-90)<3&&Math.Abs(sample.B-187)<3,"JPEG color roundtrip");
        bool rejected=false;try{using var invalid=codec.Decode([1,2,3]);}catch(IOException){rejected=true;}Require(rejected,"Malformed JPEG accepted");
        var clock=Stopwatch.StartNew();for(int i=0;i<120;i++){var frame=codec.Encode(image,95);using var result=codec.Decode(frame);}
        Program.Write("kvm-codec-test.json",new{Pass=true,Frames=120,Seconds=clock.Elapsed.TotalSeconds,CodecFps=120/clock.Elapsed.TotalSeconds,FrameBytes=bytes.Length,Chroma="4:4:4",RealDesktop=false});
    }
    static void GpuPixels()
    {
        using var device=Vortice.Direct3D11.D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Warp,Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,Vortice.Direct3D.FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;
        using var texture=device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm,4,2,1,1,Vortice.Direct3D11.BindFlags.ShaderResource));
        uint[] pixels=Enumerable.Range(0,8).Select(i=>0xff000000u|(uint)(i%4*40)<<16|(uint)(i/4*90)<<8|(uint)(i*10)).ToArray();context.UpdateSubresource(pixels,texture,0,16);
        foreach(int rotation in new[]{1,2,3,4})
        {
            bool quarter=rotation is 2 or 4;int w=quarter?2:4,h=quarter?4:2;
            using var readback=new KvmGpuReadback(device,context,w,h);using var bitmap=new Bitmap(w,h,PixelFormat.Format32bppRgb);readback.Read(texture,bitmap,rotation,1);
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            {
                var (sx,sy)=rotation switch{2=>(y,1-x),3=>(3-x,1-y),4=>(3-y,x),_=>(x,y)};
                Require(unchecked((uint)bitmap.GetPixel(x,y).ToArgb())==pixels[sy*4+sx],"GPU rotation/pixels "+rotation);
            }
        }
    }
    public static void Live()=>LiveAsync().GetAwaiter().GetResult();
    static async Task LiveAsync()
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"kvm-video-fixture-"+Guid.NewGuid().ToString("N"));
        using var identity=new KvmIdentity(folder);using var timeout=new CancellationTokenSource(30000);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var host=new KvmOptions{Role="Host"};var options=new KvmOptions{Role="Client",Host="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port,PairingCode=identity.Code};
        async Task<KvmWire> Accept()=>await KvmWire.Accept(await listener.AcceptTcpClientAsync(timeout.Token),identity,host,timeout.Token);
        var reports=new List<object>();
        try
        {
            var accepting=Accept();using var sending=await KvmWire.Connect(options,"video",timeout.Token);using var receiving=await accepting;
            Require(receiving.Peer.ViewProtocol==1&&sending.Peer.ViewProtocol==1,"Video capability negotiation");
            using var session=new KvmVideoSession(sending,true);using var decoder=new KvmJpeg();
            var requests=Task.Run(async()=>{try{while(!timeout.IsCancellationRequested)await session.Handle(await sending.ReadAsync(timeout.Token),timeout.Token);}catch(OperationCanceledException){}catch(IOException)when(timeout.IsCancellationRequested){} });
            foreach(int fps in new[]{30,60})
            {
                string id=Guid.NewGuid().ToString("N"),device=KvmScreen.Local().First().Device;
                await receiving.SendAsync(new(){Type="view-start",Id=id,Device=device,Code=fps,Flags=0},timeout.Token);
                var clock=Stopwatch.StartNew();long bytes=0;int frames=0;double first=0,last=0,gap=0;bool stopped=false;int width=0,height=0;
                while(true)
                {
                    var message=await receiving.ReadAsync(timeout.Token);
                    if(message.Type=="view-stopped")break;
                    Require(message.Type!="view-error","Video error: "+message.Text);if(message.Type!="view-frame")continue;
                    Require(message.Id==id,"Stale stream generation");
                    using var bitmap=decoder.Decode(message.Data!);width=bitmap.Width;height=bitmap.Height;
                    await receiving.SendAsync(new(){Type="view-ack",Id=id,Size=message.Size},timeout.Token);
                    double now=clock.Elapsed.TotalSeconds;if(now>.5&&!stopped){if(frames==0)first=now;else gap=Math.Max(gap,now-last);last=now;frames++;bytes+=message.Data!.Length;}
                    if(now>4&&!stopped){await receiving.SendAsync(new(){Type="view-stop",Id=id},timeout.Token);stopped=true;}
                }
                Require(frames>20,"Video did not stream continuously");reports.Add(new{TargetFps=fps,ActualFps=(frames-1)/(last-first),Frames=frames,Width=width,Height=height,MaxGapMs=gap*1000,MegabitsPerSecond=bytes*8/(last-first)/1000000,AudioOrInputInjected=false});
            }
            timeout.Cancel();sending.Dispose();receiving.Dispose();try{await requests;}catch(ObjectDisposedException){}
        }
        finally{listener.Stop();}
        Program.Write("kvm-video-live-test.json",new{Pass=true,Transport="Authenticated TLS binary, loopback",ImagesSaved=false,Reports=reports});
    }
}
