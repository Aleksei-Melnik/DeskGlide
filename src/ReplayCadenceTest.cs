using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SdrCapture;
static class ReplayCadenceTest
{
    public static void Run(int fps,string codec="HEVC",string audio="Mixed",bool liveAudio=false)
    {
        string root=Path.Combine(AppContext.BaseDirectory,$"cadence-{fps}-{codec}-{audio}-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var options=new ReplayOptions{Enabled=true,Fps=fps,Codec=codec,AudioMode=audio,Width=2560,Height=1440,Folder=Path.Combine(root,"clips")};
        NAudio.CoreAudioApi.MMDeviceEnumerator? devices=null;NAudio.CoreAudioApi.MMDevice? endpoint=null;NAudio.Wave.WasapiOut? playback=null;
        if(liveAudio)
        {
            options.Microphone="";options.ExtraAudio="";options.GameAudio=Settings.Load().Replay.GameAudio;
            devices=new();endpoint=options.GameAudio.StartsWith("default:")?devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render,NAudio.CoreAudioApi.Role.Multimedia):devices.GetDevice(options.GameAudio);
            playback=new(endpoint,NAudio.CoreAudioApi.AudioClientShareMode.Shared,true,50);playback.Init(new AudioTimingTests.QuietTone());playback.Play();
        }
        using var recorder=new ReplayRecorder(options,!liveAudio,10);
        using var stop=new CancellationTokenSource();
        long producerSkipped=0;
        var producer=Task.Factory.StartNew(()=>
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            using var pacer=new Pacer();long origin=Stopwatch.GetTimestamp(),index=0;
            while(!stop.IsCancellationRequested)
            {
                long due=origin+index*Stopwatch.Frequency/fps;pacer.WaitUntil(due,stop.Token);
                long now=Stopwatch.GetTimestamp();
                long nextIndex=Math.Max(index,(now-origin)*fps/Stopwatch.Frequency);producerSkipped+=nextIndex-index;index=nextIndex;
                using var frame=new VideoBuffer(2560,1440){CapturedAt=origin+index*Stopwatch.Frequency/fps};
                MemoryMarshal.Cast<byte,uint>(frame.Data.AsSpan(0,frame.Length)).Fill(0x60806080);
                int id=(int)(index&65535);
                for(int y=0;y<32;y++)for(int bit=0;bit<16;bit++)for(int x=bit*16;x<(bit+1)*16;x++)
                    frame.Data[(y*frame.Width+x)*2+1]=(byte)((id&(1<<bit))!=0?235:16);
                int bar=(int)(index*8%2500)&~1;
                for(int y=100;y<frame.Height;y++)frame.Data[(y*frame.Width+bar)*2+1]=235;
                recorder.Offer(frame);index++;
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            Thread.Sleep(12500);
            if(recorder.Error!=null)throw new Exception(recorder.Error);
            var clip=recorder.SaveAsync("Cadence").GetAwaiter().GetResult();
            stop.Cancel();producer.GetAwaiter().GetResult();
            recorder.Update(options with{Enabled=false});
            var probe=ReplayTests.Probe(clip.LocalPath).GetAwaiter().GetResult();
            string raw=Path.Combine(root,"frame-ids.raw");
            using var timeout=new CancellationTokenSource(120000);
            ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",clip.LocalPath,"-an","-vf","crop=256:16:0:0,scale=16:1:flags=neighbor,format=gray","-fps_mode","passthrough","-f","rawvideo",raw],timeout.Token).GetAwaiter().GetResult();
            byte[] data=File.ReadAllBytes(raw);var ids=new List<int>();
            for(int i=0;i+16<=data.Length;i+=16){int id=0;for(int b=0;b<16;b++)if(data[i+b]>127)id|=1<<b;ids.Add(id);}
            var steps=ids.Skip(1).Zip(ids,(a,b)=>(a-b+65536)%65536).Skip(12).ToArray();
            int repeats=steps.Count(d=>d==0),skips=steps.Count(d=>d>1);
            var streams=probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            int audioTracks=streams.Count(s=>s.GetProperty("codec_type").GetString()=="audio");
            string actualCodec=streams.First(s=>s.GetProperty("codec_type").GetString()=="video").GetProperty("codec_name").GetString()!;
            var report=new{fps,codec,actualCodec,audio,audioTracks,Frames=ids.Count,Repeats=repeats,Skips=skips,MaxStep=steps.Max(),RepeatPercent=100.0*repeats/steps.Length,producerSkipped,recorder.HistoryMisses,recorder.MaxWriterLateMs,recorder.EncoderStarts,recorder.Error,File=clip.LocalPath};
            Program.Write($"cadence-{fps}-{codec}-{audio}.json",report);
            if(actualCodec!=(codec=="H264"?"h264":codec.ToLowerInvariant()))throw new Exception("Unexpected codec");
            if(audioTracks!=(audio=="Silent"?0:audio=="Separate"?3:1))throw new Exception("Wrong audio track count");
            if(recorder.EncoderStarts!=1||repeats>steps.Length*.03||steps.Max()>8)throw new Exception("Cadence validation failed; see report.");
        }
        finally{stop.Cancel();producer.GetAwaiter().GetResult();playback?.Dispose();endpoint?.Dispose();devices?.Dispose();}
    }
}
