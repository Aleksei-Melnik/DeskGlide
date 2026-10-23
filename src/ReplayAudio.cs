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
        var choices=new List<AudioChoice>{new("","Silent — без звука"),new("default:capture","Микрофон Windows по умолчанию"),new("default:render","Выход Windows по умолчанию (звук ПК)")};
        using var devices=new MMDeviceEnumerator();
        foreach(var flow in new[]{DataFlow.Capture,DataFlow.Render})
            foreach(var device in devices.EnumerateAudioEndPoints(flow,DeviceState.Active))
                using(device) choices.Add(new(device.ID,$"{(flow==DataFlow.Capture?"Вход":"Выход")}: {device.FriendlyName}"));
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
    public long MissingSamples {get;private set;}
    public long ReadSamples {get;private set;}
    public long Read(long start,Span<float> target,int channel,int channels)
    {
        long missing=0;
        lock(gate) for(int i=0;i<target.Length/channels;i++)
        {
            long position=start+i;int index=(int)((position%Capacity+Capacity)%Capacity);
            if(stamps[index]==position){target[i*channels+channel]=samples[index*2];target[i*channels+channel+1]=samples[index*2+1];}
            else {MissingSamples++;missing++;}
            ReadSamples++;
        }
        return missing;
    }
    public void Clear(){lock(gate)Array.Fill(stamps,long.MinValue);}
    public static long Position()=>Position(Stopwatch.GetTimestamp());
    public static long Position(long qpc)=>(long)(qpc*48000m/Stopwatch.Frequency);
}

sealed class ReplayAudio:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly AudioTimeline[] tracks=[new(),new(),new()];
    readonly Task[] workers;
    readonly string[] status=["Отключён","Отключён","Отключён"];
    readonly bool synthetic;
    readonly int captureBufferMilliseconds;
    readonly string[] remote=["","",""];
    readonly string[] ndi=["","",""];
    readonly long[] ndiMissing=new long[3],ndiRead=new long[3];
    readonly bool[] active;
    readonly long[] startedAt=new long[3];
    readonly long[] firstSampleAt=new long[3];
    internal long WaitForCaptureStart(CancellationToken token)
    {
        if(synthetic)return 0;
        var timeout=Stopwatch.StartNew();
        long FirstSample(int i)=>ndi[i].Length>0?NdiRecordingAudioBus.FirstSample(ndi[i]):Volatile.Read(ref firstSampleAt[i]);
        while(Enumerable.Range(0,3).Any(i=>active[i]&&remote[i].Length==0&&FirstSample(i)==0)&&timeout.ElapsedMilliseconds<500)
        {token.ThrowIfCancellationRequested();token.WaitHandle.WaitOne(5);}
        return Enumerable.Range(0,3).Max(i=>
        {
            long sample=FirstSample(i);
            long first=sample/48000*Stopwatch.Frequency+(sample%48000*Stopwatch.Frequency+47999)/48000;
            return Math.Max(Volatile.Read(ref startedAt[i]),first);
        });
    }
    public string Diagnostics=>string.Join("; ",Enumerable.Range(0,3).Where(i=>active[i]).Select(i=>$"track{i}: missing={tracks[i].MissingSamples+ndiMissing[i]}/{tracks[i].ReadSamples+ndiRead[i]}"));
    public long MissingSamples=>Enumerable.Range(0,3).Where(i=>active[i]&&remote[i].Length==0).Sum(i=>tracks[i].MissingSamples+ndiMissing[i]);
    public long ReadSamples=>Enumerable.Range(0,3).Where(i=>active[i]&&remote[i].Length==0).Sum(i=>tracks[i].ReadSamples+ndiRead[i]);
    public string Status=>string.Join("\n",new[]{"Микрофон: "+status[0],"Игра: "+status[1],"Доп. звук: "+status[2]});
    public ReplayAudio(ReplayOptions options,bool synthetic=false,int captureBufferMilliseconds=100)
    {
        this.synthetic=synthetic;this.captureBufferMilliseconds=captureBufferMilliseconds;
        var ids=options.IsSilent?new[]{"","",""}:new[]{options.Microphone,options.GameAudio,options.ExtraAudio};
        active=ids.Select(id=>id.Length>0).ToArray();
        workers=ids.Select((id,index)=>
        {
            if(id.StartsWith("ndi:",StringComparison.Ordinal)){ndi[index]=id[4..];return Task.CompletedTask;}
            if(id.StartsWith("kvm:",StringComparison.Ordinal)){remote[index]=id[4..];return Task.CompletedTask;}
            return Task.Factory.StartNew(()=>CaptureLoop(id,index),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }).ToArray();
    }
    void CaptureLoop(string id,int index)
    {
        if(id.Length==0||synthetic)return;
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        while(!stop.IsCancellationRequested)
        {
            try{TimedAudioCapture.Run(id,stop.Token,(start,data,frames)=>{tracks[index].Put(start,data.AsSpan(0,frames*2));Interlocked.CompareExchange(ref firstSampleAt[index],start,0);},text=>{status[index]=text;Volatile.Write(ref startedAt[index],Stopwatch.GetTimestamp());},bufferMilliseconds:captureBufferMilliseconds);}
            catch(OperationCanceledException){break;}
            catch(Exception e){status[index]="Недоступен: "+e.Message;Log.Write("Replay audio: "+e.Message);}
            if(stop.Token.WaitHandle.WaitOne(1500))break;
        }
    }
    // Recording can wait for complete packets without adding any NDI latency.
    public static double WaitSeconds(ReplayOptions options)=>options.IsSilent?0:
        new[]{options.Microphone,options.GameAudio,options.ExtraAudio}.Any(s=>s.StartsWith("kvm:")||s.StartsWith("ndi:"))?.50:.30;
    public void Read(long start,byte[] bytes)
    {
        var target=MemoryMarshal.Cast<byte,float>(bytes.AsSpan());target.Clear();
        if(synthetic)
        {
            for(int i=0;i<target.Length/6;i++)for(int ch=0;ch<6;ch++)target[i*6+ch]=(float)(.1*Math.Sin(2*Math.PI*(440+220*(ch/2))*(start+i)/48000));
        }
        else for(int i=0;i<3;i++)
        {
            if(!active[i])continue;
            if(ndi[i].Length>0){ndiMissing[i]+=NdiRecordingAudioBus.Read(ndi[i],start,target,i*2);ndiRead[i]+=target.Length/6;status[i]=NdiRecordingAudioBus.Active(ndi[i])?"Звук по NDI":"Ожидание звука по NDI";}
            else if(remote[i].Length>0){KvmAudioBus.Read(remote[i],start,target,i*2);status[i]=KvmAudioBus.Active(remote[i])?"Звук по KVM":"Ожидание звука по KVM";}
            else tracks[i].Read(start,target,i*2,6);
        }
    }
    public void Dispose(){stop.Cancel();try{Task.WaitAll(workers,2500);}catch{}/* Tokens can still be observed by device cleanup. */}
}
