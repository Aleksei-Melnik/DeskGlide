using System.Runtime.InteropServices;

namespace SdrCapture;
static class KvmInput
{
    internal static readonly UIntPtr Marker=new(0x5344524B);
    static readonly object gate=new();
    static readonly Dictionary<int,KvmMessage> keys=new();
    static readonly KvmMouseButtons buttons=new();
    static bool releasePending;
    internal static Func<KvmMessage,bool>? TestSend;
    [StructLayout(LayoutKind.Sequential)] internal struct PointNative {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct MouseInput {public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] struct KeyInput {public ushort Vk,Scan;public uint Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Explicit)] struct Union {[FieldOffset(0)]public MouseInput Mouse;[FieldOffset(0)]public KeyInput Key;}
    [StructLayout(LayoutKind.Sequential)] struct Input {public uint Type;public Union Data;}
    internal static bool Valid(KvmMessage message)=>message.Type=="release"||
        message.Type=="key"&&message.Code is >=1 and <=254&&message.X is >=0 and <=255&&(message.Flags&~3)==0||
        message.Type=="mouse"&&(message.Flags&~(0x2|0x4|0x8|0x10|0x20|0x40|0x80|0x100|0x800|0x1000))==0&&((message.Flags&(0x80|0x100))==0||message.Code is 1 or 2);
    public static bool Inject(KvmMessage message)
    {
        if(!Valid(message))return false;
        lock(gate)
        {
            if(message.Type=="release")return ReleaseAll();
            if(KvmInputAccess.Check()!=KvmInputBlock.None){releasePending=true;return false;}
            if(releasePending&&!ReleaseAll())return false;
            bool sent=Send(message);
            if(!sent){releasePending=true;return false;}
            if(message.Type=="key")
            {
                bool up=(message.Flags&2)!=0;
                if(up)keys.Remove(message.Code);else keys[message.Code]=message;
            }
            else buttons.Track(message.Flags,message.Code);
            return true;
        }
    }
    static bool Send(KvmMessage message,bool move=true)
    {
        if(TestSend!=null)return TestSend(message);
        Input input;
        if(message.Type=="key")input=new(){Type=1,Data=new(){Key=new(){Vk=(ushort)(message.X==0?message.Code:0),Scan=(ushort)message.X,Flags=(uint)(message.Flags|(message.X!=0?8:0)),Extra=Marker}}};
        else
        {
            var bounds=SystemInformation.VirtualScreen;
            if(bounds.Width<2||bounds.Height<2)return false;
            input=new(){Data=new(){Mouse=new(){
                X=move?(int)((long)(Math.Clamp(message.X,bounds.Left,bounds.Right-1)-bounds.Left)*65535/(bounds.Width-1)):0,
                Y=move?(int)((long)(Math.Clamp(message.Y,bounds.Top,bounds.Bottom-1)-bounds.Top)*65535/(bounds.Height-1)):0,
                Data=unchecked((uint)message.Code),Flags=(move?0x8000u|0x4000u|1u:0)|(uint)message.Flags,Extra=Marker}}};
        }
        return SendInput(1,[input],Marshal.SizeOf<Input>())==1;
    }
    public static bool ReleaseAll()
    {
        lock(gate)
        {
            if(keys.Count==0&&buttons.Count==0){releasePending=false;return true;}
            if(KvmInputAccess.Check()!=KvmInputBlock.None){releasePending=true;return false;}
            bool complete=true;
            foreach(var key in keys.Values.ToArray())
                if(Send(key with{Flags=key.Flags|2}))keys.Remove(key.Code);else complete=false;
            foreach(var pair in buttons.Release())
            {
                if(!Send(new(){Type="mouse",Flags=pair.Up,Code=pair.Code},false)){buttons.Track(pair.Up/2,pair.Code);complete=false;}
            }
            releasePending=!complete;return complete;
        }
    }
    public static void RetryRelease(){lock(gate){if(releasePending)ReleaseAll();}}
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
}

