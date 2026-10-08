using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing.Imaging;

namespace SdrCapture;
static class FeatureTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        Require(JsonSerializer.Deserialize<Settings>("{}")!.PreventIdleSleep,"Existing settings did not enable idle-sleep protection by default");
        var sleepSettings=new Settings{PreventIdleSleep=false};Require(!sleepSettings.Copy().PreventIdleSleep&&!JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(sleepSettings))!.PreventIdleSleep,"Explicit idle-sleep preference lost");
        using(var awake=new KeepAwake())
        {
            awake.Set(true);Require((KeepAwake.Request(KeepAwake.Continuous|KeepAwake.System|KeepAwake.Display)&3)==3,"System/display power request not applied");
            awake.Set(false);Require((KeepAwake.Request(KeepAwake.Continuous)&3)==0,"Power request not released");
        }
        using(var awake=new KeepAwake())awake.Set(true);
        Require((KeepAwake.Request(KeepAwake.Continuous)&3)==0,"Disposing idle-sleep protection left a power request");
        DiscordDeviceTests.Rules();
        var buttons=new KvmMouseButtons();Require(buttons.Release().Length==0,"Idle release must not synthesize clicks");
        buttons.Track(8,0);buttons.Track(16,0);Require(buttons.Release().Length==0,"Released right button repeated");
        buttons.Track(8,0);var release=buttons.Release();Require(release.Length==1&&release[0].Up==16,"Held right not released exactly once");Require(buttons.Release().Length==0,"Duplicate release");
        buttons.Track(128,1);buttons.Track(128,2);Require(buttons.Release().Length==2,"X buttons lost");
        var local=new MonitorPlacement{Peer="local",Device="center",X=1920,Y=0,Width=2560,Height=1440};
        var left=new MonitorPlacement{Peer="stream",Device="left",X=0,Y=0,Width=1920,Height=1080};
        var right=new MonitorPlacement{Peer="stream",Device="right",X=4480,Y=0,Width=1080,Height=1920};
        var screen=new KvmScreen("center",0,0,2560,1440,true);
        foreach(var point in new[]{new Point(-120,20),new Point(0,0)})Require(KvmLayout.EdgeCrossing([local,left,right],local,screen,point)?.Target==left,"Fast left / corner crossing");
        Require(KvmLayout.EdgeCrossing([local,left,right],local,screen,new(2800,30))?.Target==right,"Fast right crossing");
        Require(KvmLayout.EdgeCrossing([local,left,right],local,screen,new(100,100))==null,"Interior must stay local");
        var gameMode=new KvmScreen("center",0,0,1280,1024,true);
        Require(KvmLayout.EdgeCrossing([local,left,right],local,gameMode,new(1279,512))?.Target==right,"Fullscreen resolution broke the right edge");
        Require(KvmLayout.ToPhysical(local,gameMode,new(local.X+2559,local.Y+1439))==new Point(1279,1023),"Fullscreen cursor mapping was not scaled");
        using(var image=new Bitmap(2560,1440))
        {
            using(var graphics=Graphics.FromImage(image))graphics.Clear(Color.FromArgb(13,91,187));
            var encoded=KvmImageCodec.Encode(image,0);using var memory=new MemoryStream(encoded.Data);using var decoded=new Bitmap(memory);
            Require(decoded.Width==2560&&decoded.Height==1440&&decoded.GetPixel(100,100).ToArgb()==image.GetPixel(100,100).ToArgb(),"KVM native PNG altered pixels");
        }
        using(var noise=new Bitmap(3840,2160,PixelFormat.Format24bppRgb))
        {
            var bits=noise.LockBits(new(0,0,noise.Width,noise.Height),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
            try{byte[] noiseBytes=System.Security.Cryptography.RandomNumberGenerator.GetBytes(bits.Stride*noise.Height);Marshal.Copy(noiseBytes,0,bits.Scan0,noiseBytes.Length);}finally{noise.UnlockBits(bits);}
            var encoded=KvmImageCodec.Encode(noise,0);Require(encoded.Data.Length<=2800000&&encoded.Description.Contains("JPEG"),"Noisy KVM frame exceeds wire budget");
        }
        string root=Path.Combine(AppContext.BaseDirectory,"profile-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var options=new Settings{Kvm=new(){Role="Host",RemoteOnlyPeers=["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]},Discord=new(){Source="Gaming (SdrCapture SDR)",Volume=75}};
        var profile=ConfigurationBackup.Capture(options,true,root);byte[] data=ConfigurationBackup.Encode(profile,"fixture-password");
        var decodedProfile=ConfigurationBackup.Decode(data,"fixture-password");Require(decodedProfile.Settings.Kvm.RemoteOnlyPeers.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")&&decodedProfile.Settings.Discord.Volume==75,"Profile lost settings");
        bool wrong=false;try{ConfigurationBackup.Decode(data,"wrong-password");}catch{wrong=true;}Require(wrong,"Wrong password accepted");
        string restored=Path.Combine(root,"restore");Directory.CreateDirectory(restored);File.WriteAllText(Path.Combine(restored,"settings.json"),"original");
        bool rollback=false;try{ConfigurationBackup.Restore(decodedProfile,restored,n=>{if(n==1)throw new IOException("fixture");});}catch{rollback=true;}
        Require(rollback&&File.ReadAllText(Path.Combine(restored,"settings.json"))=="original"&&!File.Exists(Path.Combine(restored,"Kvm","host-identity.json")),"Profile rollback failed");
        ConfigurationBackup.Restore(decodedProfile,restored);Require(File.Exists(Path.Combine(restored,"Kvm","host-identity.json")),"Host identity not restored");
        bool blocked=false;try{CameraInstallation.RequireReceiver("Host");}catch(InvalidOperationException){blocked=true;}Require(blocked,"Host installation not blocked");
        bool hash=false;try{DiscordSetup.VerifyPackage([1,2,3]);}catch(IOException){hash=true;}Require(hash,"Driver hash not checked");
        string cache=Path.Combine(root,"cache"),temporary=Path.Combine(cache,Guid.NewGuid().ToString("N")),permanent=Path.Combine(root,"desktop-copy.txt"),unmarked=Path.Combine(cache,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);Directory.CreateDirectory(unmarked);File.WriteAllText(Path.Combine(temporary,"file.txt"),"payload");File.Copy(Path.Combine(temporary,"file.txt"),permanent);
        KvmFileCache.Mark(cache,temporary);string lease=Path.Combine(cache+".leases",Path.GetFileName(temporary)+".json");File.WriteAllText(lease,JsonSerializer.Serialize(DateTimeOffset.UtcNow.AddHours(-5)));
        KvmFileCache.Clean(cache,TimeSpan.FromHours(4),new HashSet<string>{temporary});Require(Directory.Exists(temporary),"Active drag cache deleted");
        KvmFileCache.Clean(cache,TimeSpan.FromHours(4));Require(!Directory.Exists(temporary)&&Directory.Exists(unmarked)&&File.ReadAllText(permanent)=="payload","Cache cleanup touched a permanent/unmarked destination");
        Program.Write("feature-tests.json",new{Pass=true,MouseButtons=true,FastEdgeOvershoot=true,NativeKvmPixels=true,EncryptedProfileRollback=true,HostDriverBlocked=true,InstallerIntegrity=true});
    }
    public static unsafe void Camera(int width=1920,int height=1080)
    {
        using var frames=new CameraFrames();using var stop=new CancellationTokenSource();
        using var ready=new ManualResetEventSlim();
        var publish=Task.Factory.StartNew(()=>
        {
            var pixels=new byte[width*height*4];var clock=Stopwatch.StartNew();int previous=-1;
            fixed(byte* ptr=pixels)while(!stop.IsCancellationRequested)
            {
                int index=(int)(clock.Elapsed.TotalSeconds*60);if(index==previous){Thread.Sleep(1);continue;}
                previous=index;Array.Fill(pixels,(byte)(index%250+1));
                for(int p=0;p<width*32*4;p+=4){pixels[p]=0;pixels[p+1]=0;pixels[p+2]=255;int bottom=p+width*(height-32)*4;pixels[bottom]=255;pixels[bottom+1]=0;pixels[bottom+2]=0;}
                frames.Write((IntPtr)ptr,width*4,width,height);ready.Set();
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            Require(ready.Wait(3000),"Camera publisher did not start");
            foreach(string mode in new[]{"rgb24","rgb32","30"})
            {
                var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"camera","CameraProbe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"camera","ScreenCapture.Camera.dll"));start.ArgumentList.Add(mode);start.ArgumentList.Add("pattern");
                using var process=Process.Start(start)!;string text=process.StandardOutput.ReadToEnd(),error=process.StandardError.ReadToEnd();process.WaitForExit();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,$"camera-native-{width}-{mode}-test.json"),text);
                Require(process.ExitCode==0,"Native camera "+mode+" failed: "+text+error);
                using var report=JsonDocument.Parse(text);Require(report.RootElement.GetProperty("Width").GetInt32()==width&&report.RootElement.GetProperty("Height").GetInt32()==height,"Camera changed native dimensions");
            }
        }
        finally{stop.Cancel();publish.GetAwaiter().GetResult();}
        float[] sample=[10,-10,float.NaN,5,2,-2,float.PositiveInfinity,1];fixed(float* ptr=sample)
        {
            var bytes=DiscordReceiver.ConvertAudio(new(){Data=(IntPtr)ptr,Samples=4,Channels=2,Stride=16},50);
            var values=MemoryMarshal.Cast<byte,float>(bytes);Require(values[0]==.5f&&Math.Abs(values[1]-.1f)<.00001&&values[4]==0&&values[5]==0,"NDI audio normalization/NaN handling");
        }
    }
    public static unsafe void CameraNativeFormats()
    {
        using var frames=new CameraFrames();var results=new List<JsonElement>();
        foreach(var (width,height) in new[]{(1920,1080),(2560,1440),(3840,2160),(7680,4320),(3440,1440),(1080,1920)})
        {
            byte[] pixels=new byte[width*height*4];fixed(byte* ptr=pixels)frames.Write((IntPtr)ptr,width*4,width,height);
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"camera","CameraProbe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"camera","ScreenCapture.Camera.dll"));start.ArgumentList.Add("caps");using var process=Process.Start(start)!;
            string text=process.StandardOutput.ReadToEnd(),error=process.StandardError.ReadToEnd();process.WaitForExit();using var report=JsonDocument.Parse(text);
            Require(process.ExitCode==0&&report.RootElement.GetProperty("Width").GetInt32()==width&&report.RootElement.GetProperty("Height").GetInt32()==height,"Camera native format negotiation: "+text+error);results.Add(report.RootElement.Clone());
        }
        Program.Write("camera-native-formats-test.json",new{Pass=true,Scope="IPC dimensions and DirectShow format negotiation; not 8K frame-rate or network throughput",Formats=results});
    }
    public static unsafe void CameraHold()
    {
        using var frames=new CameraFrames();byte[] pixels=new byte[1920*1080*4];Array.Fill(pixels,(byte)113);
        fixed(byte* ptr=pixels)frames.Write((IntPtr)ptr,1920*4,1920,1080);
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"camera","CameraProbe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"camera","ScreenCapture.Camera.dll"));using var process=Process.Start(start)!;
        string text=process.StandardOutput.ReadToEnd(),error=process.StandardError.ReadToEnd();process.WaitForExit();using var report=JsonDocument.Parse(text);
        Require(report.RootElement.GetProperty("Frames").GetInt32()>=200&&report.RootElement.GetProperty("Unique").GetInt32()==1,"Paused publisher changed the last image: "+text+error);
        Program.Write("camera-hold-test.json",new{Pass=true,HoldSeconds=4,Probe=report.RootElement.Clone()});
    }
    public static void NdiCamera()
    {
        var total=Stopwatch.StartNew();long sent=0;double sendMs=0;
        var observed=new System.Collections.Concurrent.ConcurrentQueue<(int Original,int Scaled)>();DiscordReceiver.TestFrame=(a,b)=>observed.Enqueue((a,b));
        using var sender=new NdiSender("ScreenCapture Camera fixture "+Guid.NewGuid().ToString("N"));
        using var receiver=new DiscordReceiver(new(){Enabled=true,Source=sender.SourceName});
        using var stop=new CancellationTokenSource();
        var publish=Task.Factory.StartNew(()=>
        {
            using var frame=new VideoBuffer(2560,1440);var clock=Stopwatch.StartNew();int previous=-1;
            while(!stop.IsCancellationRequested)
            {
                int index=(int)(clock.Elapsed.TotalSeconds*60);if(previous==index){Thread.Sleep(1);continue;}previous=index;
                for(int i=0;i<frame.Length;i+=2){frame.Data[i]=128;frame.Data[i+1]=(byte)(20+(index*7)%210);}
                var start=Stopwatch.GetTimestamp();sender.Send(frame);sent++;sendMs=Math.Max(sendMs,Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            while(observed.Count<60&&total.Elapsed<TimeSpan.FromSeconds(20))Thread.Sleep(50);
            Require(observed.Count>=60,"NDI source did not connect: "+receiver.Status);
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"camera","CameraProbe.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"camera","ScreenCapture.Camera.dll"));using var process=Process.Start(start)!;
            string text=process.StandardOutput.ReadToEnd(),error=process.StandardError.ReadToEnd();process.WaitForExit();
            var samples=observed.ToArray();Program.Write("camera-ndi-test.json",new{Probe=JsonSerializer.Deserialize<JsonElement>(text),Receiver=receiver.Status,ExitCode=process.ExitCode,Observed=samples.Length,Sent=sent,SendMs=sendMs,Elapsed=total.Elapsed.TotalSeconds,OriginalChanges=samples.Zip(samples.Skip(1)).Count(p=>p.First.Original!=p.Second.Original),ScaledChanges=samples.Zip(samples.Skip(1)).Count(p=>p.First.Scaled!=p.Second.Scaled),First=samples.Take(4).Select(p=>new{p.Original,p.Scaled})});
            Require(process.ExitCode==0,"NDI → camera: "+text+error+receiver.Status);
        }
        finally{stop.Cancel();publish.GetAwaiter().GetResult();DiscordReceiver.TestFrame=null;}
    }
}
