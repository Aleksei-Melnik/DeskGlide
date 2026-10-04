using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace SdrCapture;
internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        try
        {
            if(args.Contains("--apply-update"))return UpdateInstaller.Run(args[Array.IndexOf(args,"--apply-update")+1]);
            if(args.Contains("--kvm")&&EventWaitHandle.TryOpenExisting("Local\\SdrCapture.OpenKvm",out var openKvm)){using(openKvm)openKvm.Set();return 0;}
            if(args.Contains("--update-tests")){UpdateTests.Run();return 0;}
            if(args.Contains("--ui-tests")){UiTests.Run();return 0;}
            if(args.Contains("--feature-tests")){FeatureTests.Run();return 0;}
            if(args.Contains("--camera-test")){FeatureTests.Camera();return 0;}
            if(args.Contains("--camera-ndi-test")){FeatureTests.NdiCamera();return 0;}
            if(args.Contains("--verify-cable")){string path=args[Array.IndexOf(args,"--verify-cable")+1];DiscordSetup.VerifySignature(path);Write("cable-signature-test.json",new{Pass=true,AuthenticodeVerified=true,Installed=false});return 0;}
            if(args.Contains("--check-update")){var release=Updates.Check().GetAwaiter().GetResult();Write("update-check.json",new{Current=Updates.VersionText,Latest=release?.Manifest.Version,SignatureVerified=release!=null});return 0;}
            if(args.Contains("--repair-update"))
            {
                if(!EventWaitHandle.TryOpenExisting("Local\\SdrCapture.UpdateThis",out var command))return 2;
                using(command)command.Set();return 0;
            }
            if(args.Contains("--ndi-audio-probe")){NdiAudioTests.Probe();return 0;}
            if(args.Contains("--ndi-audio-test")){NdiAudioTests.Run();return 0;}
            if(args.Contains("--audio-timing-test")){AudioTimingTests.Run();return 0;}
            if(args.Contains("--audio-live-test")){AudioTimingTests.Live(args.Contains("--play-tone"));return 0;}
            if(args.Contains("--kvm-test")){KvmTests.Run();return 0;}
            if(args.Contains("--replay-local-test")){int i=Array.IndexOf(args,"--seconds"),f=Array.IndexOf(args,"--fps");ReplayTests.LiveLocal(i<0?35:int.Parse(args[i+1]),f<0?120:int.Parse(args[f+1]));return 0;}
            if(args.Contains("--cadence-test")){int f=Array.IndexOf(args,"--fps"),c=Array.IndexOf(args,"--codec"),a=Array.IndexOf(args,"--audio");ReplayCadenceTest.Run(f<0?60:int.Parse(args[f+1]),c<0?"HEVC":args[c+1],a<0?"Mixed":args[a+1],args.Contains("--live-audio"));return 0;}
            if(args.Contains("--save-replay"))
            {
                if(!EventWaitHandle.TryOpenExisting("Local\\SdrCapture.SaveReplay",out var signal))return 2;
                using(signal)signal.Set();return 0;
            }
            if(args.Contains("--audio-devices")){Write("audio-devices.json",AudioChoice.All());return 0;}
            if(args.Contains("--replay-test")){ReplayTests.Run();return 0;}
            if(args.Contains("--replay-ui-preview")){ReplayTests.Preview();return 0;}
            if(args.Contains("--replay-live-test")){int i=Array.IndexOf(args,"--seconds"),f=Array.IndexOf(args,"--fps");ReplayTests.Live(i<0?35:int.Parse(args[i+1]),f<0?60:int.Parse(args[f+1]));return 0;}
            if(args.Contains("--self-test")){SelfTest.Run();NdiTests.ColorAndTransport();return 0;}
            if(args.Contains("--fullscreen-test")){FullscreenTest.Run();return 0;}
            if(args.Contains("--ndi-test"))
            {
                int i=Array.IndexOf(args,"--seconds");int seconds=i<0?60:int.Parse(args[i+1]);
                NdiTests.Integration(seconds,args.Contains("--chaos"));return 0;
            }
            if(args.Contains("--diagnose")){Write("diagnostics.json",DisplayInfo.All());return 0;}
            using var mutex=new Mutex(true,"Local\\SdrCapture.Tray",out bool first);
            if(!first) return 0;
            Application.EnableVisualStyles();
            using var app=new TrayApp();
            if(args.Contains("--kvm"))app.OpenKvm();
            if(args.Contains("--updated"))
            {
                string nonce=args[Array.IndexOf(args,"--updated")+1];
                EventHandler? ready=null;ready=(_,_)=>{Application.Idle-=ready;UpdateInstaller.Acknowledge(nonce);};Application.Idle+=ready;
            }
            Application.Run(app);return 0;
        }
        catch(Exception e){Log.Write(e.ToString());Write("error.txt",e.ToString());return 1;}
        finally{NdiNative.Shutdown();}
    }
    public static void Write(string name,object report)=>File.WriteAllText(Path.Combine(AppContext.BaseDirectory,name),
        report is string s?s:JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
}

