using System.Diagnostics;

namespace SdrCapture;

static class RecordingDuplicateTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        string? previous=Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT");
        string root=Path.Combine(Path.GetTempPath(),"DeskGlide-DuplicateTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        try{RunIsolated();}
        finally{Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);}
    }
    static void RunIsolated()
    {
        using var recorder=new ReplayRecorder(new ReplayOptions{Enabled=true,Fps=120},true,8,null,startEncoder:false);
        long origin=Stopwatch.GetTimestamp(),millisecond=Stopwatch.Frequency/1000;
        void Offer(int at,byte value,int width=8,int height=6)
        {
            using var frame=new VideoBuffer(width,height){CapturedAt=origin+at*millisecond};
            for(int i=0;i<frame.Length;i+=2){frame.Data[i]=128;frame.Data[i+1]=value;}
            recorder.Offer(frame);
        }
        Offer(0,40);Offer(4,40);Offer(8,80);Offer(12,80);Offer(16,120);Offer(20,120);
        var timeout=Stopwatch.StartNew();
        while((recorder.DuplicateSourceFrames<3||recorder.SourceHistoryCount<3)&&timeout.ElapsedMilliseconds<5000)Thread.Sleep(1);
        Require(recorder.DuplicateSourceFrames==3&&recorder.SourceHistoryCount==3,"New timestamps made unchanged pixels into new video images");
        Require(recorder.ConvertedSourceFrames==3,"Unchanged source pictures were converted to NV12 before being discarded.");
        // Before filtering, the entries at +4/+12 ms won over genuinely new
        // images, repeating A/B even though B/C existed within the CFR window.
        using(var b=recorder.SelectForTest(origin+4*millisecond,origin,8*millisecond))
            Require(b!=null&&b.Data[0]==80&&b.CapturedAt==origin+8*millisecond,"An identical newer entry hid a fresh video image");
        using(var c=recorder.SelectForTest(origin+12*millisecond,origin+8*millisecond,8*millisecond))
            Require(c!=null&&c.Data[0]==120&&c.CapturedAt==origin+16*millisecond,"Image selection repeated pixels or changed their presentation clock");
        Offer(24,120,12,4);
        timeout.Restart();while(recorder.SourceHistoryCount<2&&timeout.ElapsedMilliseconds<5000)Thread.Sleep(1);
        using(var changed=recorder.SelectForTest(long.MaxValue,-1,0))
            Require(changed is {Width:12,Height:4}&&recorder.DuplicateSourceFrames==3,"Same byte length hid a resolution/aspect change");
        RawComparisonLifetime();
        Program.Write("recording-duplicate-tests.json",new{Pass=true,NewTimestampsDoNotInventNewImages=true,FreshAvailableImagesSelected=true,ExactPixelsCompared=true,FirstPresentationTimeRetained=true,FormatChangesPreserved=true,RawDuplicatesSkipNv12Packing=true,SourceCacheBoundedAndReleased=true,PackedPixelFallbackPreserved=true,RecordingGenerationRespected=true});
    }
    static void RawComparisonLifetime()
    {
        var options=new ReplayOptions{Enabled=true,Fps=120};
        using var recorder=new ReplayRecorder(options,true,8,null,startEncoder:false){SynchronousFrameConversionForTest=true};
        using var first=new VideoBuffer(8,6){CapturedAt=100};
        using var second=new VideoBuffer(8,6){CapturedAt=200};
        first.Data.AsSpan().Fill(40);second.Data.AsSpan().Fill(40);
        for(int y=0;y<6;y++)for(int x=0;x<8;x+=2)
        {
            // These UYVY images differ, but their vertically averaged NV12
            // chroma is identical. The packed fallback must keep filtering it.
            first.Data[(y*8+x)*2]=first.Data[(y*8+x)*2+2]=(byte)((y&1)==0?10:14);
            second.Data[(y*8+x)*2]=second.Data[(y*8+x)*2+2]=(byte)((y&1)==0?11:13);
        }
        recorder.Offer(first);recorder.Offer(second);recorder.Offer(second);
        Require(recorder.ConvertedSourceFrames==2&&recorder.SkippedSourceConversions==1&&recorder.DuplicateSourceFrames==2&&recorder.SourceHistoryCount==1,"Raw comparison bypassed the packed-image fallback or failed to cache its source.");
        Require(recorder.SourceComparisonBytes==first.Length,"Raw comparison retained more than one source frame.");
        // Same pixels in a changed recording configuration still go through
        // packing once; an old generation's comparison cannot short-circuit it.
        recorder.Update(options with{Fps=60});recorder.Offer(second);
        Require(recorder.ConvertedSourceFrames==3&&recorder.SkippedSourceConversions==1,"Raw comparison reused an obsolete recording generation.");
        recorder.Update(options with{Enabled=false});
        Require(recorder.SourceComparisonBytes==0&&recorder.SourceHistoryCount==0,"Disabling replay retained the comparison source.");
        recorder.Update(options);recorder.Offer(second);
        Require(recorder.SourceHistoryCount==1&&recorder.ConvertedSourceFrames==4,"Re-enabling replay discarded its first image.");
        recorder.Dispose();
        Require(recorder.SourceComparisonBytes==0,"Stopping replay retained the comparison source.");
    }
}
