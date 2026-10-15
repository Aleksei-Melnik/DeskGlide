using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace SdrCapture;

enum KvmInputBlock { None, Administrator, Desktop, Unavailable }

// Read-only checks. Never switches desktops, disables UAC or changes another app.
static class KvmInputAccess
{
    static readonly object gate=new();
    static readonly bool elevated=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    static IntPtr foreground;
    static KvmInputBlock cached;
    static long checkedAt;
    public static bool Elevated=>elevated;
    internal static Func<KvmInputBlock>? TestProbe;
    public static KvmInputBlock Check()
    {
        if(TestProbe!=null)return TestProbe();
        lock(gate)
        {
            long now=Environment.TickCount64;var window=GetForegroundWindow();
            if(window==foreground&&now-checkedAt<50)return cached;
            foreground=window;checkedAt=now;
            var desktop=OpenInputDesktop(0,false,1);
            if(desktop==IntPtr.Zero)return cached=KvmInputBlock.Desktop;
            try
            {
                string? input=Name(desktop),current=Name(GetThreadDesktop(GetCurrentThreadId()));
                if(input==null||current==null||input!=current)return cached=KvmInputBlock.Desktop;
            }
            finally{CloseDesktop(desktop);}
            if(elevated||window==IntPtr.Zero)return cached=KvmInputBlock.None;
            GetWindowThreadProcessId(window,out uint pid);
            if(pid==Environment.ProcessId)return cached=KvmInputBlock.None;
            var process=OpenProcess(0x1000,false,pid);
            if(process==IntPtr.Zero)return cached=KvmInputBlock.Unavailable;
            try
            {
                if(!OpenProcessToken(process,8,out var token))return cached=KvmInputBlock.Unavailable;
                try
                {
                    if(!GetTokenInformation(token,20,out int high,4,out _))return cached=KvmInputBlock.Unavailable;
                    return cached=high!=0?KvmInputBlock.Administrator:KvmInputBlock.None;
                }
                finally{CloseHandle(token);}
            }
            finally{CloseHandle(process);}
        }
    }
    static string? Name(IntPtr handle)
    {
        var text=new StringBuilder(256);
        return GetUserObjectInformation(handle,2,text,512,out _)?text.ToString():null;
    }
    public static string Describe(KvmInputBlock reason)=>reason switch
    {
        KvmInputBlock.Administrator=>"An administrator app has focus. Enable administrator app control in KVM settings.",
        KvmInputBlock.Desktop=>"Windows is showing a protected desktop. Control has returned to this PC.",
        _=>"KVM could not retain input control. Control has returned to this PC."
    };
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",SetLastError=true)]static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")]static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")]static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetUserObjectInformationW")]static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int length,out uint needed);
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll")]static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll")]static extern bool GetTokenInformation(IntPtr token,int info,out int data,int length,out int needed);
}

interface IKvmInputTransport
{
    KvmPeerInfo[] Peers {get;}
    bool Send(string peer,KvmMessage message);
}
