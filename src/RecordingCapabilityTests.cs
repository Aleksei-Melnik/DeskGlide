using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SdrCapture;

static class RecordingCapabilityTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){try{action();}catch(ArgumentException){return;}throw new Exception(message);}
    public static void Run()
    {
        var old=JsonSerializer.Deserialize<ReplayOptions>("{\"Minutes\":5,\"Codec\":\"HEVC\",\"Fps\":60}")!;
        old.Validate();
        using var saved=JsonDocument.Parse(JsonSerializer.Serialize(old));
        Require(saved.RootElement.TryGetProperty("Encoder",out var backend)&&backend.GetString()=="GPU","Legacy profiles lost the explicit GPU backend default.");
        Require(saved.RootElement.TryGetProperty("RateControl",out var rate)&&rate.GetString()=="Quality","Legacy settings did not acquire the Quality rate-control default.");
        Require(saved.RootElement.TryGetProperty("EncodingComputer",out var computer)&&computer.GetString()=="","An old replay profile did not remain on this computer.");
        foreach(string invalid in new[]{"{\"Encoder\":\"shell\"}","{\"Encoder\":null}","{\"Encoder\":\"CPU\",\"Codec\":\"HEVC\"}","{\"Encoder\":\"CPU\",\"Codec\":\"AV1\"}","{\"RateControl\":\"invalid\"}","{\"BitrateMbps\":-1}","{\"BitrateMbps\":1001}","{\"KeyframeSeconds\":0}","{\"KeyframeSeconds\":11}","{\"EncoderPreset\":\"invalid\"}","{\"Width\":0,\"Height\":1080}","{\"EncodingComputer\":\"not-a-peer\"}","{\"EncodingComputer\":\"f705575b-d789-4976-a55d-cc16ce76a6bd\"}","{\"EncodingComputer\":null}"})
            Reject(()=>JsonSerializer.Deserialize<ReplayOptions>(invalid)!.Validate(),"Invalid encoding settings were accepted: "+invalid);
        var configured=JsonSerializer.Deserialize<ReplayOptions>("{\"KeyframeSeconds\":3,\"EncoderPreset\":\"Quality\",\"RateControl\":\"CBR\",\"BitrateMbps\":32}")!;
        foreach(var encoder in RecordingEncoders.Candidates(configured,2560,1440))
        {
            Require(Argument(encoder,"-g")=="180",encoder.Name+" ignored the configured keyframe interval.");
            Require(Argument(encoder,"-b:v")=="32M",encoder.Name+" ignored the configured bitrate.");
        }
        Profiles(old,configured);CpuProfiles();
        Discovery().GetAwaiter().GetResult();
        Program.Write("recording-capability-tests.json",new{Pass=true,LegacyDefaults=true,InvalidProfilesRejected=true,ConfiguredGopAndRate=true,AllVendorRateControls=true,ProfileIdentity=true,ProfileAwareCapabilityFiltering=true,CancellationPropagated=true,ExplicitCpuX264=true,GpuNeverFallsBackToCpu=true,BackendSpecificCapabilitiesAndCache=true});
    }
    static void CpuProfiles()
    {
        var cpu=new ReplayOptions{Encoder="CPU",Codec="H264",KeyframeSeconds=3,BitrateMbps=32};cpu.Validate();
        var quality=RecordingEncoders.Candidates(cpu,2560,1440).Single();
        Require(quality.Name=="libx264"&&!quality.GpuScale&&Argument(quality,"-preset")=="veryfast"&&Argument(quality,"-crf")=="21"&&Argument(quality,"-g")=="180"&&Argument(quality,"-bf")=="0"&&Argument(quality,"-pix_fmt")=="yuv420p","CPU profile did not select bounded SDR x264 quality encoding.");
        var cbr=RecordingEncoders.Candidates(cpu with{RateControl="CBR",EncoderPreset="Quality"},2560,1440).Single();
        Require(Argument(cbr,"-preset")=="medium"&&Argument(cbr,"-b:v")=="32M"&&Argument(cbr,"-minrate")=="32M"&&Argument(cbr,"-maxrate")=="32M"&&Argument(cbr,"-bufsize")=="64M"&&Argument(cbr,"-crf")==null&&Argument(cbr,"-x264-params")=="nal-hrd=cbr:force-cfr=1","CPU CBR or quality preset was ignored.");
        var vbr=RecordingEncoders.Candidates(cpu with{RateControl="VBR",EncoderPreset="Performance"},2560,1440).Single();
        Require(Argument(vbr,"-preset")=="ultrafast"&&Argument(vbr,"-b:v")=="24M"&&Argument(vbr,"-maxrate")=="32M"&&Argument(vbr,"-minrate")==null&&Argument(vbr,"-crf")==null,"CPU VBR did not reserve headroom or used a quality-only mode.");
        Require(RecordingEncoders.Candidates(cpu with{Encoder="GPU"},2560,1440).All(e=>e.Name!="libx264"),"GPU mode silently included CPU encoding as fallback.");
        Require(!RecordingCapabilities.EncodingEquivalent(cpu,cpu with{Encoder="GPU"}),"CPU and GPU profiles shared the same encoder identity.");
        Require(RecordingEncoders.CacheKey(cpu,2560,1440,2560,1440)!=RecordingEncoders.CacheKey(cpu with{Encoder="GPU"},2560,1440,2560,1440),"CPU and GPU probes shared a cached result.");
    }
    static void Profiles(ReplayOptions old,ReplayOptions configured)
    {
        Require(old.BitrateMbps==0&&old.KeyframeSeconds==2&&old.EncoderPreset=="Balanced","Old preferences changed the shipped encoding defaults.");
        var defaults=RecordingEncoders.Candidates(old,2560,1440);
        Require(Argument(defaults[0],"-preset")=="p4"&&Argument(defaults[0],"-rc")=="vbr"&&Argument(defaults[0],"-cq")=="21"&&Argument(defaults[0],"-b:v")=="0"&&Argument(defaults[0],"-maxrate")=="40M","The legacy NVENC quality path changed.");
        Require(Argument(defaults[1],"-quality")=="balanced"&&Argument(defaults[1],"-rc")=="cqp"&&Argument(defaults[1],"-qp_i")=="21","The legacy AMF quality path changed.");
        Require(Argument(defaults[2],"-preset")=="veryfast"&&Argument(defaults[2],"-global_quality")=="21","The legacy QSV quality path changed.");
        foreach(var encoder in RecordingEncoders.Candidates(configured,2560,1440))
        {
            Require(Argument(encoder,"-maxrate")=="32M"&&Argument(encoder,"-minrate")=="32M"&&Argument(encoder,"-bufsize")=="64M","CBR did not bound the complete vendor profile: "+encoder.Name);
            Require(Argument(encoder,"-cq")==null&&Argument(encoder,"-global_quality")==null&&Argument(encoder,"-qp_i")==null,"CBR retained quality-only rate control: "+encoder.Name);
        }
        var variable=RecordingEncoders.Candidates(configured with{RateControl="VBR"},2560,1440);
        foreach(var encoder in variable)
            Require(Argument(encoder,"-b:v")=="24M"&&Argument(encoder,"-maxrate")=="32M"&&Argument(encoder,"-minrate")==null,"VBR did not reserve headroom beneath its configured cap: "+encoder.Name);
        Require(Argument(variable[0],"-rc")=="vbr"&&Argument(variable[1],"-rc")=="vbr_peak","Vendor VBR mode is incorrect.");
        Require(Argument(variable[0],"-preset")=="p6"&&Argument(variable[1],"-quality")=="quality"&&Argument(variable[2],"-preset")=="medium","Quality preset was not applied to all vendors.");
        var av1=RecordingEncoders.Candidates(old with{Codec="AV1"},2560,1440);
        Require(Argument(av1[0],"-level")=="6.2"&&Argument(av1[0],"-tier")=="1"&&Argument(av1[1],"-qp_i")=="84","AV1 lost its decoder limit or scaled AMF quantizer.");
        var remote=JsonSerializer.Deserialize<ReplayOptions>("{\"EncodingComputer\":\"f705575bd7894976a55dcc16ce76a6bd\"}")!;remote.Validate();
        foreach(var unchanged in new[]{old with{Folder=Path.GetTempPath()},old with{Enabled=true},old with{DurationSeconds=120},old with{Minutes=3},old with{HotkeyKey=2,HotkeyModifiers=0},old with{SaveSoundEnabled=false,SaveSoundVolume=90,GroupByApp=false},remote})
            Require(RecordingCapabilities.EncodingEquivalent(old,unchanged),"Destination/trigger settings unnecessarily split the hardware encoder.");
        ReplayOptions[] changed=[old with{Codec="H264"},old with{Quality="Low"},old with{Fps=120},old with{Width=1920,Height=1080},old with{AudioMode="Silent"},old with{Silent=true},old with{Microphone=""},old with{GameAudio=""},old with{ExtraAudio="extra"},old with{RateControl="CBR"},old with{BitrateMbps=45},old with{KeyframeSeconds=4},old with{EncoderPreset="Performance"}];
        foreach(var different in changed)Require(!RecordingCapabilities.EncodingEquivalent(old,different),"Different encoding/audio profiles shared an incompatible encoder.");
        string key=RecordingEncoders.CacheKey(old,2560,1440,2560,1440);
        foreach(var different in changed.Where(value=>value.AudioMode==old.AudioMode&&value.Silent==old.Silent&&value.Microphone==old.Microphone&&value.GameAudio==old.GameAudio&&value.ExtraAudio==old.ExtraAudio))
            Require(RecordingEncoders.CacheKey(different,different.Width==0?2560:different.Width,different.Height==0?1440:different.Height,2560,1440)!=key,"Hardware preflight reused another encoding profile.");
        Require(RecordingEncoders.CacheKey(old,2560,1440,1920,1080)!=key,"Hardware preflight ignored source scaling dimensions.");
    }
    static async Task Discovery()
    {
        var options=new ReplayOptions{Codec="AV1",Width=1920,Height=1080,BitrateMbps=32,RateControl="CBR",EncoderPreset="Quality",KeyframeSeconds=3};
        Task<RecordingEncoder> Select(ReplayOptions profile,int width,int height,CancellationToken token,int sourceWidth,int sourceHeight)
        {
            Require(width==1920&&height==1080&&sourceWidth==2560&&sourceHeight==1440,"Capability preflight lost source/output dimensions.");
            Require(profile.BitrateMbps==32&&profile.RateControl=="CBR"&&profile.EncoderPreset=="Quality"&&profile.KeyframeSeconds==3,"Capability preflight discarded selected encoder options.");
            if(profile.Codec=="AV1"||(profile.Fps==120&&profile.Codec=="HEVC"))throw new IOException("Actual test encode rejected this complete format.");
            return Task.FromResult(RecordingEncoders.Candidates(profile,width,height)[0]);
        }
        var normal=await RecordingCapabilities.DiscoverAsync(options,2560,1440,CancellationToken.None,Select);
        Require(normal.Select(value=>value.Codec).SequenceEqual(new[]{"H264","HEVC"}),"An unsupported AV1 encoder was advertised or a supported codec was hidden.");
        Require(normal.All(value=>value.Width==1920&&value.Height==1080&&value.Fps==60&&value.RateControl=="CBR"&&value.BitrateMbps==32),"Supported capabilities lost their actual tested profile.");
        var fast=await RecordingCapabilities.DiscoverAsync(options with{Fps=120},2560,1440,CancellationToken.None,Select);
        Require(fast.Count==1&&fast[0].Codec=="H264"&&fast[0].Fps==120,"Capabilities from 60 FPS leaked into an unsupported 120-FPS profile.");
        Require(options.Codec=="AV1","Capability discovery mutated the selected recording codec.");
        int cpuProbes=0;
        var cpu=await RecordingCapabilities.DiscoverAsync(options with{Encoder="CPU",Codec="H264"},2560,1440,CancellationToken.None,(profile,width,height,_,_,_)=>
        {cpuProbes++;Require(profile.Encoder=="CPU"&&profile.Codec=="H264","CPU capability discovery probed a different backend or codec.");return Task.FromResult(RecordingEncoders.Candidates(profile,width,height).Single());});
        Require(cpuProbes==1&&cpu.Count==1&&cpu[0].Backend=="CPU"&&cpu[0].Encoder=="libx264"&&cpu[0].Codec=="H264","CPU capabilities advertised unsupported hardware or non-x264 codecs.");
        var none=await RecordingCapabilities.DiscoverAsync(options,2560,1440,CancellationToken.None,(_,_,_,_,_,_)=>throw new IOException("Unavailable"));
        Require(none.Count==0,"Discovery invented support when every real probe failed.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool propagated=false;
        try{await RecordingCapabilities.DiscoverAsync(options,2560,1440,cancelled.Token,Select);}catch(OperationCanceledException){propagated=true;}
        Require(propagated,"Capability discovery swallowed cancellation.");
        using var interrupted=new CancellationTokenSource();propagated=false;
        try
        {
            await RecordingCapabilities.DiscoverAsync(options,2560,1440,interrupted.Token,(profile,width,height,_,_,_)=>
            {
                if(profile.Codec=="AV1")interrupted.Cancel();
                return Task.FromResult(RecordingEncoders.Candidates(profile,width,height)[0]);
            });
        }
        catch(OperationCanceledException){propagated=true;}
        Require(propagated,"Cancellation during the final hardware probe was reported as supported capabilities.");
    }
    static string? Argument(RecordingEncoder encoder,string key)
    {
        int at=Array.IndexOf(encoder.Arguments,key);return at<0?null:encoder.Arguments[at+1];
    }
    public static void CpuMedia()=>CpuMediaAsync().GetAwaiter().GetResult();
    static async Task CpuMediaAsync()
    {
        Require(RecordingTools.Ready,"Recording tools are needed for the real CPU media test.");
        string root=Path.Combine(AppContext.BaseDirectory,"recording-cpu-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(root);
        string? previous=Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT");Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var options=new ReplayOptions{Enabled=true,Encoder="CPU",Codec="H264",Fps=60,Width=320,Height=240,AudioMode="Mixed",Folder=Path.Combine(root,"destination")};
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));var token=deadline.Token;
        var results=new List<object>();RecordingCoordinator? coordinator=null;Task? producer=null;using var feedStop=new CancellationTokenSource();
        try
        {
            foreach(string control in new[]{"Quality","VBR","CBR"})
            {
                var profile=options with{RateControl=control,BitrateMbps=4,EncoderPreset=control=="Quality"?"Quality":control=="VBR"?"Performance":"Balanced"};
                var encoder=await RecordingEncoders.SelectAsync(profile,320,240,token,640,360);
                Require(encoder.Name=="libx264"&&!encoder.GpuScale,"The explicit CPU probe selected a hardware encoder.");
                var supported=await RecordingCapabilities.DiscoverAsync(profile,640,360,token);
                Require(supported.Count==1&&supported[0] is{Backend:"CPU",Codec:"H264",Encoder:"libx264",Width:320,Height:240,SourceWidth:640,SourceHeight:360},"The real CPU capability probe advertised an untested backend or codec.");
                string output=Path.Combine(root,"x264-"+control+".mp4");var watch=Stopwatch.StartNew();
                await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-hide_banner","-loglevel","error","-nostdin","-y","-f","lavfi","-i","testsrc2=size=640x360:rate=60,format=nv12,setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709","-frames:v","90","-an",
                    ..encoder.Arguments,..RecordingScale.Arguments(encoder,640,360,320,240),..RecordingSdr.Tags,"-movflags","+faststart",output],token);
                using var info=await ReplayTests.Probe(output);var video=info.RootElement.GetProperty("streams").EnumerateArray().Single(s=>s.GetProperty("codec_type").GetString()=="video");
                CheckCpuVideo(video);Require(video.GetProperty("nb_frames").GetString()=="90","CPU rate-control test lost frames.");
                await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-i",output,"-f","null","-"],token);
                results.Add(new{RateControl=control,Encoder=encoder.Name,Preset=Argument(encoder,"-preset"),Frames=90,Width=320,Height=240,Fps=60,SourceWidth=640,SourceHeight=360,ElapsedSeconds=watch.Elapsed.TotalSeconds,File=output});
            }
            coordinator=new(options,syntheticAudio:true,testWindowSeconds:3);var owner=coordinator;
            producer=Task.Factory.StartNew(()=>
            {
                using var pacing=new Pacer();int index=0;
                while(!feedStop.IsCancellationRequested)
                {
                    pacing.Wait(feedStop.Token);if(feedStop.IsCancellationRequested)break;
                    using var frame=new VideoBuffer(320,240){CapturedAt=Stopwatch.GetTimestamp()};
                    for(int y=0;y<240;y++)for(int x=0;x<320;x+=2)
                    {int at=(y*320+x)*2;frame.Data[at]=128;frame.Data[at+1]=(byte)(16+(x+y+index)%210);frame.Data[at+2]=128;frame.Data[at+3]=(byte)(16+(x+y+index+1)%210);}
                    owner.Offer(frame);index++;
                }
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            var startup=Stopwatch.StartNew();
            while(owner.Replay.BufferedSeconds<2&&owner.Error==null&&startup.Elapsed.TotalSeconds<30)await Task.Delay(50,token);
            Require(owner.Error==null&&owner.Replay.BufferedSeconds>=2,"The actual CPU replay encoder did not produce completed segments: "+owner.Error);
            owner.StartRecording(options,"CPU recording regression");await Task.Delay(4500,token);
            var replay=await owner.Replay.SaveAsync("CPU replay regression");var recording=await owner.StopRecordingAsync();
            Require(recording.Seconds>=4&&replay.Seconds<=3.1&&owner.Replay.EncoderStarts==1,"CPU replay and recording did not share a compatible encoder or the recording was truncated to replay length.");
            using var recorded=await ReplayTests.Probe(recording.LocalPath);var streams=recorded.RootElement.GetProperty("streams").EnumerateArray().ToArray();CheckCpuVideo(streams.Single(s=>s.GetProperty("codec_type").GetString()=="video"));
            Require(streams.Count(s=>s.GetProperty("codec_type").GetString()=="audio")==1,"CPU continuous recording lost the synthetic audio track.");
            await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-i",recording.LocalPath,"-f","null","-"],token);
            string pcm=Path.Combine(root,"recording-audio.f32");
            await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",recording.LocalPath,"-map","0:a:0","-t","2","-ac","1","-f","f32le",pcm],token);
            float[] samples=MemoryMarshal.Cast<byte,float>(File.ReadAllBytes(pcm)).ToArray();
            Require(samples.Length>=48000&&samples.Any(v=>Math.Abs(v)>.04),"CPU recording audio was silent or truncated.");
            Program.Write("recording-cpu-tests.json",new{Pass=true,Backend="CPU",ActualEncoder="libx264",AutomaticHardwareFallback=false,RealPreflight=true,
                QualityVbrCbrFilesDecode=true,SdrBt709Limited=true,SoftwareScaleAndLetterbox=true,ReplayAndContinuousRecording=true,SharedEncoderStarts=owner.Replay.EncoderStarts,
                RecordingSeconds=recording.Seconds,ReplaySeconds=replay.Seconds,AudioTrack=true,Recording=recording.LocalPath,Replay=replay.LocalPath,DirectFiles=results,
                SyntheticSource=true,GamePerformanceMeasured=false,PhysicalTwoPcTest=false});
        }
        finally
        {
            feedStop.Cancel();if(producer!=null)try{await producer;}catch(OperationCanceledException){}
            coordinator?.Dispose();Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);
        }
    }
    static void CheckCpuVideo(JsonElement video)
    {
        string[] average=video.GetProperty("avg_frame_rate").GetString()!.Split('/');
        double fps=double.Parse(average[0],CultureInfo.InvariantCulture)/double.Parse(average[1],CultureInfo.InvariantCulture);
        // MP4 edit lists can hide keyframe preroll, and Matroska timestamps are
        // millisecond based. Their aggregate rate need not reduce to 60/1.
        Require(video.GetProperty("codec_name").GetString()=="h264"&&video.GetProperty("width").GetInt32()==320&&video.GetProperty("height").GetInt32()==240&&video.GetProperty("pix_fmt").GetString()=="yuv420p"&&video.GetProperty("r_frame_rate").GetString()=="60/1"&&double.IsFinite(fps)&&Math.Abs(fps-60)<.1,"CPU output changed its selected codec, dimensions or frame rate.");
        Require(video.GetProperty("color_primaries").GetString()=="bt709"&&video.GetProperty("color_transfer").GetString()=="bt709"&&video.GetProperty("color_space").GetString()=="bt709"&&video.GetProperty("color_range").GetString()=="tv","CPU output lost SDR limited BT.709 tags.");
    }
}
