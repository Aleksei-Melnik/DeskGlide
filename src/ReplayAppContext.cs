using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SdrCapture;

static class ReplayAppContext
{
    static string last="Desktop";
    static double lastAt;
    static readonly Dictionary<string,string> known=new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2"]="Counter-Strike 2",["csgo"]="Counter-Strike Global Offensive",["dota2"]="Dota 2",
        ["VALORANT-Win64-Shipping"]="VALORANT",["FortniteClient-Win64-Shipping"]="Fortnite",
        ["r5apex"]="Apex Legends",["RustClient"]="Rust",["eldenring"]="ELDEN RING",
        ["Cyberpunk2077"]="Cyberpunk 2077",["GTA5"]="Grand Theft Auto V",["TslGame"]="PUBG"
    };
    public static string Capture()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(),out uint id);
            if(id==0)return "Desktop";
            using var process=Process.GetProcessById((int)id);
            string name=process.ProcessName;
            if(id==Environment.ProcessId||name.Equals("StreamDeck",StringComparison.OrdinalIgnoreCase)||name.Equals("ShellExperienceHost",StringComparison.OrdinalIgnoreCase))return ReplayRecorder.Now-lastAt<30?last:"Desktop";
            if(new[]{"explorer","SearchHost","StartMenuExperienceHost","ApplicationFrameHost","steam","steamwebhelper","EpicGamesLauncher","chrome","msedge","firefox","Discord","obs64","Codex"}.Contains(name,StringComparer.OrdinalIgnoreCase))return "Desktop";
            if(known.TryGetValue(name,out string? friendly))name=friendly;
            else try
            {
                string? path=process.MainModule?.FileName;
                string? product=path==null?null:FileVersionInfo.GetVersionInfo(path).ProductName;
                if(!string.IsNullOrWhiteSpace(product))name=product;
            }catch{}
            last=SafeName(name);lastAt=ReplayRecorder.Now;return last;
        }
        catch{return "Desktop";}
    }
    public static string SafeName(string name)
    {
        var invalid=Path.GetInvalidFileNameChars();
        string safe=new(name.Select(c=>invalid.Contains(c)||char.IsControl(c)?'_':c).ToArray());
        safe=safe.Trim().TrimEnd('.');if(safe.Length==0||safe is "." or "..")safe="Desktop";
        if(safe.Length>80)safe=safe[..80].TrimEnd('.',' ');
        string stem=safe.Split('.')[0];
        if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem,StringComparer.OrdinalIgnoreCase))safe="_"+safe;
        return safe;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
}
