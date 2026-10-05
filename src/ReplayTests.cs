using System.Diagnostics;
using System.Text.Json;

namespace SdrCapture;

static class ReplayTests
{
    public static void Preview()
    {
        Application.EnableVisualStyles();Ui.WpfUiTests.Run();
    }
    public static void Live(int seconds,int fps=60)=>LiveAsync(seconds,fps).GetAwaiter().GetResult();
    public static void LiveLocal(int seconds,int fps=120)=>LiveAsync(seconds,fps,true).GetAwaiter().GetResult();
    static async Task LiveAsync(int seconds,int fps,bool localOnly=false)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"replay-live-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var display=DisplayInfo.All().First();var config=new ReplayOptions{Enabled=true,Fps=fps,Folder=Path.Combine(root,"clips")};
        using var replay=new ReplayRecorder(config);
        using var engine=new StreamEngine{NdiEnabled=!localOnly,ReplayEnabled=true,ReplayFrameRate=fps,FrameAvailable=replay.Offer};
        engine.Start(new(display.Device,true,true),"SdrCapture Replay Test");
        var samples=new List<object>();var clock=Stopwatch.StartNew();long priorRecorded=0,priorNdi=0;
        while(clock.Elapsed.TotalSeconds<seconds)
        {
            await Task.Delay(1000);int second=(int)clock.Elapsed.TotalSeconds;
            Program.Write("replay-live-progress.json",new{second,fps,engine.NdiEnabled,engine.Frames,engine.CapturedFrames,engine.CaptureMs,engine.CaptureStatus,CaptureError=engine.Error,replay.EncodedFrames,replay.EncoderStarts,replay.BufferedSeconds,replay.Status,replay.AudioStatus,replay.Error});
            if(second>=8&&engine.CapturedFrames==0)throw new Exception("Desktop capture unavailable: "+engine.Error);
            if(!localOnly&&second==10){engine.NdiEnabled=false;priorRecorded=replay.EncodedFrames;}
            if(!localOnly&&second==15){if(replay.EncodedFrames-priorRecorded<fps*3)throw new Exception("Turning NDI off stopped replay: "+(replay.EncodedFrames-priorRecorded));engine.NdiEnabled=true;}
            if(second==20)engine.RequestCaptureRestart();
            if(second%5==0)samples.Add(new{second,engine.NdiEnabled,engine.Fps,engine.Frames,engine.CapturedFrames,engine.ReplacedFrames,engine.MissedSlots,replay.EncodedFrames,replay.EncoderStarts,replay.BufferedSeconds,replay.CacheBytes,replay.AudioStatus,replay.Error});
            if(second>5&&replay.Error!=null)throw new Exception("Live replay error: "+replay.Error);
        }
        var saved=await replay.SaveAsync("Live-test");
        var info=await Probe(saved.LocalPath);double duration=double.Parse(info.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
        replay.Update(config with{Enabled=false});engine.ReplayEnabled=false;await Task.Delay(1500);priorNdi=engine.Frames;await Task.Delay(1500);
        if(!localOnly&&engine.Frames-priorNdi<60)throw new Exception("Turning replay off stopped NDI.");
        Program.Write(localOnly?"replay-local-test.json":"replay-live-test.json",new{Pass=true,LocalOnly=localOnly,seconds,fps,duration,Source=engine.SourceName,File=saved.LocalPath,Audio=replay.AudioStatus,Samples=samples});
    }
    public static void Run()=>RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        string root=Path.Combine(AppContext.BaseDirectory,"replay-test-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        Directory.CreateDirectory(root);
        var config=new ReplayOptions{Enabled=true,Minutes=5,Quality="High",Folder=Path.Combine(root,"destination"),Width=640,Height=360};
        var observations=new List<object>();
        using var recorder=new ReplayRecorder(config,true,10);
        using var feedStop=new CancellationTokenSource();
        var feed=Task.Run(async()=>
        {
            long frameIndex=0;
            while(!feedStop.IsCancellationRequested)
            {
                // Repeated input mode changes must not restart the encoder or corrupt the output.
                int width=frameIndex%360<180?640:800,height=frameIndex%360<180?360:600;
                using var frame=new VideoBuffer(width,height);
                byte luma=(byte)(32+frameIndex%160);
                for(int i=0;i<frame.Length;i+=4){frame.Data[i]=128;frame.Data[i+1]=luma;frame.Data[i+2]=128;frame.Data[i+3]=luma;}
                recorder.Offer(frame);frameIndex++;
                await Task.Delay(16,feedStop.Token);
            }
        });
        try
        {
            await Task.Delay(24000);
            if(recorder.Error!=null||recorder.BufferedSeconds<8)throw new Exception("Replay failed: "+recorder.Error+" "+recorder.Status);
            var first=await recorder.SaveAsync();
            var info=await Probe(first.LocalPath);
            var streams=info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            if(streams.Count(s=>s.GetProperty("codec_type").GetString()=="audio")!=1)throw new Exception("Expected one mixed audio track.");
            var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");
            if(video.GetProperty("codec_name").GetString()!="hevc"||video.GetProperty("width").GetInt32()!=640||video.GetProperty("height").GetInt32()!=360||video.GetProperty("color_space").GetString()!="bt709"||video.GetProperty("color_range").GetString()!="tv")throw new Exception("Unexpected video format/color.");
            double duration=double.Parse(info.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
            if(duration<7||duration>10.3)throw new Exception("Replay trim/window failure: "+duration);
            using(var timeout=new CancellationTokenSource(30000))await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-xerror","-i",first.LocalPath,"-map","0","-f","null","-"],timeout.Token);
            if(recorder.EncoderStarts!=1)throw new Exception("Resolution changes restarted recording.");
            observations.Add(new{Phase="audio-and-rolling-window",duration,recorder.EncoderStarts,recorder.EncodedFrames,recorder.CacheBytes,File=first.LocalPath});
            await ReplayDelivery.CopyAsync(new(first.LocalPath,first.Destination,first.Seconds),CancellationToken.None);
            if(!File.Exists(first.Destination))throw new Exception("Delivery failed");
            string invalid=Path.Combine(root,"not-a-folder");File.WriteAllText(invalid,"test");
            bool copyFailed=false;try{await ReplayDelivery.CopyAsync(new(first.LocalPath,Path.Combine(invalid,"clip.mp4")),CancellationToken.None);}catch(IOException){copyFailed=true;}
            if(!copyFailed||!File.Exists(first.LocalPath))throw new Exception("Failed destination lost local clip.");
            recorder.Update(config with{Silent=true});await Task.Delay(7000);
            var silent=await recorder.SaveAsync();var silentInfo=await Probe(silent.LocalPath);
            if(silentInfo.RootElement.GetProperty("streams").EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="audio"))throw new Exception("Silent file contains an audio track.");
            observations.Add(new{Phase="silent-and-failed-destination",File=silent.LocalPath,RetainedAfterFailedCopy=copyFailed});
            recorder.Update(config with{Enabled=false});await Task.Delay(1500);long before=recorder.EncodedFrames;await Task.Delay(1200);
            if(recorder.EncodedFrames!=before)throw new Exception("Disabled replay still encodes.");
            Program.Write("replay-test.json",new{Pass=true,TestWindowSeconds=10,ConfiguredDefaultMinutes=5,Observations=observations});
        }
        finally{feedStop.Cancel();try{await feed;}catch(OperationCanceledException){}}
    }
    internal static async Task<JsonDocument> Probe(string path)
    {
        using var timeout=new CancellationTokenSource(30000);
        return JsonDocument.Parse(await ReplayTools.RunAsync(ReplayTools.Ffprobe,["-v","error","-show_streams","-show_format","-of","json",path],timeout.Token));
    }
}
