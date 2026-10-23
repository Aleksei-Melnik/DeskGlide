using System.Reflection;
namespace SdrCapture;
static class RecordingSessionTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run()
    {
        string previous=ReplayTools.Root,root=Path.Combine(Path.GetTempPath(),"DeskGlide-SessionTests-"+Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        try
        {
            using var first=new ReplayRecorder(new(){Enabled=false},true,4,null,startEncoder:false);
            string firstRun=(string)typeof(ReplayRecorder).GetField("runFolder",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(first)!;
            string session=Path.Combine(firstRun,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(session);
            string segment=Path.Combine(session,"seg-000000.mkv");File.WriteAllText(segment,"active segment fixture");
            using var second=new ReplayRecorder(new(){Enabled=false},true,4,null,startEncoder:false);
            Require(File.Exists(segment),"Creating a second recorder deleted the first recorder's active segment.");
            Require(typeof(ReplayRecorder).GetMethod("BeginRecording",BindingFlags.Instance|BindingFlags.NonPublic)!=null,"Independent continuous recording is missing.");
            var version=typeof(ReplayRecorder).GetField("version",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var current=new ReplayOptions{Enabled=false};
            foreach(var next in new[]{current with{RateControl="CBR"},current with{BitrateMbps=30},current with{KeyframeSeconds=3},current with{EncoderPreset="Quality"},current with{Codec="H264",Encoder="CPU"}})
            {
                int before=(int)version.GetValue(first)!;first.Update(next);
                Require((int)version.GetValue(first)!>before,"Changing encoder settings kept the old running encoder configuration.");first.Update(current);
            }
            int unchanged=(int)version.GetValue(first)!;first.Update(current with{Folder=root,DurationSeconds=600});
            Require((int)version.GetValue(first)! == unchanged,"Changing only storage or duration needlessly restarts the encoder.");
            using(var empty=new ReplayRecorder(current with{Enabled=true},true,4,null,startEncoder:false))
            {
                var lease=empty.BeginRecording(current,"Journal regression");
                string journal=empty.RecordingJournalPath(lease),blocked=journal+".tmp";Directory.CreateDirectory(blocked);
                try{empty.StopRecordingAsync(lease,true).GetAwaiter().GetResult();throw new Exception("Journal failure fixture did not fail.");}
                catch(IOException){}catch(UnauthorizedAccessException){}
                Require(!lease.Finishing,"A failed journal write permanently blocked Stop retries.");Directory.Delete(blocked);
                var result=empty.StopRecordingAsync(lease,true).GetAwaiter().GetResult();
                Require(result.Seconds==0&&result.LocalPath.Length==0&&!File.Exists(journal),"An empty recording cannot stop cleanly or pretends to have saved video.");
            }
            using(var journalOwner=new ReplayRecorder(current with{Enabled=true},true,4,null,startEncoder:false))
            {
                var lease=journalOwner.BeginRecording(current,"Segment journal regression");
                string blocked=journalOwner.RecordingJournalPath(lease)+".tmp";Directory.CreateDirectory(blocked);
                var chunk=new ReplayChunk("segment fixture",lease.Start-.1,lease.Start+1,32,1,"H264",60,320,240);
                var add=typeof(ReplayRecorder).GetMethod("AddChunk",BindingFlags.Instance|BindingFlags.NonPublic)!;
                try{add.Invoke(journalOwner,[chunk]);throw new Exception("Journal insertion failure fixture did not fail.");}
                catch(TargetInvocationException e)when(e.InnerException is IOException or UnauthorizedAccessException){}
                Directory.Delete(blocked);add.Invoke(journalOwner,[new ReplayChunk(chunk.Path,chunk.Start,chunk.End,chunk.Bytes,1,"H264",60,320,240)]);
                Require(lease.Chunks.Count==1&&lease.Chunks[0].Pins==1&&journalOwner.CacheBytes==32,"Retrying a segment after a journal failure duplicated footage or pins.");
            }
            Program.Write("recording-session-tests.json",new{Pass=true,ActiveRecorderOwnership=true});
        }
        finally{Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    internal static void Media()
    {
        if(!RecordingTools.Ready)throw new IOException("Install recording tools before the media regression.");
        string previous=ReplayTools.Root,root=Path.Combine(Path.GetTempPath(),"DeskGlide-MediaTests-"+Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var options=new ReplayOptions{Enabled=true,Codec="H264",AudioMode="Mixed",Fps=60,Width=320,Height=240,Folder=Path.Combine(root,"videos")};
        var software=new RecordingEncoder("libx264","Synthetic software regression",["-c:v","libx264","-preset","ultrafast","-crf","16","-g","120","-bf","0","-pix_fmt","yuv420p"]);
        using var process=System.Diagnostics.Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var watch=System.Diagnostics.Stopwatch.StartNew();
        RecordingCoordinator? coordinator=null;Task? producer=null;using var stop=new CancellationTokenSource();
        try
        {
            coordinator=new(options,true,software,2);var owner=coordinator;
            producer=Task.Factory.StartNew(()=>
            {
                using var pacing=new Pacer();int index=0;
                while(!stop.IsCancellationRequested)
                {
                    pacing.Wait(stop.Token);if(stop.IsCancellationRequested)break;
                    using var frame=new VideoBuffer(320,240);frame.CapturedAt=System.Diagnostics.Stopwatch.GetTimestamp();
                    for(int y=0;y<240;y++)for(int x=0;x<320;x+=2)
                    {int at=(y*320+x)*2;frame.Data[at]=128;frame.Data[at+1]=(byte)(16+(x+y+index)%210);frame.Data[at+2]=128;frame.Data[at+3]=(byte)(16+(x+y+index+1)%210);}
                    owner.Offer(frame);index++;
                }
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            Wait(()=>owner.Replay.BufferedSeconds>=2,30,"Replay encoder did not start.");
            var shared=owner.Replay;owner.StartRecording(options,"Recording regression");double began=ReplayRecorder.Now;
            Thread.Sleep(4500);var replaySave=owner.Replay.SaveAsync("Replay regression").GetAwaiter().GetResult();
            while(ReplayRecorder.Now-began<10.5)Thread.Sleep(50);
            long framesBefore=shared.EncodedFrames;owner.UpdateReplay(options with{Enabled=false});Thread.Sleep(1800);
            Require(shared.EncodedFrames>framesBefore+50,"Disabling replay interrupted the shared continuous encoder.");
            var recording=owner.StopRecordingAsync().GetAwaiter().GetResult();
            Require(recording.Seconds>=10&&replaySave.Seconds<=3&&shared.EncoderStarts==1,"Continuous recording was truncated to the replay window or started a duplicate encoder.");
            Require(!owner.NeedsFrames&&!owner.HasRecording,"Stopping both consumers left capture enabled.");
            using var info=ReplayTests.Probe(recording.LocalPath).GetAwaiter().GetResult();
            var streams=info.RootElement.GetProperty("streams").EnumerateArray().ToArray();var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");
            Require(video.GetProperty("width").GetInt32()==320&&video.GetProperty("height").GetInt32()==240&&video.GetProperty("codec_name").GetString()=="h264","The recording format changed.");
            Require(streams.Count(s=>s.GetProperty("codec_type").GetString()=="audio")==1,"Continuous recording lost its audio track.");
            string raw=Path.Combine(root,"audio.f32");
            ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",recording.LocalPath,"-map","0:a:0","-t","2","-ac","1","-f","f32le",raw],default).GetAwaiter().GetResult();
            var audio=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(File.ReadAllBytes(raw));
            Require(audio.Length>=48000&&audio.ToArray().Any(v=>Math.Abs(v)>.04),"Continuous recording audio is silent or truncated.");
            owner.StartRecording(options,"Recovery regression");Thread.Sleep(4500);owner.PauseInput();Thread.Sleep(600);
            string journal=RecordingRecovery.Pending().Single();var metadata=System.Text.Json.JsonSerializer.Deserialize<RecordingJournal>(File.ReadAllText(journal))!;
            Require(metadata.Chunks.Length>0,"No completed segments were journaled.");
            string part=Path.Combine(Path.GetDirectoryName(journal)!,metadata.Chunks[0].File),held=part+".held";File.Move(part,held);
            bool failed=false;try{owner.StopRecordingAsync().GetAwaiter().GetResult();}catch(IOException){failed=true;}
            Require(failed&&File.Exists(journal),"A failed export discarded its recovery journal.");
            owner.Dispose();coordinator=null;File.Move(held,part);
            var recovered=RecordingRecovery.RecoverAsync(journal).GetAwaiter().GetResult();
            Require(File.Exists(recovered.LocalPath)&&recovered.Seconds>1&&!File.Exists(journal),"Recovering closed segments did not produce a video.");
            using var recoveredInfo=ReplayTests.Probe(recovered.LocalPath).GetAwaiter().GetResult();
            process.Refresh();Program.Write("recording-session-media-tests.json",new{Pass=true,SharedEncoderStarts=shared.EncoderStarts,RecordingSeconds=recording.Seconds,ReplaySeconds=replaySave.Seconds,RecoveredSeconds=recovered.Seconds,ReplayOffDidNotInterruptRecording=true,FailedExportRetainedSegments=true,RecoveryFileDecodes=true,AudioTrack=true,Width=320,Height=240,Fps=60,ElapsedSeconds=watch.Elapsed.TotalSeconds,ProcessCpuSeconds=(process.TotalProcessorTime-cpu).TotalSeconds,WorkingSetBytes=process.WorkingSet64,RealGameCaptured=false,PhysicalTwoPcTest=false});
        }
        finally
        {
            stop.Cancel();if(producer!=null)try{producer.GetAwaiter().GetResult();}catch(OperationCanceledException){}
            coordinator?.Dispose();Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    static void Wait(Func<bool> condition,int seconds,string message)
    {var watch=System.Diagnostics.Stopwatch.StartNew();while(!condition()&&watch.Elapsed.TotalSeconds<seconds)Thread.Sleep(50);Require(condition(),message);}

}