// A synthetic right-button UP alone opens a context menu in some applications.
// Keep only buttons pressed by this KVM session, never release every Windows button.
sealed class KvmMouseButtons
{
    readonly HashSet<(int Up,int Code)> pressed=[];
    public int Count=>pressed.Count;
    public void Track(int flags,int code)
    {
        if((flags&0x1fe)==0)return;
        foreach(var pair in new[]{(Down:2,Up:4,Code:0),(Down:8,Up:16,Code:0),(Down:32,Up:64,Code:0),(Down:128,Up:256,Code:code)})
        {
            if((flags&pair.Down)!=0)pressed.Add((pair.Up,pair.Code));
            if((flags&pair.Up)!=0)pressed.Remove((pair.Up,pair.Code));
        }
    }
    public (int Up,int Code)[] Release(){var result=pressed.ToArray();pressed.Clear();return result;}
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
    readonly IKvmInputTransport service;
    KvmOptions options;
    MonitorPlacement? remote;
    Point logical,anchor,returnPoint;
    Point? pendingWarp;
    readonly HashSet<int> swallowed=[];
    readonly HashSet<int> physicalKeys=[];
    List<MonitorPlacement> currentMonitors=[];
    Dictionary<string,KvmScreen> physicalScreens=[];
    KvmScreen[] localScreens=[];
    volatile bool seamless;
    public volatile bool ViewerActive;
    public bool Seamless {get=>seamless;set=>seamless=value;}
    public event Action<bool>? SeamlessChanged;
    public event Action? EmergencyReturn;
    public event Action? OpenRequested;
    public event Action<string>? Notification;
    public event Action? StateChanged;
    System.Windows.Forms.Timer? inputWatch;
    KvmInputBlock lastBlock;
    long lastMouseHook=Environment.TickCount64,missingHookSince;
    internal static Func<Point,bool>? TestWarp;
    readonly bool testMode;
    public KvmController(IKvmInputTransport service,KvmOptions options,bool installHooks=true)
    {
        this.service=service;this.options=options.Copy();Seamless=options.Seamless;testMode=!installHooks;
        Refresh();
        mouseProc=MouseHook;keyProc=KeyHook;
        if(testMode)return;
        // The KVM launcher shortcut is also available when networking is off.
        {
            using var ready=new ManualResetEventSlim();Exception? failure=null;bool started=false;
            inputThread=new Thread(()=>
            {
                try
                {
                    inputDispatcher=options.Role=="Host"?new KvmRawMouse(MoveRemote):new Control();_=inputDispatcher.Handle;
                    if(options.Role=="Host")mouseHook=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);
                    keyHook=SetWindowsHookEx(13,keyProc,GetModuleHandle(null),0);
                    if((options.Role=="Host"&&mouseHook==IntPtr.Zero)||keyHook==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    inputWatch=new(){Interval=100};inputWatch.Tick+=(_,_)=>{CheckAccess();KvmInput.RetryRelease();};inputWatch.Start();
                    started=true;ready.Set();Application.Run();
                }
                catch(Exception e){failure=e;if(!started)ready.Set();else Log.Write("KVM input thread: "+e);}
                finally
                {
                    inputWatch?.Stop();inputWatch?.Dispose();ReturnLocal();if(mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(mouseHook);if(keyHook!=IntPtr.Zero)UnhookWindowsHookEx(keyHook);
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
    public string ActiveMonitorKey=>remote?.Key??options.Id+"|"+Screen.FromPoint(Cursor.Position).DeviceName;
    public void Refresh()
    {
        if(Dispatch(Refresh))return;
        localScreens=KvmScreen.Local();
        var peers=new[]{new KvmPeerInfo(options.Id,Environment.MachineName,localScreens)}.Concat(service.Peers).ToArray();
        physicalScreens=peers.SelectMany(p=>p.Screens.Select(s=>(Key:p.Id+"|"+s.Device,Screen:s))).ToDictionary(p=>p.Key,p=>p.Screen);
        currentMonitors=KvmLayout.Merge(options,peers).Where(m=>physicalScreens.ContainsKey(m.Key)).ToList();
        if(remote!=null&&(!physicalScreens.ContainsKey(remote.Key)||!currentMonitors.Any(m=>m.Key==remote.Key)))ReturnLocal();
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
        if(target.Peer==options.Id){ReturnLocalCore(physical);return;}
        if(!CheckAccess()){LoseControl(KvmInputAccess.Check());return;}
        if(remote==null){returnPoint=Cursor.Position;var bounds=Screen.FromPoint(returnPoint).Bounds;anchor=new(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2);if(!Warp(anchor)){LoseControl(KvmInputBlock.Unavailable);return;}if(!testMode)Cursor.Hide();}
        remote=target;logical=position;
        if(!service.Send(target.Peer,new(){Type="mouse",X=physical.X,Y=physical.Y})){ReturnLocalCore();return;}
        StateChanged?.Invoke();
    }
    public void ReturnLocal()
    {
        if(Dispatch(ReturnLocal))return;
        ReturnLocalCore();
    }
    void ReturnLocalCore(Point? destination=null)
    {
        if(remote!=null){service.Send(remote.Peer,new(){Type="release"});remote=null;if(!testMode)Cursor.Show();Warp(destination??returnPoint);}
        else if(destination.HasValue)Warp(destination.Value);
        StateChanged?.Invoke();
    }
    bool Warp(Point point)
    {
        // SetCursorPos can succeed while ClipCursor clamps the requested position.
        bool success=TestWarp?.Invoke(point)??(KvmInput.SetCursorPos(point.X,point.Y)&&Cursor.Position==point);
        pendingWarp=success?point:null;return success;
    }
    bool CheckAccess()
    {
        var block=KvmInputAccess.Check();
        if(block==KvmInputBlock.None){if(lastBlock!=KvmInputBlock.None)RepairHooks();lastBlock=block;return true;}
        KvmInput.ReleaseAll();
        if(remote!=null)LoseControl(block);else{physicalKeys.Clear();swallowed.Clear();lastBlock=block;}
        return false;
    }
    internal void TestWatch()=>CheckAccess();
    internal void TestMove(int dx,int dy)=>MoveRemote(dx,dy);
    void LoseControl(KvmInputBlock reason)
    {
        ReturnLocalCore();physicalKeys.Clear();swallowed.Clear();pendingWarp=null;
        if(lastBlock!=reason){lastBlock=reason;Notification?.Invoke(KvmInputAccess.Describe(reason));}
    }
    void MoveRemote(int dx,int dy)
    {
        if(ViewerActive||(dx==0&&dy==0)||!CheckAccess())return;
        long now=Environment.TickCount64;
        if(now-lastMouseHook>150)
        {
            if(missingHookSince==0)missingHookSince=now;
            else if(now-missingHookSince>=30){LoseControl(KvmInputBlock.Unavailable);RepairHooks();return;}
        }
        else missingHookSince=0;
        if(remote==null)return;
        var candidate=new Point((int)Math.Clamp((long)logical.X+dx,-100000,100000),(int)Math.Clamp((long)logical.Y+dy,-100000,100000));
        var target=Seamless?KvmLayout.At(Monitors,candidate):null;
        bool cornerBlocked=options.BlockScreenCorners&&physicalScreens.TryGetValue(remote.Key,out var screen)&&KvmLayout.ProtectedCorner(remote,screen,candidate);
        if(target!=null&&target.Key!=remote.Key&&!cornerBlocked){Activate(target,candidate);return;}
        logical=KvmLayout.Clamp(remote,candidate);
        if(!TryPhysical(remote,logical,out var physical)||!service.Send(remote.Peer,new(){Type="mouse",X=physical.X,Y=physical.Y}))ReturnLocal();
    }
    // Feed the same path as WH_KEYBOARD_LL without sending any OS input in regression tests.
    internal IntPtr TestKey(int key,bool up,bool injected)
    {
        if(inputDispatcher?.InvokeRequired==true)return (IntPtr)inputDispatcher.Invoke(()=>TestKey(key,up,injected));
        var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<KeyData>());
        try{Marshal.StructureToPtr(new KeyData{Vk=(uint)key,Flags=(up?128u:0)|(injected?16u:0)},ptr,false);return KeyHook(0,IntPtr.Zero,ptr);}
        finally{Marshal.FreeHGlobal(ptr);}
    }
    internal IntPtr TestMouse(int message,Point point)
    {
        var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<MouseData>());
        try{Marshal.StructureToPtr(new MouseData{Point=new(){X=point.X,Y=point.Y}},ptr,false);return MouseHook(0,(IntPtr)message,ptr);}
        finally{Marshal.FreeHGlobal(ptr);}
    }
    IntPtr MouseHook(int code,IntPtr w,IntPtr l)
    {
        if(code<0)return CallNextHookEx(IntPtr.Zero,code,w,l);
        var data=Marshal.PtrToStructure<MouseData>(l);int message=w.ToInt32();
        if(data.Extra==KvmInput.Marker||(data.Flags&1)!=0)return CallNextHookEx(IntPtr.Zero,code,w,l);
        lastMouseHook=Environment.TickCount64;missingHookSince=0;
        var point=new Point(data.Point.X,data.Point.Y);
        try
        {
            if(!CheckAccess())return CallNextHookEx(IntPtr.Zero,code,w,l);
            // Ignore our own recentering event, without delaying the next physical movement.
            if(message==0x200&&pendingWarp==point){pendingWarp=null;return remote!=null?(IntPtr)1:CallNextHookEx(IntPtr.Zero,code,w,l);}
            if(remote==null)
            {
                if(message==0x200&&Seamless&&!ViewerActive&&!(Down(1)||Down(2)||Down(4)))
                {
                    // Low-level hooks can see an unclamped position after a fast edge overshoot.
                    var screen=localScreens.FirstOrDefault(s=>s.Bounds.Contains(point))??localScreens.FirstOrDefault(s=>s.Bounds.Contains(Cursor.Position));
                    var local=Monitors.FirstOrDefault(m=>m.Peer==options.Id&&m.Device==screen?.Device);
                    if(local!=null&&screen!=null)
                    {
                        var crossing=KvmLayout.EdgeCrossing(Monitors,local,screen,point,options.BlockScreenCorners);
                        if(crossing is { } edge&&edge.Target.Peer!=options.Id){Activate(edge.Target,edge.Point);return (IntPtr)1;}
                    }
                }
                return CallNextHookEx(IntPtr.Zero,code,w,l);
            }
            if(message==0x200)
            {
                // WM_MOUSEMOVE can be produced by SetCursorPos/ClipCursor in games.
                // Only WM_INPUT physical movement advances the remote pointer.
                return (IntPtr)1;
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
        // Ignore only our own remote input. Stream Deck and accessibility tools set LLKHF_INJECTED too.
        if(data.Extra==KvmInput.Marker)return CallNextHookEx(IntPtr.Zero,code,w,l);
        if(!CheckAccess())return CallNextHookEx(IntPtr.Zero,code,w,l);
        int key=(int)data.Vk;bool up=(data.Flags&128)!=0;
        bool repeat=!up&&!physicalKeys.Add(key);if(up)physicalKeys.Remove(key);
        if(up&&swallowed.Remove(key))return (IntPtr)1;
        if(repeat&&swallowed.Contains(key))return (IntPtr)1;
        uint modifiers=KvmShortcut.Modifiers(physicalKeys);
        if(!up&&!repeat)
        {
            if(KvmShortcut.Matches(options.OpenHotkeyModifiers,options.OpenHotkeyKey,modifiers,key)){ReturnLocal();OpenRequested?.Invoke();swallowed.Add(key);return (IntPtr)1;}
            if(modifiers==3&&key==27){ReturnLocal();EmergencyReturn?.Invoke();swallowed.Add(key);return (IntPtr)1;}
            if(options.Role=="Host"&&KvmShortcut.Matches(options.ToggleHotkeyModifiers,options.ToggleHotkeyKey,modifiers,key)){Seamless=!Seamless;if(!Seamless)ReturnLocal();SeamlessChanged?.Invoke(Seamless);swallowed.Add(key);return (IntPtr)1;}
            var target=options.Role!="Host"||ViewerActive||modifiers!=3?null:Monitors.FirstOrDefault(m=>m.Hotkey>0&&key==(int)Keys.F1+m.Hotkey-1);
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
    void RepairHooks()
    {
        if(testMode||inputDispatcher==null||Volatile.Read(ref disposed)!=0)return;
        if(options.Role=="Host")
        {
            if(mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(mouseHook);
            mouseHook=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);
        }
        if(keyHook!=IntPtr.Zero)UnhookWindowsHookEx(keyHook);
        keyHook=SetWindowsHookEx(13,keyProc,GetModuleHandle(null),0);
        lastMouseHook=Environment.TickCount64;missingHookSince=0;
        if(keyHook==IntPtr.Zero||(options.Role=="Host"&&mouseHook==IntPtr.Zero))LoseControl(KvmInputBlock.Unavailable);
    }
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
