using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SdrCapture;

static class ReplayAppContext
{
    static readonly ReplayAppTimeline timeline=new();
    static readonly object gate=new();
    static IntPtr priorWindow;
    static uint priorPid;
    static string? priorApp;
    static double checkedAt;
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
            var window=GetForegroundWindow();GetWindowThreadProcessId(window,out uint id);
            if(id==0)return timeline.Last;
            double now=ReplayRecorder.Now;
            lock(gate)
            {
                if(window==priorWindow&&id==priorPid&&now-checkedAt<5)
                {if(priorApp!=null)timeline.Observe(priorApp,now);return timeline.Last;}
            }
            using var process=Process.GetProcessById((int)id);
            string? app=ReadApplication(process,window);
            lock(gate){priorWindow=window;priorPid=id;priorApp=app;checkedAt=now;}
            if(app!=null)timeline.Observe(app,now);return timeline.Last;
        }
        catch{return timeline.Last;}
    }
    static string? ReadApplication(Process owner,IntPtr window)
    {
        var className=new StringBuilder(256);GetClassName(window,className,className.Capacity);
        if(window!=IntPtr.Zero&&owner.ProcessName.Equals("ApplicationFrameHost",StringComparison.OrdinalIgnoreCase))
        {
            // Older packaged apps put their content in a child window owned by
            // the real app process. The frame host's product name is not the app.
            uint childId=0;
            EnumChildWindows(window,(child,_)=>
            {
                GetWindowThreadProcessId(child,out uint id);
                var kind=new StringBuilder(256);GetClassName(child,kind,kind.Capacity);
                if(id!=0&&id!=owner.Id&&kind.ToString()=="Windows.UI.Core.CoreWindow"){childId=id;return false;}return true;
            },IntPtr.Zero);
            if(childId!=0)try{using var child=Process.GetProcessById((int)childId);return ReadApplication(child,IntPtr.Zero);}catch{}
        }
        string? product=null;
        try{string? path=owner.MainModule?.FileName;if(path!=null)product=FileVersionInfo.GetVersionInfo(path).ProductName;}catch{}
        return ApplicationName(owner.ProcessName,product,className.ToString(),owner.Id==Environment.ProcessId);
    }
    internal static string? ApplicationName(string process,string? product,string windowClass="",bool own=false)
    {
        if(own||process.Equals("StreamDeck",StringComparison.OrdinalIgnoreCase))return null;
        if(new[]{"SearchHost","StartMenuExperienceHost","ShellExperienceHost"}.Contains(process,StringComparer.OrdinalIgnoreCase)||
            new[]{"Progman","WorkerW","Shell_TrayWnd","Shell_SecondaryTrayWnd"}.Contains(windowClass,StringComparer.OrdinalIgnoreCase))return "Desktop";
        if(process.Equals("explorer",StringComparison.OrdinalIgnoreCase))return "File Explorer";
        if(known.TryGetValue(process,out string? friendly))return friendly;
        return SafeName(string.IsNullOrWhiteSpace(product)?process:product);
    }
    public static string ForInterval(double start,double end)=>timeline.Resolve(start,end);
    internal static string Destination(ReplayOptions options,string app,string file)=>options.GroupByApp?Path.Combine(options.Folder,SafeName(app),file):Path.Combine(options.Folder,file);
    public static string SafeName(string name)
    {
        var invalid=Path.GetInvalidFileNameChars();
        string safe=new(name.Select(c=>invalid.Contains(c)||char.IsControl(c)?'_':c).ToArray());
        safe=safe.Trim().TrimEnd('.');if(safe.Length==0||safe is "." or "..")safe="Desktop";
        if(safe.Length>80)safe=safe[..80].TrimEnd('.',' ');
        string stem=safe.Split('.')[0];
        if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem,StringComparer.OrdinalIgnoreCase))safe="_"+safe[..Math.Min(79,safe.Length)].TrimEnd('.',' ');
        return safe;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int maximum);
    delegate bool EnumChildCallback(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,EnumChildCallback callback,IntPtr parameter);
}
