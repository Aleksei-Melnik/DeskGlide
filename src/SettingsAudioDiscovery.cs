using NAudio.CoreAudioApi;

namespace SdrCapture;

// A slow audio driver must never hold the tray/UI thread or accumulate one worker per window.
static class SettingsAudioDiscovery
{
    static readonly object gate=new();
    static Task<DiscordDevices.Endpoint[]>? pending;
    public static Task<DiscordDevices.Endpoint[]> Load()
    {
        lock(gate)
            return pending is {IsCompleted:false}?pending:pending=Task.Run(()=>DiscordDevices.Endpoints().ToArray());
    }
}

partial class AppSettingsForm
{
    readonly Func<Task<DiscordDevices.Endpoint[]>> loadAudio;
    readonly CancellationTokenSource deviceLifetime=new();
    readonly Label deviceStatus=new(){Text="Аудиоустройства загружаются… Сохранённый выбор сохранится.",AutoSize=true,ForeColor=Color.FromArgb(67,85,108)};
    DiscordDevices.Endpoint[] audioEndpoints=[];
    bool refreshingAudio,updatingAudioChoices,devicesDisposed;

    static List<AudioChoice> DefaultAudioChoices()=>[new("","Silent — без звука"),new("default:capture","Микрофон Windows по умолчанию"),new("default:render","Выход Windows по умолчанию (звук ПК)")];

    static void ReplaceChoices(ComboBox box,List<AudioChoice> choices,string fallback="",string missing="Сохранённое устройство (недоступно)")
    {
        // Use the current edit, not the value from when discovery began.
        string selected=(box.SelectedItem as AudioChoice)?.Id??fallback;
        if(!choices.Any(c=>c.Id==selected))choices.Add(new(selected,missing));
        box.BeginUpdate();
        try{box.Items.Clear();box.Items.AddRange(choices.ToArray());box.SelectedItem=choices.First(c=>c.Id==selected);}
        finally{box.EndUpdate();}
    }

    async Task RefreshAudioDevices()
    {
        if(refreshingAudio||IsDisposed)return;
        refreshingAudio=true;deviceStatus.Text="Загружаем аудиоустройства… Настройками уже можно пользоваться.";
        try
        {
            var endpoints=await loadAudio().WaitAsync(TimeSpan.FromSeconds(8),deviceLifetime.Token);
            if(IsDisposed)return;
            audioEndpoints=endpoints;updatingAudioChoices=true;
            try
            {
                var choices=DefaultAudioChoices();
                choices.AddRange(endpoints.OrderBy(e=>e.Flow==DataFlow.Capture?0:1).Select(e=>new AudioChoice(e.Id,$"{(e.Flow==DataFlow.Capture?"Вход":"Выход")}: {e.Name}")));
                foreach(var box in new[]{ndiAudio,mic,game,remoteAudio})ReplaceChoices(box,[..choices]);
                foreach(var peer in service?.Peers??[])choices.Add(new("kvm:"+peer.Id,peer.Name+" · звук по KVM"));
                ReplaceChoices(extra,choices);
                ReplaceChoices(discordOutput,endpoints.Where(e=>e.Flow==DataFlow.Render).OrderByDescending(DiscordDevices.IsCable).Select(e=>new AudioChoice(e.Id,e.Name)).Prepend(new AudioChoice("","Silent — без звука")).ToList());
                ReplaceChoices(discordInput,endpoints.Where(e=>e.Flow==DataFlow.Capture).Select(e=>new AudioChoice(e.Id,e.Name)).Prepend(new AudioChoice("","Выберите вход для Discord…")).ToList());
                // A refresh never changes the configured input. Pairing is only done on an explicit output change.
            }
            finally{updatingAudioChoices=false;}
            deviceStatus.Text="Аудиоустройства обновлены.";
        }
        catch(OperationCanceledException){}
        catch(Exception e)
        {
            if(IsDisposed)return;
            deviceStatus.Text="Аудиоустройства не ответили. Сохранённый выбор оставлен; можно повторить обновление.";
            Log.Write("Settings audio discovery: "+e.Message);
        }
        finally{refreshingAudio=false;}
    }
}
