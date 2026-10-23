using System.Diagnostics;
using System.Globalization;

namespace SdrCapture;

public sealed record ReplayOptions
{
    public bool Enabled {get;set;}=false;
    public int Minutes {get;set;}=5;
    // Zero means an older profile still uses Minutes. New profiles store seconds.
    public int DurationSeconds {get;set;}
    [System.Text.Json.Serialization.JsonIgnore] public int BufferSeconds
    {
        get
        {
            int seconds=DurationSeconds==0?(int)Math.Clamp((long)Minutes*60,int.MinValue,int.MaxValue):DurationSeconds;
            // Preserve 0.7.12 profiles while applying the new one-minute minimum.
            return seconds is >=30 and <60?60:seconds;
        }
    }
    public bool SaveSoundEnabled {get;set;}=true;
    public int SaveSoundVolume {get;set;}=25;
    public int Fps {get;set;}=60;
    public string Quality {get;set;}="High";
    public string Codec {get;set;}="HEVC";
    // GPU preserves the hardware-only behavior of older profiles. CPU is an
    // explicit libx264 choice; it is never an automatic hardware fallback.
    public string Encoder {get;set;}="GPU";
    public string RateControl {get;set;}="Quality";
    // Zero retains the automatic limit derived from quality, resolution and FPS.
    public int BitrateMbps {get;set;}
    public int KeyframeSeconds {get;set;}=2;
    public string EncoderPreset {get;set;}="Balanced";
    // Empty selects this PC; a paired peer's GUID-N identity selects its encoder.
    public string EncodingComputer {get;set;}="";
    public string AudioMode {get;set;}="Mixed";
    public string Folder {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"SdrCapture");
    public string Microphone {get;set;}="default:capture";
    public string GameAudio {get;set;}="default:render";
    public string ExtraAudio {get;set;}="";
    public bool Silent {get;set;}
    public bool GroupByApp {get;set;}=true;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsSilent=>AudioMode=="Silent"||Silent||(string.IsNullOrEmpty(Microphone)&&string.IsNullOrEmpty(GameAudio)&&string.IsNullOrEmpty(ExtraAudio));
    public int Width {get;set;}
    public int Height {get;set;}
    public uint HotkeyModifiers {get;set;}=3; // Ctrl + Alt
    public uint HotkeyKey {get;set;}=(uint)Keys.F10;
    public void Validate()
    {
        if(BufferSeconds is <60 or >7200) throw new ArgumentException(UiStrings.T("Replay duration must be between 1 minute and 2 hours."));
        if(SaveSoundVolume is <0 or >100)throw new ArgumentException(UiStrings.T("Sound volume must be between 0 and 100%."));
        if(Codec is not ("HEVC" or "H264" or "AV1"))throw new ArgumentException("Неизвестный видеокодек.");
        if(Encoder is not ("GPU" or "CPU"))throw new ArgumentException(UiStrings.T("Choose GPU hardware encoding or CPU x264 encoding."));
        if(Encoder=="CPU"&&Codec!="H264")throw new ArgumentException(UiStrings.T("CPU x264 encoding requires the H.264 codec."));
        if(AudioMode is not ("Mixed" or "Separate" or "Silent"))throw new ArgumentException("Неизвестный режим аудио.");
        if(Fps is not (60 or 120)) throw new ArgumentException("Выберите 60 или 120 FPS.");
        if(Quality is not ("Ultra" or "High" or "Medium" or "Low")) throw new ArgumentException("Неизвестное качество записи.");
        if(RateControl is not ("Quality" or "VBR" or "CBR"))throw new ArgumentException("Unknown recording rate control.");
        if(BitrateMbps is <0 or >1000)throw new ArgumentException("Recording bitrate must be automatic or between 1 and 1000 Mbps.");
        if(KeyframeSeconds is <1 or >10)throw new ArgumentException("Recording keyframe interval must be between 1 and 10 seconds.");
        if(EncoderPreset is not ("Performance" or "Balanced" or "Quality"))throw new ArgumentException("Unknown recording encoder preset.");
        if(EncodingComputer is null||(EncodingComputer.Length>0&&!Guid.TryParseExact(EncodingComputer,"N",out _)))throw new ArgumentException("Invalid recording computer identity.");
        if(string.IsNullOrWhiteSpace(Folder)||!Path.IsPathFullyQualified(Folder)) throw new ArgumentException(@"Укажите полный путь, например C:\Clips или \\STREAM-PC\Clips.");
        if((Width==0&&Height!=0)||(Width!=0 && (Width<320||Width>7680||Height<240||Height>4320||(Width&1)!=0||(Height&1)!=0))) throw new ArgumentException("Недопустимое разрешение записи.");
        if(Microphone==null||GameAudio==null||ExtraAudio==null)throw new ArgumentException(UiStrings.T("Recording audio source identifiers cannot be null."));
        var chosen=new[]{Microphone,GameAudio,ExtraAudio}.Where(s=>s.Length>0).ToArray();
        if(chosen.Distinct().Count()!=chosen.Length) throw new ArgumentException("Один источник звука выбран дважды — это создаст эхо.");
    }
}

static class ReplayTime
{
    public static int SliderMinutes(int seconds)=>(int)Math.Clamp(Math.Ceiling(seconds/60.0),1,120);
    public static string DurationLabel(int minutes)
    {
        if(minutes<60)return UiStrings.F("{0} min",minutes);
        return minutes%60==0?UiStrings.F("{0} h",minutes/60):UiStrings.F("{0} h {1} min",minutes/60,minutes%60);
    }
    public static string Format(double seconds)
    {
        var time=TimeSpan.FromSeconds(Math.Max(0,seconds));
        return time.TotalHours>=1?$"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}":$"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}

static class ReplayTools
{
    public static string Root=>Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT")??Path.Combine(Log.Folder,"Replay");
    public static string Ffmpeg=>Path.Combine(RecordingTools.DirectoryPath??RecordingTools.SharedDirectory,"ffmpeg.exe");
    public static string Ffprobe=>Path.Combine(RecordingTools.DirectoryPath??RecordingTools.SharedDirectory,"ffprobe.exe");
    public static string Number(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
    public static ProcessStartInfo StartInfo(string executable,IEnumerable<string> arguments)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }
    public static async Task<string> RunAsync(string executable,IEnumerable<string> arguments,CancellationToken token)
    {
        using var process=Process.Start(StartInfo(executable,arguments))??throw new IOException("Не удалось запустить FFmpeg.");
        using var registration=token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch{}});
        var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        string errors=await error;
        if(process.ExitCode!=0) throw new IOException($"FFmpeg: {errors[^Math.Min(errors.Length,1800)..]}");
        return await output;
    }
}
