using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;

// Hidden, isolated GPU workload. Never changes the desktop, keys, profiles or live recorder.
static class ReplayLoadTests
{
    public static void Run(int fps,string codec,bool stress,int seconds=30,string? encoderName=null,bool hdr=false,bool sync=false,int window=12,int width=2560,int height=1440,string audio="Mixed",bool synthetic=false)
    {
        string root=Path.Combine(AppContext.BaseDirectory,$"load-{fps}-{codec}-{stress}-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var options=new ReplayOptions{Enabled=true,Fps=fps,Codec=codec,Quality="Ultra",Width=width,Height=height,AudioMode=audio,
            Microphone="",GameAudio="default:render",ExtraAudio="",Folder=Path.Combine(root,"clips")};
        using var devices=new MMDeviceEnumerator();using var endpoint=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        using var playback=new WasapiOut(endpoint,AudioClientShareMode.Shared,true,200);
        playback.Init(sync?new CodedTone():new AudioTimingTests.QuietTone());if(!synthetic)playback.Play();
        using var stop=new CancellationTokenSource();
        var referencePackets=new List<float[]>();
        long referenceStart=long.MinValue;
        var reference=synthetic?Task.CompletedTask:Task.Factory.StartNew(()=>TimedAudioCapture.Run("default:render",stop.Token,(start,data,frames)=>{if(referenceStart==long.MinValue)referenceStart=start;referencePackets.Add(data.AsSpan(0,frames*2).ToArray());},_=>{},bufferMilliseconds:200),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        var load=stress?Task.Factory.StartNew(()=>GpuLoad(stop.Token),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default):Task.CompletedTask;
        var cpu=stress?Enumerable.Range(0,Math.Max(1,Environment.ProcessorCount-2)).Select(_=>Task.Factory.StartNew(()=>
        {
            double value=.1;while(!stop.IsCancellationRequested)for(int i=0;i<10000;i++)value=Math.Sin(value+.1);
            GC.KeepAlive(value);
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)).ToArray():[];
        var encoder=encoderName==null?null:RecordingEncoders.Candidates(options,width,height).Single(e=>e.Name==encoderName);
        using var recorder=new ReplayRecorder(options,synthetic,window,encoder);
        var memorySamples=new List<object>();
        long producerSkipped=0,captured=0,producerOrigin=0;double maxConvertMs=0;
        var sourceMissingIds=new System.Collections.Concurrent.ConcurrentQueue<long>();
        var producer=Task.Factory.StartNew(()=>
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            using var device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.BgraSupport,FeatureLevel.Level_11_0);
            using var context=device.ImmediateContext;using var color=new ColorPipeline(device,context);
            using var source=device.CreateTexture2D(new Texture2DDescription(hdr?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm,2560,1440,1,1,BindFlags.ShaderResource|BindFlags.UnorderedAccess));
            using var target=device.CreateUnorderedAccessView(source);
            using var pattern=device.CreateComputeShader(Compiler.Compile(Scene,"main","ReplayLoadScene.hlsl","cs_5_0").Span);
            using var parameters=device.CreateBuffer(new BufferDescription(16,BindFlags.ConstantBuffer,ResourceUsage.Default));
            using var pacer=new Pacer();long origin=Stopwatch.GetTimestamp(),index=0;producerOrigin=origin;
            while(!stop.IsCancellationRequested)
            {
                pacer.WaitUntil(origin+index*Stopwatch.Frequency/fps,stop.Token);
                long now=Stopwatch.GetTimestamp(),next=Math.Max(index,(now-origin)*fps/Stopwatch.Frequency);
                for(long missing=index;missing<next;missing++)sourceMissingIds.Enqueue(missing);
                producerSkipped+=next-index;index=next;
                context.UpdateSubresource(new uint[]{(uint)index,hdr?1u:0u,0,0},parameters);
                context.CSSetShader(pattern);context.CSSetConstantBuffer(0,parameters);context.CSSetUnorderedAccessView(0,target);
                context.Dispatch(320,180,1);context.CSSetUnorderedAccessView(0,null);
                long begin=Stopwatch.GetTimestamp();
                using var frame=color.ConvertNdi(source,hdr,1);frame.CapturedAt=origin+index*Stopwatch.Frequency/fps;
                maxConvertMs=Math.Max(maxConvertMs,Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                recorder.Offer(frame);captured++;index++;
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            for(int elapsed=1;elapsed<=seconds;elapsed++)
            {
                Thread.Sleep(1000);
                if(elapsed%5==0)
                {
                    using var process=Process.GetCurrentProcess();
                    memorySamples.Add(new{elapsed,recorder.FrameMemoryBytes,process.WorkingSet64,process.PrivateMemorySize64,ManagedBytes=GC.GetTotalMemory(false)});
                }
                if(elapsed%5==0)Program.Write("replay-load-progress.json",new{elapsed,fps,codec,captured,producerSkipped,recorder.EncodedFrames,recorder.RepeatedInputFrames,recorder.HistoryMisses,recorder.MaxWriterLateMs,recorder.EncoderStarts,recorder.AudioDiagnostics,recorder.Error});
            }
            if(recorder.Error!=null)throw new Exception(recorder.Error);
            var first=recorder.SaveAsync("Load").GetAwaiter().GetResult();
            Thread.Sleep(3000);
            var second=recorder.SaveAsync("Load").GetAwaiter().GetResult();
            Thread.Sleep(1000);
            var third=recorder.SaveAsync("Load").GetAwaiter().GetResult();
            Program.Write($"load-{fps}-{codec}-{stress}{(encoderName==null?"":"-"+encoderName)}{(hdr?"-hdr":"")}{(sync?"-sync":"")}.json",new{fps,codec,stress,seconds,encoderName,hdr,sync,width,height,audio,synthetic,producerOrigin,referenceStart,QpcFrequency=Stopwatch.Frequency,captured,producerSkipped,SourceMissingIds=sourceMissingIds.ToArray(),GpuConvertMs=maxConvertMs,
                recorder.EncodedFrames,recorder.RepeatedInputFrames,recorder.HistoryMisses,recorder.MaxWriterLateMs,recorder.EncoderStarts,
                recorder.MaxConvertMs,recorder.MaxFramePreparationAgeMs,recorder.SessionEpochTicks,recorder.AudioReadyTicks,recorder.ConversionQueueDrops,recorder.ConversionQueueBytes,recorder.MaxPipeWriteMs,recorder.AudioStatus,recorder.AudioDiagnostics,recorder.Error,Memory=memorySamples,First=first,Second=second,Third=third,Root=root});
            if(recorder.Error!=null||recorder.EncoderStarts!=1)throw new Exception("Recording restarted under load; see report");
        }
        finally
        {
            stop.Cancel();Task.WaitAll([producer,load,reference,..cpu]);
            using var referenceFile=File.Create(Path.Combine(root,"reference.f32"));
            foreach(var packet in referencePackets)referenceFile.Write(MemoryMarshal.AsBytes(packet.AsSpan()));
        }
    }
    sealed class CodedTone:IWaveProvider
    {
        long sample;
        public WaveFormat WaveFormat=>WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
        public int Read(byte[] buffer,int offset,int count)
        {
            var output=MemoryMarshal.Cast<byte,float>(buffer.AsSpan(offset,count));
            for(int i=0;i<output.Length/2;i++)
            {
                uint hash=(uint)(sample/2400)*1973+9277;hash=(hash^(hash>>13))*1274126177;
                double amplitude=(hash&1)==0?.45:1;
                float v=(float)(.01*amplitude*Math.Sin(2*Math.PI*440*sample++/48000));output[i*2]=output[i*2+1]=v;
            }
            return count;
        }
    }
    const string Scene="""
        RWTexture2D<float4> dst:register(u0);
        cbuffer SceneSettings:register(b0) { uint frame; uint hdr; uint2 unused; }
        [numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID) {
            uint2 q=uint2((p.x+frame*7)%2560,(p.y+frame*3)%1440);
            uint hash=(q.x/2)*1973+(q.y/2)*9277; hash=(hash^(hash>>13))*1274126177;
            float grain=(hash&255)/255.0;
            float3 rgb=saturate(float3(q.x/2560.0,q.y/1440.0,((q.x/48+q.y/48)%2)*.4)+grain*.35);
            if(hdr!=0) rgb=pow(rgb,2.2)*1.5;
            if(p.x<256 && p.y<16) rgb=((frame>>(p.x/16))&1)!=0?1:0;
            dst[p.xy]=float4(rgb,1);
        }
        """;
    static void GpuLoad(CancellationToken token)
    {
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        using var device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.None,FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;
        const string code="""
            RWTexture2D<float> dst:register(u0);
            [numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID) {
                float v=p.x*.001+p.y*.002;
                [loop] for(int i=0;i<256;i++) v=sin(v)*.95+.01;
                dst[p.xy]=v;
            }
            """;
        using var shader=device.CreateComputeShader(Compiler.Compile(code,"main","ReplayLoad.hlsl","cs_5_0").Span);
        using var texture=device.CreateTexture2D(new Texture2DDescription(Format.R32_Float,2048,1024,1,1,BindFlags.UnorderedAccess));
        using var uav=device.CreateUnorderedAccessView(texture);using var query=device.CreateQuery(new QueryDescription(QueryType.Event));
        context.CSSetShader(shader);context.CSSetUnorderedAccessView(0,uav);
        while(!token.IsCancellationRequested)
        {
            for(int i=0;i<32;i++)context.Dispatch(256,128,1);context.End(query);context.Flush();
            while(!context.GetData(query,out int done)||done==0){if(token.IsCancellationRequested)return;Thread.Sleep(1);}
        }
    }
}
