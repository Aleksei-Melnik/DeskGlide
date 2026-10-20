using System.Runtime.InteropServices;

namespace SdrCapture;

// A viewer lease is limited to the parent's own viewport and foreground
// window. The elevated input helper must never capture for an arbitrary HWND.
static class KvmKeyboardFocus
{
    [StructLayout(LayoutKind.Sequential)] struct GuiThread
    {
        public uint Size,Flags;
        public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;
        public int Left,Top,Right,Bottom;
    }
    internal static Func<long,long,int,bool>? TestOwned;
    internal static Func<long,long,bool>? TestFocused;
    internal static bool Owned(long window,long viewport,int process)
    {
        if(TestOwned!=null)return TestOwned(window,viewport,process);
        if(window<=0||viewport<=0)return false;
        var root=new IntPtr(window);var child=new IntPtr(viewport);
        return GetWindowThreadProcessId(root,out uint owner)!=0&&owner==process&&
            GetWindowThreadProcessId(child,out uint childOwner)!=0&&childOwner==process&&GetAncestor(child,2)==root;
    }
    internal static bool Focused(long window,long viewport,int process)
    {
        if(!Owned(window,viewport,process))return false;
        if(TestFocused!=null)return TestFocused(window,viewport);
        var root=new IntPtr(window);var child=new IntPtr(viewport);
        if(GetForegroundWindow()!=root)return false;
        uint thread=GetWindowThreadProcessId(root,out _);
        var info=new GuiThread{Size=(uint)Marshal.SizeOf<GuiThread>()};
        return GetGUIThreadInfo(thread,ref info)&&(info.Focus==child||IsChild(child,info.Focus));
    }
    internal static long Root(IntPtr viewport)=>GetAncestor(viewport,2).ToInt64();
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")]static extern bool IsChild(IntPtr parent,IntPtr child);
    [DllImport("user32.dll")]static extern bool GetGUIThreadInfo(uint thread,ref GuiThread info);
}

sealed record KvmViewerKeyboard(string Peer,long Window,long Viewport);
