using System.Runtime.InteropServices;
using System.Buffers;
using System.Reflection;

namespace SdrCapture;

// Stable public NDI C ABI. Uses the user's installed NDI runtime; no HX/Advanced SDK.
internal static class NdiNative
{
    const string Dll="Processing.NDI.Lib.x64.dll";
    static bool initialized;
    static IntPtr nativeLibrary;
    static readonly object gate=new();
    public static string RuntimePath {get;private set;}="";
    static NdiNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(NdiNative).Assembly,(name,assembly,path)=>
        {
            if(name!=Dll) return IntPtr.Zero;
            var library=TryLoadRuntime();if(library!=IntPtr.Zero)return library;
            throw new DllNotFoundException(UiStrings.T("NDI Runtime is still unavailable. Retry setup in Screen streaming."));
        });
    }
    internal static IEnumerable<string> RuntimeCandidates(string programFiles,string appFolder,Func<string,EnvironmentVariableTarget,string?> readEnvironment)
    {
        foreach(string version in new[]{"V6","V5","V4"})
        foreach(var target in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine})
        {
            string? folder=readEnvironment("NDI_RUNTIME_DIR_"+version,target);
            if(!string.IsNullOrWhiteSpace(folder))yield return Path.Combine(folder,Dll);
        }
        foreach(string version in new[]{"NDI 6 Runtime","NDI 5 Runtime"})yield return Path.Combine(programFiles,"NDI",version,version.Contains('6')?"v6":"v5",Dll);
        foreach(string version in new[]{"NDI 6 Tools","NDI 5 Tools"})yield return Path.Combine(programFiles,"NDI",version,"Runtime",Dll);
        yield return Path.Combine(appFolder,Dll);
    }
    internal static IntPtr TryLoadRuntime()
    {
        lock(gate)
        {
            if(nativeLibrary!=IntPtr.Zero)return nativeLibrary;
            // The installer updates persistent environment variables; the running
            // process's environment still has its pre-install snapshot.
            foreach(string file in RuntimeCandidates(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),AppContext.BaseDirectory,Environment.GetEnvironmentVariable).Distinct(StringComparer.OrdinalIgnoreCase))
                if(File.Exists(file)&&NativeLibrary.TryLoad(file,out nativeLibrary)){RuntimePath=file;return nativeLibrary;}
            return IntPtr.Zero;
        }
    }
    public static void EnsureInitialized()
    {
        lock(gate)
        {
            if(initialized) return;
            if(Marshal.SizeOf<Video>()!=72 || Marshal.SizeOf<SendSettings>()!=24 || Marshal.SizeOf<RecvSettings>()!=40 || Marshal.SizeOf<Audio>()!=56)
                throw new InvalidOperationException("NDI x64 ABI mismatch");
            if(!NDIlib_initialize()) throw new InvalidOperationException("NDI initialization failed");
            initialized=true;
        }
    }
    public static void Shutdown(){lock(gate){if(initialized){NDIlib_destroy();initialized=false;}}}
    [StructLayout(LayoutKind.Sequential)] internal struct Source {public IntPtr Name,Url;}
    [StructLayout(LayoutKind.Sequential)] internal struct FindSettings {[MarshalAs(UnmanagedType.I1)]public bool Local;public IntPtr Groups,ExtraIps;}
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_find_create_v2(ref FindSettings settings);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void NDIlib_find_destroy(IntPtr finder);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool NDIlib_find_wait_for_sources(IntPtr finder,uint timeout);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_find_get_current_sources(IntPtr finder,out uint count);
    [StructLayout(LayoutKind.Sequential)] internal struct SendSettings
    {
        public IntPtr Name,Groups;
        [MarshalAs(UnmanagedType.I1)] public bool ClockVideo;
        [MarshalAs(UnmanagedType.I1)] public bool ClockAudio;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct RecvSettings
    {
        public Source Source;public int Color,Bandwidth;
        [MarshalAs(UnmanagedType.I1)] public bool Fields;
        public IntPtr Name;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Video
    {
        public int Width,Height,FourCC,RateN,RateD;public float Aspect;public int Format;
        public long Timecode;public IntPtr Data;public int Stride;public IntPtr Metadata;public long Timestamp;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Audio
    {
        public int Rate,Channels,Samples;public long Timecode;public IntPtr Data;
        public int Stride;public IntPtr Metadata;public long Timestamp;
    }
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void NDIlib_send_send_audio_v2(IntPtr sender,ref Audio frame);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int NDIlib_recv_capture_v2(IntPtr receiver,IntPtr video,ref Audio audio,IntPtr metadata,uint timeout);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void NDIlib_recv_free_audio_v2(IntPtr receiver,ref Audio audio);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] static extern bool NDIlib_initialize();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] static extern void NDIlib_destroy();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_send_create(ref SendSettings settings);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void NDIlib_send_destroy(IntPtr sender);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void NDIlib_send_send_video_v2(IntPtr sender,ref Video frame);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int NDIlib_send_get_no_connections(IntPtr sender,uint timeout);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_send_get_source_name(IntPtr sender);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr NDIlib_recv_create_v3(ref RecvSettings settings);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void NDIlib_recv_destroy(IntPtr receiver);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern int NDIlib_recv_capture_v3(IntPtr receiver,ref Video frame,IntPtr audio,IntPtr metadata,uint timeout);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] internal static extern void NDIlib_recv_free_video_v2(IntPtr receiver,ref Video frame);
}

