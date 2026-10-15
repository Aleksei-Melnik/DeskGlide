using System.Diagnostics;

namespace SdrCapture;

// The main process retains networking, settings, recording and the UI. An optional
// UAC-approved child owns only capture/injection and a private same-user pipe.
sealed class KvmInputManager:IDisposable
{
    readonly KvmService service;
    readonly object gate=new();
    readonly CancellationTokenSource stop=new();
    volatile KvmOptions options;
    volatile KvmController? native;
    volatile KvmHelperPipe? helper;
    Process? child;
    volatile bool disposed,viewer,seamless,ready;
    volatile string active="";
    long lastReply;
    public event Action<bool>? SeamlessChanged;
    public event Action? EmergencyReturn;
    public event Action? OpenRequested;
    public event Action<string>? Notification;
    public string Status {get;private set;}="";
    public KvmInputManager(KvmService service,KvmOptions options)
    {
        this.service=service;this.options=options.Copy();seamless=options.Seamless;
        service.InputInjector=Inject;service.InputBlocked+=RemoteBlocked;
        if(options.UsesAdministratorInput&&options.Role!="Off"&&!KvmInputAccess.Elevated)
        {Status="Waiting for Windows approval for administrator app control…";_=Task.Run(StartHelper);}
        else StartNative();
    }
    void StartNative()
    {
        lock(gate)
        {
            if(disposed||native!=null)return;
            if(Status.Length==0)Status=KvmInputAccess.Elevated?"Administrator app control is active.":"Standard input control is active.";
            native=new(service,options);native.ViewerActive=viewer;
            native.OpenRequested+=()=>OpenRequested?.Invoke();native.EmergencyReturn+=()=>EmergencyReturn?.Invoke();
            native.SeamlessChanged+=value=>{seamless=value;SeamlessChanged?.Invoke(value);};
            native.Notification+=text=>{Status=text;Notification?.Invoke(text);};
        }
    }
    async Task StartHelper()
    {
        try
        {
            var connection=await KvmInputHelper.Start(stop.Token);
            lock(gate)
            {
                if(disposed){connection.Pipe.Dispose();connection.Process.Dispose();return;}
                helper=connection.Pipe;child=connection.Process;lastReply=Environment.TickCount64;Configure();
            }
            _=Task.Run(async()=>
            {
                try{while(!stop.IsCancellationRequested){await Task.Delay(500,stop.Token);if(Environment.TickCount64-Interlocked.Read(ref lastReply)>3000){connection.Pipe.Dispose();break;}if(connection.Pipe.Post(new(){Type="ping"})!=true)break;}}
                catch(OperationCanceledException){}
            });
            while(!stop.IsCancellationRequested)
            {
                var message=await connection.Pipe.Read(stop.Token);if(message==null)break;
                Interlocked.Exchange(ref lastReply,Environment.TickCount64);
                switch(message.Type)
                {
                    case "pong":break;
                    case "ready":ready=true;Status="Administrator app control is active.";break;
                    case "send":
                        if(message.Input==null||!KvmInput.Valid(message.Input)||!service.Peers.Any(p=>p.Id==message.Peer))throw new IOException("Invalid helper output.");
                        if(!service.Send(message.Peer,message.Input))connection.Pipe.Post(new(){Type="local"});break;
                    case "state":active=message.Key;break;
                    case "open":OpenRequested?.Invoke();break;
                    case "emergency":EmergencyReturn?.Invoke();break;
                    case "toggle":seamless=message.Flag;SeamlessChanged?.Invoke(message.Flag);break;
                    case "notice":Status=message.Key;Notification?.Invoke(message.Key);break;
                    case "blocked":service.ReportInputBlocked();break;
                    default:throw new IOException("Unsupported helper output.");
                }
            }
        }
        catch(Exception e)when(e is IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {if(!stop.IsCancellationRequested){Status="Administrator app control was not started. Enable it again in KVM settings to retry.";Log.Write("KVM helper: "+e.GetType().Name);}}
        finally
        {
            ready=false;active="";helper?.Dispose();helper=null;
            foreach(var peer in service.Peers)service.Send(peer.Id,new(){Type="release"});
            if(!stop.IsCancellationRequested)
            {
                Status="Administrator app control was not started. Enable it again in KVM settings to retry.";
                StartNative();Notification?.Invoke(Status);
            }
        }
    }
    KvmPeerInfo[] Peers()=>new[]{new KvmPeerInfo(options.Id,Environment.MachineName,KvmScreen.Local())}.Concat(service.Peers).ToArray();
    void Configure()
    {
        helper?.Post(new(){Type="configure",Options=KvmInputHelper.SafeOptions(options with{Seamless=seamless}),Peers=service.Peers.Select(p=>new KvmPeerInfo(p.Id,p.Name,p.Screens,RemoteViewOnly:p.RemoteViewOnly)).ToArray()});
        helper?.Post(new(){Type="viewer",Flag=viewer});
    }
    public List<MonitorPlacement> Monitors
    {
        get{if(native is {} input)return input.Monitors;var peers=Peers();return KvmLayout.Merge(options,peers).Where(m=>peers.Any(p=>p.Id==m.Peer&&p.Screens.Any(s=>s.Device==m.Device))).ToList();}
    }
    public string ActiveMonitorKey=>native?.ActiveMonitorKey??(ready&&Monitors.Any(m=>m.Key==active)?active:options.Id+"|"+Screen.FromPoint(Cursor.Position).DeviceName);
    public bool Seamless
    {
        get=>native?.Seamless??seamless;
        set{seamless=value;if(native!=null)native.Seamless=value;helper?.Post(new(){Type="seamless",Flag=value});}
    }
    public bool ViewerActive
    {
        get=>viewer;
        set{viewer=value;if(native!=null)native.ViewerActive=value;helper?.Post(new(){Type="viewer",Flag=value});}
    }
    public void Refresh(){if(native!=null)native.Refresh();else Configure();}
    public void UpdateLayout(KvmOptions value){options=value.Copy();seamless=value.Seamless;if(native!=null)native.UpdateLayout(value);else Configure();}
    public void Activate(MonitorPlacement monitor,Point? point=null)
    {
        if(native!=null){native.Activate(monitor,point);return;}
        if(!ready||helper?.Post(new(){Type="activate",Key=monitor.Key,Point=point})!=true)throw new IOException("Waiting for Windows approval for administrator app control…");
    }
    public void ReturnLocal(){active="";if(native!=null)native.ReturnLocal();helper?.Post(new(){Type="local"});}
    bool Inject(KvmMessage message)
    {
        if(disposed)return false;
        if(helper is {} link)return ready&&link.Post(new(){Type="input",Input=message});
        // Never split one pressed-key set between ordinary and elevated processes
        // while the user is still deciding the Windows consent prompt.
        return native!=null&&KvmInput.Inject(message);
    }
    void RemoteBlocked(){ReturnLocal();EmergencyReturn?.Invoke();Notification?.Invoke("The other PC could not accept input. Control has returned to this PC.");}
    public void Dispose()
    {
        lock(gate){if(disposed)return;disposed=true;}
        service.InputBlocked-=RemoteBlocked;service.InputInjector=null;
        ReturnLocal();helper?.Post(new(){Type="stop"});stop.Cancel();helper?.Dispose();native?.Dispose();
        try{if(child!=null&&!child.HasExited)child.WaitForExit(3000);}catch(InvalidOperationException){}
        child?.Dispose();
    }
}
