using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;
public sealed record CaptureOptions(string Device,bool Compensate=true,bool Cursor=true);

public sealed class StreamEngine:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int,int),byte> sizes=new();
    public bool HasCaptureSize(int width,int height)=>sizes.ContainsKey((width,height));
    public string[] CaptureSizes=>sizes.Keys.Select(s=>$"{s.Item1}x{s.Item2}").ToArray();
    CaptureOptions options=new("");
    VideoBuffer? pending;
    Task? captureTask,sendTask;
    int restartVersion;
    int disposed;
    public string Status {get;private set;}="Остановлено";
    public string CaptureStatus {get;private set;}="Подготовка";
    public string? Error {get;private set;}
    public string SourceName {get;private set;}="SdrCapture SDR";
    public long Frames {get;private set;}
    public long CapturedFrames {get;private set;}
    public long DesktopFrames {get;private set;}
    public long AccumulatedDesktopFrames {get;private set;}
    public long SkippedDesktopFrames {get;private set;}
    public long GpuReadbackDrops {get;private set;}
    public int CaptureRate {get;private set;}
    public long ReplacedFrames {get;private set;}
    public long MissedSlots {get;private set;}
    public long Recoveries {get;private set;}
    public int Connections {get;private set;}
    public double Fps {get;private set;}
    public double WhiteNits {get;private set;}
    public double CaptureMs {get;private set;}
    public double MaxCaptureMs {get;private set;}
    public double TotalCaptureMs {get;private set;}
    public double MaxObserverMs {get;private set;}
    public double SendMs {get;private set;}
    public double MaxSendMs {get;private set;}
    public long LastFreshAt {get;private set;}
    public bool Running=>sendTask is {IsCompleted:false};
    public Action<VideoBuffer>? FrameAvailable {get;set;}
    internal Action<CaptureObservation>? CaptureObserved;
    internal bool BlockingReadbackForTest;
    public volatile bool NdiEnabled=true;
    public volatile int NdiAudioVolume=50;
    public volatile string NdiAudioDevice="";
    public string NdiAudioStatus {get;private set;}="Silent";
    public volatile bool ReplayEnabled;
    public volatile int ReplayFrameRate=60;
    public void Start(CaptureOptions value,string name="SdrCapture SDR")
    {
        if(sendTask!=null) throw new InvalidOperationException("Already started");
        options=value;
        captureTask=Task.Factory.StartNew(CaptureLoop,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        sendTask=Task.Factory.StartNew(()=>SendLoop(name),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    public void Update(CaptureOptions value)=>Volatile.Write(ref options,value);
    public void RequestCaptureRestart()=>Interlocked.Increment(ref restartVersion);
    internal static int RecordingCaptureRate(int requested)=>Math.Min(240,requested*2);
    void CaptureLoop()
    {
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        using var pacing=new Pacer();
        DesktopCapture? capture=null;
        int activeVersion=-1;
        string activeDevice="";
        string canvasDevice="";int canvasWidth=0,canvasHeight=0;
        long nextRetry=0,logAt=0;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                int requested=Math.Max(NdiEnabled?60:1,ReplayEnabled?ReplayFrameRate:1);
                // An integer acquisition/output ratio avoids the recurring
                // short/long motion steps caused by resampling 72/144 Hz to
                // 60/120 FPS. Two polls per recording interval also provide
                // recovery headroom without changing recording or NDI rates.
                CaptureRate=ReplayEnabled?RecordingCaptureRate(requested):requested;
                pacing.SetRate(CaptureRate);
                pacing.Wait(stop.Token);
                if(stop.IsCancellationRequested) break;
                var config=Volatile.Read(ref options);
                if(!NdiEnabled&&!ReplayEnabled)
                {
                    capture?.Dispose();capture=null;CaptureStatus="Захват выключен";
                    Thread.Sleep(100);continue;
                }
                if(capture!=null && (activeVersion!=Volatile.Read(ref restartVersion)||activeDevice!=config.Device))
                {capture.Dispose();capture=null;nextRetry=0;}
                if(capture==null && Stopwatch.GetTimestamp()<nextRetry) continue;
                try
                {
                    if(capture==null)
                    {
                        var displays=DisplayInfo.All();
                        var display=displays.FirstOrDefault(d=>d.Device==config.Device);
                        if(display==null) throw new InvalidOperationException("Выбранный монитор временно недоступен");
                        if(canvasDevice!=config.Device)
                        {
                            var preferred=PreferredResolution.Read(display.Device,display.Width,display.Height);
                            canvasWidth=preferred.Width;canvasHeight=preferred.Height;canvasDevice=config.Device;
                        }
                        capture=new DesktopCapture(display,config.Compensate,canvasWidth,canvasHeight);
                        activeDevice=config.Device;activeVersion=Volatile.Read(ref restartVersion);
                        Recoveries++;CaptureStatus="Захват";
                        Log.Write($"Capture opened: {display.Device}, {display.Width}x{display.Height}, output={canvasWidth}x{canvasHeight}, HDR={display.Hdr}");
                    }
                    capture.CompensateSdr=config.Compensate;capture.CaptureCursor=config.Cursor;
                    capture.Observe=CaptureObserved;
                    capture.BlockingReadbackForTest=BlockingReadbackForTest;
                    long begin=Stopwatch.GetTimestamp();
                    var fresh=capture.Next();
                    CaptureMs=Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    WhiteNits=capture.WhiteNits;
                    GpuReadbackDrops=capture.ReadbackDrops;
                    if(capture.DesktopUpdated){DesktopFrames++;AccumulatedDesktopFrames+=capture.AccumulatedFrames;SkippedDesktopFrames+=Math.Max(0,(long)capture.AccumulatedFrames-1);}
                    while(fresh!=null)
                    {
                        sizes.TryAdd((fresh.Width,fresh.Height),0);
                        LastFreshAt=fresh.CapturedAt;
                        long observer=Stopwatch.GetTimestamp();
                        try{FrameAvailable?.Invoke(fresh);}catch(Exception e){Log.Write("Replay frame observer: "+e.Message);}
                        MaxObserverMs=Math.Max(MaxObserverMs,Stopwatch.GetElapsedTime(observer).TotalMilliseconds);
                        var displaced=Interlocked.Exchange(ref pending,fresh);
                        if(displaced!=null){displaced.Dispose();ReplacedFrames++;}
                        CapturedFrames++;Error=null;CaptureStatus="Захват";
                        // A GPU stall can finish several queued copies at once.
                        // Preserve their timestamps in replay immediately; do
                        // not carry that backlog into future capture slots.
                        begin=Stopwatch.GetTimestamp();
                        fresh=capture.ReadCompleted();
                        CaptureMs+=Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    }
                    MaxCaptureMs=Math.Max(MaxCaptureMs,CaptureMs);TotalCaptureMs+=CaptureMs;
                }
                catch(Exception e)
                {
                    capture?.Dispose();capture=null;
                    Error=e.Message;CaptureStatus="Восстановление захвата";
                    long now=Stopwatch.GetTimestamp();
                    if(now>=logAt){Log.Write(e.ToString());logAt=now+Stopwatch.Frequency*2;}
                    nextRetry=e.HResult==unchecked((int)0x887A0026)||e is CaptureResetException?now:now+Stopwatch.Frequency/10;
                }
            }
        }
        finally{capture?.Dispose();}
    }
    void SendLoop(string name)
    {
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        using var pacing=new Pacer();
        VideoBuffer? current=null;
        try
        {
            NdiSender? sender=null;
            try
            {
            long reportAt=Stopwatch.GetTimestamp(),reportFrames=0;
            while(!stop.IsCancellationRequested)
            {
                MissedSlots+=pacing.Wait(stop.Token);
                if(stop.IsCancellationRequested) break;
                var fresh=Interlocked.Exchange(ref pending,null);
                if(fresh!=null){current?.Dispose();current=fresh;}
                if(!NdiEnabled)
                {
                    sender?.Dispose();sender=null;Connections=0;Status="NDI выключен";
                    if(!ReplayEnabled){current?.Dispose();current=null;}
                    continue;
                }
                if(sender==null)
                {
                    try
                    {
                        NdiRuntime.EnsureAsync(message=>Status=message,stop.Token).GetAwaiter().GetResult();
                        if(!NdiEnabled)continue;
                    }
                    catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
                    catch(Exception e)
                    {
                        Status=e.Message;Connections=0;
                        // Local capture/replay and KVM remain usable if setup is
                        // declined. A manual retry can enable this sender later.
                        if(stop.Token.WaitHandle.WaitOne(2000))break;continue;
                    }
                    sender=new NdiSender(name,()=>NdiAudioDevice,()=>NdiAudioVolume);SourceName=sender.SourceName;
                    Log.Write($"NDI source created: {SourceName}; runtime={NdiNative.RuntimePath}");
                }
                if(current==null){Status="Ожидание первого SDR-кадра";continue;}
                long begin=Stopwatch.GetTimestamp();
                sender.Send(current);
                SendMs=Stopwatch.GetElapsedTime(begin).TotalMilliseconds;MaxSendMs=Math.Max(MaxSendMs,SendMs);
                NdiAudioStatus=sender.AudioStatus;
                Frames++;
                long now=Stopwatch.GetTimestamp();
                if(now-reportAt>=Stopwatch.Frequency)
                {
                    Fps=(Frames-reportFrames)/(double)(now-reportAt)*Stopwatch.Frequency;
                    reportFrames=Frames;reportAt=now;Connections=sender.Connections;
                }
                Status=CaptureStatus=="Восстановление захвата"?"NDI · последний SDR-кадр · восстановление":$"NDI · SDR · {Fps:F1} FPS · подключений {Connections}";
            }
            }
            finally{sender?.Dispose();}
        }
        catch(Exception e){Error=e.Message;Status="Ошибка NDI";Log.Write(e.ToString());stop.Cancel();}
        finally{current?.Dispose();}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();
        var workers=new[]{captureTask,sendTask}.OfType<Task>();
        if(!MediaShutdown.Drain(workers,()=>{Interlocked.Exchange(ref pending,null)?.Dispose();stop.Dispose();}))
        {
            // Do not call NDI's global destroy concurrently with the blocked
            // sender destroy. Process termination releases the remaining native state.
            NdiNative.AbandonShutdown();
            Log.Write("Capture/NDI shutdown exceeded 1500 ms; allowing application exit.");
        }
    }
}

