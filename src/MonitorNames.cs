using System.Runtime.InteropServices;
namespace SdrCapture;
static class MonitorNames
{
    public sealed record Entry(string Device,string Label);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct DeviceInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Id;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Key;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool EnumDisplayDevices(string? name,uint index,ref DeviceInfo device,uint flags);
    public static Entry[] Choices()=>Screen.AllScreens.Select(s=>
    {
        var d=new DeviceInfo{Size=(uint)Marshal.SizeOf<DeviceInfo>()};
        string name=EnumDisplayDevices(s.DeviceName,0,ref d,0)&&!string.IsNullOrWhiteSpace(d.Description)?d.Description:s.DeviceName.Replace(@"\\.\","");
        var native=PreferredResolution.Read(s.DeviceName,s.Bounds.Width,s.Bounds.Height);
        return new Entry(s.DeviceName,$"{name} · {native.Width} × {native.Height} · {s.DeviceName.Replace(@"\\.\","")}");
    }).ToArray();
}