internal sealed class TestReceiver:IDisposable
{
    readonly IntPtr handle;
    public TestReceiver(string name)
    {
        NdiNative.EnsureInitialized();IntPtr text=Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var settings=new NdiNative.RecvSettings{Source=new(){Name=text},Bandwidth=100,Color=100,Fields=true};
            handle=NdiNative.NDIlib_recv_create_v3(ref settings);
            if(handle==IntPtr.Zero) throw new Exception("Test NDI receiver creation failed");
        }
        finally{Marshal.FreeCoTaskMem(text);}
    }
    public bool Read(Action<NdiNative.Video> inspect,uint timeout=200)
    {
        NdiNative.Video video=default;
        int type=NdiNative.NDIlib_recv_capture_v3(handle,ref video,IntPtr.Zero,IntPtr.Zero,timeout);
        if(type==4) throw new Exception("NDI receive error");
        if(type!=1) return false;
        try{inspect(video);return true;}finally{NdiNative.NDIlib_recv_free_video_v2(handle,ref video);}
    }
    public void Dispose()=>NdiNative.NDIlib_recv_destroy(handle);
}

static class NdiTests
{
    public static void ColorAndTransport()
    {
        using var device=Vortice.Direct3D11.D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware,Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,Vortice.Direct3D.FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;
        using var pipeline=new ColorPipeline(device,context);
        using var texture=device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.R16G16B16A16_Float,4,2,1,1,Vortice.Direct3D11.BindFlags.ShaderResource));
        foreach(float level in new[]{0f,1f})
        {
            var values=Enumerable.Range(0,32).Select(i=>(Half)(i%4==3?1:level)).ToArray();
            context.UpdateSubresource(values,texture,0,32);
            using var frame=pipeline.ConvertNdi(texture,false,1);
            for(int i=0;i<frame.Length;i++)
                if(frame.Data[i]!=(i%2==0?128:level==0?16:235)) throw new Exception("UYVY GPU range/packing failure");
        }
        // Codec roundtrip tests both dimensions and native-range values across a live NDI source.
        using var sender=new NdiSender("SdrCapture SDR Color Test");
        using var receiver=new TestReceiver(sender.SourceName);
        foreach(var size in new[]{(640,360),(1280,720),(640,360)})
        {
            using var frame=new VideoBuffer(size.Item1,size.Item2);
            for(int y=0;y<frame.Height;y++) for(int x=0;x<frame.Width;x+=2)
            {
                int off=(y*frame.Width+x)*2;byte code=(byte)(x<frame.Width/3?16:x<frame.Width*2/3?126:235);
                frame.Data[off]=128;frame.Data[off+1]=code;frame.Data[off+2]=128;frame.Data[off+3]=code;
            }
            bool valid=false;var clock=Stopwatch.StartNew();
            while(clock.Elapsed.TotalSeconds<8 && !valid)
            {
                sender.Send(frame);
                receiver.Read(v=>
                {
                    if(v.Width!=frame.Width||v.Height!=frame.Height) return;
                    if(v.FourCC!=0x59565955) throw new Exception("Unexpected NDI test format");
                    foreach(var p in new[]{(frame.Width/6,16),(frame.Width/2,126),(frame.Width*5/6,235)})
                    {
                        int code=Marshal.ReadByte(v.Data+v.Stride*(v.Height/2),p.Item1*2+1);
                        if(Math.Abs(code-p.Item2)>2) throw new Exception($"NDI range altered: {code} instead of {p.Item2}");
                    }
                    valid=true;
                },50);
                Thread.Sleep(10);
            }
            if(!valid) throw new Exception("NDI local roundtrip timed out");
        }
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"self-test.txt"),"\nPASS NDI: GPU UYVY packing/range; live local NDI receive, limited black/mid/white and 640x360 -> 1280x720 -> 640x360 without rebuilding sender.");
    }
    public static void Integration(int seconds,bool chaos)
    {
        var display=DisplayInfo.All().First();using var engine=new StreamEngine();
        var options=new CaptureOptions(display.Device,true,true);
        engine.Start(options,"SdrCapture SDR Test");
        var startup=Stopwatch.StartNew();
        while(engine.SourceName=="SdrCapture SDR" && startup.Elapsed.TotalSeconds<10) Thread.Sleep(20);
        if(!engine.Running) throw new Exception(engine.Error??"Sender stopped");
        TestReceiver? receiver=new(engine.SourceName);
        long received=0;double maxGap=0,last=-1;int receiveErrors=0;bool disconnected=false;
        var clock=Stopwatch.StartNew();int changed=-1;var phases=new List<object>();
        var receivedModes=new HashSet<string>();Exception? failure=null;
        try
        {
            while(clock.Elapsed.TotalSeconds<seconds)
            {
                int second=(int)clock.Elapsed.TotalSeconds;
                if(chaos && second!=changed)
                {
                    changed=second;
                    if(second>0 && second%5==0) engine.RequestCaptureRestart();
                    engine.Update(options with {Cursor=second%4<2,Device=second%17==15?"missing-test-monitor":display.Device});
                    if(second==20){receiver?.Dispose();receiver=null;disconnected=true;last=-1;}
                    if(second==23){receiver=new(engine.SourceName);disconnected=false;last=-1;}
                    if(second%5==0) phases.Add(new {second,engine.Frames,engine.CapturedFrames,engine.Recoveries,engine.SourceName,engine.Error});
                }
                if(receiver!=null)
                {
                    bool got=receiver.Read(v=>
                    {
                        if(!engine.HasCaptureSize(v.Width,v.Height)||v.RateN/(double)v.RateD!=60) throw new Exception($"Wrong dimensions/rate: {v.Width}x{v.Height}, {v.RateN}/{v.RateD}");
                        receivedModes.Add($"{v.Width}x{v.Height}");
                        if(v.FourCC!=0x59565955) throw new Exception("Unexpected raw format");
                        if(v.Metadata!=IntPtr.Zero && (Marshal.PtrToStringUTF8(v.Metadata)??"").Contains("bt_2100")) throw new Exception("Unexpected HDR metadata");
                        double now=clock.Elapsed.TotalSeconds;
                        if(last>=0) maxGap=Math.Max(maxGap,now-last);
                        last=now;received++;
                    },100);
                    if(!got && !disconnected && last>=0) receiveErrors++;
                }
                else Thread.Sleep(10);
            }
        }
        catch(Exception e){failure=e;}
        finally{receiver?.Dispose();}
        var report=new{seconds,chaos,display,received,MaxReceiverGapMs=maxGap*1000,ReceiveTimeouts=receiveErrors,
            engine.SourceName,engine.Frames,engine.CapturedFrames,engine.ReplacedFrames,engine.MissedSlots,engine.Recoveries,
            engine.CaptureMs,engine.SendMs,engine.MaxSendMs,engine.Fps,engine.Error,engine.CaptureSizes,ReceivedSizes=receivedModes.ToArray(),Phases=phases,Failure=failure?.ToString()};
        Program.Write("ndi-test.json",report);
        if(failure!=null) throw failure;
        if(received<seconds*35 || maxGap>1 || !engine.Running) throw new Exception("NDI integration failed; see ndi-test.json");
    }
}
