using System.Diagnostics;

namespace SdrCapture;

// Capture publishes an immutable reference and returns immediately. NV12 packing
// must not delay the next desktop acquisition. Both queued references and bytes
// are bounded, including when an encoder or the OS stops making progress.
sealed class RecordingFrameQueue:IDisposable
{
    readonly object gate=new();
    readonly record struct Entry(VideoBuffer Frame,int Version);
    readonly Queue<Entry> frames=[];
    readonly AutoResetEvent ready=new(false);
    readonly Task worker;
    readonly Action<VideoBuffer,int> convert;
    readonly Action<Exception> failure;
    readonly int capacity;
    readonly long maximumBytes;
    bool closing,disposed;
    long bytes,dropped;
    internal long Dropped=>Interlocked.Read(ref dropped);
    internal long QueuedBytes {get{lock(gate)return bytes;}}
    internal int Count {get{lock(gate)return frames.Count;}}
    internal Task Completion=>worker;
    public RecordingFrameQueue(Action<VideoBuffer,int> convert,Action<Exception> failure,int capacity=8,long maximumBytes=64L*1024*1024)
    {
        if(capacity is <1 or >8||maximumBytes<1)throw new ArgumentOutOfRangeException(nameof(capacity));
        this.convert=convert;this.failure=failure;this.capacity=capacity;this.maximumBytes=maximumBytes;
        worker=Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    public void Offer(VideoBuffer source,int version=0)
    {
        var reference=source.Retain();if(reference==null)return;
        if(reference.CapturedAt<=0)reference.CapturedAt=Stopwatch.GetTimestamp();
        lock(gate)
        {
            if(closing){reference.Dispose();return;}
            long budget=Math.Max(maximumBytes,reference.Length);
            while(frames.Count>0&&(frames.Count>=capacity||bytes+reference.Length>budget))
            {var old=frames.Dequeue();bytes-=old.Frame.Length;old.Frame.Dispose();Interlocked.Increment(ref dropped);}
            frames.Enqueue(new(reference,version));bytes+=reference.Length;ready.Set();
        }
    }
    void Run()
    {
        try
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            while(true)
            {
                Entry? next=null;
                lock(gate)
                {
                    if(closing)return;
                    if(frames.Count>0){next=frames.Dequeue();bytes-=next.Value.Frame.Length;}
                }
                if(next==null){ready.WaitOne();continue;}
                using(next.Value.Frame)try{convert(next.Value.Frame,next.Value.Version);}catch(Exception e){failure(e);}
            }
        }
        catch(Exception e){failure(e);}
        finally{lock(gate){closing=true;Drain();}}
    }
    void Drain(){while(frames.TryDequeue(out var entry))entry.Frame.Dispose();bytes=0;}
    internal void Clear(){lock(gate)Drain();}
    public void Dispose()
    {
        lock(gate){if(disposed)return;disposed=true;closing=true;Drain();ready.Set();}
        MediaShutdown.Drain([worker],ready.Dispose);
    }
}
