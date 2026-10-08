using System.Runtime.InteropServices;

namespace SdrCapture;
static class PreferredResolution
{
    [StructLayout(LayoutKind.Sequential)] struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)] struct Header{public uint Type,Size;public Luid Adapter;public uint Id;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Source{public Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;}
    [StructLayout(LayoutKind.Sequential)] struct Preferred{public Header Header;public uint Width,Height;public ulong S0,S1,S2,S3,S4,S5;}
    [DllImport("user32.dll")]static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")]static extern int QueryDisplayConfig(uint flags,ref uint paths,IntPtr path,ref uint modes,IntPtr mode,IntPtr topology);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]static extern int Name(ref Source source);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]static extern int Mode(ref Preferred target);
    public static (int Width,int Height) Read(string device,int fallbackWidth,int fallbackHeight)
    {
        for(int attempt=0;attempt<3;attempt++)
        {
            if(GetDisplayConfigBufferSizes(2,out uint paths,out uint modes)!=0)break;
            IntPtr p=Marshal.AllocHGlobal(checked((int)paths*72)),m=Marshal.AllocHGlobal(checked((int)modes*64));
            try
            {
                int result=QueryDisplayConfig(2,ref paths,p,ref modes,m,IntPtr.Zero);if(result==122)continue;if(result!=0)break;
                for(int i=0;i<paths;i++)
                {
                    var path=p+i*72;
                    var source=new Source{Header=new(){Type=1,Size=84,Adapter=Marshal.PtrToStructure<Luid>(path),Id=(uint)Marshal.ReadInt32(path,8)}};
                    if(Name(ref source)!=0||source.Name!=device)continue;
                    var preferred=new Preferred{Header=new(){Type=3,Size=(uint)Marshal.SizeOf<Preferred>(),Adapter=Marshal.PtrToStructure<Luid>(path+20),Id=(uint)Marshal.ReadInt32(path,28)}};
                    if(Mode(ref preferred)==0&&preferred.Width is >=2 and <=8192&&preferred.Height is >=2 and <=8192)
                        return((int)preferred.Width&~1,(int)preferred.Height);
                }
            }
            finally{Marshal.FreeHGlobal(p);Marshal.FreeHGlobal(m);}
        }
        return(fallbackWidth&~1,fallbackHeight);
    }
}
