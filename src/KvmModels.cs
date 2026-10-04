using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace SdrCapture;

public sealed record KvmOptions
{
    public string Role {get;set;}="Off";
    public int Port {get;set;}=48460;
    public string Host {get;set;}="";
    public string PairingCode {get;set;}="";
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public bool Seamless {get;set;}=true;
    public uint OpenHotkeyModifiers {get;set;}=3;
    public uint OpenHotkeyKey {get;set;}=(uint)Keys.K;
    public uint ToggleHotkeyModifiers {get;set;}=3;
    public uint ToggleHotkeyKey {get;set;}=(uint)Keys.Pause;
    public bool ClipboardText {get;set;}=true;
    public bool ClipboardFiles {get;set;}=true;
    public string AudioDevice {get;set;}="";
    public bool AllowView {get;set;}=true;
    public bool RemoteViewOnly {get;set;}
    public List<string> RemoteOnlyPeers {get;set;}=[];
    public List<MonitorPlacement> Layout {get;set;}=[];
    public KvmOptions Copy()=>this with{Layout=Layout.Select(m=>m with{}).ToList(),RemoteOnlyPeers=RemoteOnlyPeers.ToList()};
    public void Validate()
    {
        if(Role is not ("Off" or "Host" or "Client"))throw new ArgumentException("Неизвестная роль KVM.");
        if(Port<1024||Port>65535)throw new ArgumentException("Порт KVM: 1024–65535.");
        if(!Guid.TryParseExact(Id,"N",out _))throw new ArgumentException("Повреждён идентификатор KVM.");
        if(RemoteOnlyPeers.Count>64||RemoteOnlyPeers.Any(id=>!Guid.TryParseExact(id,"N",out _)||id==Id))throw new ArgumentException("Недопустимый список серверов KVM.");
        if(Role=="Client"){if(string.IsNullOrWhiteSpace(Host))throw new ArgumentException("Введите имя или IP управляющего ПК.");KvmPairing.Decode(PairingCode);}
        if(Layout.Count>32||Layout.GroupBy(m=>m.Key).Any(g=>g.Count()>1))throw new ArgumentException("Недопустимая схема мониторов.");
        foreach(var m in Layout)
            if(Math.Abs((long)m.X)>100000||Math.Abs((long)m.Y)>100000||m.Width<1||m.Width>16384||m.Height<1||m.Height>16384||m.Hotkey<0||m.Hotkey>12)
                throw new ArgumentException("Недопустимое положение или размер монитора.");
        if(Layout.Where(m=>m.Hotkey>0).GroupBy(m=>m.Hotkey).Any(g=>g.Count()>1))throw new ArgumentException("Горячая клавиша назначена двум мониторам.");
        KvmShortcut.Validate(OpenHotkeyModifiers,OpenHotkeyKey);KvmShortcut.Validate(ToggleHotkeyModifiers,ToggleHotkeyKey);
        var shortcuts=Layout.Where(m=>m.Hotkey>0).Select(m=>(3u,(uint)Keys.F1+(uint)m.Hotkey-1)).Append((OpenHotkeyModifiers,OpenHotkeyKey)).Append((ToggleHotkeyModifiers,ToggleHotkeyKey)).Where(k=>k.Item2!=0).ToArray();
        if(shortcuts.Distinct().Count()!=shortcuts.Length||shortcuts.Contains((3u,(uint)Keys.Escape)))throw new ArgumentException("Горячие клавиши KVM должны отличаться друг от друга и от Ctrl+Alt+Esc.");
    }
}

public sealed record MonitorPlacement
{
    public string Peer {get;set;}="";
    public string Device {get;set;}="";
    public string Name {get;set;}="";
    public int X {get;set;}
    public int Y {get;set;}
    public int Width {get;set;}
    public int Height {get;set;}
    public int Hotkey {get;set;}
    [JsonIgnore] public string Key=>Peer+"|"+Device;
    [JsonIgnore] public Rectangle Bounds=>new(X,Y,Width,Height);
}
public sealed record KvmScreen(string Device,int X,int Y,int Width,int Height,bool Primary)
{
    public static KvmScreen[] Local()=>Screen.AllScreens.Select(s=>new KvmScreen(s.DeviceName,s.Bounds.X,s.Bounds.Y,s.Bounds.Width,s.Bounds.Height,s.Primary)).ToArray();
    [JsonIgnore] public Rectangle Bounds=>new(X,Y,Width,Height);
}
public sealed record KvmPeerInfo(string Id,string Name,KvmScreen[] Screens,string Version="",int UpdateProtocol=0,bool RemoteViewOnly=false,int ViewProtocol=0);

