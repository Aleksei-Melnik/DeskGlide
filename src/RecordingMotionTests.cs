using System.Collections.Concurrent;
using System.Diagnostics;

namespace SdrCapture;

// Real encoded frame IDs expose changes in motion phase that a repeat counter
// alone cannot detect. The source is synthetic; no desktop/audio device is used.
static class RecordingMotionTests
{
    readonly record struct Selection(long Due,long Source);
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run(bool hardware=false)=>RunAsync(hardware).GetAwaiter().GetResult();
    static async Task RunAsync(bool hardware)
    {
        string root=Path.Combine(Path.GetTempPath(),"DeskGlide-MotionTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string? previous=Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT");
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var results=new List<object>();
        try
        {
            foreach(string codec in hardware?new[]{"H264","HEVC","AV1"}:new[]{"H264"})foreach(int fps in new[]{60,120})
                results.Add(await Case(root,codec,fps,hardware));
            Program.Write("recording-motion-tests.json",new{Pass=true,Hardware=hardware,DesktopCapture=false,AudioEndpoints=false,Cases=results,Root=root});
        }
        finally{Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);}
    }
    static async Task<object> Case(string root,string codec,int fps,bool hardware)
    {
        const int width=640,height=360;
        var options=new ReplayOptions{Enabled=true,Codec=codec,Fps=fps,Width=width,Height=height,AudioMode="Mixed",Folder=Path.Combine(root,"clips"),GroupByApp=false};
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var encoder=hardware?await RecordingEncoders.SelectAsync(options,width,height,deadline.Token):
            new RecordingEncoder("libx264","Software regression",["-c:v","libx264","-preset","ultrafast","-crf","16","-g",(fps*2).ToString(),"-bf","0","-pix_fmt","yuv420p"]);
        using var recorder=new ReplayRecorder(options,true,6,encoder);
        var selections=new ConcurrentQueue<Selection>();var sourceTimes=new ConcurrentDictionary<int,long>();
        recorder.FrameSelectedForTest=(due,source)=>selections.Enqueue(new(due,source));
        using var stop=new CancellationTokenSource();
        var producer=Task.Factory.StartNew(()=>
        {
            using var pacing=new Pacer();long origin=Stopwatch.GetTimestamp();double next=origin;int id=1;
            while(!stop.IsCancellationRequested)
            {
                long timestamp=(long)Math.Round(next);pacing.WaitUntil(timestamp,stop.Token);if(stop.IsCancellationRequested)break;
                using var frame=new VideoBuffer(width,height){CapturedAt=timestamp};
                int offset=(int)((timestamp-origin)*80/Stopwatch.Frequency);
                for(int y=0;y<height;y++)for(int x=0;x<width;x+=2)
                {
                    int at=(y*width+x)*2;byte luma=(byte)(16+(x+offset)%220);
                    if(y<16&&x<256){int bits=x<128?id:id^65535;luma=(byte)(((bits>>(x/8%16))&1)!=0?235:16);}
                    frame.Data[at]=128;frame.Data[at+1]=luma;frame.Data[at+2]=128;frame.Data[at+3]=luma;
                }
                sourceTimes[id++]=timestamp;recorder.Offer(frame);
                int rate=((timestamp-origin)/(Stopwatch.Frequency*15/100)%2)==0?fps*3/2:fps*2;
                next+=Stopwatch.Frequency/(double)rate;
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        ReplayResult saved;
        try
        {
            while((recorder.BufferedSeconds<6||selections.Count<fps*8)&&!deadline.IsCancellationRequested)
            {Require(recorder.Error==null,"Motion encoder failed: "+recorder.Error);await Task.Delay(30,deadline.Token);}
            saved=await recorder.SaveAsync("Motion "+codec+" "+fps).WaitAsync(deadline.Token);
        }
        finally{stop.Cancel();await producer;recorder.Update(options with{Enabled=false});}
        string raw=Path.Combine(root,$"{codec}-{fps}.ids");
        await ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",saved.LocalPath,"-an","-vf","crop=256:16:0:0,scale=32:1:flags=neighbor,format=gray","-fps_mode","passthrough","-f","rawvideo",raw],deadline.Token);
        byte[] pixels=await File.ReadAllBytesAsync(raw,deadline.Token);var ids=new List<int>();
        for(int at=0;at+32<=pixels.Length;at+=32)
        {
            int id=0,inverse=0;for(int bit=0;bit<16;bit++){if(pixels[at+bit]>128)id|=1<<bit;if(pixels[at+16+bit]>128)inverse|=1<<bit;}
            Require((id^inverse)==65535&&sourceTimes.ContainsKey(id),"Encoded frame marker is corrupt or was never offered.");ids.Add(id);
        }
        Require(ids.Count>=fps*4,"Motion export is too short to test capture-density changes.");
        var selected=selections.ToArray();long first=sourceTimes[ids[0]];int start=Array.FindIndex(selected,s=>s.Source==first);
        Require(start>=0&&start+ids.Count<=selected.Length,"Encoded pictures do not map to the selected source timeline.");
        double maximum=0;
        for(int frame=0;frame<ids.Count;frame++)
        {
            long source=sourceTimes[ids[frame]];Require(source==selected[start+frame].Source,"The saved file dropped, repeated or reordered selected pictures.");
            if(frame>=fps)maximum=Math.Max(maximum,Math.Abs(source-selected[start+frame].Due)*1000.0/Stopwatch.Frequency);
        }
        using var info=await ReplayTests.Probe(saved.LocalPath);var streams=info.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");var audio=streams.Single(s=>s.GetProperty("codec_type").GetString()=="audio");
        double videoSeconds=double.Parse(video.GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
        double audioSeconds=double.Parse(audio.GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
        Require(video.GetProperty("r_frame_rate").GetString()==fps+"/1"&&Math.Abs(videoSeconds-audioSeconds)<.12,"Motion export lost its frame rate or audio synchronization.");
        double bound=1000.0/(fps*1.5)/2+.5;
        var result=new{codec,fps,Encoder=encoder.Name,Frames=ids.Count,MaximumPhaseErrorMs=maximum,BoundMs=bound,SelectedPicturesPreserved=true,VideoSeconds=videoSeconds,AudioSeconds=audioSeconds,saved.LocalPath};
        Program.Write($"recording-motion-{codec}-{fps}.json",result);
        Require(maximum<=bound,$"Encoded {fps} FPS motion drifted {maximum:F3} ms; bound {bound:F3} ms.");
        return result;
    }
}
