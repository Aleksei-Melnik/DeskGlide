using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;

static class RecordingNdiTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        var failures=new List<string>();int checks=0;
        void Check(string name,Action action){checks++;try{action();}catch(Exception e){failures.Add(name+": "+e.Message);}}
        Check("CommonClock",()=>
        {
            // A receiver that timestamps each packet at arrival will turn this
            // delayed audio packet into an A/V offset and stretch the video step.
            var clock=new NdiRecordingClock(10_000_000);
            Require(clock.Map(100_000_000,20_000_000)==20_000_000,"The first source timestamp did not anchor locally.");
            Require(clock.Map(100_100_000,20_700_000)==20_100_000,"Arrival jitter changed the source A/V timeline.");
            Require(clock.Map(100_050_000,20_900_000)==20_050_000,"Out-of-order audio could not use the video clock.");
        });
        Check("InvalidClock",()=>
        {
            foreach(long invalid in new[]{0L,long.MinValue,long.MaxValue})
            {
                bool rejected=false;try{new NdiRecordingClock(10_000_000).Map(invalid,20_000_000);}catch(IOException){rejected=true;}
                Require(rejected,"An undefined source timestamp was accepted.");
            }
            var clock=new NdiRecordingClock(10_000_000);clock.Map(100_000_000,20_000_000);
            bool jumped=false;try{clock.Map(700_000_000,21_000_000);}catch(IOException){jumped=true;}
            Require(jumped,"A discontinuous source clock created distant future footage.");
        });
        Check("UyvyStride",()=>
        {
            IntPtr data=Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(new byte[]{128,16,128,17,128,18,128,19,9,9,9,9,128,20,128,21,128,22,128,23,8,8,8,8},0,data,24);
                var input=new NdiNative.Video{Width=4,Height=2,FourCC=0x59565955,Stride=12,Format=1,RateN=120,RateD=1,Data=data};
                using var copied=NdiRecordingFrames.Copy(input,123);
                Require(copied.CapturedAt==123&&copied.Length==16,"The frame timestamp or dimensions changed.");
                Require(copied.Data.AsSpan(0,16).SequenceEqual(new byte[]{128,16,128,17,128,18,128,19,128,20,128,21,128,22,128,23}),"Padded native UYVY rows were not copied directly.");
                foreach(var invalid in new[]{input with {Width=3},input with {Stride=7},input with {Stride=9},input with {Format=0},input with {FourCC=0x41524742},input with {Data=IntPtr.Zero},input with {Width=8192,Height=8192,Stride=16384},input with {Stride=int.MaxValue-1}})
                {bool rejected=false;try{using var bad=NdiRecordingFrames.Copy(invalid,123);}catch(IOException){rejected=true;}Require(rejected,"Unsupported or oversized native video was accepted.");}
            }
            finally{Marshal.FreeHGlobal(data);}
        });
        Check("PlanarSmpteAudio",()=>
        {
            IntPtr data=Marshal.AllocHGlobal(24);
            try
            {
                Marshal.Copy(new float[]{.5f,float.NaN,123,-.25f,float.PositiveInfinity,123},0,data,6);
                var input=new NdiNative.Audio{Rate=48000,Channels=2,Samples=2,Stride=12,Data=data};
                var pcm=NdiRecordingAudio.Convert(input);
                Require(pcm.Length==4&&Math.Abs(pcm[0]-.05f)<1e-6&&Math.Abs(pcm[1]+.025f)<1e-6&&pcm[2]==0&&pcm[3]==0,"NDI planar channels/headroom/nonfinite samples were not normalized.");
                var mono=NdiRecordingAudio.Convert(input with {Channels=1});
                Require(mono[0]==mono[1],"Mono audio was not duplicated to the stereo game track.");
                foreach(var invalid in new[]{input with {Rate=44100},input with {Channels=0},input with {Channels=65},input with {Samples=48001},input with {Stride=7},input with {Data=IntPtr.Zero},input with {Stride=int.MaxValue-3,Channels=64}})
                {bool rejected=false;try{NdiRecordingAudio.Convert(invalid);}catch(IOException){rejected=true;}Require(rejected,"Malformed or oversized audio was accepted.");}
            }
            finally{Marshal.FreeHGlobal(data);}
        });
        Check("TransportLiveness",()=>
        {
            var health=new NdiRecordingHealth(1000,0);
            Require(health.Observe(10,100),"First source image was discarded.");
            Require(!health.Observe(2500,100),"A held source image was offered as a fresh capture.");
            Require(!health.Expired(5499)&&health.Expired(5500),"A held image did not keep its live transport alive for the bounded timeout.");
        });
        Check("SenderClock",()=>
        {
            var clock=new NdiSenderClock(10_000_000,20_000_000,40_000_000);
            Require(clock.Video(25_000_000)==45_000_000,"Sender discarded original video capture time.");
            Require(clock.Audio(120_000)==45_000_000,"Sender audio and video did not use the same source clock.");
            Require(clock.Video(25_000_000)==45_000_000,"A held image acquired a new capture timestamp.");
        });
        Check("CaptureHealth",()=>
        {
            IntPtr metadata=Marshal.StringToCoTaskMemUTF8("<deskglide capture=\"recovering\"/>");
            try{Require(!NdiRecordingFrames.SourceActive(metadata),"An alive sender hid its failed desktop capture.");}
            finally{Marshal.FreeCoTaskMem(metadata);}
            Require(StreamEngine.CaptureIsActive(3999,1000,1000,false),"A healthy static capture poll was marked lost.");
            Require(!StreamEngine.CaptureIsActive(4001,1000,1000,false),"A blocked capture worker continued publishing healthy last images.");
            Require(!StreamEngine.CaptureIsActive(1001,1000,1000,true),"Capture recovery was published as healthy.");
        });
        Check("HeldPictureClock",()=>
        {
            var clock=new NdiSenderClock(10_000_000,20_000_000,40_000_000);
            Require(clock.Present(25_000_000,50_000_000,60)==70_000_000,"An old static picture could not bootstrap current recording audio.");
            Require(clock.Present(50_080_000,50_100_000,60)==70_080_000,"A fresh gameplay frame lost its original capture timestamp.");
            Require(clock.Present(50_050_000,50_200_000,60)==70_080_001,"A delayed image reordered the output source clock.");
        });
        Check("NdiAudioRouting",()=>
        {
            using var writer=NdiRecordingAudioBus.Register("recording-unit");
            long start=AudioTimeline.Position()-480;
            writer.Put(start,Enumerable.Range(0,960).Select(i=>i%2==0?.05f:-.025f).ToArray());
            using var audio=new ReplayAudio(new ReplayOptions{Microphone="",GameAudio="ndi:recording-unit",ExtraAudio="",AudioMode="Mixed"});
            var bytes=new byte[480*6*4];audio.Read(start,bytes);
            var pcm=MemoryMarshal.Cast<byte,float>(bytes.AsSpan());
            Require(Math.Abs(pcm[2]-.05f)<1e-6&&Math.Abs(pcm[3]+.025f)<1e-6&&pcm[0]==0&&pcm[4]==0,"NDI audio did not enter only the selected stereo recording track.");
            writer.Dispose();audio.Read(start,bytes);
            Require(MemoryMarshal.Cast<byte,float>(bytes.AsSpan()).IndexOfAnyExcept(0f)<0,"A disposed NDI source left stale audio available.");
        });
        Check("BoundedShutdown",()=>
        {
            var nativeCall=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int released=0;
            var timer=Stopwatch.StartNew();
            Require(!MediaShutdown.Drain([nativeCall.Task],()=>Interlocked.Increment(ref released),20),"A blocked receiver reported successful shutdown.");
            Require(timer.Elapsed.TotalSeconds<1&&released==0,"Shutdown freed a receiver still used by native capture or exceeded its deadline.");
            nativeCall.SetResult();
            Require(SpinWait.SpinUntil(()=>Volatile.Read(ref released)==1,1000),"Deferred native resources were not released after capture returned.");
        });
        Program.Write("recording-ndi-tests.json",new{Pass=failures.Count==0,Checks=checks,Failures=failures,CommonSourceClock=true,ArrivalJitterIgnored=true});
        if(failures.Count>0)throw new Exception(string.Join("\n",failures));
    }
    public static void Loopback()
    {
        using var sender=new NdiSender("DeskGlide synthetic recording "+Guid.NewGuid().ToString("N")[..8]);
        int online=0,offline=0;long observed=0;
        using var frame=new VideoBuffer(640,360){CapturedAt=Stopwatch.GetTimestamp()-Stopwatch.Frequency*3};
        for(int i=0;i<frame.Length;i++)frame.Data[i]=(byte)(i%2==0?128:126);
        using var source=new RecordingNdiSource(sender.SourceName,frame=>
        {
            Require(frame.Width==640&&frame.Height==360,"Remote recording changed native source dimensions.");
            Require(frame.Data[1]>=14&&frame.Data[1]<=237,"Remote recording changed limited UYVY range.");
            Interlocked.Increment(ref observed);
        },available=>{if(available)Interlocked.Increment(ref online);else Interlocked.Increment(ref offline);});
        using var recordingAudio=new ReplayAudio(new ReplayOptions{Microphone="",GameAudio=source.SourceAudioId,ExtraAudio="",AudioMode="Mixed"});
        static float Tone(long position,int channel)=>(float)((channel==0?.05:.025)*Math.Sin(2*Math.PI*(channel==0?503.7:711.37)*position/48000));
        using var audioStop=new CancellationTokenSource();
        var audioProducer=Task.Factory.StartNew(()=>
        {
            using var pacer=new Pacer();long position=AudioTimeline.Position();var stereo=new float[960];
            while(!audioStop.IsCancellationRequested)
            {
                pacer.WaitUntil((long)(position* (decimal)Stopwatch.Frequency/48000),audioStop.Token);
                for(int i=0;i<480;i++){stereo[i*2]=Tone(position+i,0);stereo[i*2+1]=Tone(position+i,1);}
                sender.SendAudio(stereo,position);position+=480;
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        void SendFor(int rate,double seconds,bool fresh=true,bool active=true)
        {
            using var pacer=new Pacer();pacer.SetRate(rate);var timer=Stopwatch.StartNew();
            while(timer.Elapsed.TotalSeconds<seconds)
            {
                pacer.Wait(CancellationToken.None);
                long now=Stopwatch.GetTimestamp();if(fresh||frame.CapturedAt==0)frame.CapturedAt=now;
                sender.Send(frame,rate,active);
            }
        }
        double SendUntil(int rate,Func<bool> ready)
        {
            var timer=Stopwatch.StartNew();
            while(!ready()&&timer.Elapsed.TotalSeconds<10)SendFor(rate,.1);
            return timer.Elapsed.TotalMilliseconds;
        }
        try
        {
            SendFor(60,8,false);
            Require(source.Available&&observed>=10,"Synthetic UYVY source was not received: "+source.Status);
            SendFor(120,1.5);
            Require(Math.Abs(source.SourceFrameRate-120)<.01,"The 120 FPS NDI declaration was not preserved.");
            Require(source.AudioPackets>20,"Synthetic NDI audio was not received: "+source.Status);
            double receiveFps=source.FramesPerSecond;
            Thread.Sleep(100); // Drain the last video packet before sampling its paired clock fields.
            long sample=AudioTimeline.Position(source.LastCapturedAt)-4800;
            long expectedSample=(long)Math.Round((source.LastSourceTimecode-sender.AudioTimecode(0))*48000m/10_000_000)-4800;
            var audioBytes=new byte[480*6*4];recordingAudio.Read(sample,audioBytes);
            var pcm=MemoryMarshal.Cast<byte,float>(audioBytes.AsSpan());
            double maxAudioError=0;
            for(int i=0;i<480;i++)for(int channel=0;channel<2;channel++)maxAudioError=Math.Max(maxAudioError,Math.Abs(pcm[i*6+2+channel]-Tone(expectedSample+i,channel)));
            Require(maxAudioError<.004,"The real NDI A/V clock or planar audio changed tone phase: "+maxAudioError);
            long beforeHeld=source.Frames;SendFor(120,.6,false);
            Require(source.Frames>beforeHeld&&source.Available,"Held source pictures could not carry current presentation/audio time.");
            SendFor(120,.4,false,false);
            Require(!source.Available&&offline>0,"Sender capture loss was hidden by its last image.");
            double captureRecoveryMs=SendUntil(60,()=>source.Available&&online>=2);
            Require(source.Available&&online>=2,"The source did not recover after capture resumed: "+source.Status+" · "+source.Diagnostics);
            var wait=Stopwatch.StartNew();while(source.Available&&wait.Elapsed.TotalSeconds<4.5)Thread.Sleep(20);
            Require(!source.Available&&source.SourceGaps>=2,"Stopped source packets did not expire the bounded recording input.");
            double transportRecoveryMs=SendUntil(120,()=>source.Available&&online>=3);
            Require(source.Available&&online>=3,"The receiver did not reconnect after transport loss: "+source.Status+" · "+source.Diagnostics);
            var shutdown=Stopwatch.StartNew();source.Dispose();shutdown.Stop();
            Require(shutdown.Elapsed.TotalSeconds<2,"NDI recording shutdown was not bounded.");
            Program.Write("recording-ndi-loopback.json",new{Pass=true,source.Frames,source.AudioPackets,source.DuplicateFrames,source.SourceGaps,FramesPerSecond=receiveFps,OnlineTransitions=online,OfflineTransitions=offline,SourceRates=new[]{60,120},SharedAvClock=true,MaximumAudioPhaseError=maxAudioError,AudioTrackIsolation=true,NativeUyvy=true,StaticInitialPictureSeconds=3,CaptureRecoveryMilliseconds=captureRecoveryMs,TransportRecoveryMilliseconds=transportRecoveryMs,DesktopCaptured=false,OsAudioDevicesOpened=false,TwoComputerLanTested=false,ShutdownMilliseconds=shutdown.Elapsed.TotalMilliseconds});
        }
        finally{audioStop.Cancel();if(!audioProducer.Wait(1500))throw new Exception("Synthetic NDI audio producer did not stop.");}
    }
}
