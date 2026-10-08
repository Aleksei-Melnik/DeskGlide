using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;

namespace SdrCapture;

sealed record DiscordOptions
{
    public bool Enabled {get;set;}
    public string Source {get;set;}="";
    public string AudioDevice {get;set;}="";
    public string AudioMode {get;set;}="Network";
    public string CaptureDevice {get;set;}="";
    public string CameraName {get;set;}="";
    public int Volume {get;set;}=100;
    public void Validate(){if(Source==null||AudioDevice==null||CaptureDevice==null||CameraName==null||Source.Length>1024||Volume is <0 or >100||AudioMode is not("Network" or "Local" or "Silent"))throw new ArgumentException("Некорректные настройки Discord.");if(CameraName.Length>0)CameraInstallation.ValidateName(CameraName);if(Enabled&&string.IsNullOrWhiteSpace(Source))throw new ArgumentException("Выберите NDI-источник игрового ПК для Discord.");}
}

// Bounded-capacity IPC, with native source dimensions in the header. One latest frame.
sealed class CameraFrames:IDisposable
{
    public const int Width=1920,Height=1080,Bytes=Width*Height*4;
    public const int MaxBytes=7680*4320*4;
    internal static string Prefix=>@"Local\ScreenCapture.Camera.v3."+WindowsIdentity.GetCurrent().User!.Value+".";
    readonly MemoryMappedFile mapping;
    readonly MemoryMappedViewAccessor view;
    readonly Mutex mutex;
    long number;
    public CameraFrames(){mutex=new(false,Prefix+"lock");mapping=MemoryMappedFile.CreateOrOpen(Prefix+"frame",32+MaxBytes,MemoryMappedFileAccess.ReadWrite);view=mapping.CreateViewAccessor();}
    public unsafe void Write(IntPtr topRow,int stride,int width=Width,int height=Height)
    {
        if(width<2||height<2||width>8192||height>8192||(long)width*height*4>MaxBytes||stride<width*4||topRow==IntPtr.Zero)throw new IOException("Некорректный кадр виртуальной камеры.");
        bool held=false;
        try
        {
            try{held=mutex.WaitOne(5);}catch(AbandonedMutexException){held=true;}
            if(!held)return;
            byte* dest=null;view.SafeMemoryMappedViewHandle.AcquirePointer(ref dest);
            try
            {
                *(uint*)dest=0;
                // DirectShow RGB DIBs are bottom-up. GDI input is top-down.
                for(int y=0;y<height;y++)Buffer.MemoryCopy((byte*)topRow+y*stride,dest+32+(height-1-y)*width*4,width*4,width*4);
                *(int*)(dest+4)=width;*(int*)(dest+8)=height;*(int*)(dest+12)=width*4;
                *(long*)(dest+16)=Environment.TickCount64;*(long*)(dest+24)=++number;
                Thread.MemoryBarrier();*(uint*)dest=0x53434331;
            }
            finally{view.SafeMemoryMappedViewHandle.ReleasePointer();}
        }
        finally{if(held)mutex.ReleaseMutex();}
    }
    public void Dispose(){view.Dispose();mapping.Dispose();mutex.Dispose();}
}

