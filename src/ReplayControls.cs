using System.Runtime.InteropServices;

namespace SdrCapture;

sealed class ReplayHotkey:NativeWindow,IDisposable
{
    public event Action? SaveRequested;
    public ReplayHotkey(){CreateHandle(new CreateParams{Caption="SdrCapture Replay Controls",Parent=new IntPtr(-3)});}
    public bool Set(uint modifiers,uint key){UnregisterHotKey(Handle,1);return RegisterHotKey(Handle,1,modifiers|0x4000,key);}
    protected override void WndProc(ref Message m){if(m.Msg==0x312&&m.WParam.ToInt32()==1)SaveRequested?.Invoke();base.WndProc(ref m);}
    public void Dispose(){UnregisterHotKey(Handle,1);DestroyHandle();}
    public static string Text(ReplayOptions options)=>string.Join(" + ",new[]{(options.HotkeyModifiers&2)!=0?"Ctrl":null,(options.HotkeyModifiers&1)!=0?"Alt":null,(options.HotkeyModifiers&4)!=0?"Shift":null,((Keys)options.HotkeyKey).ToString()}.Where(s=>s!=null));
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool RegisterHotKey(IntPtr handle,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterHotKey(IntPtr handle,int id);
}
