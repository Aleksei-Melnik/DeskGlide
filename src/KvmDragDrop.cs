using System.Runtime.InteropServices;
using System.Text.Json;

namespace SdrCapture;

// Only this application's marked cache directories expire. Explorer copies are outside this root.
static class KvmFileCache
{
    public static void Mark(string root,string folder){Directory.CreateDirectory(root+".leases");string path=Path.Combine(root+".leases",Path.GetFileName(folder)+".json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(DateTimeOffset.UtcNow));File.Move(path+".tmp",path,true);}
    public static void Clean(string root,TimeSpan age,ISet<string>? pinned=null)
    {
        string leases=root+".leases";if(!Directory.Exists(leases))return;
        if((File.GetAttributes(leases)&FileAttributes.ReparsePoint)!=0||Directory.Exists(root)&&(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)return;
        string fullRoot=Path.GetFullPath(root)+Path.DirectorySeparatorChar;
        foreach(string lease in Directory.EnumerateFiles(leases,"*.json"))
        {
            try
            {
                string id=Path.GetFileNameWithoutExtension(lease);if(!Guid.TryParseExact(id,"N",out _))continue;
                string folder=Path.GetFullPath(Path.Combine(root,id));if(!folder.StartsWith(fullRoot,StringComparison.OrdinalIgnoreCase)||pinned?.Contains(folder)==true)continue;
                var created=JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(lease));if(DateTimeOffset.UtcNow-created<age)continue;
                if(Directory.Exists(folder)){RejectLinks(folder);Directory.Delete(folder,true);}File.Delete(lease);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}catch(JsonException){}
        }
    }
    static void RejectLinks(string folder)
    {
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("Cache link");
        foreach(string path in Directory.EnumerateFileSystemEntries(folder)){var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Cache link");if((attributes&FileAttributes.Directory)!=0)RejectLinks(path);}
    }
}

sealed class KvmDragDrop:IDisposable
{
    readonly KvmService service;
    readonly KvmClipboard files;
    readonly KvmController controller;
    readonly KvmOptions options;
    readonly Control ui;
    readonly List<EdgePortal> portals=[];
    readonly System.Collections.Concurrent.ConcurrentDictionary<string,(string Peer,string[] Paths,DateTimeOffset Time)> ready=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    string topology="";
    Pending? pending;
    RemoteDrag? active;
    bool disposed;
    sealed class Pending(string peer,string id,MonitorPlacement monitor,Point point,EdgePortal portal):IDisposable
    {
        public readonly string Peer=peer,Id=id;public readonly MonitorPlacement Monitor=monitor;public readonly Point Point=point;public readonly EdgePortal Portal=portal;
        public readonly CancellationTokenSource Stop=new();public bool HandedOff;public void Dispose(){Stop.Cancel();}
    }
    sealed class RemoteDrag(string peer,string id,string[] paths)
    {
        public readonly string Peer=peer,Id=id;public readonly string[] Paths=paths;public volatile bool Drop,Cancel;
    }
    public event Action<string>? Notification;
    public KvmDragDrop(KvmService service,KvmClipboard files,KvmController controller,KvmOptions options,Control ui)
    {
        this.service=service;this.files=files;this.controller=controller;this.options=options.Copy();this.ui=ui;
        files.DragFilesReady+=FilesReady;service.Received+=Receive;service.Disconnected+=Disconnected;service.BeforeInput=Input;
        timer.Tick+=(_,_)=>Refresh();timer.Start();Refresh();
    }
    void Refresh()
    {
        foreach(var item in ready.Where(p=>DateTimeOffset.UtcNow-p.Value.Time>TimeSpan.FromMinutes(5)).ToArray())ready.TryRemove(item.Key,out _);
        string root=Path.Combine(Log.Folder,"Kvm","Clipboard");var pinned=ready.Values.SelectMany(v=>v.Paths).Concat(active?.Paths??[]).Select(Path.GetDirectoryName).Where(p=>p!=null).Select(p=>p!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(DateTime.UtcNow.Second%30==0)_=Task.Run(()=>KvmFileCache.Clean(root,TimeSpan.FromHours(4),pinned));
        string signature=options.Role=="Host"&&options.ClipboardFiles&&controller.Seamless&&!controller.ViewerActive?JsonSerializer.Serialize(controller.Monitors):"";
        if(signature==topology)return;topology=signature;CancelPending();foreach(var portal in portals)portal.Dispose();portals.Clear();
        if(signature.Length==0)return;
        foreach(var screen in KvmScreen.Local())
        {
            var local=controller.Monitors.FirstOrDefault(m=>m.Peer==options.Id&&m.Device==screen.Device);if(local==null)continue;
            foreach(var bounds in SharedEdges(controller.Monitors,local,screen))
            {
                var portal=new EdgePortal(bounds);portal.DragEnter+=(s,e)=>Enter(portal,local,screen,e);portal.DragOver+=(_,e)=>e.Effect=pending?.Portal==portal?DragDropEffects.Copy:DragDropEffects.None;
                portal.DragLeave+=(_,_)=>{if(pending?.Portal==portal&&!pending.HandedOff)CancelPending();};
                portal.DragDrop+=(_,_)=>{if(pending?.Portal==portal&&!pending.HandedOff){Notification?.Invoke("Передача ещё идёт. Держите кнопку мыши у края до перехода на другой ПК.");CancelPending();}};
                portals.Add(portal);portal.Show();
            }
        }
    }
    internal static IEnumerable<Rectangle> SharedEdges(IEnumerable<MonitorPlacement> monitors,MonitorPlacement local,KvmScreen screen)
    {
        foreach(var neighbor in monitors.Where(m=>m.Peer!=local.Peer))
        {
            int top=Math.Max(local.Y,neighbor.Y),bottom=Math.Min(local.Bounds.Bottom,neighbor.Bounds.Bottom);
            int left=Math.Max(local.X,neighbor.X),right=Math.Min(local.Bounds.Right,neighbor.Bounds.Right);
            if(top<bottom&&neighbor.Bounds.Right==local.X)yield return new(screen.X,screen.Y+top-local.Y,2,bottom-top);
            if(top<bottom&&neighbor.X==local.Bounds.Right)yield return new(screen.Bounds.Right-2,screen.Y+top-local.Y,2,bottom-top);
            if(left<right&&neighbor.Bounds.Bottom==local.Y)yield return new(screen.X+left-local.X,screen.Y,right-left,2);
            if(left<right&&neighbor.Y==local.Bounds.Bottom)yield return new(screen.X+left-local.X,screen.Bounds.Bottom-2,right-left,2);
        }
    }
    void Enter(EdgePortal portal,MonitorPlacement local,KvmScreen screen,DragEventArgs args)
    {
        args.Effect=DragDropEffects.None;if(!controller.Seamless||controller.ViewerActive||pending!=null||args.Data?.GetData(DataFormats.FileDrop) is not string[] paths||paths.Length==0)return;
        Point position=new(args.X,args.Y);
        // Portal is two pixels wide; project it onto the actual outer boundary.
        if(portal.Width==2)position.X=portal.Left==screen.X?screen.X:screen.Bounds.Right-1;
        else position.Y=portal.Top==screen.Y?screen.Y:screen.Bounds.Bottom-1;
        var crossing=KvmLayout.EdgeCrossing(controller.Monitors,local,screen,position);if(crossing is not {} edge||edge.Target.Peer==options.Id)return;
        var peer=service.Peers.FirstOrDefault(p=>p.Id==edge.Target.Peer);if(peer==null||!Version.TryParse(peer.Version,out var version)||version<new Version(0,7,0)){Notification?.Invoke("Для перетаскивания обновите оба ПК до 0.7.0.");return;}
        var transfer=new Pending(peer.Id,Guid.NewGuid().ToString("N"),edge.Target,edge.Point,portal);pending=transfer;args.Effect=DragDropEffects.Copy;
        Notification?.Invoke("Передаю файлы. Удерживайте кнопку мыши — после передачи курсор перейдёт на другой ПК.");
        _=Task.Run(async()=>{try{await files.SendDrag(transfer.Peer,transfer.Id,paths,transfer.Stop.Token);}catch(Exception e){Ui(()=>{if(pending==transfer){Notification?.Invoke("Перетаскивание отменено: "+e.Message);CancelPending();}});}});
    }
    void CancelPending(){var prior=pending;pending=null;if(prior!=null){prior.Dispose();if(!prior.HandedOff)service.Send(prior.Peer,new(){Type="drag-cancel",Id=prior.Id});}}
    void FilesReady(string peer,string id,string[] paths)
    {
        if(options.Role!="Client"||ready.Count>=4)return;ready[id]=(peer,paths,DateTimeOffset.UtcNow);service.Send(peer,new(){Type="drag-ready",Id=id});
    }
    void Receive(string peer,KvmMessage message)
    {
        if(!message.Type.StartsWith("drag-")||!Guid.TryParseExact(message.Id,"N",out _))return;
        // Establish state on the control-reader thread BEFORE the next mouse-up is handled.
        if(message.Type=="drag-start")
        {
            if(options.Role!="Client"||!ready.TryRemove(message.Id,out var item)||item.Peer!=peer)return;
            var drag=new RemoteDrag(peer,message.Id,item.Paths);if(Interlocked.CompareExchange(ref active,drag,null)!=null)return;
            Ui(()=>Perform(drag));return;
        }
        if(message.Type=="drag-cancel")
        {
            if(ready.TryGetValue(message.Id,out var cached)&&cached.Peer==peer)ready.TryRemove(message.Id,out _);
            if(active?.Peer==peer&&active.Id==message.Id)active.Cancel=true;return;
        }
        Ui(()=>
        {
            switch(message.Type)
            {
                case "drag-ready":
                    if(pending is not {} transfer||transfer.Peer!=peer||transfer.Id!=message.Id)return;
                    if((KvmInput.GetAsyncKeyState(1)&0x8000)==0){CancelPending();return;}
                    transfer.HandedOff=true;
                    // Cancel Explorer's local OLE drag, preserving originals; resume a COPY on the peer.
                    KvmInput.Inject(new(){Type="key",Code=27});KvmInput.Inject(new(){Type="key",Code=27,Flags=2});
                    service.Send(peer,new(){Type="drag-start",Id=message.Id});controller.Activate(transfer.Monitor,transfer.Point);pending=null;transfer.Dispose();break;
                case "drag-error":
                    if(pending?.Peer==peer&&pending.Id==message.Id){Notification?.Invoke("Перетаскивание: "+message.Text);CancelPending();}break;
            }
        });
    }
    bool Input(string peer,KvmMessage message)
    {
        var drag=Volatile.Read(ref active);if(drag==null||drag.Peer!=peer)return false;
        if(message.Type=="release"||message.Type=="key"&&message.Code==27){drag.Cancel=true;return true;}
        if(message.Type=="mouse")
        {
            if((message.Flags&4)!=0)drag.Drop=true;
            KvmInput.Inject(message with{Flags=0,Code=0});return true;
        }
        return false;
    }
    void Perform(RemoteDrag drag)
    {
        if(disposed||drag.Cancel){active=null;return;}
        using var source=new Control();_=source.Handle;
        source.QueryContinueDrag+=(_,e)=>e.Action=drag.Cancel||e.EscapePressed?DragAction.Cancel:drag.Drop?DragAction.Drop:DragAction.Continue;
        // OLE polls the drop source on mouse messages. Wake it on cancellation/disconnect too.
        using var wake=new System.Windows.Forms.Timer{Interval=20};wake.Tick+=(_,_)=>{if(drag.Cancel||drag.Drop)PostMessage(source.Handle,0x200,IntPtr.Zero,IntPtr.Zero);};wake.Start();
        try{var data=new DataObject(DataFormats.FileDrop,drag.Paths);var effect=source.DoDragDrop(data,DragDropEffects.Copy);Notification?.Invoke(effect==DragDropEffects.Copy?"Файлы переданы приложению. Временный кэш хранится 4 часа.":"Перетаскивание отменено.");}
        catch(Exception e){Notification?.Invoke("Перетаскивание: "+e.Message);}
        finally{wake.Stop();active=null;service.Send(drag.Peer,new(){Type="drag-finished",Id=drag.Id});}
    }
    void Disconnected(string peer){if(active?.Peer==peer)active.Cancel=true;Ui(()=>{if(pending?.Peer==peer)CancelPending();});}
    void Ui(Action action){if(disposed)return;try{ui.BeginInvoke(()=>{if(!disposed)action();});}catch(InvalidOperationException){}}
    public void Dispose(){disposed=true;timer.Stop();timer.Dispose();CancelPending();if(active!=null)active.Cancel=true;foreach(var portal in portals)portal.Dispose();files.DragFilesReady-=FilesReady;service.Received-=Receive;service.Disconnected-=Disconnected;service.BeforeInput=null;}
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
    sealed class EdgePortal:Form
    {
        public EdgePortal(Rectangle bounds){FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=bounds;ShowInTaskbar=false;TopMost=true;AllowDrop=true;Opacity=.004;BackColor=Color.FromArgb(33,105,211);}
        protected override bool ShowWithoutActivation=>true;
        protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p;}}
    }
}