sealed class DiscordReceiver:IDisposable
{
    internal static Action<int,int>? TestFrame;
    readonly DiscordOptions options;
    readonly CancellationTokenSource stop=new();
    readonly Task task;
    volatile string status="Подключение…";
    public string Status=>status;
    public DiscordReceiver(DiscordOptions options){this.options=options with{};options.Validate();task=Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);}
    void Run()
    {
        while(!stop.IsCancellationRequested)
        {
            try{Session().GetAwaiter().GetResult();}
            catch(Exception e){status="Discord: "+e.Message;Log.Write(status);}
            if(stop.Token.WaitHandle.WaitOne(1000))break;
        }
    }
    async Task Session()
    {
        NdiNative.EnsureInitialized();IntPtr text=Marshal.StringToCoTaskMemUTF8(options.Source),receiver=IntPtr.Zero;
        try
        {
            var config=new NdiNative.RecvSettings{Source=new(){Name=text},Color=0,Bandwidth=100,Fields=false};
            receiver=NdiNative.NDIlib_recv_create_v3(ref config);
            if(receiver==IntPtr.Zero)throw new IOException("Не удалось открыть NDI-источник.");
            using var frames=new CameraFrames();
            using var audioStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var audio=Task.Factory.StartNew(()=>ReceiveAudio(receiver,audioStop.Token),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            var clock=Stopwatch.StartNew();long count=0,last=0;double fps=0;
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    NdiNative.Video video=default;int kind=NdiNative.NDIlib_recv_capture_v3(receiver,ref video,IntPtr.Zero,IntPtr.Zero,100);
                    if(kind==4)throw new IOException("Соединение NDI потеряно.");
                    if(kind!=1){if(clock.ElapsedMilliseconds-last>2000)status="Ожидание источника «"+options.Source+"»";continue;}
                    try
                    {
                        if(video.Width<1||video.Height<1||video.Width>16384||video.Height>16384||video.Data==IntPtr.Zero||video.Stride<video.Width*4||video.FourCC is not (0x41524742 or 0x58524742))throw new IOException("NDI не вернул кадр BGRA/BGRX.");
                        // The sender already stretches to the native monitor canvas.
                        // A second GPU resize/readback was forcing 1080p and stalling reception.
                        frames.Write(video.Data,video.Stride,video.Width,video.Height);
                        if(TestFrame!=null){int center=Marshal.ReadInt32(video.Data,(video.Height/2)*video.Stride+video.Width/2*4);TestFrame(center,center);}
                        count++;if(clock.ElapsedMilliseconds-last>=1000){fps=count*1000.0/(clock.ElapsedMilliseconds-last);last=clock.ElapsedMilliseconds;count=0;}
                        status=$"Камера {video.Width} × {video.Height} · 60 FPS · вход {fps:F1} FPS · {audioStatus}";
                    }
                    finally{NdiNative.NDIlib_recv_free_video_v2(receiver,ref video);}
                }
            }
            finally{audioStop.Cancel();await audio;}
        }
        finally{if(receiver!=IntPtr.Zero)NdiNative.NDIlib_recv_destroy(receiver);Marshal.FreeCoTaskMem(text);}
    }
    volatile string audioStatus="Silent";
    void ReceiveAudio(IntPtr receiver,CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try{ReceiveAudioSession(receiver,token);}
            catch(Exception e){audioStatus="звук: "+e.Message;if(token.WaitHandle.WaitOne(1000))return;}
        }
    }
    unsafe void ReceiveAudioSession(IntPtr receiver,CancellationToken token)
    {
        using var devices=new MMDeviceEnumerator();
        using var outputDevice=options.AudioMode!="Network"||string.IsNullOrEmpty(options.AudioDevice)?null:devices.GetDevice(options.AudioDevice);
        if(outputDevice!=null&&outputDevice.DataFlow!=DataFlow.Render)throw new IOException("Нужен выход виртуального кабеля (CABLE Input).");
        using var output=outputDevice==null?null:new WasapiOut(outputDevice,AudioClientShareMode.Shared,true,30);
        var buffer=new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000,2)){BufferDuration=TimeSpan.FromMilliseconds(160),DiscardOnBufferOverflow=true,ReadFully=true};
        output?.Init(buffer);output?.Play();long lastAudio=Environment.TickCount64;
        while(!token.IsCancellationRequested)
        {
            NdiNative.Audio frame=default;int kind=NdiNative.NDIlib_recv_capture_v2(receiver,IntPtr.Zero,ref frame,IntPtr.Zero,100);
            if(kind==4)throw new IOException("NDI отключён");
            if(kind!=2){if(output!=null&&Environment.TickCount64-lastAudio>2000)audioStatus="нет звука NDI; проверьте выход на игровом ПК";continue;}
            try
            {
                if(output==null){audioStatus=options.AudioMode=="Local"?"звук берётся Discord напрямую из выбранного входа":"Silent";continue;}
                if(frame.Rate!=48000||frame.Channels<1||frame.Channels>64||frame.Samples<1||frame.Samples>48000||frame.Data==IntPtr.Zero||frame.Stride<frame.Samples*4)throw new IOException("Нужен NDI-звук 48 кГц");
                var pcm=ConvertAudio(frame,options.Volume);
                // Prevent clock drift/reconnects from accumulating seconds of delay.
                if(buffer.BufferedDuration.TotalMilliseconds>100)buffer.ClearBuffer();
                buffer.AddSamples(pcm,0,pcm.Length);lastAudio=Environment.TickCount64;audioStatus="звук → виртуальный кабель";
            }
            finally{NdiNative.NDIlib_recv_free_audio_v2(receiver,ref frame);}
        }
    }
    internal static unsafe byte[] ConvertAudio(NdiNative.Audio frame,int volume)
    {
        var pcm=new byte[frame.Samples*8];float gain=Math.Clamp(volume,0,100)/1000f;
        fixed(byte* ptr=pcm)for(int i=0;i<frame.Samples;i++)for(int channel=0;channel<2;channel++)
        {float value=*((float*)((byte*)frame.Data+Math.Min(channel,frame.Channels-1)*frame.Stride)+i)*gain;((float*)ptr)[i*2+channel]=float.IsFinite(value)?Math.Clamp(value,-1,1):0;}
        return pcm;
    }
    public void Dispose(){stop.Cancel();task.GetAwaiter().GetResult();stop.Dispose();}
}

