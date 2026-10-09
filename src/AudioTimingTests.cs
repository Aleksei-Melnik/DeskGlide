using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;

static class AudioTimingTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    public static void Run()
    {
        int Simulate(int wait)
        {
            var timeline=new AudioTimeline();int packet=0,misses=0;float[] samples=Enumerable.Repeat(.25f,4800).ToArray();
            for(int frame=0;frame<120;frame++)
            {
                long readAt=frame*800+wait*48;
                while((packet+1)*2400<=readAt){timeline.Put(packet*2400,samples);packet++;}
                float[] result=new float[1600];timeline.Read(frame*800,result,0,2);misses+=result.Count(v=>v==0)/2;
            }
            return misses;
        }
        int oldMissing=Simulate(40),fixedMissing=Simulate(117);
        Require(oldMissing>10000&&fixedMissing==0,"Large loopback packet deadline regression");
        var clock=new AudioPacketClock();long next=-1,maxError=0;
        for(int i=0;i<12000;i++)
        {
            long timestamp=(long)Math.Round(i*480*1.0003);var p=clock.Place(i*480,timestamp,480);
            if(next>=0)Require(p.Start==next,"Clock drift inserted a gap/overlap");
            next=p.Start+p.Frames;maxError=Math.Max(maxError,Math.Abs(p.Start-timestamp));
        }
        Require(maxError<200,"Device clock drift is not bounded");
        var resumed=clock.Place(12000*480+24000,next+24000,480,true);
        Require(resumed.Start==next+24000,"A real pause was collapsed");
        var network=new NetworkAudioTimeline();var random=new Random(71);float[] tone=Enumerable.Repeat(.25f,960).ToArray();
        var arrivals=new List<(long Arrival,int Packet)>();long prior=0;
        for(int i=0;i<300;i++)
        {
            long arrival=(i+1)*480+(i==0?240:random.Next(0,4800));arrival=Math.Max(prior,arrival);prior=arrival;arrivals.Add((arrival,i));
        }
        int received=0,missing=0;
        for(int block=6;block<170;block++)
        {
            long start=block*800,readAt=start+800+9600;
            while(received<arrivals.Count&&arrivals[received].Arrival<=readAt)
            {
                var a=arrivals[received++];network.Put(900000+a.Packet*480,tone,a.Arrival);
            }
            float[] target=new float[800*6];network.Read(start,target,4,6);
            Require(target.Where((_,i)=>i%6<4).All(v=>v==0),"Network audio leaked into another track");
            missing+=target.Where((_,i)=>i%6==4).Count(v=>v==0);
        }
        Require(missing==0,"Network jitter created artificial silence");
        Program.Write("audio-timing-test.json",new{Pass=true,Old40msMissingSamples=oldMissing,FixedMissingSamples=fixedMissing,
            DeviceDriftPpm=300,DriftSeconds=120,MaxDriftErrorSamples=maxError,RealPausePreserved=true,
            NetworkJitterMs=100,NetworkMissingSamples=missing,ThirdTrackIsolation=true});
    }

    internal sealed class QuietTone:IWaveProvider
    {
        long sample;
        public WaveFormat WaveFormat=>WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
        public int Read(byte[] buffer,int offset,int count)
        {
            var output=MemoryMarshal.Cast<byte,float>(buffer.AsSpan(offset,count));
            for(int i=0;i<output.Length/2;i++){float v=(float)(.01*Math.Sin(2*Math.PI*440*sample++/48000));output[i*2]=output[i*2+1]=v;}
            return count;
        }
    }
    sealed class Probe(long origin,int seconds,double wait)
    {
        readonly AudioTimeline timeline=new();readonly object gate=new();
        readonly bool[] arrived=new bool[seconds*48000],read=new bool[seconds*48000];
        public long Packets,Frames;public int LargestPacket;public double MaxDeliveryMs;public double Energy;
        public void Put(long start,float[] data,int frames)
        {
            lock(gate)
            {
                Packets++;Frames+=frames;LargestPacket=Math.Max(LargestPacket,frames);
                MaxDeliveryMs=Math.Max(MaxDeliveryMs,(AudioTimeline.Position()-start-frames)/48.0);
                for(int i=0;i<frames*2;i++)Energy+=data[i]*data[i];
                for(long i=Math.Max(0,start-origin);i<Math.Min(arrived.Length,start-origin+frames);i++)arrived[i]=true;
                float[] ones=Enumerable.Repeat(1f,frames*2).ToArray();timeline.Put(start,ones);
            }
        }
        public Task Read()=>Task.Factory.StartNew(()=>
        {
            using var pacer=new Pacer();float[] result=new float[800*2];
            for(int i=0;i<seconds*60;i++)
            {
                long pos=origin+i*800;
                pacer.WaitUntil((long)((pos/48000.0+wait)*Stopwatch.Frequency),CancellationToken.None);
                Array.Clear(result);timeline.Read(pos,result,0,2);
                for(int j=0;j<800;j++)read[i*800+j]=result[j*2]!=0;
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        public object Report(){lock(gate)return new{Packets,Frames,LargestPacket,MaxDeliveryMs,Rms=Frames==0?0:Math.Sqrt(Energy/(Frames*2)),
            CapturedSamples=arrived.Count(v=>v),LateSamples=arrived.Where((v,i)=>v&&!read[i]).Count()};}
        public int Late {get{lock(gate)return arrived.Where((v,i)=>v&&!read[i]).Count();}}
    }
    public static void Live(bool playTone=false,string? deviceId=null)
    {
        string id=deviceId??Settings.Load().Replay.GameAudio;
        Require(id.Length>0&&!id.StartsWith("kvm:"),"Select a local PC audio endpoint first");
        using var devices=new MMDeviceEnumerator();
        using var device=id.StartsWith("default:")?devices.GetDefaultAudioEndpoint(id=="default:render"?DataFlow.Render:DataFlow.Capture,Role.Multimedia):devices.GetDevice(id);
        Require(device.DataFlow==DataFlow.Render,"This diagnostic tests a render endpoint only");
        long origin=AudioTimeline.Position()+96000;
        var old=new Probe(origin,10,.04);var corrected=new Probe(origin,10,ReplayAudio.WaitSeconds(new ReplayOptions())+1.0/60);
        using var stop=new CancellationTokenSource();
        using var legacy=new WasapiLoopbackCapture(device){WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(48000,2)};
        long oldNext=long.MinValue;
        legacy.DataAvailable+=(_,e)=>
        {
            var pcm=MemoryMarshal.Cast<byte,float>(e.Buffer.AsSpan(0,e.BytesRecorded));int frames=pcm.Length/2;
            if(frames==0)return;long actual=AudioTimeline.Position()-frames;
            if(oldNext==long.MinValue||Math.Abs(actual-oldNext)>2400)oldNext=actual;
            old.Put(oldNext,pcm.ToArray(),frames);oldNext+=frames;
        };
        using var playback=playTone?new WasapiOut(device,AudioClientShareMode.Shared,true,50):null;
        if(playback!=null){playback.Init(new QuietTone());playback.Play();}
        legacy.StartRecording();
        var capture=Task.Factory.StartNew(()=>TimedAudioCapture.Run(id,stop.Token,corrected.Put,_=>{}),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try{Task.WaitAll(old.Read(),corrected.Read());Thread.Sleep(150);}
        finally{legacy.StopRecording();stop.Cancel();capture.GetAwaiter().GetResult();}
        Program.Write("audio-live-test.json",new{Device=device.FriendlyName,Seconds=10,Old=old.Report(),Corrected=corrected.Report(),
            Pass=corrected.Frames>48000&&corrected.Late==0,PlaybackStarted=playTone,RawAudioSaved=false});
        Require(corrected.Frames>48000,"No audio packets; run this diagnostic while PC audio is playing");
        Require(corrected.Late==0,"Audio arrived after the corrected recording deadline");
    }
}
