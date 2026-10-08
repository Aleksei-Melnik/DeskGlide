namespace SdrCapture;

sealed class UpdateCoordinator(Control ui,Func<KvmService?> service,Func<UpdateOptions> options,Func<Task> beforeInstall,Action exit)
{
    readonly Dictionary<string,TaskCompletionSource<KvmMessage>> waiting=[];
    PreparedUpdate? prepared;
    string preparedRequest="";
    public bool Busy {get;private set;}
    public string Status {get;private set;}="Обновления ещё не проверялись.";
    public AvailableRelease? Latest {get;private set;}
    public Dictionary<string,string> Peers {get;}=[];
    public event Action? Changed;
    public void SetStatus(string value){Status=UiStrings.T(value);Changed?.Invoke();}
    public async Task Check()
    {
        SetStatus("Проверяю GitHub Releases…");Latest=await Updates.Check();
        SetStatus(Latest==null?"Опубликованных релизов пока нет.":Updates.ParseVersion(Latest.Manifest.Version)>Updates.Current?UiStrings.F("Version {0} is available",Latest.Manifest.Version):UiStrings.F("Current version: {0}",Updates.VersionText));
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
            await Check();var release=Latest??throw new IOException("Пока нет релиза.");
            if(!repair&&Updates.ParseVersion(release.Manifest.Version)<=Updates.Current)return;
            await PrepareLocal(release);SetStatus("Готовлю перезапуск…");
            await beforeInstall();await Updates.LaunchInstaller(prepared!);exit();
        }
        catch(Exception e){SetStatus("Обновление не установлено: "+e.Message);}
        finally{Busy=false;Changed?.Invoke();}
    }
    public async Task InstallAll()
    {
        if(Busy)return;Busy=true;Changed?.Invoke();
        try
        {
            var network=service();if(network?.Options.Role!="Host")throw new IOException("Обновление всех ПК запускается с управляющего ПК KVM.");
            await Check();var release=Latest??throw new IOException("Пока нет релиза.");
            var peers=network.Peers.ToArray();Peers.Clear();
            foreach(var old in peers.Where(p=>p.UpdateProtocol<1))Peers[old.Id]=$"{old.Name}: нужна первая установка ScreenCapture 0.6 вручную";
            var compatible=peers.Where(p=>p.UpdateProtocol>=1).ToArray();
            bool localNew=Updates.ParseVersion(release.Manifest.Version)>Updates.Current;
            if(localNew)await PrepareLocal(release);
            string request=Guid.NewGuid().ToString("N");
            var results=await Task.WhenAll(compatible.Select(async peer=>
            {
                Peers[peer.Id]=peer.Name+": подготовка";Changed?.Invoke();
                var result=await Request(network,peer.Id,new(){Type="update-prepare",Id=request,Version=release.Manifest.Version},TimeSpan.FromMinutes(10));
                Peers[peer.Id]=peer.Name+": "+result.Text;Changed?.Invoke();return(peer,result);
            }));
            if(results.Any(r=>r.result.Code==0))throw new IOException("Не все ПК готовы. Ничего не перезапущено; причины показаны в списке.");
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
                Peers[peer.Id]=peer.Name+(ready?$": обновлён до {release.Manifest.Version}":": ещё не вернулся в сеть; проверьте его состояние");
            }
            Changed?.Invoke();
            if(restarting.Any(p=>!network.Peers.Any(n=>n.Id==p.Id&&n.Version==release.Manifest.Version)))throw new IOException("Подтверждение запуска получено не от всех ПК. Управляющий ПК оставлен включённым.");
            if(localNew)
            {
                SetStatus("Подключённые ПК готовы. Перезапускаю управляющий ПК…");
                await beforeInstall();await Updates.LaunchInstaller(prepared!);exit();
            }
            else SetStatus(peers.Any(p=>p.UpdateProtocol<1)?"Совместимые ПК обновлены. Для старых версий нужна первая установка.":"Все подключённые ПК используют актуальный релиз.");
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
            if(message.Type=="update-status"){Peers[peer]=(network.Peers.FirstOrDefault(p=>p.Id==peer)?.Name??"ПК")+": "+message.Text;Changed?.Invoke();}
            else if(message.Type is "update-prepared" or "update-restarting")
                if(waiting.TryGetValue(peer+"|"+message.Id,out var result))result.TrySetResult(message);
            return;
        }
        if(network.Options.Role!="Client"||message.Type is not ("update-prepare" or "update-commit"))return;
        string response=message.Type=="update-prepare"?"update-prepared":"update-restarting";
        async Task Reply(int code,string text)=>await network.SendControl(peer,new(){Type=response,Id=message.Id,Code=code,Text=text,Version=Updates.VersionText});
        if(!options().AllowFromHost){try{await Reply(0,"Обновления с управляющего ПК выключены в настройках.");}catch{}return;}
        if(Busy){try{await Reply(0,"На ПК уже выполняется обновление.");}catch{}return;}
        Busy=true;Changed?.Invoke();
        try
        {
            if(message.Type=="update-prepare")
            {
                _=Updates.ParseVersion(message.Version);
                await Check();var release=Latest??throw new IOException("Релиз не найден.");
                if(release.Manifest.Version!=message.Version)throw new IOException("Последний подписанный релиз отличается от запрошенного.");
                if(Updates.ParseVersion(message.Version)<=Updates.Current){await Reply(2,"Уже установлена "+Updates.VersionText);return;}
                preparedRequest=message.Id;
                var progress=new Progress<string>(s=>{SetStatus(s);network.Send(peer,new(){Type="update-status",Id=message.Id,Text=s});});
                prepared=await Updates.Prepare(release,s=>((IProgress<string>)progress).Report(s));
                SetStatus("Готово к обновлению по команде управляющего ПК.");await Reply(1,"Пакет скачан и проверен");
            }
            else
            {
                if(prepared==null||preparedRequest!=message.Id||prepared.Manifest.Version!=message.Version)throw new IOException("Пакет для этой команды не подготовлен.");
                SetStatus("Готовлю перезапуск…");await beforeInstall();await Updates.LaunchInstaller(prepared);
                await Reply(1,"Перезапуск…");exit();
            }
        }
        catch(Exception e){SetStatus("Обновление: "+e.Message);try{await Reply(0,e.Message);}catch{}}
        finally{Busy=false;Changed?.Invoke();}
    }
}