static class CameraInstallation
{
    public const string ClassId="{72984451-D4C4-46EB-A610-519DB1F6A820}";
    const string ClassKey=@"Software\Classes\CLSID\"+ClassId;
    const string DeviceKey=@"Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\"+ClassId;
    public static bool Installed{get{using var key=Registry.CurrentUser.OpenSubKey(ClassKey+@"\InprocServer32");return key?.GetValue(null) is string path&&File.Exists(path);}}
    public static string Name(string? configured=null)
    {
        if(!string.IsNullOrWhiteSpace(configured))return configured.Trim();
        using var key=Registry.CurrentUser.OpenSubKey(DeviceKey);return key?.GetValue("FriendlyName") as string??"ScreenCapture Camera";
    }
    public static void ValidateName(string name){if(string.IsNullOrWhiteSpace(name)||name.Length<4||name.Length>60||name.Any(char.IsControl))throw new ArgumentException("Имя камеры: от 4 до 60 символов, без переносов строк.");}
    public static void Install(string role,string? configured=null)
    {
        RequireReceiver(role);
        string name=Name(configured);ValidateName(name);
        string source=Path.Combine(AppContext.BaseDirectory,"camera","ScreenCapture.Camera.dll");
        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)))[..16];
        string directory=Path.Combine(Log.Folder,"Discord","Camera",hash);Directory.CreateDirectory(directory);
        string target=Path.Combine(directory,"ScreenCapture.Camera.dll");if(!File.Exists(target))File.Copy(source,target);
        using(var key=Registry.CurrentUser.CreateSubKey(ClassKey)){key.SetValue(null,name);}
        using(var key=Registry.CurrentUser.CreateSubKey(ClassKey+@"\InprocServer32")){key.SetValue(null,target);key.SetValue("ThreadingModel","Both");}
        using(var key=Registry.CurrentUser.CreateSubKey(DeviceKey)){key.SetValue("CLSID",ClassId);key.SetValue("FriendlyName",name);}
        // Native diagnostics contain only negotiation/counters; cap retained logs across Discord restarts.
        string logs=Path.Combine(Log.Folder,"Discord");
        foreach(var old in Directory.EnumerateFiles(logs,"camera-*.log",SearchOption.TopDirectoryOnly).Select(p=>new FileInfo(p)).OrderByDescending(f=>f.LastWriteTimeUtc).Skip(16))
            try{old.Delete();}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    public static void Uninstall(){Registry.CurrentUser.DeleteSubKeyTree(DeviceKey,false);Registry.CurrentUser.DeleteSubKeyTree(ClassKey,false);}
    public static void RequireReceiver(string role){if(role=="Host")throw new InvalidOperationException("На игровом управляющем ПК виртуальные устройства не устанавливаются. Откройте этот раздел на стрим-ПК.");}
}

static class NdiDiscovery
{
    public static string[] Sources()
    {
        NdiNative.EnsureInitialized();var config=new NdiNative.FindSettings{Local=true};IntPtr finder=NdiNative.NDIlib_find_create_v2(ref config);
        if(finder==IntPtr.Zero)throw new IOException("NDI discovery недоступен.");
        try
        {
            var names=new HashSet<string>();var deadline=Stopwatch.StartNew();
            while(deadline.ElapsedMilliseconds<1500){NdiNative.NDIlib_find_wait_for_sources(finder,250);IntPtr data=NdiNative.NDIlib_find_get_current_sources(finder,out uint count);if(count>4096)throw new IOException("Слишком много NDI-источников");for(int i=0;i<count;i++){var source=Marshal.PtrToStructure<NdiNative.Source>(data+i*Marshal.SizeOf<NdiNative.Source>());string? name=Marshal.PtrToStringUTF8(source.Name);if(!string.IsNullOrWhiteSpace(name))names.Add(name);}}
            return names.Order().ToArray();
        }
        finally{NdiNative.NDIlib_find_destroy(finder);}
    }
}
