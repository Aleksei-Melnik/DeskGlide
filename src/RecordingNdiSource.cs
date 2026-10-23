using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;

public sealed class RecordingNdiSource:IDisposable
{
    readonly string source;
    readonly Action<VideoBuffer> offer;
    readonly Action<bool>? availability;
    readonly CancellationTokenSource stop=new();
    readonly NdiRecordingAudioBus.Lease audio;
    readonly Task worker;
    volatile string status="Подключение NDI…",audioStatus="Ожидание звука NDI";
    int available,disposed,abandoned;
    long frames,audioPackets,duplicateFrames,sourceGaps,videoPackets,rejectedAudio,reconnects,lastCapturedAt,lastSourceTimecode;
    double framesPerSecond,sourceFrameRate,maxFrameAge;
    public string Status=>status+" · "+audioStatus;
    public string SourceAudioId {get;}
    public long Frames=>Interlocked.Read(ref frames);
    public double FramesPerSecond=>Volatile.Read(ref framesPerSecond);
    public long SourceGaps=>Interlocked.Read(ref sourceGaps);
    public long AudioPackets=>Interlocked.Read(ref audioPackets);
    public long DuplicateFrames=>Interlocked.Read(ref duplicateFrames);
    public bool Available=>Volatile.Read(ref available)!=0;
    public double SourceFrameRate=>Volatile.Read(ref sourceFrameRate);
    public long VideoPackets=>Interlocked.Read(ref videoPackets);
    public long RejectedAudioPackets=>Interlocked.Read(ref rejectedAudio);
    public long Reconnects=>Interlocked.Read(ref reconnects);
    public long LastCapturedAt=>Interlocked.Read(ref lastCapturedAt);
    public long LastSourceTimecode=>Interlocked.Read(ref lastSourceTimecode);
    public string Diagnostics=>$"NDI video={Frames}/{VideoPackets}, duplicates={DuplicateFrames}, source={SourceFrameRate:F2} FPS, receive={FramesPerSecond:F2} FPS, audio={AudioPackets}, rejectedAudio={RejectedAudioPackets}, gaps={SourceGaps}, reconnects={Reconnects}, maxAge={Volatile.Read(ref maxFrameAge):F1} ms";
    public RecordingNdiSource(string source,Action<VideoBuffer> offer,Action<bool>? availability=null)
    {
        if(string.IsNullOrWhiteSpace(source)||source.Length>1024||source.Any(char.IsControl))throw new ArgumentException("Select a valid NDI recording source.",nameof(source));
        ArgumentNullException.ThrowIfNull(offer);
        this.source=source;this.offer=offer;this.availability=availability;SourceAudioId="ndi:"+source;
        audio=NdiRecordingAudioBus.Register(source);audio.SetAvailable(false);
        worker=Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    void SetAvailable(bool value,bool countGap=true)
    {
        if(value&&Volatile.Read(ref disposed)!=0)return;
        int before=Interlocked.Exchange(ref available,value?1:0);
        if(before==(value?1:0))return;
        audio.SetAvailable(value);
        if(!value&&countGap)Interlocked.Increment(ref sourceGaps);
        try{availability?.Invoke(value);}catch(Exception e){Log.Write("Recording NDI availability observer: "+e.Message);}
    }
    void Run()
    {
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                try{Session();}
                catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
                catch(Exception e){status="NDI: "+e.Message;Log.Write("Recording NDI: "+e.Message);}
                if(Volatile.Read(ref abandoned)!=0)break;
                if(stop.Token.WaitHandle.WaitOne(1000))break;
                Interlocked.Increment(ref reconnects);
            }
        }
        finally{SetAvailable(false);Volatile.Write(ref framesPerSecond,0);if(stop.IsCancellationRequested)status="NDI остановлен";}
    }
    void Session()
    {
        // Recording input never starts an installer or audio endpoint. Runtime
        // setup belongs to the application settings flow.
        NdiNative.EnsureInitialized();
        IntPtr name=Marshal.StringToCoTaskMemUTF8(source),receiver=IntPtr.Zero;
        CancellationTokenSource? sessionStop=null;Task? audioWorker=null;
        try
        {
            var settings=new NdiNative.RecvSettings{Source=new(){Name=name},Color=1,Bandwidth=100,Fields=false};
            // UYVY_BGRA returns native UYVY for opaque sources. Alpha sources
            // produce BGRA and are explicitly rejected, never CPU-converted.
            receiver=NdiNative.NDIlib_recv_create_v3(ref settings);
            if(receiver==IntPtr.Zero)throw new IOException("Не удалось открыть источник записи.");
            sessionStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var token=sessionStop.Token;var clock=new NdiRecordingClock(Stopwatch.Frequency);
            long connectingAt=Stopwatch.GetTimestamp();bool sawVideo=false;
            var health=new NdiRecordingHealth(Stopwatch.Frequency,connectingAt);
            Exception? audioFailure=null;
            audioWorker=Task.Factory.StartNew(()=>
            {
                try{ReceiveAudio(receiver,clock,token);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){}
                catch(Exception e){Interlocked.CompareExchange(ref audioFailure,e,null);sessionStop.Cancel();}
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            long reportAt=Stopwatch.GetTimestamp(),reportPackets=VideoPackets;
            status="Ожидание NDI «"+source+"»";
            while(!token.IsCancellationRequested)
            {
                NdiNative.Video native=default;
                int kind=NdiNative.NDIlib_recv_capture_v3(receiver,ref native,IntPtr.Zero,IntPtr.Zero,100);
                if(kind==4)throw new IOException("Соединение с источником потеряно.");
                long now=Stopwatch.GetTimestamp();
                if(kind==1)
                {
                    try
                    {
                        NdiRecordingFrames.Validate(native);
                        if(!NdiRecordingFrames.SourceActive(native.Metadata))throw new IOException("Игровой ПК восстанавливает захват; запись приостановлена.");
                        if(native.Timecode<=0||native.Timecode==long.MaxValue)throw new IOException("Источник не передал исходную временную метку.");
                        Interlocked.Increment(ref videoPackets);Volatile.Write(ref sourceFrameRate,native.RateN/(double)native.RateD);
                        sawVideo=true;
                        if(health.Observe(now,native.Timecode))
                        {
                            long captured=clock.Map(native.Timecode,now);
                            using var frame=NdiRecordingFrames.Copy(native,captured);
                            Interlocked.Exchange(ref lastCapturedAt,captured);
                            Interlocked.Exchange(ref lastSourceTimecode,native.Timecode);
                            Volatile.Write(ref maxFrameAge,Math.Max(Volatile.Read(ref maxFrameAge),(now-captured)*1000.0/Stopwatch.Frequency));
                            SetAvailable(true);
                            // Borrowed immutable frame, matching StreamEngine.
                            // Consumers that retain it beyond this callback own
                            // a Retain() reference; no receive FIFO can grow here.
                            if(!stop.IsCancellationRequested)try{offer(frame);}catch(Exception e){Log.Write("Recording NDI frame observer: "+e.Message);}
                            Interlocked.Increment(ref frames);
                        }
                        else Interlocked.Increment(ref duplicateFrames);
                        status=$"NDI · UYVY · {native.Width} × {native.Height} · {SourceFrameRate:F0} FPS";
                    }
                    finally{NdiNative.NDIlib_recv_free_video_v2(receiver,ref native);}
                }
                if(now-reportAt>=Stopwatch.Frequency)
                {
                    Volatile.Write(ref framesPerSecond,(VideoPackets-reportPackets)*Stopwatch.Frequency/(double)(now-reportAt));
                    reportAt=now;reportPackets=VideoPackets;
                }
                // Discovery of a brand-new local source can take several
                // seconds. Once connected, loss is always bounded to 3 s.
                if(health.Expired(now)&&(sawVideo||now-connectingAt>Stopwatch.Frequency*8))throw new IOException("Нет кадров NDI более 3 секунд; запись приостановлена.");
            }
            if(audioFailure!=null)throw new IOException("NDI audio connection failed.",audioFailure);
        }
        finally
        {
            SetAvailable(false,!stop.IsCancellationRequested);audio.SetAvailable(false);sessionStop?.Cancel();
            void Release(){if(receiver!=IntPtr.Zero)NdiNative.NDIlib_recv_destroy(receiver);Marshal.FreeCoTaskMem(name);sessionStop?.Dispose();}
            if(audioWorker==null)Release();
            else if(!MediaShutdown.Drain([audioWorker],Release,500))
            {
                Interlocked.Exchange(ref abandoned,1);NdiNative.AbandonShutdown();
                status="NDI receiver did not stop; native teardown deferred.";
                Log.Write("Recording NDI audio shutdown exceeded 500 ms; preserving native resources until capture returns.");
            }
        }
    }
    void ReceiveAudio(IntPtr receiver,NdiRecordingClock clock,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            NdiNative.Audio native=default;
            int kind=NdiNative.NDIlib_recv_capture_v2(receiver,IntPtr.Zero,ref native,IntPtr.Zero,100);
            if(kind==4)throw new IOException("NDI audio disconnected.");
            if(kind!=2)continue;
            try
            {
                float[] pcm;
                try{pcm=NdiRecordingAudio.Convert(native);}
                catch(IOException e){Interlocked.Increment(ref rejectedAudio);audioStatus=e.Message;continue;}
                long captured=clock.Map(native.Timecode,Stopwatch.GetTimestamp());
                audio.Put(AudioTimeline.Position(captured),pcm);
                Interlocked.Increment(ref audioPackets);audioStatus="NDI audio 48 kHz";
            }
            finally{NdiNative.NDIlib_recv_free_audio_v2(receiver,ref native);}
        }
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();SetAvailable(false,false);
        if(!MediaShutdown.Drain([worker],()=>{audio.Dispose();stop.Dispose();}))
        {
            NdiNative.AbandonShutdown();
            Log.Write("Recording NDI shutdown exceeded 1500 ms; allowing application exit.");
        }
    }
}

