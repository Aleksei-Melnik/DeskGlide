using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SdrCapture;
// Arrival jitter must never reposition every packet on the recording timeline.
sealed class NetworkAudioTimeline
{
    readonly AudioTimeline timeline=new();
    readonly AudioPacketClock clock=new();
    readonly object gate=new();
    double offset=double.NaN,lowest;
    long last;
    float[] converted=[];
    public void Put(long sourcePosition,ReadOnlySpan<float> pcm,long now)
    {
        lock(gate)
        {
            int frames=pcm.Length/2;if(frames==0)return;
            double observed=now-frames-sourcePosition;
            if(double.IsNaN(offset)||now-last>48000*2){offset=lowest=observed;}
            else
            {
                // Lower envelope estimates clock offset; slowly release it to allow
                // clock drift in either direction, without following network bursts.
                long elapsed=Math.Max(0,now-last);
                lowest=Math.Min(observed,lowest+elapsed*.001);
                offset+=Math.Clamp(lowest-offset,-elapsed*.0005,elapsed*.0005);
            }
            var mapped=clock.Place(sourcePosition,(long)Math.Round(sourcePosition+offset),frames);
            if(converted.Length<mapped.Frames*2)converted=new float[mapped.Frames*2];
            var output=converted.AsSpan(0,mapped.Frames*2);
            AudioPacketClock.Resample(pcm,output);
            for(int i=0;i<output.Length;i++)if(!float.IsFinite(output[i]))output[i]=0;
            timeline.Put(mapped.Start,output);last=now;
        }
    }
    public void Read(long start,Span<float> target,int channel,int channels)=>timeline.Read(start,target,channel,channels);
    public bool Active {get{lock(gate)return AudioTimeline.Position()-last<48000*2;}}
}
static class KvmAudioBus
{
    static readonly ConcurrentDictionary<string,NetworkAudioTimeline> sources=new();
    public static void Put(string peer,long sourcePosition,byte[] bytes)=>
        sources.GetOrAdd(peer,_=>new()).Put(sourcePosition,MemoryMarshal.Cast<byte,float>(bytes.AsSpan()),AudioTimeline.Position());
    public static void Read(string peer,long start,Span<float> target,int channel)
    {if(sources.TryGetValue(peer,out var source))source.Read(start,target,channel,6);}
    public static bool Active(string peer)=>sources.TryGetValue(peer,out var s)&&s.Active;
    public static void Remove(string peer)=>sources.TryRemove(peer,out _);
}
sealed class KvmAudioSender:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly string deviceId;
    readonly KvmWire wire;
    readonly Channel<KvmMessage> queue=Channel.CreateBounded<KvmMessage>(new BoundedChannelOptions(24){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    readonly Task worker,writer;
    public volatile bool Enabled;
    public KvmAudioSender(string deviceId,KvmWire wire)
    {
        this.deviceId=deviceId;this.wire=wire;
        worker=Task.Factory.StartNew(Capture,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        writer=Task.Run(async()=>
        {
            try{await foreach(var data in queue.Reader.ReadAllAsync(stop.Token))await wire.SendAsync(data,stop.Token);}
            catch(OperationCanceledException){}catch(Exception e){Log.Write("Remote audio: "+e.Message);}
        });
    }
    void Capture()
    {
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                if(!Enabled||deviceId.Length==0){if(stop.Token.WaitHandle.WaitOne(100))break;continue;}
                TimedAudioCapture.Run(deviceId,stop.Token,(start,data,frames)=>
                {
                    if(!Enabled)return;
                    var bytes=MemoryMarshal.AsBytes(data.AsSpan(0,frames*2));
                    for(int offset=0;offset<bytes.Length;offset+=16384)
                    {
                        int count=Math.Min(16384,bytes.Length-offset)&~7;
                        if(count>0)queue.Writer.TryWrite(new(){Type="pcm",Data=bytes.Slice(offset,count).ToArray(),Size=start+offset/8});
                    }
                },_=>{},()=>Enabled);
            }
            catch(OperationCanceledException){break;}
            catch(Exception e){Log.Write("Remote audio capture: "+e.Message);if(stop.Token.WaitHandle.WaitOne(1500))break;}
        }
    }
    public void Dispose(){Enabled=false;stop.Cancel();queue.Writer.TryComplete();}
}