static class KvmPairing
{
    public static string Encode(byte[] fingerprint,byte[] key)=>Convert.ToBase64String(fingerprint.Concat(key).ToArray()).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static (byte[] Fingerprint,byte[] Key) Decode(string code)
    {
        try
        {
            string s=code.Trim().Replace('-','+').Replace('_','/');
            byte[] bytes=Convert.FromBase64String(s.PadRight((s.Length+3)/4*4,'='));
            if(bytes.Length!=64)throw new FormatException();
            return(bytes[..32],bytes[32..]);
        }
        catch{throw new ArgumentException("Код подключения неполный. Скопируйте весь код с управляющего ПК.");}
    }
}

static class KvmLayout
{
    public static (MonitorPlacement Target,Point Point)? EdgeCrossing(IEnumerable<MonitorPlacement> monitors,MonitorPlacement local,KvmScreen screen,Point point)
    {
        var logical=new Point(local.X+Math.Clamp(point.X-screen.X,0,local.Width-1),local.Y+Math.Clamp(point.Y-screen.Y,0,local.Height-1));
        var candidates=new List<Point>();
        if(point.X<=screen.X)candidates.Add(new(local.X-1,logical.Y));
        else if(point.X>=screen.X+screen.Width-1)candidates.Add(new(local.X+local.Width,logical.Y));
        if(point.Y<=screen.Y)candidates.Add(new(logical.X,local.Y-1));
        else if(point.Y>=screen.Y+screen.Height-1)candidates.Add(new(logical.X,local.Y+local.Height));
        foreach(var position in candidates){var target=At(monitors,position);if(target!=null&&target.Key!=local.Key)return(target,position);}
        return null;
    }
    public static List<MonitorPlacement> Merge(KvmOptions options,IEnumerable<KvmPeerInfo> peers)
    {
        var available=peers.ToArray();
        var remoteOnly=options.RemoteOnlyPeers.Concat(available.Where(p=>p.RemoteViewOnly&&p.Id!=options.Id).Select(p=>p.Id)).ToHashSet();
        var result=options.Layout.Where(m=>!remoteOnly.Contains(m.Peer)).Select(m=>m with{}).ToList();
        foreach(var peer in available.Where(p=>!remoteOnly.Contains(p.Id)))
        foreach(var screen in peer.Screens)
        {
            var found=result.FirstOrDefault(m=>m.Peer==peer.Id&&m.Device==screen.Device);
            if(found!=null){found.Width=screen.Width;found.Height=screen.Height;found.Name=peer.Name+" · "+screen.Device;continue;}
            int x=peer.Id==options.Id?screen.X:result.Count==0?0:result.Max(m=>m.X+m.Width);
            result.Add(new(){Peer=peer.Id,Device=screen.Device,Name=peer.Name+" · "+screen.Device,X=x,Y=peer.Id==options.Id?screen.Y:0,Width=screen.Width,Height=screen.Height});
        }
        return result;
    }
    public static MonitorPlacement? At(IEnumerable<MonitorPlacement> monitors,Point point)=>monitors.FirstOrDefault(m=>m.Bounds.Contains(point));
    public static Point Clamp(MonitorPlacement monitor,Point point)=>new(Math.Clamp(point.X,monitor.X,monitor.X+monitor.Width-1),Math.Clamp(point.Y,monitor.Y,monitor.Y+monitor.Height-1));
    public static Point ToPhysical(MonitorPlacement monitor,KvmScreen screen,Point point)=>new(screen.X+Math.Clamp(point.X-monitor.X,0,screen.Width-1),screen.Y+Math.Clamp(point.Y-monitor.Y,0,screen.Height-1));
}