sealed class NdiRecordingClock(long frequency)
{
    readonly object gate=new();
    long originSource,originLocal;
    public long Map(long sourceTimecode,long arrival)
    {
        if(sourceTimecode<=0||sourceTimecode==long.MaxValue)throw new IOException("NDI source has no usable source timecode.");
        lock(gate)
        {
            if(originSource==0){originSource=sourceTimecode;originLocal=arrival;}
            decimal mapped=originLocal+((decimal)sourceTimecode-originSource)*frequency/10_000_000;
            if(mapped<=0||mapped>long.MaxValue||Math.Abs(mapped-arrival)>frequency*5m)
                throw new IOException("NDI source clock changed; reconnecting.");
            return (long)Math.Round(mapped);
        }
    }
}

static class NdiRecordingFrames
{
    internal const long MaximumBytes=40L*1024*1024;
    internal static bool SourceActive(IntPtr metadata)
    {
        if(metadata==IntPtr.Zero)return true;
        int length=0;while(length<256&&Marshal.ReadByte(metadata,length)!=0)length++;
        if(length==256)return true;
        return !(Marshal.PtrToStringUTF8(metadata,length)??"").Contains("<deskglide capture=\"recovering\"/>",StringComparison.Ordinal);
    }
    public static VideoBuffer Copy(NdiNative.Video video,long capturedAt)
    {
        int row=Validate(video);
        if(capturedAt<=0)throw new IOException("Recording requires a valid mapped capture timestamp.");
        var result=new VideoBuffer(video.Width,video.Height){CapturedAt=capturedAt};
        try
        {
            for(int y=0;y<video.Height;y++)Marshal.Copy(video.Data+y*video.Stride,result.Data,y*row,row);
            return result;
        }
        catch{result.Dispose();throw;}
    }
    public static int Validate(NdiNative.Video video)
    {
        long row=(long)video.Width*2,extent=(long)video.Stride*Math.Max(0,video.Height-1)+row;
        if(video.Width<2||video.Height<1||(video.Width&1)!=0||video.Width>16384||video.Height>16384||
            row*video.Height>MaximumBytes||video.Stride<row||(video.Stride&1)!=0||extent>MaximumBytes||
            video.Data==IntPtr.Zero||video.FourCC!=0x59565955||video.Format!=1||video.RateN<=0||video.RateD<=0||
            video.RateN/(double)video.RateD>240)
            throw new IOException("Recording requires bounded progressive UYVY NDI video (no alpha/HDR/fields).");
        return (int)row;
    }
}
static class NdiRecordingAudio
{
    public static unsafe float[] Convert(NdiNative.Audio audio)
    {
        long extent=(long)audio.Stride*Math.Max(0,audio.Channels-1)+(long)audio.Samples*4;
        if(audio.Rate!=48000||audio.Channels is <1 or >64||audio.Samples is <1 or >48000||
            audio.Stride<(long)audio.Samples*4||(audio.Stride&3)!=0||extent>NdiRecordingFrames.MaximumBytes||audio.Data==IntPtr.Zero)
            throw new IOException("Recording requires bounded planar 48 kHz NDI audio.");
        var result=new float[audio.Samples*2];
        for(int i=0;i<audio.Samples;i++)for(int channel=0;channel<2;channel++)
        {
            float value=*((float*)((byte*)audio.Data+Math.Min(channel,audio.Channels-1)*audio.Stride)+i)/10;
            result[i*2+channel]=float.IsFinite(value)?Math.Clamp(value,-1,1):0;
        }
        return result;
    }
}
sealed class NdiRecordingHealth(long frequency,long started)
{
    long lastPacket=started,lastImage;
    public bool Observe(long now,long timecode)
    {
        lastPacket=now;
        if(timecode==lastImage)return false;
        if(timecode<lastImage)throw new IOException("NDI source capture clock restarted.");
        lastImage=timecode;return true;
    }
    public bool Expired(long now)=>now-lastPacket>=frequency*3;
}
