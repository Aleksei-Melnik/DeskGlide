using System.Diagnostics;

namespace SdrCapture;

static class RecordingDuplicateTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
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
        Program.Write("recording-duplicate-tests.json",new{Pass=true,NewTimestampsDoNotInventNewImages=true,FreshAvailableImagesSelected=true,ExactPixelsCompared=true,FirstPresentationTimeRetained=true,FormatChangesPreserved=true});
    }
}
