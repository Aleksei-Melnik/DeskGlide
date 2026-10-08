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
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
