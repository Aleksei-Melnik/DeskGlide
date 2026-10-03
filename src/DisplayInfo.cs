using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace SdrCapture;

public record DisplayInfo(uint Adapter, uint Output, string Device, string Gpu, int Width, int Height, bool Hdr, double WhiteNits)
{
    public static List<DisplayInfo> All()
    {
        var list = new List<DisplayInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        using (adapter)
        for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
        using (output)
        using (var output6 = output.QueryInterface<IDXGIOutput6>())
        {
            var d = output6.Description1;
            if (!d.AttachedToDesktop) continue;
            list.Add(new(a, o, d.DeviceName, adapter.Description1.Description,
                d.DesktopCoordinates.Right - d.DesktopCoordinates.Left,
                d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top,
                d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020,
                d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020 ? WhiteLevel.Read(d.DeviceName) : 80));
        }
        return list;
    }
}

// Documented DISPLAYCONFIG API. Reads settings; never changes the Windows slider.
public static class WhiteLevel
{
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] struct White { public Header Header; public uint Level; }
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr path, ref uint modes, IntPtr mode, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetName(ref SourceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetWhite(ref White white);
    public static double Read(string device)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            int rc = GetDisplayConfigBufferSizes(2, out var pc, out var mc);
            if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);
            var paths = Marshal.AllocHGlobal(checked((int)pc * 72));
            var modes = Marshal.AllocHGlobal(checked((int)mc * 64));
            try
            {
                rc = QueryDisplayConfig(2, ref pc, paths, ref mc, modes, IntPtr.Zero);
                if (rc == 122) continue;
                if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);
                for (int i = 0; i < pc; i++)
                {
                    var p = paths + i * 72;
                    var name = new SourceName { Header = new() { Type = 1, Size = 84,
                        Adapter = Marshal.PtrToStructure<Luid>(p), Id = (uint)Marshal.ReadInt32(p, 8) } };
                    if (GetName(ref name) != 0 || name.Name != device) continue;
                    var white = new White { Header = new() { Type = 11, Size = 24,
                        Adapter = Marshal.PtrToStructure<Luid>(p + 20), Id = (uint)Marshal.ReadInt32(p, 28) } };
                    rc = GetWhite(ref white);
                    if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);
                    if (white.Level < 1) throw new InvalidOperationException("Windows returned an invalid SDR white level.");
                    return white.Level * .08;
                }
                throw new InvalidOperationException("Display not found in active Windows display paths.");
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("Display configuration is changing. Retry capture.");
    }
}
