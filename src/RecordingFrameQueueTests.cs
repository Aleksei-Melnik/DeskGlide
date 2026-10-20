using System.Collections.Concurrent;

namespace SdrCapture;

static class RecordingFrameQueueTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        foreach(long budget in new long[]{12,8})
        {
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var completed=new ManualResetEventSlim();
            var observed=new ConcurrentQueue<(int Value,long Timestamp,int Version)>();Exception? failure=null;
            using var queue=new RecordingFrameQueue((frame,version)=>
            {
                int value=frame.Data[0];if(value==0){entered.Set();release.Wait();}
                observed.Enqueue((value,frame.CapturedAt,version));if(value==5)completed.Set();
            },e=>failure=e,capacity:3,maximumBytes:budget);
            void Offer(int value){using var frame=new VideoBuffer(2,1){CapturedAt=100+value};frame.Data.AsSpan(0,frame.Length).Fill((byte)value);queue.Offer(frame,50+value);}
            try
            {
                Offer(0);Require(entered.Wait(5000),"Conversion worker did not start");
                var producer=Task.Run(()=>{for(int value=1;value<=5;value++)Offer(value);});
                Require(producer.Wait(5000),"Capture waited for a blocked conversion worker");
                Require(queue.Count==(budget==12?3:2)&&queue.QueuedBytes<=budget,"Incoming frame references or bytes are unbounded");
                Require(queue.Dropped==(budget==12?2:3),"Overloaded queue did not account for dropped old frames");
                release.Set();Require(completed.Wait(5000),"Queued conversion did not resume");queue.Dispose();
                int[] expected=budget==12?[0,3,4,5]:[0,4,5];var actual=observed.ToArray();
                Require(actual.Select(x=>x.Value).SequenceEqual(expected),"Queue changed frame order or reused a retained source buffer");
                Require(actual.All(x=>x.Timestamp==100+x.Value&&x.Version==50+x.Value),"Queue changed the source clock or recording generation");
                Require(failure==null&&queue.Completion.IsCompleted&&queue.QueuedBytes==0,"Conversion worker failed or retained shutdown frames");
                Offer(9);Require(queue.QueuedBytes==0,"A disposed queue accepted another source reference");
            }
            finally{release.Set();}
        }
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())using(var completed=new ManualResetEventSlim())
        {
            var seen=new ConcurrentQueue<int>();
            using var queue=new RecordingFrameQueue((frame,_)=>{if(frame.CapturedAt==1){entered.Set();release.Wait();}seen.Enqueue((int)frame.CapturedAt);if(frame.CapturedAt==3)completed.Set();},e=>throw e);
            void Offer(int id){using var frame=new VideoBuffer(2,1){CapturedAt=id};queue.Offer(frame);}
            try
            {
                Offer(1);Require(entered.Wait(5000),"Clear fixture did not start");Offer(2);queue.Clear();
                Require(queue.Count==0&&queue.QueuedBytes==0,"Disabling recording retained pending source frames");
                Offer(3);release.Set();Require(completed.Wait(5000),"Queue did not accept a new recording after clear");queue.Dispose();
                Require(seen.SequenceEqual(new[]{1,3}),"Clear converted a stale frame or lost the next recording");
            }
            finally{release.Set();}
        }
        Program.Write("recording-frame-queue-tests.json",new{Pass=true,CaptureDoesNotWaitForConversion=true,SourceReferencesRetained=true,SourceClockAndGenerationPreserved=true,QueueCountAndBytesBounded=true,OverloadDropsOldest=true,ClearDrainsPendingFrames=true,ShutdownDrainsReferences=true});
    }
}
