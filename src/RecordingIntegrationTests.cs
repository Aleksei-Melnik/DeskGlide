using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SdrCapture;

// Real NDI, recorder, FFmpeg, exports and delivery. The software encoder is
// injected at the existing validation boundary; no desktop or audio endpoint
// is opened, and this test makes no two-computer or gaming performance claim.
static class RecordingIntegrationTests
{
    const int Width=320,Height=240,Fps=60;
    static string Id()=>Guid.NewGuid().ToString("N");
    static void Require(bool condition,string message){if(!condition)throw new IOException(message);}
    public static void Run()=>RunAsync().GetAwaiter().GetResult();
    public static void AudioTail()
    {
        string name="audio-tail-"+Id();using var writer=NdiRecordingAudioBus.Register(name);
        using var audio=new ReplayAudio(new(){Microphone="",GameAudio="ndi:"+name,ExtraAudio="",AudioMode="Mixed"});
        long start=AudioTimeline.Position()-480;
        writer.Put(start,Enumerable.Range(0,960).Select(i=>i%2==0?.05f:-.025f).ToArray());
        writer.SetAvailable(false);var bytes=new byte[480*6*4];audio.Read(start,bytes);
        var pcm=MemoryMarshal.Cast<byte,float>(bytes.AsSpan());
        Require(!NdiRecordingAudioBus.Active(name)&&Math.Abs(pcm[2]-.05f)<1e-6&&Math.Abs(pcm[3]+.025f)<1e-6,"Source loss erased timestamped NDI audio still needed by the delayed recording writer.");
        writer.SetAvailable(true);audio.Read(start,bytes);
        Require(MemoryMarshal.Cast<byte,float>(bytes.AsSpan()).IndexOfAnyExcept(0f)<0,"A reconnected source reused audio from the previous clock mapping.");
        writer.Put(start+480,Enumerable.Repeat(.075f,960).ToArray());audio.Read(start+480,bytes);
        Require(Math.Abs(MemoryMarshal.Cast<byte,float>(bytes.AsSpan())[2]-.075f)<1e-6,"A reconnected source did not resume its recording audio.");
        writer.Dispose();audio.Read(start+480,bytes);
        Require(MemoryMarshal.Cast<byte,float>(bytes.AsSpan()).IndexOfAnyExcept(0f)<0,"A disposed source retained a published audio bus.");
        Program.Write("recording-audio-tail-tests.json",new{Pass=true,InactiveHistoricalAudioReadable=true,ReconnectInvalidatesOldClock=true,DisposeUnpublishesAudio=true,OsAudioDevicesOpened=false});
    }
    static async Task RunAsync()
    {
        AudioTail();
        Require(RecordingTools.Ready,"Recording tools must already be installed for the NDI media integration test.");
        string run=Id(),root=Path.Combine(Path.GetTempPath(),"DeskGlide-NdiIntegration-"+run);
        string artifacts=Path.Combine(AppContext.BaseDirectory,"recording-integration-artifacts",run);
        string? previous=Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT");
        Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var saved=new ConcurrentQueue<ReplayResult>();var sources=new ConcurrentQueue<ObservedSource>();
        var coordinators=new ConcurrentQueue<RecordingCoordinator>();var media=new List<MediaReport>();
        var timer=Stopwatch.StartNew();using var process=Process.GetCurrentProcess();var initialCpu=process.TotalProcessorTime;
        using var stop=new CancellationTokenSource();Task[] producers=[];RecordingNode? node=null;bool completed=false;
        long offered=0,skipped=0;int paused=0;
        using var sender=new NdiSender("DeskGlide media integration "+run[..8]);
        try
        {
            var replayOptions=new ReplayOptions{Enabled=true,Codec="H264",Fps=Fps,Width=Width,Height=Height,
                Folder=Path.Combine(root,"receiver-replays"),Microphone="",GameAudio="",ExtraAudio="",AudioMode="Mixed",GroupByApp=false,SaveSoundEnabled=false};
            var preferences=new RecordingOptions{AllowRemoteEncoding=false,Profile=replayOptions with{Folder=Path.Combine(root,"receiver-recordings")}};
            var software=new RecordingEncoder("libx264","NDI integration software validation",
                ["-c:v","libx264","-threads","1","-preset","ultrafast","-crf","14","-g","120","-bf","0","-pix_fmt","yuv420p"]);
            node=new RecordingNode(()=>preferences,()=>replayOptions,saved.Enqueue,(name,offer,available)=>
            {
                var input=new ObservedSource(name,offer,available);sources.Enqueue(input);return input;
            },profile=>
            {
                var coordinator=new RecordingCoordinator(profile,false,software,4);coordinators.Enqueue(coordinator);return coordinator;
            },controlLease:TimeSpan.FromMinutes(3));
            string peer=Id(),replaySession=Id(),normalSession=Id(),interruptedSession=Id();
            var profile=RecordingProfile.FromReplay(replayOptions) with{SourceWidth=Width,SourceHeight=Height};
            var denied=await node.Handle(peer,new(Id(),"configureReplay",replaySession,sender.SourceName,profile));
            Require(!denied.Ok&&denied.Code=="denied"&&sources.IsEmpty&&coordinators.IsEmpty,"Receiver opt-in was bypassed by the real media path.");
            preferences.AllowRemoteEncoding=true;
            await Command(node,peer,new(Id(),"configureReplay",replaySession,sender.SourceName,profile));

            // Use one exact 48-kHz origin for the visual frame IDs and the
            // amplitude-coded audio. The MP4 decoder can then detect A/V shifts
            // without comparing packet arrival times or relying on an audible tone.
            long sampleOrigin=AudioTimeline.Position()+4800;
            long sourceOrigin=(long)Math.Round(sampleOrigin*(decimal)Stopwatch.Frequency/48000);
            producers=
            [
                Task.Factory.StartNew(()=>
                {
                    using var pacing=new Pacer();long index=0;
                    while(!stop.IsCancellationRequested)
                    {
                        pacing.WaitUntil(Pacer.Deadline(sourceOrigin,index,Fps),stop.Token);
                        if(stop.IsCancellationRequested)break;
                        long next=Math.Max(index,(Stopwatch.GetTimestamp()-sourceOrigin)*Fps/Stopwatch.Frequency);
                        Interlocked.Add(ref skipped,next-index);index=next;
                        if(Volatile.Read(ref paused)!=0){index++;continue;}
                        using var frame=new VideoBuffer(Width,Height){CapturedAt=Pacer.Deadline(sourceOrigin,index,Fps)};
                        Pattern(frame,(int)index);sender.Send(frame,Fps);Interlocked.Increment(ref offered);index++;
                    }
                },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default),
                Task.Factory.StartNew(()=>
                {
                    using var pacing=new Pacer();long position=sampleOrigin;var stereo=new float[960];
                    while(!stop.IsCancellationRequested)
                    {
                        pacing.WaitUntil((long)(position*(decimal)Stopwatch.Frequency/48000),stop.Token);
                        if(stop.IsCancellationRequested)break;
                        for(int i=0;i<480;i++)
                        {
                            long relative=position+i-sampleOrigin;double amplitude=Amplitude((int)(relative/2400));
                            stereo[i*2]=(float)(amplitude*Math.Sin(2*Math.PI*1000*relative/48000));
                            stereo[i*2+1]=(float)(amplitude*.5*Math.Sin(2*Math.PI*1600*relative/48000));
                        }
                        sender.SendAudio(stereo,position);position+=480;
                    }
                },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)
            ];
            RecordingCoordinator Owner()=>coordinators.Last();
            await Wait(()=>Owner().Replay.BufferedSeconds>=4,35,"The real NDI replay did not close its first segments.",()=>Details(node,coordinators,sources,producers));
            var shared=Owner().Replay;
            await Command(node,peer,new(Id(),"startRecording",normalSession,sender.SourceName,profile,"NDI integration"));
            double normalStarted=ReplayRecorder.Now;
            Require(ReferenceEquals(Owner().MetricsRecorder,shared)&&shared.EncoderStarts==1,"Equal replay and recording profiles did not share the real encoder.");
            await Task.Delay(3500);
            await Command(node,peer,new(Id(),"saveReplay",replaySession,App:"NDI integration"));
            Require(node.Snapshot.Recording&&Owner().HasRecording&&shared.EncoderStarts==1,"Saving replay interrupted the ordinary recording or duplicated its encoder.");
            var replay=saved.Single(s=>Path.GetFileName(s.LocalPath).StartsWith("Replay-",StringComparison.Ordinal));
            while(ReplayRecorder.Now-normalStarted<8.5)await Task.Delay(50);
            await Command(node,peer,new(Id(),"stopRecording",normalSession));
            var normal=saved.Single(s=>Path.GetFileName(s.LocalPath).StartsWith("Recording-",StringComparison.Ordinal));
            Require(normal.Seconds>=8&&normal.Seconds>replay.Seconds+2&&replay.Seconds<=4.05,"Ordinary recording was trimmed to the replay window.");
            Require(!node.Snapshot.Recording&&node.Snapshot.ReplayEnabled&&!Owner().HasRecording&&shared.EncoderStarts==1,"Stopping recording stopped the shared replay encoder.");
            int encoderStartsBeforeInterruption=shared.EncoderStarts;
            long framesAfterStop=shared.EncodedFrames;
            await Wait(()=>shared.EncodedFrames>framesAfterStop+30,5,"Replay stopped advancing after ordinary Stop.",()=>Details(node,coordinators,sources,producers));

            // The logical remote session survives a transport interruption, but
            // its media is sealed and resumed as separate ordinary MP4 parts.
            await Command(node,peer,new(Id(),"startRecording",interruptedSession,sender.SourceName,profile,"NDI interruption"));
            long partStarted=Owner().MetricsRecorder.EncodedFrames;
            await Wait(()=>Owner().MetricsRecorder.EncodedFrames>=partStarted+Fps*5,12,"The interruption fixture did not encode five seconds.",()=>Details(node,coordinators,sources,producers));
            Volatile.Write(ref paused,1);
            await Wait(()=>saved.Count(s=>Path.GetFileName(s.LocalPath).StartsWith("Recording-",StringComparison.Ordinal))>=2&&!Owner().HasRecording,15,
                "Source loss did not finalize the interrupted media part.",()=>Details(node,coordinators,sources,producers));
            Require(node.Snapshot.Recording&&node.Snapshot.RecordingSession==interruptedSession&&node.Snapshot.OutputFps==0,"Source loss discarded the logical recording session or kept encoding a frozen image.");
            var interrupted=saved.Where(s=>Path.GetFileName(s.LocalPath).StartsWith("Recording-",StringComparison.Ordinal)&&s.LocalPath!=normal.LocalPath).Single();
            Volatile.Write(ref paused,0);
            await Wait(()=>sources.Last().Available&&Owner().HasRecording&&Owner().IsRecording,15,"Real NDI reconnect did not resume the same logical session.",()=>Details(node,coordinators,sources,producers));
            Require(node.Snapshot.RecordingSession==interruptedSession,"Reconnect changed the logical recording session.");
            long resumedAt=Owner().MetricsRecorder.EncodedFrames;
            await Wait(()=>Owner().MetricsRecorder.EncodedFrames>=resumedAt+Fps*5,15,"The resumed recording did not encode five seconds.",()=>Details(node,coordinators,sources,producers));
            // Planned updates temporarily revoke incoming encoding permission
            // and await this same release path before the process exits.
            preferences.AllowRemoteEncoding=false;
            await node.RefreshPreferencesAsync();
            Require(!node.Snapshot.ReplayEnabled&&!node.Snapshot.Recording&&!Owner().HasRecording&&sources.Single().Disposed,
                "Permission revocation did not finalize recording, stop replay, and release its NDI input.");
            var reentry=await node.Handle(peer,new(Id(),"configureReplay",Id(),sender.SourceName,profile));
            Require(!reentry.Ok&&reentry.Code=="denied"&&sources.Count==1,"A new incoming session bypassed the update preparation permission block.");
            var resumed=saved.Where(s=>Path.GetFileName(s.LocalPath).StartsWith("Recording-",StringComparison.Ordinal)&&s.LocalPath!=normal.LocalPath&&s.LocalPath!=interrupted.LocalPath).Single();
            Require(saved.Count==4,"Receiver permission revocation did not complete all expected media exports.");
            Require(interrupted.Seconds>=4&&resumed.Seconds>=4,"The interrupted or resumed part was truncated.");
            Require(sources.Count==1&&sources.Single().Gaps>=1,"Source loss created a second independent input or was never observed.");
            Require(!RecordingRecovery.Pending().Any(),"Successful exports left an unfinished recovery journal.");
            Require(!node.Snapshot.ReplayEnabled&&!node.Snapshot.Recording&&sources.Single().Disposed,"Releasing the final consumer left its NDI input active.");
            stop.Cancel();await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(5));
            node.Dispose();await node.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(15));

