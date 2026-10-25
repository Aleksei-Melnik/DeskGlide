using System.Diagnostics;
using System.Runtime.InteropServices;
namespace SdrCapture;
static class WindowActivation
{
    public static void Show(Form form)
    {
        if(form.IsDisposed)throw new ObjectDisposedException(form.Name);
        if(!form.Visible)form.Show();
        if(form.WindowState==FormWindowState.Minimized)form.WindowState=FormWindowState.Normal;
        if(!Screen.AllScreens.Any(s=>Rectangle.Intersect(s.WorkingArea,form.Bounds) is {Width:>=100,Height:>=60}))
        {var area=Screen.FromPoint(Cursor.Position).WorkingArea;form.Location=new(area.Left+Math.Max(0,(area.Width-form.Width)/2),area.Top+Math.Max(0,(area.Height-form.Height)/2));}
        form.BringToFront();form.Activate();SetForegroundWindow(form.Handle);
    }
    public static void AllowExisting()
    {
        foreach(string name in new[]{"DeskGlide","ScreenCapture","SdrCapture"})foreach(var process in Process.GetProcessesByName(name))using(process)
            try{if(process.Id!=Environment.ProcessId&&process.SessionId==Process.GetCurrentProcess().SessionId&&Path.GetDirectoryName(process.MainModule?.FileName)==AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))AllowSetForegroundWindow(process.Id);}catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}
    }
    internal static bool ForegroundIsFullscreen()
    {
        var window=GetForegroundWindow();
        if(window==IntPtr.Zero||window==GetShellWindow()||window==GetDesktopWindow()||IsIconic(window)||!GetClientRect(window,out var client))return false;
        var origin=new NativePoint();if(!ClientToScreen(window,ref origin))return false;
        var monitor=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(MonitorFromWindow(window,2),ref monitor))return false;
        var name=new System.Text.StringBuilder(256);GetClassNameW(window,name,name.Capacity);
        return IsFullscreenClient(name.ToString(),new(origin.X,origin.Y,client.Right,client.Bottom),
            Rectangle.FromLTRB(monitor.Bounds.Left,monitor.Bounds.Top,monitor.Bounds.Right,monitor.Bounds.Bottom));
    }
    // Compare the client area to the entire monitor, not its work area.
    // A maximized browser still reserves its caption and/or the taskbar.
    internal static bool IsFullscreenClient(string windowClass,Rectangle client,Rectangle monitor)=>
        !string.Equals(windowClass,"WorkerW",StringComparison.OrdinalIgnoreCase)&&!string.Equals(windowClass,"Progman",StringComparison.OrdinalIgnoreCase)&&
        client.Left<=monitor.Left&&client.Top<=monitor.Top&&client.Right>=monitor.Right&&client.Bottom>=monitor.Bottom;
    internal static void KeepKvmBehindFullscreen(IntPtr window)=>SetWindowPos(window,(IntPtr)1,0,0,0,0,0x13); // HWND_BOTTOM; no activation, move or resize.
    internal static void RaiseKvm(IntPtr window)
    {
        if(ForegroundIsFullscreen()){KeepKvmBehindFullscreen(window);return;}
        // IPC callbacks may lack foreground permission. Lift once, then return
        // immediately to the ordinary window band; never stay always-on-top.
        try{SetWindowPos(window,(IntPtr)(-1),0,0,0,0,0x13);}
        finally{SetWindowPos(window,(IntPtr)(-2),0,0,0,0,0x13);}
        if(ForegroundIsFullscreen())KeepKvmBehindFullscreen(window);
        else SetForegroundWindow(window);
    }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo{public int Size;public NativeRect Bounds,WorkArea;public uint Flags;}
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr window,System.Text.StringBuilder name,int maximum);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window,out NativeRect rectangle);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window,ref NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
