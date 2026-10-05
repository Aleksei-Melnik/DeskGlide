using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;

// Only the selected endpoint goes to NDI. Replay microphone/remote tracks never
// enter this path. Its clock and short wait are independent of the replay writer.
sealed class NdiAudioOutput:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly Task worker;
    public string Status {get;private set;}="Silent";
    public NdiAudioOutput(NdiSender sender,Func<string> source,Func<int> volume)
    {
        worker=Task.Factory.StartNew(()=>
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            using var pacing=new Pacer();
            byte[] pcm=new byte[480*6*4];float[] stereo=new float[960];
            while(!stop.IsCancellationRequested)
            {
                string id=source();
                if(id.Length==0){Status="Silent";if(stop.Token.WaitHandle.WaitOne(50))break;continue;}
                try
                {
                    using var capture=new ReplayAudio(new ReplayOptions{Microphone="",GameAudio=id,ExtraAudio="",AudioMode="Mixed"});
                    long sample=AudioTimeline.Position();
                    while(!stop.IsCancellationRequested&&source()==id)
                    {
                        // First sample is 30 ms old; the last has had 20 ms to arrive.
                        pacing.WaitUntil((long)((sample/48000.0+.03)*Stopwatch.Frequency),stop.Token);
                        if(stop.IsCancellationRequested)break;
                        if(AudioTimeline.Position()-sample>9600)sample=AudioTimeline.Position()-1440;
                        capture.Read(sample,pcm);
                        var six=MemoryMarshal.Cast<byte,float>(pcm.AsSpan());
                        for(int i=0;i<480;i++){stereo[i*2]=six[i*6+2];stereo[i*2+1]=six[i*6+3];}
                        sender.SendAudio(stereo,sample,Math.Clamp(volume(),0,100)/100f);sample+=480;Status=capture.Status;
                    }
                }
                catch(Exception e){Status="Ошибка звука NDI: "+e.Message;Log.Write(Status);if(stop.Token.WaitHandle.WaitOne(1500))break;}
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    public void Dispose(){stop.Cancel();worker.GetAwaiter().GetResult();stop.Dispose();}
}

static class NdiAudioTests
{
    public static void Probe()
    {
        NdiNative.EnsureInitialized();
        IntPtr name=Marshal.StringToCoTaskMemUTF8(Environment.MachineName+" (SdrCapture SDR)"),handle;
        try{var settings=new NdiNative.RecvSettings{Source=new(){Name=name},Bandwidth=100,Color=100};handle=NdiNative.NDIlib_recv_create_v3(ref settings);}
        finally{Marshal.FreeCoTaskMem(name);}
        if(handle==IntPtr.Zero)throw new Exception("Could not open running NDI source");
        using var devices=new NAudio.CoreAudioApi.MMDeviceEnumerator();
        string id=Settings.Load().NdiAudioDevice;
        using var endpoint=id.StartsWith("default:")?devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render,NAudio.CoreAudioApi.Role.Multimedia):devices.GetDevice(id);
        using var playback=new NAudio.Wave.WasapiOut(endpoint,NAudio.CoreAudioApi.AudioClientShareMode.Shared,true,50);
        playback.Init(new AudioTimingTests.QuietTone());playback.Play();
        try
        {
            long frames=0,packets=0;float peak=0;int gaps=0;long next=0;
            var timer=Stopwatch.StartNew();
            while(timer.Elapsed.TotalSeconds<6)
            {
                NdiNative.Audio audio=default;
                if(NdiNative.NDIlib_recv_capture_v2(handle,IntPtr.Zero,ref audio,IntPtr.Zero,100)!=2)continue;
                try
                {
                    if(audio.Rate!=48000||audio.Channels!=2)throw new Exception("Wrong live audio format");
                    float[] channel=new float[audio.Samples];Marshal.Copy(audio.Data,channel,0,audio.Samples);
                    foreach(float sample in channel)peak=Math.Max(peak,Math.Abs(sample));
                    if(next>0&&Math.Abs(audio.Timecode-next)>5000)gaps++;
                    next=audio.Timecode+(long)Math.Round(audio.Samples*10000000.0/48000);frames+=audio.Samples;packets++;
                }
                finally{NdiNative.NDIlib_recv_free_audio_v2(handle,ref audio);}
            }
            bool pass=frames>=48000&&peak>.01f&&gaps==0;
            Program.Write("ndi-audio-live-probe.json",new{Pass=pass,ExistingSource=Environment.MachineName+" (SdrCapture SDR)",Packets=packets,Frames=frames,Peak=peak,TimestampGaps=gaps,DiscordTested=false});
            if(!pass)throw new Exception("Running NDI audio failed; see ndi-audio-live-probe.json");
        }
        finally{NdiNative.NDIlib_recv_destroy(handle);}
    }
    public static void Run()
    {
        using var sender=new NdiSender("SdrCapture synthetic audio test");
        IntPtr name=Marshal.StringToCoTaskMemUTF8(sender.SourceName),handle;
        try{var settings=new NdiNative.RecvSettings{Source=new(){Name=name},Bandwidth=100,Color=100};handle=NdiNative.NDIlib_recv_create_v3(ref settings);}
        finally{Marshal.FreeCoTaskMem(name);}
        if(handle==IntPtr.Zero)throw new Exception("NDI audio receiver creation failed");
        try
        {
            float[] data=new float[960];for(int i=0;i<480;i++){data[i*2]=.0125f;data[i*2+1]=-.025f;}
            foreach(float gain in new[]{1f,.5f,0f})
            {
            bool valid=false;long captured=AudioTimeline.Position();var clock=Stopwatch.StartNew();
            while(clock.Elapsed.TotalSeconds<8&&!valid)
            {
                sender.SendAudio(data,captured,gain);
                NdiNative.Audio audio=default;
                if(NdiNative.NDIlib_recv_capture_v2(handle,IntPtr.Zero,ref audio,IntPtr.Zero,50)!=2)continue;
                try
                {
                    if(audio.Rate!=48000||audio.Channels!=2||audio.Samples!=480)throw new Exception("NDI audio format mismatch");
                    var left=new float[480];var right=new float[480];Marshal.Copy(audio.Data,left,0,480);Marshal.Copy(audio.Data+audio.Stride,right,0,480);
                    // Ignore queued packets from the preceding gain level.
                    if(Math.Abs(audio.Timecode-sender.AudioTimecode(captured))>1)continue;
                    if(left.Any(v=>Math.Abs(v-.125f*gain)>1e-5)||right.Any(v=>Math.Abs(v+.25f*gain)>1e-5))throw new Exception("NDI gain/channel/reference-level mismatch");
                    valid=true;
                }
                finally{NdiNative.NDIlib_recv_free_audio_v2(handle,ref audio);}
            }
            if(!valid)throw new Exception("No NDI audio received");
            }
            Program.Write("ndi-audio-test.json",new{Pass=true,Rate=48000,Channels=2,Samples=480,ChannelIsolation=true,SmpteHeadroomDb=20,Volume100And50AndMute=true,CaptureTimePreserved=true,RealDesktopPublished=false,DiscordTested=false});
        }
        finally{NdiNative.NDIlib_recv_destroy(handle);}
    }
}
