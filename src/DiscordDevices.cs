using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SdrCapture;

// Endpoint IDs survive a friendly-name change. Keep the vendor interface name intact.
static class DiscordDevices
{
    internal sealed record Endpoint(string Id,DataFlow Flow,string Name,string InterfaceName);
    internal sealed record DefaultEndpoint(DataFlow Flow,Role Role,string Id);
    sealed record InstallSnapshot(DateTimeOffset Created,long BootMilliseconds,DefaultEndpoint[] Defaults,bool Completed=false);
    static readonly object recoveryGate=new();
    static string Pending=>Path.Combine(Log.Folder,"Discord","audio-defaults-before-install.json");
    static long BootMilliseconds=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-Environment.TickCount64;
    public static bool IsCable(Endpoint endpoint)=>endpoint.InterfaceName.Equals("VB-Audio Virtual Cable",StringComparison.OrdinalIgnoreCase);
    public static List<Endpoint> Endpoints()
    {
        using var devices=new MMDeviceEnumerator();var result=new List<Endpoint>();
        foreach(var device in devices.EnumerateAudioEndPoints(DataFlow.All,DeviceState.Active))using(device)
            result.Add(new(device.ID,device.DataFlow,device.FriendlyName,device.DeviceFriendlyName));
        return result;
    }
    internal static DefaultEndpoint[] Defaults()
    {
        using var devices=new MMDeviceEnumerator();var result=new List<DefaultEndpoint>();
        foreach(var flow in new[]{DataFlow.Render,DataFlow.Capture})foreach(var role in new[]{Role.Console,Role.Multimedia,Role.Communications})
            try{using var device=devices.GetDefaultAudioEndpoint(flow,role);result.Add(new(flow,role,device.ID));}catch(COMException){}
        return result.ToArray();
    }
    internal static IEnumerable<DefaultEndpoint> RestorationPlan(IEnumerable<DefaultEndpoint> before,IEnumerable<DefaultEndpoint> current,IEnumerable<Endpoint> endpoints)
    {
        var available=endpoints.ToDictionary(d=>d.Id,StringComparer.OrdinalIgnoreCase);
        foreach(var original in before)
        {
            var now=current.FirstOrDefault(d=>d.Flow==original.Flow&&d.Role==original.Role);
            // Do not replace a deliberate switch to other speakers/microphone, or a pre-existing cable default.
            if(now!=null&&now.Id!=original.Id&&available.TryGetValue(now.Id,out var added)&&IsCable(added)
                &&available.TryGetValue(original.Id,out var previous)&&previous.Flow==original.Flow&&!IsCable(previous))yield return original;
        }
    }
    static void SaveSnapshot(InstallSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pending)!);File.WriteAllText(Pending+".tmp",JsonSerializer.Serialize(snapshot));File.Move(Pending+".tmp",Pending,true);
    }
    public static void BeforeInstall(string role)
    {
        CameraInstallation.RequireReceiver(role);
        lock(recoveryGate)SaveSnapshot(new(DateTimeOffset.UtcNow,BootMilliseconds,Defaults()));
    }
    public static void CancelBeforeLaunch(string role)
    {
        CameraInstallation.RequireReceiver(role);
        lock(recoveryGate)if(File.Exists(Pending))File.Delete(Pending);
    }
    public static async Task AfterInstall(string role)
    {
        CameraInstallation.RequireReceiver(role);
        // Device installation completes asynchronously, sometimes only after reboot.
        for(int i=0;i<15;i++){RecoverDefaults(role,false);await Task.Delay(1000);}
        lock(recoveryGate)if(File.Exists(Pending))
        {
            var snapshot=JsonSerializer.Deserialize<InstallSnapshot>(File.ReadAllText(Pending))!;
            SaveSnapshot(snapshot with{Completed=Endpoints().Any(IsCable)});
        }
    }
    public static void RecoverDefaults(string role,bool startup=true)
    {
        if(role=="Host")return;
        lock(recoveryGate)
        {
            if(!File.Exists(Pending))return;
            var snapshot=JsonSerializer.Deserialize<InstallSnapshot>(File.ReadAllText(Pending))??throw new IOException("Не удалось прочитать резервную копию аудиоустройств.");
            if(DateTimeOffset.UtcNow-snapshot.Created>TimeSpan.FromDays(7)){File.Delete(Pending);return;}
            bool rebooted=Math.Abs(BootMilliseconds-snapshot.BootMilliseconds)>10000;
            if(startup&&snapshot.Completed&&!rebooted)return;
            var endpoints=Endpoints();var plan=RestorationPlan(snapshot.Defaults,Defaults(),endpoints).ToArray();
            if(plan.Length>0)using(var policy=new AudioPolicy())foreach(var device in plan)policy.SetDefault(device.Id,device.Role);
            // Keep one recovery across the installer's requested reboot. Never keep overriding preferences.
            if(startup&&rebooted&&endpoints.Any(IsCable)&&snapshot.Defaults.All(d=>endpoints.Any(e=>e.Id==d.Id)))File.Delete(Pending);
        }
    }
    internal static string CaptureName(string cameraName)=>cameraName+" Audio (VB-CABLE)";
    internal static (Endpoint Render,Endpoint Capture) Pair(string renderId,IEnumerable<Endpoint> endpoints)
    {
        var cable=endpoints.Where(IsCable).ToArray();
        var render=cable.SingleOrDefault(e=>e.Flow==DataFlow.Render&&e.Id==renderId)??throw new IOException("Выбранный выход VB-CABLE недоступен.");
        var inputs=cable.Where(e=>e.Flow==DataFlow.Capture).ToArray();
        if(inputs.Length!=1)throw new IOException("Не удалось однозначно найти аудиовход VB-CABLE. Проверьте подключённые устройства.");
        return(render,inputs[0]);
    }
    public static void PairAudio(string role,string renderId,string cameraName)
    {
        CameraInstallation.RequireReceiver(role);CameraInstallation.ValidateName(cameraName);
        if(string.IsNullOrEmpty(renderId))return;
        var pair=Pair(renderId,Endpoints());
        using var policy=new AudioPolicy();
        // Chromium GuessVideoGroupID searches the entire camera label inside the audio label.
        // "ScreenCapture Audio" alone would NOT match "ScreenCapture Camera".
        if(pair.Capture.Name!=CaptureName(cameraName))policy.Rename(pair.Capture.Id,CaptureName(cameraName));
        string renderName=cameraName+" Send (VB-CABLE)";
        if(pair.Render.Name!=renderName)policy.Rename(pair.Render.Id,renderName);
        var updated=Endpoints();
        if(updated.Single(e=>e.Id==pair.Capture.Id).Name!=CaptureName(cameraName))throw new IOException("Windows не сохранила имя аудиовхода. Нажмите «Связать устройства» ещё раз.");
    }
}

