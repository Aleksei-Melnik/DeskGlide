using System.Diagnostics;
using System.Globalization;

namespace SdrCapture;

public sealed record ReplayOptions
{
    public bool Enabled {get;set;}=false;
    public int Minutes {get;set;}=5;
    public int Fps {get;set;}=60;
    public string Quality {get;set;}="High";
    public string Codec {get;set;}="HEVC";
    public string AudioMode {get;set;}="Mixed";
    public string Folder {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"SdrCapture");
    public string Microphone {get;set;}="default:capture";
    public string GameAudio {get;set;}="default:render";
    public string ExtraAudio {get;set;}="";
    public bool Silent {get;set;}
    public bool GroupByApp {get;set;}=true;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsSilent=>AudioMode=="Silent"||Silent||(Microphone.Length==0&&GameAudio.Length==0&&ExtraAudio.Length==0);
    public int Width {get;set;}
    public int Height {get;set;}
    public uint HotkeyModifiers {get;set;}=3; // Ctrl + Alt
    public uint HotkeyKey {get;set;}=(uint)Keys.F10;
    public void Validate()
    {
        if(Minutes<5||Minutes>20) throw new ArgumentException("Replay duration must be between 5 and 20 minutes.");
        if(Codec is not ("HEVC" or "H264" or "AV1"))throw new ArgumentException("Unknown video codec.");
        if(AudioMode is not ("Mixed" or "Separate" or "Silent"))throw new ArgumentException("Unknown audio mode.");
        if(Fps is not (60 or 120)) throw new ArgumentException("Choose 60 or 120 FPS.");
        if(Quality is not ("Ultra" or "High" or "Medium" or "Low")) throw new ArgumentException("Unknown recording quality.");
        if(string.IsNullOrWhiteSpace(Folder)||!Path.IsPathFullyQualified(Folder)) throw new ArgumentException(@"Enter an absolute path, such as C:\Clips or \\STREAM-PC\Clips.");
        if(Width!=0 && (Width<320||Width>7680||Height<240||Height>4320||(Width&1)!=0||(Height&1)!=0)) throw new ArgumentException("Invalid recording resolution.");
        var chosen=new[]{Microphone,GameAudio,ExtraAudio}.Where(s=>s.Length>0).ToArray();
        if(chosen.Distinct().Count()!=chosen.Length) throw new ArgumentException("An audio source is selected twice. Choose different sources to avoid echo.");
    }
}

static class ReplayTools
{
    public static string Root=>Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT")??Path.Combine(Log.Folder,"Replay");
    public static string Ffmpeg=>Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg.exe");
    public static string Ffprobe=>Path.Combine(AppContext.BaseDirectory,"tools","ffprobe.exe");
    public static string Number(double value)=>value.ToString("0.######",CultureInfo.InvariantCulture);
    public static ProcessStartInfo StartInfo(string executable,IEnumerable<string> arguments)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }
    public static async Task<string> RunAsync(string executable,IEnumerable<string> arguments,CancellationToken token)
    {
        using var process=Process.Start(StartInfo(executable,arguments))??throw new IOException("Could not start FFmpeg.");
        using var registration=token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch{}});
        var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(token);
        string errors=await error;
        if(process.ExitCode!=0) throw new IOException($"FFmpeg: {errors[^Math.Min(errors.Length,1800)..]}");
        return await output;
    }
}