            Directory.CreateDirectory(artifacts);
            foreach(var entry in new[]{("replay",replay), ("ordinary",normal), ("interrupted",interrupted), ("resumed",resumed)})
            {
                string expectedFolder=entry.Item1=="replay"?replayOptions.Folder:preferences.Profile.Folder;
                Require(Path.GetFullPath(entry.Item2.Destination).StartsWith(Path.GetFullPath(expectedFolder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"The remote sender chose an output path instead of receiver preferences.");
                await ReplayDelivery.CopyAsync(new(entry.Item2.LocalPath,entry.Item2.Destination,entry.Item2.Seconds),CancellationToken.None);
                byte[] localHash=SHA256.HashData(await File.ReadAllBytesAsync(entry.Item2.LocalPath));
                byte[] destinationHash=SHA256.HashData(await File.ReadAllBytesAsync(entry.Item2.Destination));
                Require(localHash.AsSpan().SequenceEqual(destinationHash),"Atomic delivery changed the MP4 bytes.");
                Require(!File.Exists(entry.Item2.Destination+".uploading"),"Completed delivery left an uploading file.");
                string artifact=Path.Combine(artifacts,entry.Item1+".mp4");File.Copy(entry.Item2.Destination,artifact);
                media.Add(await Inspect(entry.Item1,artifact,entry.Item2.Seconds,artifacts));
            }
            process.Refresh();completed=true;
            Program.Write("recording-integration-tests.json",new{Pass=true,Encoder="libx264 validation injection",OptInRequired=true,NativeNdi=true,
                ReceiverOwnsPaths=true,SharedEncoderStartsBeforeInterruption=encoderStartsBeforeInterruption,ReplaySavedDuringRecording=true,
                RecordingLongerThanReplayWindow=true,LogicalSessionSurvivesSourceLoss=true,SourceCount=sources.Count,SourceGaps=sources.Single().Gaps,
                PermissionRevocationFinalizesReplayAndRecording=true,IncomingSessionsBlockedDuringPreparation=true,
                SourceFrames=sources.Single().Frames,SourceAudioPackets=sources.Single().AudioPackets,ProducerFrames=Interlocked.Read(ref offered),ProducerSkipped=Interlocked.Read(ref skipped),
                AtomicMp4Delivery=true,Media=media,ElapsedSeconds=timer.Elapsed.TotalSeconds,ProcessCpuSeconds=(process.TotalProcessorTime-initialCpu).TotalSeconds,
                WorkingSetBytes=process.WorkingSet64,Artifacts=artifacts,DesktopCaptured=false,OsAudioDevicesOpened=false,HardwareEncoderTested=false,PhysicalTwoComputerLanTested=false});
        }
        catch(Exception e)
        {
            Program.Write("recording-integration-tests.json",new{Pass=false,Failure=e.ToString(),Root=root,Artifacts=artifacts,Media=media,
                State=node?.Snapshot,Details=node==null?null:Details(node,coordinators,sources,producers)});
            throw;
        }
        finally
        {
            stop.Cancel();try{await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(5));}catch{}
            node?.Dispose();if(node!=null)await node.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);
            if(completed)
            {
                string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("DeskGlide-NdiIntegration-",StringComparison.Ordinal))throw new IOException("Unsafe integration cleanup path.");
                Directory.Delete(root,true);
            }
        }
    }
    static async Task Command(RecordingNode node,string peer,RecordingCommand command)
    {
        var response=await node.Handle(peer,command);
        Require(response.Ok,$"{command.Command} failed: {response.Code}: {response.Message}; {JsonSerializer.Serialize(response.State)}");
    }
    static async Task Wait(Func<bool> predicate,int seconds,string message,Func<string> details)
    {
        var clock=Stopwatch.StartNew();while(!predicate()&&clock.Elapsed.TotalSeconds<seconds)await Task.Delay(40);
        Require(predicate(),message+" "+details());
    }
    static string Details(RecordingNode node,ConcurrentQueue<RecordingCoordinator> owners,ConcurrentQueue<ObservedSource> sources,Task[] producers)=>
        JsonSerializer.Serialize(new{State=node.Snapshot,Recorders=owners.Select(c=>new{c.Status,c.Error,c.HasRecording,c.Replay.BufferedSeconds,c.Replay.EncoderStarts,c.Replay.EncodedFrames,c.Replay.AudioDiagnostics}),
            Sources=sources.Select(s=>new{s.Status,s.Available,s.Frames,s.AudioPackets,s.Gaps}),ProducerErrors=producers.Where(t=>t.IsFaulted).Select(t=>t.Exception?.ToString())});
    static double Amplitude(int block)
    {
        uint hash=unchecked((uint)block*1973+9277);hash=unchecked((hash^(hash>>13))*1274126177);
        return (hash&1)==0?.025:.10;
    }
    static void Pattern(VideoBuffer frame,int index)
    {
        for(int y=0;y<Height;y++)for(int x=0;x<Width;x+=2)
        {
            int at=(y*Width+x)*2;frame.Data[at]=128;frame.Data[at+1]=(byte)(16+(x+y+index*3)%210);
            frame.Data[at+2]=128;frame.Data[at+3]=(byte)(16+(x+y+index*3+1)%210);
        }
        for(int y=0;y<16;y++)for(int bit=0;bit<16;bit++)for(int x=bit*16;x<(bit+1)*16;x++)
            frame.Data[(y*Width+x)*2+1]=(byte)(((index>>bit)&1)==0?16:235);
    }
    sealed record MediaReport(string Kind,string File,double DeclaredSeconds,double VideoSeconds,double AudioSeconds,int Frames,int DistinctFrameIds,int MaximumHeldFrames,
        double AudioVideoDurationDifference,double ZeroOffsetEnvelopeMismatch,double BestEnvelopeOffsetMilliseconds,double BestEnvelopeMismatch,int ComparedEnvelopeWindows,double MinimumAudioRms,double StereoRmsRatio);
    static async Task<MediaReport> Inspect(string kind,string file,double declared,string folder)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string json=await ReplayTools.RunAsync(ReplayTools.Ffprobe,["-v","error","-show_streams","-show_format","-of","json",file],timeout.Token);
        using var info=JsonDocument.Parse(json);var streams=info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");var audio=streams.Single(s=>s.GetProperty("codec_type").GetString()=="audio");
        Require(video.GetProperty("codec_name").GetString()=="h264"&&video.GetProperty("width").GetInt32()==Width&&video.GetProperty("height").GetInt32()==Height,"Integration MP4 lost its encoded dimensions or codec.");
        foreach(string tag in new[]{"color_space","color_transfer","color_primaries"})Require(video.GetProperty(tag).GetString()=="bt709","Integration MP4 lost SDR "+tag);
        // Edit-list trims and the Matroska millisecond time base can make the
        // container's avg_frame_rate slightly fractional. Its declared source
        // grid remains 60/1; decoded frame count/duration is checked below.
        Require(video.GetProperty("color_range").GetString()=="tv"&&video.GetProperty("r_frame_rate").GetString()=="60/1","Integration MP4 lost limited range or CFR rate.");
        Require(audio.GetProperty("codec_name").GetString()=="aac"&&audio.GetProperty("sample_rate").GetString()=="48000"&&audio.GetProperty("channels").GetInt32()==2,"The incoming NDI stereo track was missing or changed.");
        static double Number(JsonElement value,string name)=>double.Parse(value.GetProperty(name).GetString()!,CultureInfo.InvariantCulture);
        double videoSeconds=Number(video,"duration"),audioSeconds=Number(audio,"duration");
        Require(Math.Abs(videoSeconds-declared)<.12,"Exported video duration differs from the selected recording interval.");
        Require(Math.Abs(audioSeconds-videoSeconds)<.15,"Exported audio and video have different final durations.");
        await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-xerror","-threads","1","-i",file,"-map","0","-f","null","-"],timeout.Token);
        string idsFile=Path.Combine(folder,kind+"-ids.raw"),audioFile=Path.Combine(folder,kind+"-audio.f32");
        await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-threads","1","-i",file,"-an","-vf","crop=256:16:0:0,scale=16:1:flags=area,format=gray","-fps_mode","passthrough","-f","rawvideo",idsFile],timeout.Token);
        await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-threads","1","-i",file,"-map","0:a:0","-ac","2","-ar","48000","-f","f32le",audioFile],timeout.Token);
        byte[] marker=await File.ReadAllBytesAsync(idsFile,timeout.Token);var ids=new int[marker.Length/16];
        for(int i=0;i<ids.Length;i++)for(int bit=0;bit<16;bit++)if(marker[i*16+bit]>128)ids[i]|=1<<bit;
        Require(ids.Length>Fps&&Math.Abs(ids.Length/(double)Fps-videoSeconds)<.12,"The exported MP4 did not decode its full CFR video.");
        int held=1,maxHeld=1;
        for(int i=1;i<ids.Length;i++)
        {
            int step=(ids[i]-ids[i-1]+65536)%65536;Require(step<32768,"Decoded source frame IDs moved backwards.");
            held=step==0?held+1:1;maxHeld=Math.Max(maxHeld,held);
        }
        Require(ids.Distinct().Count()>Fps&&maxHeld<=Fps*4,"The recorded source remained frozen beyond the bounded loss interval.");
        float[] samples=MemoryMarshal.Cast<byte,float>(await File.ReadAllBytesAsync(audioFile,timeout.Token)).ToArray();
        Require(samples.Length>48000*2&&samples.All(float.IsFinite),"Decoded NDI audio is missing or nonfinite.");
        var windows=new List<(int Start,bool High)>();double minimum=double.MaxValue,leftEnergy=0,rightEnergy=0;
        for(int i=15;i<ids.Length-15;i++)
        {
            if(ids[i]%3!=1||ids[i]==ids[i-1]||ids[i]==ids[i+1])continue;
            int at=(int)Math.Round((i+.5)*48000/Fps)-240;
            if(at<0||at+480>=samples.Length/2)continue;
            windows.Add((at,Amplitude(ids[i]/3)>.05));
            double left=0,right=0;for(int n=0;n<480;n++){left+=samples[(at+n)*2]*samples[(at+n)*2];right+=samples[(at+n)*2+1]*samples[(at+n)*2+1];}
            minimum=Math.Min(minimum,Math.Sqrt(left/480));leftEnergy+=left;rightEnergy+=right;
        }
        Require(windows.Count>=20&&windows.Count(w=>w.High)>=6&&windows.Count(w=>!w.High)>=6,"Decoded moving frame IDs did not cover enough independent audio code windows.");
        double Mismatch(int shift)
        {
            int compared=0,wrong=0;
            foreach(var window in windows)
            {
                int at=window.Start+shift;if(at<0||at+480>=samples.Length/2)continue;
                double energy=0;for(int n=0;n<480;n++)energy+=samples[(at+n)*2]*samples[(at+n)*2];
                if((Math.Sqrt(energy/480)>.04)!=window.High)wrong++;compared++;
            }
            return compared==0?1:wrong/(double)compared;
        }
        double zeroMismatch=Mismatch(0),best=1;int bestShift=0;
        for(int shift=-7200;shift<=7200;shift+=240)
        {
            double mismatch=Mismatch(shift);
            if(mismatch<best||mismatch==best&&Math.Abs(shift)<Math.Abs(bestShift)){best=mismatch;bestShift=shift;}
        }
        Require(zeroMismatch<=.20&&best<=.10&&Math.Abs(bestShift)<=1680,"Decoded frame IDs and audio amplitude code are out of sync: "+JsonSerializer.Serialize(new{kind,zeroMismatch,best,BestMilliseconds=bestShift/48.0}));
        double stereoRatio=Math.Sqrt(rightEnergy/leftEnergy);
        Require(minimum>.006&&stereoRatio is >.40 and <.60,"NDI audio contains holes or its stereo levels changed.");
        return new(kind,file,declared,videoSeconds,audioSeconds,ids.Length,ids.Distinct().Count(),maxHeld,Math.Abs(audioSeconds-videoSeconds),zeroMismatch,bestShift/48.0,best,windows.Count,minimum,stereoRatio);
    }
    sealed class ObservedSource:IRecordingNodeSource
    {
        readonly RecordingNdiSource input;
        public bool Disposed{get;private set;}
        public ObservedSource(string name,Action<VideoBuffer> offer,Action<bool> available)=>input=new(name,offer,available);
        public string SourceAudioId=>input.SourceAudioId;
        public string Status=>input.Status;
        public double FramesPerSecond=>input.FramesPerSecond;
        public long SourceGaps=>input.SourceGaps;
        public bool Available=>input.Available;
        public long Frames=>input.Frames;
        public long AudioPackets=>input.AudioPackets;
        public long Gaps=>input.SourceGaps;
        public void Dispose(){Disposed=true;input.Dispose();}
    }
}
