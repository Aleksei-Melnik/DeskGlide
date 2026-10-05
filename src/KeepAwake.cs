using System.Runtime.InteropServices;
namespace SdrCapture;

// Owned by the WinForms UI thread. Windows releases this request when the thread exits.
sealed class KeepAwake:IDisposable
{
    internal const uint Continuous=0x80000000,System=1,Display=2;
    readonly int thread=Environment.CurrentManagedThreadId;
    public bool Enabled {get;private set;}
    public void Set(bool enabled)
    {
        if(Environment.CurrentManagedThreadId!=thread)throw new InvalidOperationException("Power request must be changed on its owning thread.");
        if(Request(Continuous|(enabled?System|Display:0))==0)throw new InvalidOperationException("Windows could not enable idle-sleep protection.");
        Enabled=enabled;
    }
    public void Dispose()
    {
        if(!Enabled)return;
        try{Set(false);}catch(Exception e){Log.Write("Release idle-sleep request: "+e.Message);}
    }
    [DllImport("kernel32.dll",EntryPoint="SetThreadExecutionState")]internal static extern uint Request(uint flags);
}
