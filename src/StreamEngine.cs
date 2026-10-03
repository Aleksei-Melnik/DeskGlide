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
    public string Status {get;private set;}="Остановлено";
    public string CaptureStatus {get;private set;}="Подготовка";
    public string? Error {get;private set;}
    public string SourceName {get;private set;}="SdrCapture SDR";
    public long Frames {get;private set;}
    public long CapturedFrames {get;private set;}
    public long ReplacedFrames {get;private set;}
    public long MissedSlots {get;private set;}
    public long Recoveries {get;private set;}
    public int Connections {get;private set;}
    public double Fps {get;private set;}
    public double WhiteNits {get;private set;}
    public double CaptureMs {get;private set;}
    public double SendMs {get;private set;}
    public double MaxSendMs {get;private set;}
    public long LastFreshAt {get;private set;}
    public bool Running=>sendTask is {IsCompleted:false};
    public Action<VideoBuffer>? FrameAvailable {get;set;}
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
    void CaptureLoop()
    {
        Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
        using var pacing=new Pacer();
        DesktopCapture? capture=null;
        int activeVersion=-1;
        string activeDevice="";
        long nextRetry=0,logAt=0;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                pacing.SetRate(Math.Max(NdiEnabled?60:1,ReplayEnabled?ReplayFrameRate:1));
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
                        capture=new DesktopCapture(display,config.Compensate);
                        activeDevice=config.Device;activeVersion=Volatile.Read(ref restartVersion);
                        Recoveries++;CaptureStatus="Захват";
                        Log.Write($"Capture opened: {display.Device}, {display.Width}x{display.Height}, HDR={display.Hdr}");
                    }
                    capture.CompensateSdr=config.Compensate;capture.CaptureCursor=config.Cursor;
                    long begin=Stopwatch.GetTimestamp();
                    var fresh=capture.Next();
                    CaptureMs=Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
                    WhiteNits=capture.WhiteNits;
                    if(fresh!=null)
                    {
                        sizes.TryAdd((fresh.Width,fresh.Height),0);
                        LastFreshAt=fresh.CapturedAt;
                        try{FrameAvailable?.Invoke(fresh);}catch(Exception e){Log.Write("Replay frame observer: "+e.Message);}
                        var displaced=Interlocked.Exchange(ref pending,fresh);
                        if(displaced!=null){displaced.Dispose();ReplacedFrames++;}
                        CapturedFrames++;Error=null;CaptureStatus="Захват";
                    }
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
        stop.Cancel();
        try{captureTask?.GetAwaiter().GetResult();sendTask?.GetAwaiter().GetResult();}
        finally{Interlocked.Exchange(ref pending,null)?.Dispose();stop.Dispose();}
    }
}

// Absolute deadlines avoid drift and never perform catch-up bursts after a stall.
sealed class Pacer:IDisposable
{
    readonly IntPtr timer;
    long next=Stopwatch.GetTimestamp();
    long step=Stopwatch.Frequency/60;
    int rate=60;
    public void SetRate(int value){if(value==rate)return;rate=value;step=Stopwatch.Frequency/Math.Clamp(value,1,240);next=Stopwatch.GetTimestamp();}
    public Pacer()
    {
        timer=CreateWaitableTimerExW(IntPtr.Zero,null,2,0x1F0003);
        if(timer==IntPtr.Zero) timer=CreateWaitableTimerExW(IntPtr.Zero,null,0,0x1F0003);
    }
    public long Wait(CancellationToken token)
    {
        long now=Stopwatch.GetTimestamp(),missed=0;
        if(now-next>=step){missed=(now-next)/step;next=now;}
        WaitUntil(next,token);
        next+=step;return missed;
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
