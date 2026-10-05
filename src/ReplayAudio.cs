using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;

public sealed record AudioChoice(string Id,string Label)
{
    public override string ToString()=>Label;
    public static List<AudioChoice> All()
    {
        var choices=new List<AudioChoice>{new("","Silent — no audio"),new("default:capture","Default Windows microphone"),new("default:render","Default Windows playback (PC audio)")};
        using var devices=new MMDeviceEnumerator();
        foreach(var flow in new[]{DataFlow.Capture,DataFlow.Render})
            foreach(var device in devices.EnumerateAudioEndPoints(flow,DeviceState.Active))
                using(device) choices.Add(new(device.ID,$"{(flow==DataFlow.Capture?"Input":"Output")}: {device.FriendlyName}"));
        return choices;
    }
}

// Timestamped, bounded PCM ring. Silent render endpoints produce no WASAPI packets;
// missing positions are deliberately read as silence instead of shortening the recording.
sealed class AudioTimeline
{
    const int Capacity=48000*3;
    readonly object gate=new();
    readonly float[] samples=new float[Capacity*2];
    readonly long[] stamps=Enumerable.Repeat(long.MinValue,Capacity).ToArray();
    public void Put(long start,ReadOnlySpan<float> stereo)
    {
        lock(gate) for(int i=0;i<stereo.Length/2;i++)
        {
            long position=start+i;int index=(int)((position%Capacity+Capacity)%Capacity);
            samples[index*2]=stereo[i*2];samples[index*2+1]=stereo[i*2+1];stamps[index]=position;
        }
    }
    public void Read(long start,Span<float> target,int channel,int channels)
    {
        lock(gate) for(int i=0;i<target.Length/channels;i++)
        {
            long position=start+i;int index=(int)((position%Capacity+Capacity)%Capacity);
            if(stamps[index]==position){target[i*channels+channel]=samples[index*2];target[i*channels+channel+1]=samples[index*2+1];}
        }
    }
    public static long Position()=>(long)(Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency*48000);
}

sealed class ReplayAudio:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly AudioTimeline[] tracks=[new(),new(),new()];
    readonly Task[] workers;
    readonly string[] status=["Off","Off","Off"];
    readonly bool synthetic;
    readonly string[] remote=["","",""];
    public string Status=>string.Join("\n",new[]{"Microphone: "+status[0],"Game: "+status[1],"Extra audio: "+status[2]});
    public ReplayAudio(ReplayOptions options,bool synthetic=false)
    {
        this.synthetic=synthetic;
        var ids=options.IsSilent?new[]{"","",""}:new[]{options.Microphone,options.GameAudio,options.ExtraAudio};
        workers=ids.Select((id,index)=>{if(id.StartsWith("kvm:")){remote[index]=id[4..];return Task.CompletedTask;}return Task.Factory.StartNew(()=>CaptureLoop(id,index),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}).ToArray();
    }
    void CaptureLoop(string id,int index)
    {
        if(id.Length==0||synthetic)return;
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        while(!stop.IsCancellationRequested)
        {
            try{TimedAudioCapture.Run(id,stop.Token,(start,data,frames)=>tracks[index].Put(start,data.AsSpan(0,frames*2)),text=>status[index]=text);}
            catch(OperationCanceledException){break;}
            catch(Exception e){status[index]="Unavailable: "+e.Message;Log.Write("Replay audio: "+e.Message);}
            if(stop.Token.WaitHandle.WaitOne(1500))break;
        }
    }
    // Recording can wait for complete packets without adding any NDI latency.
    public static double WaitSeconds(ReplayOptions options)=>options.IsSilent?0:
        new[]{options.Microphone,options.GameAudio,options.ExtraAudio}.Any(s=>s.StartsWith("kvm:"))?.20:.10;
    public void Read(long start,byte[] bytes)
    {
        var target=MemoryMarshal.Cast<byte,float>(bytes.AsSpan());target.Clear();
        if(synthetic)
        {
            for(int i=0;i<target.Length/6;i++)for(int ch=0;ch<6;ch++)target[i*6+ch]=(float)(.1*Math.Sin(2*Math.PI*(440+220*(ch/2))*(start+i)/48000));
        }
        else for(int i=0;i<3;i++)
        {
            if(remote[i].Length>0){KvmAudioBus.Read(remote[i],start,target,i*2);status[i]=KvmAudioBus.Active(remote[i])?"KVM audio":"Waiting for KVM audio";}
            else tracks[i].Read(start,target,i*2,6);
        }
    }
    public void Dispose(){stop.Cancel();try{Task.WaitAll(workers,2500);}catch{}/* Tokens can still be observed by device cleanup. */}
}
