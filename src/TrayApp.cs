using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace SdrCapture;
sealed class Settings
{
    public string Language {get;set;}=System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="ru"?"ru":"en";
    public string Device {get;set;}="";
    public bool Compensate {get;set;}=true;
    public bool CaptureCursor {get;set;}=true;
    public bool PreventIdleSleep {get;set;}=true;
    public int NdiAudioVolume {get;set;}=50;
    public UpdateOptions Updates {get;set;}=new();
    public string NdiAudioDevice {get;set;}="";
    public bool SendOnLaunch {get;set;}=false;
    public ReplayOptions Replay {get;set;}=new();
    public KvmOptions Kvm {get;set;}=new();
    public DiscordOptions Discord {get;set;}=new();
    public static string PathName=>Path.Combine(Log.Folder,"settings.json");
    public static Settings Load(){try{return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName))??new();}catch{return new();}}
    public void Save(){Directory.CreateDirectory(Log.Folder);File.WriteAllText(PathName+".tmp",JsonSerializer.Serialize(this));File.Move(PathName+".tmp",PathName,true);}
    public Settings Copy()=>new(){Language=Language,Device=Device,Compensate=Compensate,CaptureCursor=CaptureCursor,PreventIdleSleep=PreventIdleSleep,SendOnLaunch=SendOnLaunch,NdiAudioDevice=NdiAudioDevice,NdiAudioVolume=NdiAudioVolume,Updates=Updates with{},Replay=Replay with{},Kvm=Kvm.Copy(),Discord=Discord with{}};
    [System.Text.Json.Serialization.JsonIgnore] public CaptureOptions Options=>new(Device,Compensate,CaptureCursor);
}
sealed class TrayApp:ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Icon appIcon=LoadAppIcon();
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly Settings settings=Settings.Load();
    readonly KeepAwake keepAwake=new();
    readonly StreamEngine engine=new();
    readonly ReplayRecorder replay;
    readonly ReplayDelivery delivery=new();
    readonly ReplaySaveSound saveSound=new();
    readonly ReplayHotkey hotkey=new();
    readonly EventWaitHandle saveEvent=new(false,EventResetMode.AutoReset,"Local\\SdrCapture.SaveReplay");
    readonly System.Collections.Concurrent.ConcurrentQueue<string> notifications=new();
    readonly RegisteredWaitHandle saveWait;
    readonly Control dispatcher=new();
    KvmService? kvm;
    KvmController? controller;
    KvmClipboard? clipboard;
    KvmDragDrop? dragDrop;
    DiscordReceiver? discord;
    readonly UpdateCoordinator updater;
    Ui.UpdatesWindow? updatesForm;
    Ui.KvmWindow? kvmHub;
    Ui.SettingsWindow? settingsWindow;
    bool settingsOpenQueued;
    readonly Dictionary<string,Ui.ViewerWindow> viewers=[];
    readonly EventWaitHandle kvmEvent=new(false,EventResetMode.AutoReset,"Local\\SdrCapture.OpenKvm");
    readonly RegisteredWaitHandle kvmWait;
    readonly EventWaitHandle updateEvent=new(false,EventResetMode.AutoReset,"Local\\SdrCapture.UpdateThis");
    readonly RegisteredWaitHandle updateWait;
    int ticks;
    bool closing;
    bool restartQueued;
    string? lastReplayError;
    const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public TrayApp()
    {
        try
        {
            var displays=DisplayInfo.All();
            settings.Device=(displays.FirstOrDefault(d=>d.Device==settings.Device)??displays.FirstOrDefault())?.Device??settings.Device;
        }
        catch(Exception e){Log.Write("Initial display enumeration will be retried: "+e.Message);}
        if(string.IsNullOrEmpty(settings.Device)) settings.Device=@"\\.\DISPLAY1";
        settings.Save();
        // Follow the executable the user actually launched, keeping an existing
        // autorun preference when switching from a folder to the portable file.
        try{if(PortableResources.Bundled&&IsAutorun())
        {using var run=Registry.CurrentUser.CreateSubKey(RunKey);run.SetValue("DeskGlide",$"\"{Environment.ProcessPath}\"");run.DeleteValue("ScreenCapture",false);run.DeleteValue("SdrCapture",false);}}
        catch(Exception e){Log.Write("Portable autorun migration: "+e.Message);}
        try{keepAwake.Set(settings.PreventIdleSleep);}catch(Exception e){Log.Write(e.ToString());notifications.Enqueue(e.Message);}
        replay=new ReplayRecorder(settings.Replay);
        engine.FrameAvailable=replay.Offer;engine.NdiEnabled=settings.SendOnLaunch;engine.NdiAudioDevice=settings.NdiAudioDevice;engine.NdiAudioVolume=settings.NdiAudioVolume;engine.ReplayEnabled=settings.Replay.Enabled;engine.ReplayFrameRate=settings.Replay.Fps;
        _=dispatcher.Handle;
        updater=new(dispatcher,()=>kvm,()=>settings.Updates,()=>
        {
            controller?.ReturnLocal();settings.Save();
            return Task.CompletedTask;
        },()=>ExitThread());
        updateWait=ThreadPool.RegisterWaitForSingleObject(updateEvent,(_,_)=>{if(!closing)try{dispatcher.BeginInvoke(async()=>await updater.InstallHere(true));}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        kvmWait=ThreadPool.RegisterWaitForSingleObject(kvmEvent,(_,_)=>{if(!closing)try{dispatcher.BeginInvoke(OpenKvm);}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        saveWait=ThreadPool.RegisterWaitForSingleObject(saveEvent,(_,_)=>{if(!closing)try{dispatcher.BeginInvoke(SaveReplay);}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        hotkey.SaveRequested+=SaveReplay;
        if(!hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey))notifications.Enqueue("Горячая клавиша занята. Выберите другую в Replay Buffer → Settings.");
        delivery.Notification+=message=>notifications.Enqueue(message);
        delivery.Saved+=_=>
        {
            if(!closing)try{dispatcher.BeginInvoke(()=>{if(!closing&&settings.Replay.SaveSoundEnabled)saveSound.Play(settings.Replay.SaveSoundVolume);});}catch(InvalidOperationException){}
        };
        UiStrings.Shared.Language=settings.Language;
        tray=new NotifyIcon{Icon=appIcon,Text=BuildTooltip(engine.NdiEnabled,settings.Replay.Enabled),Visible=true,ContextMenuStrip=new ContextMenuStrip()};
        TrayMenuStyle.Apply(tray.ContextMenuStrip);
        tray.ContextMenuStrip.Opening+=(_,_)=>BuildMenu();
        tray.DoubleClick+=(_,_)=>OpenSettings();
        timer.Tick+=(_,_)=>
        {
            tray.Text=BuildTooltip(engine.NdiEnabled,settings.Replay.Enabled);
            ReplayAppContext.Capture();
            if(++ticks%5==0){kvm?.RefreshScreens();controller?.Refresh();}
            if(ticks==8&&settings.Updates.CheckOnStartup&&!updater.Busy)_=CheckForUpdatesQuietly();
            if(replay.Error!=null&&replay.Error!=lastReplayError){notifications.Enqueue("Проблема записи повтора: "+replay.Error);lastReplayError=replay.Error;}
            if(notifications.TryDequeue(out string? message))tray.ShowBalloonTip(5000,"DeskGlide",message[..Math.Min(255,message.Length)],ToolTipIcon.Info);
        };
        timer.Start();engine.Start(settings.Options);StartKvm();StartDiscord();
        _=Task.Run(async()=>{try{await RecordingTools.MigrateLegacyAsync();}catch(Exception e){Log.Write("Recording tools migration: "+e.Message);}});
        if(settings.Kvm.Role!="Host")_=Task.Run(async()=>{for(int i=0;i<30&&!closing;i++){try{DiscordDevices.RecoverDefaults(settings.Kvm.Role);}catch(Exception e){Log.Write("Audio default recovery: "+e.Message);}await Task.Delay(1000);}});
    }
    internal static string BuildTooltip(bool streaming,bool replay)
    {
        string text=$"DeskGlide · {UiStrings.T("Streaming")}: {UiStrings.T(streaming?"On":"Off")} · {UiStrings.T("Replay")}: {UiStrings.T(replay?"On":"Off")}";
        return text[..Math.Min(63,text.Length)];
    }
    static Icon LoadAppIcon()
    {
        using var stream=typeof(TrayApp).Assembly.GetManifestResourceStream("SdrCapture.AppIcon.ico")
            ?? throw new InvalidOperationException("Application icon resource is missing.");
        using var icon=new Icon(stream,SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
    void Guard(Action action){try{action();}catch(Exception e){Log.Write(e.ToString());MessageBox.Show(e.Message,"DeskGlide",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    void BuildMenu()
    {
        var items=tray.ContextMenuStrip!.Items;
        while(items.Count>0){var item=items[0];items.RemoveAt(0);item.Dispose();}
        items.Add(UiStrings.T("Settings…"),null,(_,_)=>OpenSettings());
        items.Add(UiStrings.T("Computers · KVM…"),null,(_,_)=>dispatcher.BeginInvoke(OpenKvm));
        items.Add(new ToolStripSeparator());
        var ndiToggle=new ToolStripMenuItem(UiStrings.T("Screen streaming")){Checked=settings.SendOnLaunch};
        ndiToggle.Click+=(_,_)=>Guard(()=>{settings.SendOnLaunch=!settings.SendOnLaunch;settings.Save();engine.NdiEnabled=settings.SendOnLaunch;engine.NdiAudioDevice=settings.NdiAudioDevice;engine.NdiAudioVolume=settings.NdiAudioVolume;});items.Add(ndiToggle);
        var replayToggle=new ToolStripMenuItem(UiStrings.T("Instant replay")){Checked=settings.Replay.Enabled};
        replayToggle.Click+=(_,_)=>Guard(()=>ApplyReplay(settings.Replay with{Enabled=!settings.Replay.Enabled}));items.Add(replayToggle);
        var save=new ToolStripMenuItem(UiStrings.T("Save replay")){Enabled=replay.BufferedSeconds>0,ShortcutKeyDisplayString=ReplayHotkey.Text(settings.Replay),ToolTipText=UiStrings.F("Save the last {0}",ReplayTime.Format(settings.Replay.BufferSeconds))};
        save.Click+=(_,_)=>SaveReplay();items.Add(save);
        items.Add(UiStrings.T("Open clips folder"),null,async(_,_)=>{try{await RecordingFolder.Open(settings.Replay.Folder);}catch(Exception e){notifications.Enqueue(UiStrings.T("Could not open the recordings folder: ")+e.Message);}});
        items.Add(new ToolStripSeparator());items.Add(UiStrings.T("Updates…"),null,(_,_)=>OpenUpdates());
        var restart=new ToolStripMenuItem(UiStrings.T("Restart DeskGlide")){Enabled=!closing&&!restartQueued&&!updater.Busy};
        restart.Click+=(_,_)=>Restart();items.Add(restart);
        items.Add(UiStrings.T("Quit DeskGlide"),null,(_,_)=>ExitThread());
    }
    void Restart()
    {
        if(closing||restartQueued||updater.Busy)return;
        restartQueued=true;
        dispatcher.BeginInvoke(()=>
        {
            try{if(!closing&&!updater.Busy)Guard(()=>{settings.Save();ApplicationRestart.Start();ExitThread();});}
            finally{restartQueued=false;}
        });
    }
    void OpenSettings(int page=0)
    {
        if(closing||settingsOpenQueued)return;
        settingsOpenQueued=true;
        // Let the tray popup finish closing before showing/activating a top-level window.
        dispatcher.BeginInvoke(()=>
        {
            try{if(!closing)ShowSettings(page);}
            finally{settingsOpenQueued=false;}
        });
    }
    void ShowSettings(int page)=>Guard(()=>
    {
        var opening=Stopwatch.StartNew();
        controller?.ReturnLocal();
        var existing=settingsWindow is {IsClosed:false}?settingsWindow:null;
        if(existing!=null){existing.Reveal();return;}
        var form=new Ui.SettingsWindow(settings,kvm,IsAutorun(),Diagnostics,OpenUpdates,page,overview:StatusOverview);settingsWindow=form;
        form.Closed+=(_,_)=>{settingsWindow=null;if(form.Accepted)Guard(()=>ApplySettings(form));};
        form.Reveal();
        Log.Write($"Settings window shown in {opening.ElapsedMilliseconds} ms");
    });
    void ApplySettings(Ui.SettingsWindow form)
    {
        var result=form.ResultSettings;
        bool discordChanged=settings.Discord!=result.Discord||settings.Kvm.Role!=result.Kvm.Role;
        if(!hotkey.Set(result.Replay.HotkeyModifiers,result.Replay.HotkeyKey))
        {hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey);throw new InvalidOperationException("Клавиша сохранения занята другой программой.");}
        string priorKvm=JsonSerializer.Serialize(settings.Kvm with{Layout=[],RemoteOnlyPeers=[],ExcludedMonitors=[],Seamless=true,BlockScreenCorners=false});
        if(form.ImportedProfile!=null)
        {
            try{ConfigurationBackup.Restore(form.ImportedProfile,Log.Folder);}
            catch{hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey);throw;}
        }
        keepAwake.Set(result.PreventIdleSleep);settings.PreventIdleSleep=result.PreventIdleSleep;
        settings.Language=result.Language;UiStrings.Shared.Language=settings.Language;settings.Device=result.Device;settings.Compensate=result.Compensate;settings.CaptureCursor=result.CaptureCursor;settings.SendOnLaunch=result.SendOnLaunch;settings.NdiAudioDevice=result.NdiAudioDevice;settings.NdiAudioVolume=result.NdiAudioVolume;settings.Updates=result.Updates;settings.Replay=result.Replay;settings.Kvm=result.Kvm;settings.Save();
        engine.Update(settings.Options);engine.NdiEnabled=settings.SendOnLaunch;engine.NdiAudioDevice=settings.NdiAudioDevice;engine.NdiAudioVolume=settings.NdiAudioVolume;engine.ReplayEnabled=settings.Replay.Enabled;engine.ReplayFrameRate=settings.Replay.Fps;replay.Update(settings.Replay);
        if(IsAutorun()!=form.StartWithWindows)
        {
            using var key=Registry.CurrentUser.CreateSubKey(RunKey);
            if(form.StartWithWindows)key.SetValue("DeskGlide",$"\"{Environment.ProcessPath}\"");else key.DeleteValue("DeskGlide",false);key.DeleteValue("ScreenCapture",false);key.DeleteValue("SdrCapture",false);
        }
        if(form.ImportedProfile!=null||priorKvm!=JsonSerializer.Serialize(settings.Kvm with{Layout=[],RemoteOnlyPeers=[],ExcludedMonitors=[],Seamless=true,BlockScreenCorners=false}))StartKvm();else controller?.UpdateLayout(settings.Kvm);
        settings.Discord=result.Discord;settings.Save();if(discordChanged)StartDiscord();
        SelectRemoteAudio();notifications.Enqueue("Настройки применены.");
    }
    string Diagnostics()=>$"{engine.SourceName}\r\n{engine.Status}\r\n{engine.CaptureStatus}\r\nЗахват: {engine.CaptureMs:F1} мс; NDI: {engine.SendMs:F1} мс\r\n\r\n{replay.Status}\r\n{replay.AudioStatus}\r\nNDI audio: {engine.NdiAudioStatus}\r\nБуфер: {replay.CacheBytes/1048576.0:F0} МБ\r\nКадры записи: {replay.EncodedFrames}; повторы: {replay.RepeatedInputFrames}\r\nПримечание: неподвижный экран тоже даёт повторы.\r\nЗапусков кодировщика: {replay.EncoderStarts}\r\n{replay.Error}\r\n{delivery.Status}\r\n\r\nDiscord: {discord?.Status??"выключен"}\r\n\r\nKVM: {kvm?.Status}\r\n{string.Join("\r\n",kvm?.Peers.Select(p=>p.Name+" · экранов: "+p.Screens.Length)??[])}";
    async Task CheckForUpdatesQuietly()
    {
        try{await updater.Check();if(updater.Latest!=null&&Updates.ParseVersion(updater.Latest.Manifest.Version)>Updates.Current)notifications.Enqueue("Доступна DeskGlide "+updater.Latest.Manifest.Version+". Откройте «Обновления».");}
        catch(Exception e){updater.SetStatus("Проверка обновлений недоступна: "+e.Message);}
    }
    void OpenUpdates()
    {
        if(updatesForm is {IsClosed:false}){updatesForm.Reveal();return;}
        controller?.ReturnLocal();updatesForm=new(updater,()=>kvm);updatesForm.Reveal();
    }
    void OpenViewer(KvmPeerInfo peer)
    {
        if(kvm==null||settings.Kvm.Role!="Host")return;
        controller?.ReturnLocal();
        if(viewers.TryGetValue(peer.Id,out var existing)&&!existing.IsClosed){existing.Reveal();return;}
        var viewer=new Ui.ViewerWindow(kvm,peer);viewers[peer.Id]=viewer;viewer.Activated+=(_,_)=>{controller?.ReturnLocal();if(controller!=null)controller.ViewerActive=true;};viewer.Deactivated+=(_,_)=>{if(controller!=null)controller.ViewerActive=false;};viewer.Closed+=(_,_)=>{viewers.Remove(peer.Id);if(controller!=null)controller.ViewerActive=false;};viewer.Reveal();
    }
    public void OpenKvm()=>Guard(()=>
    {
        controller?.ReturnLocal();
        if(kvmHub is {IsClosed:false}){kvmHub.Reveal();return;}
        kvmHub=new(()=>kvm,()=>controller?.Seamless??false,enabled=>{if(controller!=null){controller.Seamless=enabled;settings.Kvm.Seamless=enabled;settings.Save();}},()=>{controller?.ReturnLocal();foreach(var viewer in viewers.Values.ToArray())viewer.ReturnControl();},OpenViewer,OpenSettings,p=>p.RemoteViewOnly||settings.Kvm.RemoteOnlyPeers.Contains(p.Id),(p,only)=>{settings.Kvm.RemoteOnlyPeers.Remove(p.Id);if(only)settings.Kvm.RemoteOnlyPeers.Add(p.Id);settings.Save();controller?.UpdateLayout(settings.Kvm);});kvmHub.Reveal();
    });
    void StartKvm()
    {
        foreach(var viewer in viewers.Values.ToArray())viewer.Close();
        dragDrop?.Dispose();dragDrop=null;clipboard?.Dispose();controller?.Dispose();kvm?.Dispose();clipboard=null;controller=null;kvm=null;
        try
        {
            kvm=new KvmService(settings.Kvm);kvm.Notification+=m=>notifications.Enqueue(m);kvm.Received+=updater.Receive;
            controller=new KvmController(kvm,settings.Kvm);
            controller.OpenRequested+=()=>{if(!closing)try{dispatcher.BeginInvoke(OpenKvm);}catch(InvalidOperationException){}};
            controller.EmergencyReturn+=()=>{if(!closing)dispatcher.BeginInvoke(()=>{foreach(var viewer in viewers.Values.ToArray())viewer.ReturnControl();});};
            controller.SeamlessChanged+=enabled=>{if(!closing)dispatcher.BeginInvoke(()=>{settings.Kvm.Seamless=enabled;settings.Save();});};
            kvm.Disconnected+=_=>{if(!closing)try{dispatcher.BeginInvoke(()=>controller?.ReturnLocal());}catch(InvalidOperationException){}};
            kvm.PeersChanged+=()=>{if(!closing)try{dispatcher.BeginInvoke(()=>controller?.Refresh());}catch(InvalidOperationException){}};
            if(settings.Kvm.Role!="Off")
            {
                clipboard=new KvmClipboard(kvm,settings.Kvm,dispatcher);clipboard.Notification+=m=>notifications.Enqueue(m);
                if(settings.Kvm.ClipboardFiles){dragDrop=new(kvm,clipboard,controller,settings.Kvm,dispatcher);dragDrop.Notification+=m=>notifications.Enqueue(m);}
            }
            SelectRemoteAudio();
        }
        catch(Exception e){notifications.Enqueue("KVM: "+e.Message);Log.Write(e.ToString());}
    }
    void SelectRemoteAudio()=>kvm?.SelectAudio(settings.Replay.Enabled&&!settings.Replay.IsSilent&&settings.Replay.ExtraAudio.StartsWith("kvm:")?settings.Replay.ExtraAudio[4..]:null);
    void StartDiscord()
    {
        discord?.Dispose();discord=null;
        if(settings.Kvm.Role=="Host")return;
        try{if(CameraInstallation.Installed)CameraInstallation.Install(settings.Kvm.Role,settings.Discord.CameraName);}
        catch(Exception e){notifications.Enqueue("Камера Discord: "+e.Message);}
        if(!settings.Discord.Enabled)return;
        try{discord=new(settings.Discord);}
        catch(Exception e){notifications.Enqueue("Discord: "+e.Message);}
    }
    static bool IsAutorun(){using var key=Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("DeskGlide")!=null||key?.GetValue("ScreenCapture")!=null||key?.GetValue("SdrCapture")!=null;}
    public static string ConnectionText=>"На стрим-ПК: OBS → Источники → NDI Source (DistroAV).\nИсточник: ИМЯ-ИГРОВОГО-ПК (SdrCapture SDR).\n\nYUV Range: Limited\nYUV Color Space: BT.709\nLatency Mode: Low\nBandwidth: Highest\nBehavior: Always play when not visible (Keepalive)\nFramesync: выключено\nEnable audio: выключено\n\nЭто NDI High Bandwidth, не HX/HEVC. Нужна проводная локальная сеть.\nВ OBS → Настройки → Расширенные → Видео оставьте SDR Rec.709.\n\nНа приёмник передаётся SDR независимо от режима HDR игрового монитора.\nПолная задержка зависит также от сети и OBS; нулевая задержка не гарантируется.";
    void ShowHelp()=>MessageBox.Show(ConnectionText,"DeskGlide → DistroAV");
    Ui.StatusSection[] StatusOverview()=>[
        new("Screen streaming",("State",UiStrings.T(!engine.NdiEnabled?"Off":engine.Error!=null?"Recovering capture":"On")),("NDI source",engine.SourceName),("Output",string.Join(", ",engine.CaptureSizes)),("Delivery rate",$"{engine.Fps:F1} FPS"),("Capture time",$"{engine.CaptureMs:F1} ms")),
        new("Instant replay",("State",UiStrings.T(!settings.Replay.Enabled?"Off":replay.Error!=null?"Recording error":!RecordingTools.Ready?"Setting up recording tools…":"On")),("Buffered",$"{ReplayTime.Format(replay.BufferedSeconds)} / {ReplayTime.Format(settings.Replay.BufferSeconds)}"),("Buffer size",$"{replay.CacheBytes/1048576.0:F0} MB"),("Error",replay.Error??UiStrings.T("None"))),
        new("KVM & pairing",("This PC",Environment.MachineName),("Role",UiStrings.T(settings.Kvm.Role)),("Connected computers",string.Join(", ",kvm?.Peers.Select(p=>p.Name)??[]) is {Length:>0} peers?peers:UiStrings.T("None"))),
        new("Virtual devices",("Receiver",UiStrings.T(settings.Discord.Enabled?"On":"Off")),("Video",discord?.Status??UiStrings.T("Off")))
    ];
    void ApplyReplay(ReplayOptions options)
    {
        options.Validate();
        if(!hotkey.Set(options.HotkeyModifiers,options.HotkeyKey))
        {
            hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey);
            throw new InvalidOperationException("Это сочетание клавиш занято другой программой.");
        }
        settings.Replay=options;settings.Save();engine.ReplayEnabled=options.Enabled;engine.ReplayFrameRate=options.Fps;replay.Update(options);SelectRemoteAudio();
        notifications.Enqueue(options.Enabled?"Настройки повтора применены.":"Буфер повтора выключен.");
    }
    async void SaveReplay()
    {
        if(closing)return;
        try{string app=ReplayAppContext.Capture();notifications.Enqueue(UiStrings.F("Saving the last {0}…",ReplayTime.Format(settings.Replay.BufferSeconds)));var result=await replay.SaveAsync(app);delivery.Wake();}
        catch(Exception e){notifications.Enqueue("Повтор не сохранён: "+e.Message);}
    }
    protected override void ExitThreadCore(){closing=true;settingsWindow?.Close();keepAwake.Dispose();timer.Stop();discord?.Dispose();kvmHub?.Close();foreach(var viewer in viewers.Values.ToArray())viewer.Close();kvmWait.Unregister(null);kvmEvent.Dispose();updatesForm?.Close();updateWait.Unregister(null);updateEvent.Dispose();dragDrop?.Dispose();clipboard?.Dispose();controller?.Dispose();kvm?.Dispose();saveWait.Unregister(null);saveEvent.Dispose();hotkey.Dispose();engine.FrameAvailable=null;engine.Dispose();replay.Dispose();delivery.Dispose();saveSound.Dispose();dispatcher.Dispose();tray.Visible=false;tray.Dispose();appIcon.Dispose();timer.Dispose();base.ExitThreadCore();}
}
