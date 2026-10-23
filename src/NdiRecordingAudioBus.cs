using System.Collections.Concurrent;
using System.Diagnostics;

namespace SdrCapture;

static class NdiRecordingAudioBus
{
    static readonly ConcurrentDictionary<string,Lease> sources=new(StringComparer.Ordinal);
    internal sealed class Lease(string source):IDisposable
    {
        readonly AudioTimeline timeline=new();
        long first,lastPacket;
        int disposed,available=1;
        public long FirstSample=>Volatile.Read(ref first);
        public bool Active=>Volatile.Read(ref available)!=0&&Stopwatch.GetTimestamp()-Volatile.Read(ref lastPacket)<Stopwatch.Frequency*3;
        public void Put(long start,ReadOnlySpan<float> data)
        {
            if(Volatile.Read(ref disposed)!=0)return;
            timeline.Put(start,data);if(Volatile.Read(ref available)!=0)Interlocked.CompareExchange(ref first,start,0);Volatile.Write(ref lastPacket,Stopwatch.GetTimestamp());
        }
        public void SetAvailable(bool value)
        {
            int before=Interlocked.Exchange(ref available,value?1:0);
            // A recorder intentionally reads behind live video. Preserve its
            // bounded historical tail on loss; a new clock mapping invalidates
            // those old stamps only when the source actually reconnects.
            if(value&&before==0)timeline.Clear();
            if(!value||before==0)Volatile.Write(ref first,0);
        }
        public long Read(long start,Span<float> target,int channel)=>timeline.Read(start,target,channel,6);
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;sources.TryRemove(source,out _);SetAvailable(false);timeline.Clear();}
    }
    public static Lease Register(string source)
    {
        var lease=new Lease(source);
        if(!sources.TryAdd(source,lease))throw new InvalidOperationException("A recording receiver already owns this NDI source; share its frame observer.");
        return lease;
    }
    public static long Read(string source,long start,Span<float> target,int channel)=>sources.TryGetValue(source,out var lease)?lease.Read(start,target,channel):target.Length/6;
    public static bool Active(string source)=>sources.TryGetValue(source,out var lease)&&lease.Active;
    public static long FirstSample(string source)=>sources.TryGetValue(source,out var lease)?lease.FirstSample:0;
}
