using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;

// Replay owns packed NV12 frames, rather than retaining the larger NDI buffers.
// Exact lengths avoid ArrayPool's 8-MB bucket for a 5.5-MB 1440p frame. A small
// private pool reuses consumed frames without retaining the warm-up high-water mark.
sealed class FrameBufferPool(long maximumBytes=32L*1024*1024,int maximumCount=4):IDisposable
{
    readonly object gate=new();
    readonly List<byte[]> free=[];
    long cached;
    bool disposed;
    internal long CachedBytes {get{lock(gate)return cached;}}
    internal byte[] Rent(int length)
    {
        lock(gate)
        {
            int index=free.FindIndex(data=>data.Length==length);
            if(index>=0){var data=free[index];free.RemoveAt(index);cached-=length;return data;}
        }
        return GC.AllocateUninitializedArray<byte>(length);
    }
    internal void Return(byte[] data)
    {
        lock(gate)
        {
            if(disposed||cached+data.Length>maximumBytes||free.Count>=maximumCount)return;
            free.Add(data);cached+=data.Length;
        }
    }
    public void Dispose(){lock(gate){disposed=true;free.Clear();cached=0;}}
}

// Native slabs are released immediately, including a temporary startup backlog.
// A managed LOH can keep those dead multi-megabyte arrays committed until its
// next full collection; forcing that collection during gameplay causes stalls.
sealed class RecordingFramePool:IDisposable
{
    readonly object gate=new();
    readonly List<(IntPtr Address,int Length)> free=[];
    long cached;
    bool disposed;
    internal long CachedBytes {get{lock(gate)return cached;}}
    internal unsafe IntPtr Rent(int length)
    {
        lock(gate)
        {
            int index=free.FindIndex(data=>data.Length==length);
            if(index>=0){var data=free[index];free.RemoveAt(index);cached-=length;return data.Address;}
        }
        var pointer=NativeMemory.Alloc((nuint)length);
        if(pointer==null)throw new OutOfMemoryException();return (IntPtr)pointer;
    }
    internal unsafe void Return(IntPtr address,int length)
    {
        lock(gate)
        {
            if(!disposed&&cached+length<=32L*1024*1024&&free.Count<4)
            {free.Add((address,length));cached+=length;return;}
        }
        NativeMemory.Free((void*)address);
    }
    public unsafe void Dispose()
    {lock(gate){disposed=true;foreach(var data in free)NativeMemory.Free((void*)data.Address);free.Clear();cached=0;}}
}

sealed class RecordingFrame:IDisposable
{
    sealed class Storage(RecordingFramePool pool,int length)
    {
        public readonly RecordingFramePool Pool=pool;
        public readonly IntPtr Address=pool.Rent(length);
        public readonly int Length=length;
        public int References=1;
    }
    readonly Storage storage;
    int disposed;
    internal unsafe Span<byte> Data=>new((void*)storage.Address,storage.Length);
    internal IntPtr Address=>storage.Address;
    internal int Width {get;}
    internal int Height {get;}
    internal long CapturedAt {get;}
    internal RecordingFrame(VideoBuffer source,RecordingFramePool pool)
    {
        Width=source.Width;Height=source.Height&~1;
        CapturedAt=source.CapturedAt>0?source.CapturedAt:Stopwatch.GetTimestamp();
        storage=new(pool,checked(Width*Height*3/2));
        try{RecordingPixels.Nv12(source.Data,Data,Width,Height);}catch{Dispose();throw;}
    }
    RecordingFrame(RecordingFrame source)
    {Width=source.Width;Height=source.Height;CapturedAt=source.CapturedAt;storage=source.storage;}
    internal RecordingFrame Retain(){Interlocked.Increment(ref storage.References);return new(this);}
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)==0&&Interlocked.Decrement(ref storage.References)==0)
            storage.Pool.Return(storage.Address,storage.Length);
    }
}
