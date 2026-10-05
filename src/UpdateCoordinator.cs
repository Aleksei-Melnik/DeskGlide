namespace SdrCapture;

sealed class UpdateCoordinator(Control ui,Func<KvmService?> service,Func<UpdateOptions> options,Func<Task> beforeInstall,Action exit)
{
    readonly Dictionary<string,TaskCompletionSource<KvmMessage>> waiting=[];
    PreparedUpdate? prepared;
    string preparedRequest="";
    public bool Busy {get;private set;}
    public string Status {get;private set;}="Updates have not been checked yet.";
    public AvailableRelease? Latest {get;private set;}
    public Dictionary<string,string> Peers {get;}=[];
    public event Action? Changed;
    public void SetStatus(string value){Status=value;Changed?.Invoke();}
    public async Task Check()
    {
        SetStatus("Checking GitHub Releases…");Latest=await Updates.Check();
        SetStatus(Latest==null?"No published releases yet.":Updates.ParseVersion(Latest.Manifest.Version)>Updates.Current?$"Version {Latest.Manifest.Version} is available":$"You are up to date · {Updates.VersionText}");
    }
    async Task PrepareLocal(AvailableRelease release)
    {
        if(prepared?.Manifest.Version==release.Manifest.Version)return;
        var progress=new Progress<string>(SetStatus);
        prepared=await Updates.Prepare(release,s=>((IProgress<string>)progress).Report(s));
    }
    public async Task InstallHere(bool repair=false)
    {
        if(Busy)return;Busy=true;Changed?.Invoke();
        try
        {
            await Check();var release=Latest??throw new IOException("No release is available yet.");
            if(!repair&&Updates.ParseVersion(release.Manifest.Version)<=Updates.Current)return;
            await PrepareLocal(release);SetStatus("Saving replay and preparing to restart…");
            await beforeInstall();await Updates.LaunchInstaller(prepared!);exit();
        }
        catch(Exception e){SetStatus("Update could not be installed: "+e.Message);}
        finally{Busy=false;Changed?.Invoke();}
    }
    public async Task InstallAll()
    {
        if(Busy)return;Busy=true;Changed?.Invoke();
        try
        {
            var network=service();if(network?.Options.Role!="Host")throw new IOException("Run Update all PCs from the KVM host.");
            await Check();var release=Latest??throw new IOException("No release is available yet.");
            var peers=network.Peers.ToArray();Peers.Clear();
            foreach(var old in peers.Where(p=>p.UpdateProtocol<1))Peers[old.Id]=$"{old.Name}: requires a manual update to ScreenCapture 0.6 or later";
            var compatible=peers.Where(p=>p.UpdateProtocol>=1).ToArray();
            bool localNew=Updates.ParseVersion(release.Manifest.Version)>Updates.Current;
            if(localNew)await PrepareLocal(release);
            string request=Guid.NewGuid().ToString("N");
            var results=await Task.WhenAll(compatible.Select(async peer=>
            {
                Peers[peer.Id]=peer.Name+": preparing";Changed?.Invoke();
                var result=await Request(network,peer.Id,new(){Type="update-prepare",Id=request,Version=release.Manifest.Version},TimeSpan.FromMinutes(10));
                Peers[peer.Id]=peer.Name+": "+result.Text;Changed?.Invoke();return(peer,result);
            }));
            if(results.Any(r=>r.result.Code==0))throw new IOException("Some PCs are not ready. Nothing was restarted. Check the status list for details.");
            foreach(var (peer,result) in results.Where(r=>r.result.Code==1))
            {
                var ack=await Request(network,peer.Id,new(){Type="update-commit",Id=request,Version=release.Manifest.Version},TimeSpan.FromMinutes(4));
                Peers[peer.Id]=peer.Name+": "+ack.Text;Changed?.Invoke();
                if(ack.Code==0)throw new IOException(peer.Name+": "+ack.Text);
            }
            // Keep the controlling PC online until clients have come back with the new version.
            var restarting=results.Where(r=>r.result.Code==1).Select(r=>r.peer).ToArray();
            var until=DateTime.UtcNow.AddSeconds(90);
            while(restarting.Any(p=>!network.Peers.Any(n=>n.Id==p.Id&&n.Version==release.Manifest.Version))&&DateTime.UtcNow<until)
                await Task.Delay(500);
            foreach(var peer in restarting)
            {
                bool ready=network.Peers.Any(n=>n.Id==peer.Id&&n.Version==release.Manifest.Version);
                Peers[peer.Id]=peer.Name+(ready?$": updated to {release.Manifest.Version}":": has not reconnected yet; check its status");
            }
            Changed?.Invoke();
            if(restarting.Any(p=>!network.Peers.Any(n=>n.Id==p.Id&&n.Version==release.Manifest.Version)))throw new IOException("Some PCs have not confirmed startup. The host has been kept running.");
            if(localNew)
            {
                SetStatus("Clients are ready. Saving the host replay…");
                await beforeInstall();await Updates.LaunchInstaller(prepared!);exit();
            }
            else SetStatus(peers.Any(p=>p.UpdateProtocol<1)?"Compatible PCs are updated. Older versions need a manual update first.":"All connected PCs are up to date.");
        }
        catch(Exception e){SetStatus(e.Message);}
        finally{Busy=false;Changed?.Invoke();}
    }
    async Task<KvmMessage> Request(KvmService network,string peer,KvmMessage command,TimeSpan timeout)
    {
        string key=peer+"|"+command.Id;
        var completion=new TaskCompletionSource<KvmMessage>(TaskCreationOptions.RunContinuationsAsynchronously);waiting.Add(key,completion);
        try{await network.SendControl(peer,command);return await completion.Task.WaitAsync(timeout);}
        finally{waiting.Remove(key);}
    }
    public void Receive(string peer,KvmMessage message)
    {
        if(ui.IsDisposed)return;
        try{ui.BeginInvoke(()=>Handle(peer,message));}catch(InvalidOperationException){}
    }
    async void Handle(string peer,KvmMessage message)
    {
        var network=service();if(network==null)return;
        if(network.Options.Role=="Host")
        {
            if(message.Type=="update-status"){Peers[peer]=(network.Peers.FirstOrDefault(p=>p.Id==peer)?.Name??"PC")+": "+message.Text;Changed?.Invoke();}
            else if(message.Type is "update-prepared" or "update-restarting")
                if(waiting.TryGetValue(peer+"|"+message.Id,out var result))result.TrySetResult(message);
            return;
        }
        if(network.Options.Role!="Client"||message.Type is not ("update-prepare" or "update-commit"))return;
        string response=message.Type=="update-prepare"?"update-prepared":"update-restarting";
        async Task Reply(int code,string text)=>await network.SendControl(peer,new(){Type=response,Id=message.Id,Code=code,Text=text,Version=Updates.VersionText});
        if(!options().AllowFromHost){try{await Reply(0,"Updates from the host are disabled in settings.");}catch{}return;}
        if(Busy){try{await Reply(0,"An update is already running on this PC.");}catch{}return;}
        Busy=true;Changed?.Invoke();
        try
        {
            if(message.Type=="update-prepare")
            {
                _=Updates.ParseVersion(message.Version);
                await Check();var release=Latest??throw new IOException("Release not found.");
                if(release.Manifest.Version!=message.Version)throw new IOException("The latest signed release differs from the requested version.");
                if(Updates.ParseVersion(message.Version)<=Updates.Current){await Reply(2,"Already installed: "+Updates.VersionText);return;}
                preparedRequest=message.Id;
                var progress=new Progress<string>(s=>{SetStatus(s);network.Send(peer,new(){Type="update-status",Id=message.Id,Text=s});});
                prepared=await Updates.Prepare(release,s=>((IProgress<string>)progress).Report(s));
                SetStatus("Ready for the host to start the update.");await Reply(1,"Package downloaded and verified");
            }
            else
            {
                if(prepared==null||preparedRequest!=message.Id||prepared.Manifest.Version!=message.Version)throw new IOException("No package has been prepared for this command.");
                SetStatus("Saving replay before the update…");await beforeInstall();await Updates.LaunchInstaller(prepared);
                await Reply(1,"Restarting…");exit();
            }
        }
        catch(Exception e){SetStatus("Update: "+e.Message);try{await Reply(0,e.Message);}catch{}}
        finally{Busy=false;Changed?.Invoke();}
    }
}
