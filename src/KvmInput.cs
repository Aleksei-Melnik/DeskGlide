using System.Runtime.InteropServices;

namespace SdrCapture;
static class KvmInput
{
    internal static readonly UIntPtr Marker=new(0x5344524B);
    static readonly object gate=new();
    static readonly Dictionary<int,KvmMessage> keys=new();
    [StructLayout(LayoutKind.Sequential)] internal struct PointNative {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct MouseInput {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] struct KeyInput {public ushort Vk,Scan;public uint Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Explicit)] struct Union {[FieldOffset(0)]public MouseInput Mouse;[FieldOffset(0)]public KeyInput Key;}
    [StructLayout(LayoutKind.Sequential)] struct Input {public uint Type;public Union Data;}
    public static void Inject(KvmMessage message)
    {
        lock(gate)
        {
            if(message.Type=="release"){ReleaseAll();return;}
            if(message.Type=="key")
            {
                if(message.Code<1||message.Code>254||message.X<0||message.X>255||(message.Flags&~3)!=0)return;
                bool up=(message.Flags&2)!=0;
                if(up)keys.Remove(message.Code);else keys[message.Code]=message;
                var input=new Input{Type=1,Data=new(){Key=new(){Vk=(ushort)(message.X==0?message.Code:0),Scan=(ushort)message.X,Flags=(uint)(message.Flags|(message.X!=0?8:0)),Extra=Marker}}};
                SendInput(1,[input],Marshal.SizeOf<Input>());
            }
            else if(message.Type=="mouse")
            {
                const int allowed=0x2|0x4|0x8|0x10|0x20|0x40|0x80|0x100|0x800|0x1000;
                if((message.Flags&~allowed)!=0)return;
                var bounds=SystemInformation.VirtualScreen;
                if(bounds.Width<2||bounds.Height<2)return;
                var input=new Input{Type=0,Data=new(){Mouse=new()
                {
                    X=(int)((long)(Math.Clamp(message.X,bounds.Left,bounds.Right-1)-bounds.Left)*65535/(bounds.Width-1)),
                    Y=(int)((long)(Math.Clamp(message.Y,bounds.Top,bounds.Bottom-1)-bounds.Top)*65535/(bounds.Height-1)),
                    Data=unchecked((uint)message.Code),Flags=0x8000|0x4000|1|(uint)message.Flags,Extra=Marker
                }}};
                SendInput(1,[input],Marshal.SizeOf<Input>());
            }
        }
    }
    public static void ReleaseAll()
    {
        lock(gate)
        {
            foreach(var key in keys.Values.ToArray())Inject(key with{Flags=key.Flags|2});keys.Clear();
            foreach(var pair in new[]{(4,0),(16,0),(64,0),(256,1),(256,2)})
            {
                var input=new Input{Data=new(){Mouse=new(){Flags=(uint)pair.Item1,Data=(uint)pair.Item2,Extra=Marker}}};
                SendInput(1,[input],Marshal.SizeOf<Input>());
            }
        }
    }
    [DllImport("user32.dll")] static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
}