// Windows 10/11 PolicyConfig ABI. Property methods include the bFxStore argument;
// the old Windows 7 declaration without it is not ABI-compatible on Windows 11.
// https://docs.rs/com-policy-config/latest/src/com_policy_config/lib.rs.html
sealed class AudioPolicy:IDisposable
{
    readonly IPolicyConfig policy=(IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"),true)!)!;
    public void SetDefault(string id,Role role)=>Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id,role));
    public void Rename(string id,string name)
    {
        var key=PropertyKeys.PKEY_Device_FriendlyName;
        var value=new Variant{Type=31,Pointer=Marshal.StringToCoTaskMemUni(name)};
        try{Marshal.ThrowExceptionForHR(policy.SetPropertyValue(id,0,ref key,ref value));}finally{Marshal.FreeCoTaskMem(value.Pointer);}
    }
    public string ReadName(string id)
    {
        var key=PropertyKeys.PKEY_Device_FriendlyName;Variant value=default;
        try{Marshal.ThrowExceptionForHR(policy.GetPropertyValue(id,0,ref key,ref value));return value.Type==31?Marshal.PtrToStringUni(value.Pointer)??"":"";}
        finally{PropVariantClear(ref value);}
    }
    public void Dispose()=>Marshal.ReleaseComObject(policy);
    [StructLayout(LayoutKind.Explicit,Size=24)] struct Variant{[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Pointer;}
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref Variant value);
    [ComImport,Guid("F8679F50-850A-41CF-9C72-430F290290C8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IPolicyConfig
    {
        void Reserved0();void Reserved1();void Reserved2();void Reserved3();void Reserved4();void Reserved5();void Reserved6();void Reserved7();
        [PreserveSig]int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)]string id,int fxStore,ref PropertyKey key,ref Variant value);
        [PreserveSig]int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)]string id,int fxStore,ref PropertyKey key,ref Variant value);
        [PreserveSig]int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)]string id,Role role);
        [PreserveSig]int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)]string id,short visible);
    }
}