// Absolute deadlines avoid drift and never perform catch-up bursts after a stall.
sealed class Pacer:IDisposable
{
    readonly IntPtr timer;
    long origin=Stopwatch.GetTimestamp(),index;
    long step=Stopwatch.Frequency/60;
    int rate=60;
    public void SetRate(int value){value=Math.Clamp(value,1,240);if(value==rate)return;rate=value;step=Stopwatch.Frequency/rate;origin=Stopwatch.GetTimestamp();index=0;}
    // Derive each deadline from its index rather than accumulating a truncated
    // tick interval (240 Hz otherwise drifts 57.6 ms/hour with a 10 MHz QPC).
    internal static long Deadline(long origin,long index,int rate)=>origin+index/rate*Stopwatch.Frequency+index%rate*Stopwatch.Frequency/rate;
    public Pacer()
    {
        timer=CreateWaitableTimerExW(IntPtr.Zero,null,2,0x1F0003);
        if(timer==IntPtr.Zero) timer=CreateWaitableTimerExW(IntPtr.Zero,null,0,0x1F0003);
    }
    public long Wait(CancellationToken token)
    {
        long now=Stopwatch.GetTimestamp(),missed=0;
        long next=Deadline(origin,index,rate);
        if(now-next>=step)
        {
            long elapsed=now-origin;
            long current=elapsed/Stopwatch.Frequency*rate+elapsed%Stopwatch.Frequency*rate/Stopwatch.Frequency;
            missed=Math.Max(0,current-index);index+=missed;next=Deadline(origin,index,rate);
        }
        WaitUntil(next,token);
        index++;return missed;
    }
    public void WaitUntil(long deadline,CancellationToken token)
    {
        long now;
        while(!token.IsCancellationRequested && (now=Stopwatch.GetTimestamp())<deadline)
        {
            double remaining=(deadline-now)/(double)Stopwatch.Frequency;
            if(timer!=IntPtr.Zero && remaining>.0003)
            {
                long due=-(long)(remaining*10_000_000);
                if(SetWaitableTimer(timer,ref due,0,IntPtr.Zero,IntPtr.Zero,false)) WaitForSingleObject(timer,20);
                else Thread.Sleep(1);
            }
            else if(remaining>.001) Thread.Sleep(1); else Thread.SpinWait(50);
        }
    }
    public void Dispose(){if(timer!=IntPtr.Zero) CloseHandle(timer);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr callback,IntPtr arg,[MarshalAs(UnmanagedType.Bool)]bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
}

static class Log
{
    static readonly object gate=new();
    public static string Folder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SdrCapture");
    public static void Write(string text)
    {
        try{lock(gate){Directory.CreateDirectory(Folder);string path=Path.Combine(Folder,"capture.log");
            if(File.Exists(path)&&new FileInfo(path).Length>4_000_000) File.Move(path,Path.Combine(Folder,"capture.previous.log"),true);
            File.AppendAllText(path,$"{DateTime.Now:O} {text}\n");}}
        catch{}
    }
}