sealed class KvmController:IDisposable
{
    delegate IntPtr Hook(int code,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MouseData {public KvmInput.PointNative Point;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] struct KeyData {public uint Vk,Scan,Flags,Time;public UIntPtr Extra;}
    readonly Hook mouseProc,keyProc;
    IntPtr mouseHook,keyHook;
    Control? inputDispatcher;
    Thread? inputThread;
    int disposed;
    readonly KvmService service;
    KvmOptions options;
    MonitorPlacement? remote;
    Point logical,anchor,returnPoint;
    long cooldown;
    readonly HashSet<int> swallowed=[];
    readonly HashSet<int> physicalKeys=[];
    List<MonitorPlacement> currentMonitors=[];
    Dictionary<string,KvmScreen> physicalScreens=[];
    KvmScreen[] localScreens=[];
    volatile bool seamless;
    public bool Seamless {get=>seamless;set=>seamless=value;}
    public event Action<bool>? SeamlessChanged;
    public event Action? EmergencyReturn;
    public KvmController(KvmService service,KvmOptions options)
    {
        this.service=service;this.options=options.Copy();Seamless=options.Seamless;
        Refresh();
        mouseProc=MouseHook;keyProc=KeyHook;
        if(options.Role=="Host")
        {
            using var ready=new ManualResetEventSlim();Exception? failure=null;bool started=false;
            inputThread=new Thread(()=>
            {
                try
                {
                    inputDispatcher=new Control();_=inputDispatcher.Handle;
                    mouseHook=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);
                    keyHook=SetWindowsHookEx(13,keyProc,GetModuleHandle(null),0);
                    if(mouseHook==IntPtr.Zero||keyHook==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    started=true;ready.Set();Application.Run();
                }
                catch(Exception e){failure=e;if(!started)ready.Set();else Log.Write("KVM input thread: "+e);}
                finally
                {
                    ReturnLocal();if(mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(mouseHook);if(keyHook!=IntPtr.Zero)UnhookWindowsHookEx(keyHook);
                    mouseHook=keyHook=IntPtr.Zero;inputDispatcher?.Dispose();
                }
            }){IsBackground=true,Name="KVM input",Priority=ThreadPriority.AboveNormal};
            inputThread.SetApartmentState(ApartmentState.STA);inputThread.Start();ready.Wait();
            if(failure!=null)throw failure;
        }
    }
    bool Dispatch(Action action)
    {
        if(inputDispatcher==null||!inputDispatcher.InvokeRequired)return false;
        if(Volatile.Read(ref disposed)==0)try{inputDispatcher.BeginInvoke(action);}catch(InvalidOperationException){}
        return true;
    }
    public List<MonitorPlacement> Monitors=>currentMonitors;
    public void Refresh()
    {
        if(Dispatch(Refresh))return;
        localScreens=KvmScreen.Local();
        var peers=new[]{new KvmPeerInfo(options.Id,Environment.MachineName,localScreens)}.Concat(service.Peers).ToArray();
        physicalScreens=peers.SelectMany(p=>p.Screens.Select(s=>(Key:p.Id+"|"+s.Device,Screen:s))).ToDictionary(p=>p.Key,p=>p.Screen);
        currentMonitors=KvmLayout.Merge(options,peers).Where(m=>physicalScreens.ContainsKey(m.Key)).ToList();
        if(remote!=null&&!physicalScreens.ContainsKey(remote.Key))ReturnLocal();
    }
    public void UpdateLayout(KvmOptions value){if(Dispatch(()=>UpdateLayout(value)))return;ReturnLocal();options=value.Copy();Seamless=value.Seamless;Refresh();}
    bool TryPhysical(MonitorPlacement monitor,Point position,out Point physical)
    {
        if(!physicalScreens.TryGetValue(monitor.Key,out var screen)){physical=default;return false;}
        physical=KvmLayout.ToPhysical(monitor,screen,position);return true;
    }
    public void Activate(MonitorPlacement target,Point? point=null)
    {
        if(Dispatch(()=>Activate(target,point)))return;
        var position=point??new Point(target.X+target.Width/2,target.Y+target.Height/2);
        if(!TryPhysical(target,position,out var physical))return;
        if(remote!=null)service.Send(remote.Peer,new(){Type="release"});
        if(target.Peer==options.Id){ReturnLocal();KvmInput.SetCursorPos(physical.X,physical.Y);return;}
        if(remote==null){returnPoint=Cursor.Position;anchor=Screen.FromPoint(returnPoint).Bounds.Location;var bounds=Screen.FromPoint(returnPoint).Bounds;anchor=new(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2);Cursor.Hide();}
        remote=target;logical=position;cooldown=Environment.TickCount64+250;
        KvmInput.SetCursorPos(anchor.X,anchor.Y);
        service.Send(target.Peer,new(){Type="mouse",X=physical.X,Y=physical.Y});
    }
    public void ReturnLocal()
    {
        if(Dispatch(ReturnLocal))return;
        if(remote==null)return;
        service.Send(remote.Peer,new(){Type="release"});remote=null;
        Cursor.Show();KvmInput.SetCursorPos(returnPoint.X,returnPoint.Y);cooldown=Environment.TickCount64+400;
    }
    IntPtr MouseHook(int code,IntPtr w,IntPtr l)
    {
        if(code<0)return CallNextHookEx(IntPtr.Zero,code,w,l);
        var data=Marshal.PtrToStructure<MouseData>(l);int message=w.ToInt32();
        if(data.Extra==KvmInput.Marker||(data.Flags&1)!=0)return CallNextHookEx(IntPtr.Zero,code,w,l);
        var point=new Point(data.Point.X,data.Point.Y);
        try
        {
            if(remote==null)
            {
                if(message==0x200&&Seamless&&Environment.TickCount64>cooldown&&!(Down(1)||Down(2)||Down(4)))
                {
                    var screen=localScreens.FirstOrDefault(s=>s.Bounds.Contains(point));
                    var local=Monitors.FirstOrDefault(m=>m.Peer==options.Id&&m.Device==screen?.Device);
                    if(local!=null&&screen!=null)
                    {
                        var logicalPoint=new Point(local.X+point.X-screen.Bounds.X,local.Y+point.Y-screen.Bounds.Y);
                        if(point.X<=screen.Bounds.Left)logicalPoint.X=local.X-1;
                        else if(point.X>=screen.Bounds.Right-1)logicalPoint.X=local.X+local.Width;
                        if(point.Y<=screen.Bounds.Top)logicalPoint.Y=local.Y-1;
                        else if(point.Y>=screen.Bounds.Bottom-1)logicalPoint.Y=local.Y+local.Height;
                        var target=KvmLayout.At(Monitors,logicalPoint);
                        if(target!=null&&target.Peer!=options.Id){Activate(target,logicalPoint);return (IntPtr)1;}
                    }
                }
                return CallNextHookEx(IntPtr.Zero,code,w,l);
            }
            if(message==0x200)
            {
                if(point==anchor)return (IntPtr)1;
                var candidate=new Point(logical.X+point.X-anchor.X,logical.Y+point.Y-anchor.Y);
                var target=Seamless&&Environment.TickCount64>cooldown?KvmLayout.At(Monitors,candidate):null;
                if(target!=null&&target.Key!=remote.Key){Activate(target,candidate);return (IntPtr)1;}
                logical=KvmLayout.Clamp(remote,candidate);KvmInput.SetCursorPos(anchor.X,anchor.Y);
            }
            if(!TryPhysical(remote,logical,out var physical)){ReturnLocal();return (IntPtr)1;}
            int flags=message switch{0x201=>2,0x202=>4,0x204=>8,0x205=>16,0x207=>32,0x208=>64,0x20B=>128,0x20C=>256,0x20A=>2048,0x20E=>4096,_=>0};
            int mouseData=message is 0x20A or 0x20E?(short)(data.Data>>16):message is 0x20B or 0x20C?(int)(data.Data>>16):0;
            if(!service.Send(remote.Peer,new(){Type="mouse",X=physical.X,Y=physical.Y,Flags=flags,Code=mouseData}))ReturnLocal();
            return (IntPtr)1;
        }
        catch{ReturnLocal();return CallNextHookEx(IntPtr.Zero,code,w,l);}
    }
    static bool Down(int key)=>(KvmInput.GetAsyncKeyState(key)&0x8000)!=0;
    IntPtr KeyHook(int code,IntPtr w,IntPtr l)
    {
        if(code<0)return CallNextHookEx(IntPtr.Zero,code,w,l);
        var data=Marshal.PtrToStructure<KeyData>(l);
        if(data.Extra==KvmInput.Marker||(data.Flags&16)!=0)return CallNextHookEx(IntPtr.Zero,code,w,l);
        int key=(int)data.Vk;bool up=(data.Flags&128)!=0;
        if(up)physicalKeys.Remove(key);else physicalKeys.Add(key);
        if(up&&swallowed.Remove(key))return (IntPtr)1;
        bool control=physicalKeys.Any(k=>k is 17 or 162 or 163),alt=physicalKeys.Any(k=>k is 18 or 164 or 165);
        if(!up&&control&&alt)
        {
            if(key==27){ReturnLocal();EmergencyReturn?.Invoke();swallowed.Add(key);return (IntPtr)1;}
            if(key==19){Seamless=!Seamless;SeamlessChanged?.Invoke(Seamless);swallowed.Add(key);return (IntPtr)1;}
            var target=Monitors.FirstOrDefault(m=>m.Hotkey>0&&key==(int)Keys.F1+m.Hotkey-1);
            if(target!=null){Activate(target);swallowed.Add(key);return (IntPtr)1;}
        }
        if(remote==null)return CallNextHookEx(IntPtr.Zero,code,w,l);
        if(!service.Send(remote.Peer,new(){Type="key",Code=key,X=(int)data.Scan,Flags=(up?2:0)|((data.Flags&1)!=0?1:0)}))ReturnLocal();
        return (IntPtr)1;
    }
    public void Dispose()
    {
        if(inputThread!=null&&Thread.CurrentThread!=inputThread)
        {if(Dispatch(Dispose))inputThread.Join(1500);return;}
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        ReturnLocal();if(inputThread!=null)Application.ExitThread();
    }
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
