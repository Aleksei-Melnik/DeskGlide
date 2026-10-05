using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SdrCapture;
sealed class KvmService:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly ConcurrentDictionary<string,KvmWire> controls=new(),bulk=new(),audio=new(),video=new();
    readonly SemaphoreSlim handshakes=new(16);
    readonly KvmOptions options;
    readonly Task worker;
    KvmIdentity? identity;
    TcpListener? listener;
    public KvmOptions Options=>options.Copy();
    public string Status {get;private set;}="KVM is off";
    public string PairingCode=>identity?.Code??"";
    public event Action? PeersChanged;
    public event Action<string,KvmMessage>? Received;
    public event Action<string>? Disconnected;
    public event Action<string>? Notification;
    public KvmPeerInfo[] Peers=>controls.Values.Select(w=>w.Peer).ToArray();
    public string? ActiveAudioPeer {get;set;}
    public KvmService(KvmOptions value)
    {
        value.Validate();options=value.Copy();
        if(options.Role=="Host")identity=new KvmIdentity(Path.Combine(Log.Folder,"Kvm"));
        worker=options.Role=="Host"?Task.Run(HostLoop):options.Role=="Client"?Task.Run(ClientLoop):Task.CompletedTask;
    }
    async Task HostLoop()
    {
        try
        {
            listener=new TcpListener(IPAddress.Any,options.Port);listener.Start(8);
            Status=$"Waiting for clients · {Environment.MachineName}:{options.Port}";
            while(!stop.IsCancellationRequested)
            {
                var tcp=await listener.AcceptTcpClientAsync(stop.Token);
                if(!await handshakes.WaitAsync(0,stop.Token)){tcp.Dispose();continue;}
                _=Task.Run(async()=>
                {
                    KvmWire? wire=null;
                    try{wire=await KvmWire.Accept(tcp,identity!,options,stop.Token);}
                    catch(Exception e){tcp.Dispose();if(!stop.IsCancellationRequested)Log.Write("KVM handshake: "+e.Message);}
                    finally{handshakes.Release();}
                    if(wire!=null)await Serve(wire);
                });
            }
        }
        catch(OperationCanceledException){}catch(Exception e){Status="KVM: "+e.Message;Notification?.Invoke(Status);}
    }
    async Task ClientLoop()
    {
        while(!stop.IsCancellationRequested)
        {
            KvmWire? control=null,files=null,sound=null,picture=null;
            try
            {
                Status="Connecting to "+options.Host;
                control=await KvmWire.Connect(options,"control",stop.Token);
                var controlTask=Serve(control);
                files=await KvmWire.Connect(options,"bulk",stop.Token);
                var bulkTask=Serve(files);
                sound=await KvmWire.Connect(options,"audio",stop.Token);
                var audioTask=Serve(sound);
                var tasks=new List<Task>{controlTask,bulkTask,audioTask};
                if(control.Peer.ViewProtocol>=1){picture=await KvmWire.Connect(options,"video",stop.Token);tasks.Add(Serve(picture));}
                Status="Connected to "+control.Peer.Name;
                await Task.WhenAny(tasks);
            }
            catch(OperationCanceledException){}catch(Exception e){Status="Not connected: "+e.Message;}
            finally{control?.Dispose();files?.Dispose();sound?.Dispose();picture?.Dispose();}
            try{await Task.Delay(3000,stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    async Task Serve(KvmWire wire)
    {
        var peers=wire.Channel=="control"?controls:wire.Channel=="audio"?audio:wire.Channel=="video"?video:bulk;
        if(peers.Count>=16){wire.Dispose();return;}
        // Welcome can reach the client before the host has registered its control socket.
        if(wire.Channel!="control")
        {
            try{for(int attempt=0;attempt<40&&!controls.ContainsKey(wire.Peer.Id);attempt++)await Task.Delay(25,stop.Token);}
            catch(OperationCanceledException){wire.Dispose();return;}
            if(!controls.ContainsKey(wire.Peer.Id)){wire.Dispose();return;}
        }
        if(!peers.TryAdd(wire.Peer.Id,wire)){wire.Dispose();return;}
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,wire.Token);
        var ping=Task.Run(async()=>
        {
            try{while(!linked.IsCancellationRequested){await Task.Delay(2000,linked.Token);wire.Post(new(){Type="ping"});}}
            catch(OperationCanceledException){}
        });
        if(wire.Channel=="control"){Status="Connected computers: "+controls.Count;PeersChanged?.Invoke();}
        if(wire.Channel=="audio"&&options.Role=="Host"){KvmAudioBus.Remove(wire.Peer.Id);wire.Post(new(){Type="audio-subscribe",Flags=wire.Peer.Id==ActiveAudioPeer?1:0});}
        using var audioSender=wire.Channel=="audio"&&options.Role=="Client"?new KvmAudioSender(options.AudioDevice,wire):null;
        using var videoSender=wire.Channel=="video"&&options.Role=="Client"?new KvmVideoSession(wire,options.AllowView):null;
        try
        {
            while(!linked.IsCancellationRequested)
            {
                var message=await wire.ReadAsync(linked.Token);
                if(message.Type=="ping")continue;
                if(wire.Channel=="video")
                {
                    if(videoSender!=null)await videoSender.Handle(message,linked.Token);
                    else if(options.Role=="Host"&&message.Type is "view-frame" or "view-error")
                    {Received?.Invoke(wire.Peer.Id,message);if(message.Type=="view-frame")wire.Post(new(){Type="view-ack",Id=message.Id,Size=message.Size});}
                    continue;
                }
                if(message.Type=="screens")
                {
                    KvmWire.ValidateScreens(message.Screens);
                    if(wire.Channel=="control"){wire.SetScreens(message.Screens!,message.RemoteViewOnly);PeersChanged?.Invoke();}continue;
                }
                if(wire.Channel=="audio")
                {
                    if(options.Role=="Client"&&message.Type=="audio-subscribe")audioSender!.Enabled=message.Flags==1;
                    else if(options.Role=="Host"&&message.Type=="pcm"&&message.Size>=0&&message.Size<long.MaxValue/2&&message.Data is {Length:>0 and <=32768}&&message.Data.Length%8==0)KvmAudioBus.Put(wire.Peer.Id,message.Size,message.Data);
                    continue;
                }
                if(wire.Channel=="control"&&message.Type is "mouse" or "key" or "release")
                {
                    if(options.Role=="Client"&&BeforeInput?.Invoke(wire.Peer.Id,message)!=true)KvmInput.Inject(message);
                    continue;
                }
                if(wire.Channel=="bulk"&&message.Type=="view-request"&&options.Role=="Client")
                {
                    if(!options.AllowView)await wire.SendAsync(new(){Type="view-error",Text="Desktop viewing is disabled on the remote PC."},linked.Token);
                    else await KvmViewerCapture.Reply(wire,message,linked.Token);
                    continue;
                }
                if(message.Type.StartsWith("update-"))
                {
                    if(wire.Channel=="control"&&Guid.TryParseExact(message.Id,"N",out _)&&message.Text.Length<=1024&&message.Version.Length<=32)Received?.Invoke(wire.Peer.Id,message);
                    continue;
                }
                if(message.Type=="clipboard-text"&&!options.ClipboardText)continue;
                if(message.Type.StartsWith("file-")&&!options.ClipboardFiles)continue;
                if(message.Type=="clipboard-text"&&message.Text.Length>262144)throw new IOException("Clipboard too large.");
                Received?.Invoke(wire.Peer.Id,message);
            }
        }
        catch(OperationCanceledException){}catch(Exception e){if(!stop.IsCancellationRequested)Log.Write("KVM connection: "+e.Message);}
        finally
        {
            linked.Cancel();wire.Dispose();
            peers.TryRemove(new KeyValuePair<string,KvmWire>(wire.Peer.Id,wire));
            if(wire.Channel=="control")
            {
                if(bulk.TryRemove(wire.Peer.Id,out var b))b.Dispose();
                if(audio.TryRemove(wire.Peer.Id,out var a))a.Dispose();
                if(video.TryRemove(wire.Peer.Id,out var v))v.Dispose();
                if(options.Role=="Client")KvmInput.ReleaseAll();
                Disconnected?.Invoke(wire.Peer.Id);PeersChanged?.Invoke();
            }
            if(wire.Channel=="audio")KvmAudioBus.Remove(wire.Peer.Id);
        }
    }
    public bool Send(string peer,KvmMessage message)=>controls.TryGetValue(peer,out var connection)&&connection.Post(message);
    public bool SendVideo(string peer,KvmMessage message)=>video.TryGetValue(peer,out var connection)&&connection.Post(message);
    public Func<string,KvmMessage,bool>? BeforeInput {get;set;}
    public async Task SendControl(string peer,KvmMessage message)
    {
        if(!controls.TryGetValue(peer,out var connection))throw new IOException("The computer disconnected.");
        await connection.SendAsync(message,stop.Token);
    }
    public async Task SendBulk(string peer,KvmMessage message,CancellationToken token=default)
    {
        if(!bulk.TryGetValue(peer,out var connection))throw new IOException("The transfer channel is not connected yet.");
        await connection.SendAsync(message,token);
    }
    public void SelectAudio(string? peer)
    {
        ActiveAudioPeer=peer;
        foreach(var pair in audio)pair.Value.Post(new(){Type="audio-subscribe",Flags=pair.Key==peer?1:0});
    }
    public void RefreshScreens()
    {
        var screens=KvmScreen.Local();
        foreach(var connection in controls.Values)connection.Post(new(){Type="screens",Screens=screens,RemoteViewOnly=options.RemoteViewOnly});
        SelectAudio(ActiveAudioPeer);
    }
    public void Dispose()
    {
        stop.Cancel();listener?.Stop();
        foreach(var c in controls.Values.Concat(bulk.Values).Concat(audio.Values).Concat(video.Values))c.Dispose();
        if(options.Role=="Client")KvmInput.ReleaseAll();
        // Identity remains alive until every handshake has left AuthenticateAsServer.
    }
}
