using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

namespace SdrCapture;
sealed class Settings
{
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
    public Settings Copy()=>new(){Device=Device,Compensate=Compensate,CaptureCursor=CaptureCursor,PreventIdleSleep=PreventIdleSleep,SendOnLaunch=SendOnLaunch,NdiAudioDevice=NdiAudioDevice,NdiAudioVolume=NdiAudioVolume,Updates=Updates with{},Replay=Replay with{},Kvm=Kvm.Copy(),Discord=Discord with{}};
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
        try{keepAwake.Set(settings.PreventIdleSleep);}catch(Exception e){Log.Write(e.ToString());notifications.Enqueue(e.Message);}
        replay=new ReplayRecorder(settings.Replay);
        engine.FrameAvailable=replay.Offer;engine.NdiEnabled=settings.SendOnLaunch;engine.NdiAudioDevice=settings.NdiAudioDevice;engine.NdiAudioVolume=settings.NdiAudioVolume;engine.ReplayEnabled=settings.Replay.Enabled;engine.ReplayFrameRate=settings.Replay.Fps;
        _=dispatcher.Handle;
        updater=new(dispatcher,()=>kvm,()=>settings.Updates,async()=>
        {
            controller?.ReturnLocal();settings.Save();
            if(settings.Replay.Enabled&&replay.BufferedSeconds>0){await replay.SaveAsync(ReplayAppContext.Capture());delivery.Wake();}
        },()=>ExitThread());
        updateWait=ThreadPool.RegisterWaitForSingleObject(updateEvent,(_,_)=>{if(!closing)try{dispatcher.BeginInvoke(async()=>await updater.InstallHere(true));}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        kvmWait=ThreadPool.RegisterWaitForSingleObject(kvmEvent,(_,_)=>{if(!closing)try{dispatcher.BeginInvoke(OpenKvm);}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        saveWait=ThreadPool.RegisterWaitForSingleObject(saveEvent,(_,_)=>{if(!closing)try{dispatcher.BeginInvoke(SaveReplay);}catch(InvalidOperationException){}},null,Timeout.Infinite,false);
        hotkey.SaveRequested+=SaveReplay;
        if(!hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey))notifications.Enqueue("The replay shortcut is in use. Choose another in Settings → Instant replay.");
        delivery.Notification+=message=>notifications.Enqueue(message);
        tray=new NotifyIcon{Icon=appIcon,Text="ScreenCapture · Stream · Replay · KVM",Visible=true,ContextMenuStrip=new ContextMenuStrip()};
        tray.ContextMenuStrip.Opening+=(_,_)=>BuildMenu();
        tray.DoubleClick+=(_,_)=>OpenSettings();
        timer.Tick+=(_,_)=>
        {
            string text=$"ScreenCapture · NDI {(engine.NdiEnabled?"ON":"OFF")} · Replay {(settings.Replay.Enabled?"ON":"OFF")}";tray.Text=text[..Math.Min(63,text.Length)];
            ReplayAppContext.Capture();
            if(++ticks%5==0){kvm?.RefreshScreens();controller?.Refresh();}
            if(ticks==8&&settings.Updates.CheckOnStartup&&!updater.Busy)_=CheckForUpdatesQuietly();
            if(replay.Error!=null&&replay.Error!=lastReplayError){notifications.Enqueue("Replay recording error: "+replay.Error);lastReplayError=replay.Error;}
            if(notifications.TryDequeue(out string? message))tray.ShowBalloonTip(5000,"ScreenCapture",message[..Math.Min(255,message.Length)],ToolTipIcon.Info);
        };
        timer.Start();engine.Start(settings.Options);StartKvm();StartDiscord();
        if(settings.Kvm.Role!="Host")_=Task.Run(async()=>{for(int i=0;i<30&&!closing;i++){try{DiscordDevices.RecoverDefaults(settings.Kvm.Role);}catch(Exception e){Log.Write("Audio default recovery: "+e.Message);}await Task.Delay(1000);}});
    }
    static Icon LoadAppIcon()
    {
        using var stream=typeof(TrayApp).Assembly.GetManifestResourceStream("SdrCapture.AppIcon.ico")
            ?? throw new InvalidOperationException("Application icon resource is missing.");
        using var icon=new Icon(stream,SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
    void Guard(Action action){try{action();}catch(Exception e){Log.Write(e.ToString());MessageBox.Show(e.Message,"ScreenCapture",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
    void BuildMenu()
    {
        var items=tray.ContextMenuStrip!.Items;
        while(items.Count>0){var item=items[0];items.RemoveAt(0);item.Dispose();}
        var ndiToggle=new ToolStripMenuItem("Screen streaming"){Checked=settings.SendOnLaunch};
        ndiToggle.Click+=(_,_)=>Guard(()=>{settings.SendOnLaunch=!settings.SendOnLaunch;settings.Save();engine.NdiEnabled=settings.SendOnLaunch;engine.NdiAudioDevice=settings.NdiAudioDevice;engine.NdiAudioVolume=settings.NdiAudioVolume;});items.Add(ndiToggle);
        var replayToggle=new ToolStripMenuItem("Instant replay"){Checked=settings.Replay.Enabled};
        replayToggle.Click+=(_,_)=>Guard(()=>ApplyReplay(settings.Replay with{Enabled=!settings.Replay.Enabled}));items.Add(replayToggle);
        var save=new ToolStripMenuItem($"Save last {settings.Replay.Minutes} minutes"){Enabled=replay.BufferedSeconds>0,ShortcutKeyDisplayString=ReplayHotkey.Text(settings.Replay)};
        save.Click+=(_,_)=>SaveReplay();items.Add(save);
        items.Add("Computers · KVM…",null,(_,_)=>dispatcher.BeginInvoke(OpenKvm));
        items.Add(new ToolStripSeparator());
        items.Add("Settings…",null,(_,_)=>OpenSettings());
        items.Add("Check for updates…",null,(_,_)=>dispatcher.BeginInvoke(OpenUpdates));
        items.Add("Open clips folder",null,async(_,_)=>{string folder=settings.Replay.Folder;try{await Task.Run(()=>Directory.CreateDirectory(folder));Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});}catch(Exception e){notifications.Enqueue("Could not open the clips folder: "+e.Message);}});
        items.Add(new ToolStripSeparator());items.Add("Exit",null,(_,_)=>ExitThread());

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
        if(settingsWindow is {IsClosed:false}){settingsWindow.SelectPage(page);settingsWindow.Reveal();return;}
        var form=new Ui.SettingsWindow(settings,kvm,IsAutorun(),Diagnostics,OpenUpdates,page);settingsWindow=form;
        form.Closed+=(_,_)=>{settingsWindow=null;if(form.Accepted)Guard(()=>ApplySettings(form));};
        form.Reveal();
        Log.Write($"Settings window shown in {opening.ElapsedMilliseconds} ms");
    });
    void ApplySettings(Ui.SettingsWindow form)
    {
        var result=form.ResultSettings;
        bool discordChanged=settings.Discord!=result.Discord||settings.Kvm.Role!=result.Kvm.Role;
        if(!hotkey.Set(result.Replay.HotkeyModifiers,result.Replay.HotkeyKey))
        {hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey);throw new InvalidOperationException("Another application is using the replay shortcut.");}
        string priorKvm=JsonSerializer.Serialize(settings.Kvm with{Layout=[],RemoteOnlyPeers=[],Seamless=true});
        if(form.ImportedProfile!=null)
        {
            try{ConfigurationBackup.Restore(form.ImportedProfile,Log.Folder);}
            catch{hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey);throw;}
        }
        keepAwake.Set(result.PreventIdleSleep);settings.PreventIdleSleep=result.PreventIdleSleep;
        settings.Device=result.Device;settings.Compensate=result.Compensate;settings.CaptureCursor=result.CaptureCursor;settings.SendOnLaunch=result.SendOnLaunch;settings.NdiAudioDevice=result.NdiAudioDevice;settings.NdiAudioVolume=result.NdiAudioVolume;settings.Updates=result.Updates;settings.Replay=result.Replay;settings.Kvm=result.Kvm;settings.Save();
        engine.Update(settings.Options);engine.NdiEnabled=settings.SendOnLaunch;engine.NdiAudioDevice=settings.NdiAudioDevice;engine.NdiAudioVolume=settings.NdiAudioVolume;engine.ReplayEnabled=settings.Replay.Enabled;engine.ReplayFrameRate=settings.Replay.Fps;replay.Update(settings.Replay);
        if(IsAutorun()!=form.StartWithWindows)
        {
            using var key=Registry.CurrentUser.CreateSubKey(RunKey);
            if(form.StartWithWindows)key.SetValue("ScreenCapture",$"\"{Environment.ProcessPath}\"");else key.DeleteValue("ScreenCapture",false);key.DeleteValue("SdrCapture",false);
        }
        if(form.ImportedProfile!=null||priorKvm!=JsonSerializer.Serialize(settings.Kvm with{Layout=[],RemoteOnlyPeers=[],Seamless=true}))StartKvm();else controller?.UpdateLayout(settings.Kvm);
        settings.Discord=result.Discord;settings.Save();if(discordChanged)StartDiscord();
        SelectRemoteAudio();notifications.Enqueue("Settings applied.");
    }
    string Diagnostics()=>$"{engine.SourceName}\r\n{engine.Status}\r\n{engine.CaptureStatus}\r\nCapture: {engine.CaptureMs:F1} ms; NDI: {engine.SendMs:F1} ms\r\n\r\n{replay.Status}\r\n{replay.AudioStatus}\r\nNDI audio: {engine.NdiAudioStatus}\r\nBuffer: {replay.CacheBytes/1048576.0:F0} MB\r\nRecorded frames: {replay.EncodedFrames}; repeats: {replay.RepeatedInputFrames}\r\nStatic desktop content also produces repeated frames.\r\nEncoder starts: {replay.EncoderStarts}\r\n{replay.Error}\r\n{delivery.Status}\r\n\r\nDiscord: {discord?.Status??"off"}\r\n\r\nKVM: {kvm?.Status}\r\n{string.Join("\r\n",kvm?.Peers.Select(p=>p.Name+" · displays: "+p.Screens.Length)??[])}";
    async Task CheckForUpdatesQuietly()
    {
        try{await updater.Check();if(updater.Latest!=null&&Updates.ParseVersion(updater.Latest.Manifest.Version)>Updates.Current)notifications.Enqueue("ScreenCapture update available: "+updater.Latest.Manifest.Version+". Open Updates to install it.");}
        catch(Exception e){updater.SetStatus("Update check unavailable: "+e.Message);}
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
        catch(Exception e){notifications.Enqueue("Discord camera: "+e.Message);}
        if(!settings.Discord.Enabled)return;
        try{discord=new(settings.Discord);}
        catch(Exception e){notifications.Enqueue("Discord: "+e.Message);}
    }
    static bool IsAutorun(){using var key=Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("ScreenCapture")!=null||key?.GetValue("SdrCapture")!=null;}
    public static string ConnectionText=>"On the streaming PC, open OBS → Sources → NDI Source (DistroAV).\nSelect GAMING-PC-NAME (SdrCapture SDR).\n\nYUV Range: Limited\nYUV Color Space: BT.709\nLatency Mode: Low\nBandwidth: Highest\nBehavior: Always play when not visible (Keepalive)\nFramesync: off\nEnable audio: off for video only\n\nScreenCapture uses NDI High Bandwidth. A wired local network is recommended.\nIn OBS → Settings → Advanced → Video, use SDR Rec.709.\n\nEnd-to-end latency also depends on your network and OBS settings.";
    void ShowHelp()=>MessageBox.Show(ConnectionText,"ScreenCapture → DistroAV");
    void ApplyReplay(ReplayOptions options)
    {
        options.Validate();
        if(!hotkey.Set(options.HotkeyModifiers,options.HotkeyKey))
        {
            hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey);
            throw new InvalidOperationException("Another application is using this shortcut.");
        }
        settings.Replay=options;settings.Save();engine.ReplayEnabled=options.Enabled;engine.ReplayFrameRate=options.Fps;replay.Update(options);SelectRemoteAudio();
        notifications.Enqueue(options.Enabled?"Replay settings applied.":"Replay buffer disabled.");
    }
    async void SaveReplay()
    {
        if(closing)return;
        try{string app=ReplayAppContext.Capture();notifications.Enqueue($"Saving the last {settings.Replay.Minutes} minutes…");var result=await replay.SaveAsync(app);delivery.Wake();}
        catch(Exception e){notifications.Enqueue("Replay could not be saved: "+e.Message);}
    }
    protected override void ExitThreadCore(){closing=true;keepAwake.Dispose();timer.Stop();settingsWindow?.Close();discord?.Dispose();kvmHub?.Close();foreach(var viewer in viewers.Values.ToArray())viewer.Close();kvmWait.Unregister(null);kvmEvent.Dispose();updatesForm?.Close();updateWait.Unregister(null);updateEvent.Dispose();dragDrop?.Dispose();clipboard?.Dispose();controller?.Dispose();kvm?.Dispose();saveWait.Unregister(null);saveEvent.Dispose();hotkey.Dispose();engine.FrameAvailable=null;engine.Dispose();replay.Dispose();delivery.Dispose();dispatcher.Dispose();tray.Visible=false;tray.Dispose();appIcon.Dispose();timer.Dispose();base.ExitThreadCore();}
}