public sealed class VideoBuffer:IDisposable
{
    sealed class Storage(int length){public readonly byte[] Data=ArrayPool<byte>.Shared.Rent(length);public int References=1;}
    readonly Storage storage;
    public byte[] Data=>storage.Data;
    public int Width {get;} public int Height {get;}
    public int Length=>checked(Width*Height*2);
    public long CapturedAt {get;set;}
    int disposed;
    public VideoBuffer(int width,int height)
    {
        if(width<2 || height<1 || (width&1)!=0) throw new NotSupportedException("NDI 4:2:2 requires an even monitor width; hidden scaling is disabled.");
        Width=width;Height=height;storage=new Storage(Length);
    }
    VideoBuffer(VideoBuffer source){Width=source.Width;Height=source.Height;CapturedAt=source.CapturedAt;storage=source.storage;}
    public VideoBuffer? Retain()
    {
        int count;
        do{count=Volatile.Read(ref storage.References);if(count==0)return null;}
        while(Interlocked.CompareExchange(ref storage.References,count+1,count)!=count);
        return new VideoBuffer(this);
    }
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0&&Interlocked.Decrement(ref storage.References)==0)ArrayPool<byte>.Shared.Return(storage.Data);}
}

sealed class NdiSender:IDisposable
{
    IntPtr handle;
    readonly long utcOrigin=DateTime.UtcNow.Ticks-DateTime.UnixEpoch.Ticks;
    readonly long sampleOrigin=AudioTimeline.Position();
    readonly NdiAudioOutput? audio;
    float[] planar=[];
    public string AudioStatus=>audio?.Status??"Silent";
    public string SourceName {get;}
    public NdiSender(string name,Func<string>? audioSource=null,Func<int>? volume=null)
    {
        NdiNative.EnsureInitialized();
        IntPtr text=Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var settings=new NdiNative.SendSettings{Name=text,ClockVideo=false,ClockAudio=false};
            handle=NdiNative.NDIlib_send_create(ref settings);
            if(handle==IntPtr.Zero) throw new InvalidOperationException("Could not create NDI sender");
            var source=Marshal.PtrToStructure<NdiNative.Source>(NdiNative.NDIlib_send_get_source_name(handle));
            SourceName=Marshal.PtrToStringUTF8(source.Name) ?? name;
        }
        finally{Marshal.FreeCoTaskMem(text);}
        if(audioSource!=null)audio=new NdiAudioOutput(this,audioSource,volume??(()=>100));
    }
    public int Connections=>NdiNative.NDIlib_send_get_no_connections(handle,0);
    public unsafe void Send(VideoBuffer buffer)
    {
        fixed(byte* data=buffer.Data)
        {
            var frame=new NdiNative.Video{Width=buffer.Width,Height=buffer.Height,FourCC=0x59565955,
                RateN=60,RateD=1,Aspect=(float)buffer.Width/buffer.Height,Format=1,Timecode=long.MaxValue,
                Data=(IntPtr)data,Stride=buffer.Width*2};
            // Synchronous call owns the pinned input only until return. Sender thread is independent
            // from capture, and capture owns one replaceable pending frame, never a growing FIFO.
            NdiNative.NDIlib_send_send_video_v2(handle,ref frame);
        }
    }
    public long AudioTimecode(long position)=>utcOrigin+(long)Math.Round((position-sampleOrigin)*(10000000.0/48000));
    public unsafe void SendAudio(ReadOnlySpan<float> stereo,long samplePosition,float volume=1)
    {
        int samples=stereo.Length/2;if(samples==0)return;volume=float.IsFinite(volume)?Math.Clamp(volume,0,1):0;
        if(planar.Length<samples*2)planar=new float[samples*2];
        // Windows normalized float -> NDI SMPTE reference (+20 dB headroom).
        for(int i=0;i<samples;i++){planar[i]=float.IsFinite(stereo[i*2])?stereo[i*2]*10*volume:0;planar[samples+i]=float.IsFinite(stereo[i*2+1])?stereo[i*2+1]*10*volume:0;}
        fixed(float* data=planar)
        {
            var frame=new NdiNative.Audio{Rate=48000,Channels=2,Samples=samples,Timecode=AudioTimecode(samplePosition),Data=(IntPtr)data,Stride=samples*4};
            NdiNative.NDIlib_send_send_audio_v2(handle,ref frame);
        }
    }
    public void Dispose(){audio?.Dispose();if(handle!=IntPtr.Zero){NdiNative.NDIlib_send_destroy(handle);handle=IntPtr.Zero;}}
}
