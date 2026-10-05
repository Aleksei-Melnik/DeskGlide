using System.Diagnostics;
using System.Globalization;
using NAudio.CoreAudioApi;
using W=System.Windows;
using C=System.Windows.Controls;

namespace SdrCapture.Ui;

sealed class SettingsWindow:ShellWindow
{
    internal sealed record Choice(string Id,string Label){public override string ToString()=>Label;}
    readonly Settings initial;
    readonly KvmService? service;
    readonly Func<string>? diagnostics;
    readonly Action? openUpdates;
    readonly Func<Task<DiscordDevices.Endpoint[]>> discover;
    readonly CancellationTokenSource lifetime=new();
    readonly Dictionary<string,C.ComboBox> choices=[];
    readonly Dictionary<string,C.CheckBox> toggles=[];
    readonly Dictionary<string,C.TextBox> texts=[];
    readonly Dictionary<string,C.Slider> sliders=[];
    readonly Dictionary<int,W.UIElement> pages=[];
    readonly Dictionary<int,C.Button> links=[];
    readonly C.ContentControl body=new();
    readonly C.ScrollViewer scroll=new(){VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled,Padding=new(28,22,28,24)};
    readonly C.TextBlock error=Kit.Text("",12,"#FF83AB"),audioStatus=Kit.Text("Loading audio devices…",12,"#B0A9BC");
    readonly C.TextBox state=new(){IsReadOnly=true,Text="Select Refresh to load diagnostics.",AcceptsReturn=true,Height=320,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,FontFamily=new("Consolas"),TextWrapping=W.TextWrapping.Wrap};
    readonly ShortcutBox replayKey,kvmKey,edgeKey;
    readonly MonitorCanvas monitors;
    DiscordDevices.Endpoint[] endpoints=[];
    bool loadingAudio,replacingAudio,loadingState;
    public bool Accepted {get;private set;}
    public Settings ResultSettings {get;private set;}
    public ConfigurationProfile? ImportedProfile {get;private set;}
    public bool StartWithWindows=>toggles["startup"].IsChecked==true;
    internal int SelectedPage {get;private set;}
    internal string AudioStatus=>audioStatus.Text;
    internal C.ComboBox AudioChoice(string id)=>choices[id];
    public SettingsWindow(Settings value,KvmService? service=null,bool autorun=false,Func<string>? diagnostics=null,Action? openUpdates=null,int startPage=0,Func<Task<DiscordDevices.Endpoint[]>>? discoverAudio=null):base("Settings",1120,820)
    {
        initial=value.Copy();ResultSettings=value.Copy();this.service=service;this.diagnostics=diagnostics;this.openUpdates=openUpdates;discover=discoverAudio??SettingsAudioDiscovery.Load;
        replayKey=new(value.Replay.HotkeyModifiers,value.Replay.HotkeyKey);kvmKey=new(value.Kvm.OpenHotkeyModifiers,value.Kvm.OpenHotkeyKey);edgeKey=new(value.Kvm.ToggleHotkeyModifiers,value.Kvm.ToggleHotkeyKey);
        monitors=new(value.Kvm,KvmLayout.Merge(value.Kvm,new[]{new KvmPeerInfo(value.Kvm.Id,Environment.MachineName,KvmScreen.Local())}.Concat(service?.Peers??[])));
        var root=new C.Grid{Background=Kit.Brush("#08090F")};root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        root.Children.Add(Header("ScreenCapture","SDR streaming · Instant replay · KVM"));
        var middle=new C.Grid();middle.ColumnDefinitions.Add(new(){Width=new(224)});middle.ColumnDefinitions.Add(new());C.Grid.SetRow(middle,1);root.Children.Add(middle);
        var nav=new C.StackPanel{Margin=new(16,10,16,10)};
        void Group(string title){var label=Kit.Text(title,10,"#8C829D");label.FontWeight=W.FontWeights.Bold;label.Margin=new(13,10,0,4);nav.Children.Add(label);}
        void Link(int page,string title,string subtitle)
        {
            var label=Kit.Text(title,13);label.FontWeight=W.FontWeights.SemiBold;var sub=Kit.Text(subtitle,11,"#B0A9BC");sub.Margin=new(0,4,0,0);
            var button=Kit.Button("",()=>SelectPage(page));button.Content=Kit.Stack(label,sub);button.Style=(W.Style)FindResource("Nav");links[page]=button;nav.Children.Add(button);
        }
        Group("CAPTURE & REPLAY");Link(0,"Screen streaming","Always SDR · NDI");Link(8,"Discord camera","Your screen, ready to share");Link(1,"Instant replay","Keep the last 5–20 minutes");Link(2,"Replay audio","Game, mic & streaming PC");
        Group("YOUR COMPUTERS");Link(3,"KVM & pairing","Connect and control");Link(4,"Monitor layout","Arrange it like your desk");
        Group("APPLICATION");Link(5,"Updates","Every PC, one place");Link(7,"Backup & restore","Keep your setup safe");Link(6,"Diagnostics","Status and logs");
        var sidebar=new C.Border{Background=Kit.Brush("#0E0D16"),BorderBrush=Kit.Brush("#302B3E"),BorderThickness=new(0,0,1,0),Child=new C.ScrollViewer{Content=nav,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled}};middle.Children.Add(sidebar);
        scroll.Content=body;C.Grid.SetColumn(scroll,1);middle.Children.Add(scroll);
        var footer=new C.Grid{Margin=new(24,14,24,14)};footer.ColumnDefinitions.Add(new());footer.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});footer.Children.Add(error);
        var cancel=Kit.Button("Cancel",Close);cancel.IsCancel=true;
        var save=Kit.Button("Save changes",Save,true);save.IsDefault=true;
        var actions=Kit.Actions(cancel,save);C.Grid.SetColumn(actions,1);footer.Children.Add(actions);
        var bar=new C.Border{BorderBrush=Kit.Brush("#302B3E"),BorderThickness=new(0,1,0,0),Child=footer};C.Grid.SetRow(bar,2);root.Children.Add(bar);Content=root;
        BuildStream(value,autorun);BuildReplay(value);BuildAudio(value);BuildKvm(value);BuildMonitors();BuildUpdates(value);BuildProfile();BuildDiagnostics();BuildDiscord(value);
        SelectPage(startPage,false);
        Loaded+=async(_,_)=>{await RefreshAudio();};Closed+=(_,_)=>{lifetime.Cancel();lifetime.Dispose();};
    }
    C.StackPanel Page(int id,string eyebrow,string title,string description)
    {
        var page=new C.StackPanel();var badge=Kit.Text(eyebrow.ToUpperInvariant(),10,"#C991ED");badge.FontWeight=W.FontWeights.Bold;
        var heading=Kit.Text(title,27);heading.FontWeight=W.FontWeights.SemiBold;heading.Margin=new(0,8,0,8);
        var caption=Kit.Text(description,13,"#B0A9BC");caption.Margin=new(0,0,0,22);page.Children.Add(badge);page.Children.Add(heading);page.Children.Add(caption);pages[id]=page;return page;
    }
    static C.StackPanel Section(C.StackPanel page,string title,string? note=null)
    {var stack=new C.StackPanel();var heading=Kit.Text(title,14);heading.FontWeight=W.FontWeights.SemiBold;heading.Margin=new(0,0,0,12);stack.Children.Add(heading);if(note!=null){var text=Kit.Text(note,12,"#B0A9BC");text.Margin=new(0,0,0,12);stack.Children.Add(text);}page.Children.Add(Kit.Card(stack));return stack;}
    static C.Grid Row(C.StackPanel section,string label,W.UIElement control)
    {
        var row=new C.Grid{Margin=new(0,6,0,6)};row.ColumnDefinitions.Add(new(){Width=new(164)});row.ColumnDefinitions.Add(new());
        var text=Kit.Text(label,12,"#B0A9BC");text.VerticalAlignment=W.VerticalAlignment.Center;text.Margin=new(0,0,15,0);row.Children.Add(text);C.Grid.SetColumn(control,1);row.Children.Add(control);section.Children.Add(row);return row;
    }
    C.CheckBox Toggle(string id,string text,bool value){var box=new C.CheckBox{Content=Kit.Text(text),IsChecked=value};toggles[id]=box;return box;}
    C.TextBox Input(string id,string value){var box=new C.TextBox{Text=value};texts[id]=box;return box;}
    C.ComboBox Choose(string id,IEnumerable<Choice> values,string selected)
    {var box=new C.ComboBox{ItemsSource=values.ToList()};choices[id]=box;Select(box,selected);return box;}
    static void Select(C.ComboBox box,string id)
    {
        var items=box.Items.Cast<Choice>().ToList();if(!items.Any(i=>i.Id==id)){items.Add(new(id,"Saved device (unavailable)"));box.ItemsSource=items;}
        box.SelectedItem=items.First(i=>i.Id==id);
    }
    C.ComboBox Audio(string id,string selected,bool captureOnly=false)
    {
        var values=captureOnly?new List<Choice>{new("","Choose a recording input…")}:new(){new("","Silent — no audio"),new("default:capture","Default Windows microphone"),new("default:render","Default Windows playback")};
        if(selected.Length>0&&!values.Any(v=>v.Id==selected))values.Add(new(selected,"Saved device · loading…"));return Choose(id,values,selected);
    }
    C.StackPanel Volume(string id,int value)
    {
        var slider=new C.Slider{Minimum=0,Maximum=100,Value=value,TickFrequency=1,IsSnapToTickEnabled=true,Width=280};sliders[id]=slider;
        var label=Kit.Text(value+"%",12,"#31D1DB");label.Width=48;label.VerticalAlignment=W.VerticalAlignment.Center;label.Margin=new(12,0,0,0);slider.ValueChanged+=(_,_)=>label.Text=((int)slider.Value)+"%";return Kit.Actions(slider,label);
    }
    static Choice[] Options(params string[] values)=>values.Select(v=>new Choice(v,v)).ToArray();
    void BuildStream(Settings v,bool autorun)
    {
        var page=Page(0,"Stream","A great picture. Always SDR.","Send your gaming screen to OBS or Discord, whether Windows HDR is on or off.");
        var video=Section(page,"Screen output","NDI High Bandwidth · native resolution · 60 FPS · SDR BT.709");
        Row(video,"Streaming",Toggle("ndi","Stream this screen over NDI",v.SendOnLaunch));
        Row(video,"Monitor",Choose("display",Screen.AllScreens.Select(s=>new Choice(s.DeviceName,$"{s.DeviceName} · {s.Bounds.Width} × {s.Bounds.Height}")),v.Device));Row(video,"Cursor",Toggle("cursor","Include the mouse cursor",v.CaptureCursor));
        var audio=Section(page,"Stream audio","This volume affects only the audio sent to your streaming PC.");Row(audio,"Audio source",Audio("ndiAudio",v.NdiAudioDevice));Row(audio,"Volume",Volume("ndiVolume",v.NdiAudioVolume));
        audio.Children.Add(audioStatus);var refresh=Kit.AsyncButton("Refresh devices",RefreshAudio);refresh.HorizontalAlignment=W.HorizontalAlignment.Left;refresh.Margin=new(0,12,0,0);audio.Children.Add(refresh);
        var app=Section(page,"Everyday use");Row(app,"Startup",Toggle("startup","Start with Windows",autorun));Row(app,"Power",Toggle("awake","Keep this PC and its displays awake",v.PreventIdleSleep));
        app.Children.Add(Kit.Text("Applies while ScreenCapture runs. You can still put the PC to sleep manually.",12,"#B0A9BC"));
        page.Children.Add(Kit.Actions(Kit.Button("Connect OBS",()=>Kit.Notify(this,TrayApp.ConnectionText,"Connect OBS")),Kit.Button("Set up Discord",()=>SelectPage(8))));
    }
    void BuildReplay(Settings v)
    {
        var page=Page(1,"Replay","Keep the moments that matter.","A rolling GPU recording buffer. Save a clip with a shortcut whenever you need it.");
        var main=Section(page,"Instant replay");Row(main,"Replay buffer",Toggle("replay","Record in the background",v.Replay.Enabled));Row(main,"Duration",Choose("minutes",Enumerable.Range(5,16).Select(n=>new Choice(n.ToString(),$"{n} minutes")),v.Replay.Minutes.ToString()));Row(main,"Save shortcut",replayKey);
        var encoding=Section(page,"Recording quality","NVIDIA NVENC hardware encoding. Streaming stays at 60 FPS.");Row(encoding,"Codec",Choose("codec",Options("HEVC","H264","AV1"),v.Replay.Codec));Row(encoding,"Quality",Choose("quality",Options("Ultra","High","Medium","Low"),v.Replay.Quality));Row(encoding,"Frame rate",Choose("fps",[new("60","60 FPS"),new("120","120 FPS")],v.Replay.Fps.ToString()));
        Row(encoding,"Resolution",Choose("resolution",[new("0","Native resolution"),new("1920","1920 × 1080"),new("2560","2560 × 1440"),new("3840","3840 × 2160")],v.Replay.Width.ToString()));
        var storage=Section(page,"Your clips","Game and app folders are created automatically. Network paths are supported.");
        var path=Input("folder",v.Replay.Folder);Row(storage,"Save to",path);storage.Children.Add(Kit.Actions(Kit.Button("Browse…",()=>{var picker=new Microsoft.Win32.OpenFolderDialog{Title="Choose a recording folder",InitialDirectory=path.Text};if(picker.ShowDialog(this)==true)path.Text=picker.FolderName;}),Kit.AsyncButton("Test folder",async()=>
        {string destination=path.Text;try{await Task.Run(()=>{if(!Path.IsPathFullyQualified(destination))throw new IOException("Enter an absolute folder path.");Directory.CreateDirectory(destination);string probe=Path.Combine(destination,".screencapture-"+Guid.NewGuid().ToString("N"));try{File.WriteAllText(probe,"test");}finally{File.Delete(probe);}}).WaitAsync(TimeSpan.FromSeconds(10),lifetime.Token);if(!IsClosed)Kit.Notify(this,"The folder is writable.");}catch(Exception e){if(!IsClosed)error.Text=e.Message;}})));
        page.Children.Add(Kit.Text("Changing codec, quality, frame rate, resolution or audio mode starts a new buffer. Completed clips are saved locally before copying to your destination.",12,"#B0A9BC"));
    }
    void BuildAudio(Settings v)
    {
        var page=Page(2,"Sound","Your sound. Mixed or separate.","Capture game audio, your microphone and the streaming PC without changing Windows defaults.");
        var section=Section(page,"Audio tracks");var mode=Choose("audioMode",[new("Mixed","Mix into one track"),new("Separate","Three separate tracks"),new("Silent","Silent — no audio")],v.Replay.IsSilent?"Silent":v.Replay.AudioMode);Row(section,"Track mode",mode);
        Row(section,"1 · Game audio",Audio("game",v.Replay.GameAudio));Row(section,"2 · Microphone",Audio("mic",v.Replay.Microphone));Row(section,"3 · Streaming PC",Audio("extra",v.Replay.ExtraAudio));
        void Update(){foreach(var name in new[]{"game","mic","extra"})choices[name].IsEnabled=Id("audioMode")!="Silent";}mode.SelectionChanged+=(_,_)=>Update();Update();
        var help=Section(page,"Sound from your other PC","Pair the streaming PC in KVM & pairing, then choose its audio source under Send audio to host. Select that PC as track 3 here. The sound goes into your replay without playing through local speakers.");help.Children.Add(Kit.Button("Open KVM settings",()=>SelectPage(3)));
    }
    void BuildKvm(Settings v)
    {
        var page=Page(3,"Control","One keyboard. Every computer.","Pair your streaming PC or a server. Keep each physical display exactly where you want it.");
        var connection=Section(page,"Pairing");var role=Choose("role",[new("Off","Off"),new("Host","Host · keyboard and mouse are here"),new("Client","Client · streaming PC or server")],v.Kvm.Role);Row(connection,"This PC",role);
        var host=Row(connection,"Host name or IP",Input("host",v.Kvm.Host));var pairing=Row(connection,"Host pairing code",Input("pairing",v.Kvm.PairingCode));
        var code=Input("hostCode",service?.PairingCode??"");code.IsReadOnly=true;var codeRow=Row(connection,"Your pairing code",code);
        var copy=Kit.Button("Copy pairing code",()=>{try{if(code.Text.Length==0){using var identity=new KvmIdentity(Path.Combine(Log.Folder,"Kvm"));code.Text=identity.Code;}W.Clipboard.SetText(code.Text);}catch(Exception e){error.Text=e.Message;}});copy.HorizontalAlignment=W.HorizontalAlignment.Left;connection.Children.Add(copy);
        var hint=Kit.Text("",12,"#B0A9BC");hint.Margin=new(0,12,0,0);connection.Children.Add(hint);
        var advanced=new C.Expander{Header="Advanced network settings",Margin=new(0,16,0,0)};var port=Input("port",v.Kvm.Port.ToString());advanced.Content=port;connection.Children.Add(advanced);
        var control=Section(page,"Control & shortcuts");Row(control,"Edge switching",Toggle("seamless","Move between physical displays",v.Kvm.Seamless));Row(control,"Open KVM",kvmKey);Row(control,"Lock / unlock edges",edgeKey);control.Children.Add(Kit.Text("Ctrl+Alt+Esc always returns control here. Monitor shortcuts are configured in Monitor layout.",12,"#B0A9BC"));
        var clipboard=Section(page,"Clipboard");Row(clipboard,"Text",Toggle("clipboardText","Share clipboard text",v.Kvm.ClipboardText));Row(clipboard,"Files",Toggle("clipboardFiles","Share files and folders",v.Kvm.ClipboardFiles));clipboard.Children.Add(Kit.Text("Ctrl+C on one PC, Ctrl+V on another. Drag and drop is experimental. Temporary transfer files expire after 4 hours; pasted copies stay.",12,"#B0A9BC"));
        var client=Section(page,"Client permissions");Row(client,"Desktop",Toggle("allowView","Allow remote desktop viewing",v.Kvm.AllowView));Row(client,"Server mode",Toggle("remoteOnly","Remote view only · no edge switching",v.Kvm.RemoteViewOnly));Row(client,"Send audio to host",Audio("remoteAudio",v.Kvm.AudioDevice));
        client.Children.Add(Kit.Text("A headless server needs an active desktop from a virtual display or HDMI dummy plug. Windows lock screens and elevated windows may be unavailable.",12,"#B0A9BC"));
        void Update()
        {
            bool isHost=Id("role")=="Host",isClient=Id("role")=="Client";
            host.Visibility=pairing.Visibility=isClient?W.Visibility.Visible:W.Visibility.Collapsed;codeRow.Visibility=copy.Visibility=isHost?W.Visibility.Visible:W.Visibility.Collapsed;
            ((W.UIElement)client.Parent).Visibility=isClient?W.Visibility.Visible:W.Visibility.Collapsed;control.IsEnabled=isHost;clipboard.IsEnabled=isHost||isClient;
            hint.Text=isHost?$"On the other PC choose Client, enter {Environment.MachineName} and paste this pairing code.":isClient?"Enter the host name and code from your gaming PC. Audio sent to the host is used for recording.":"Choose a role to pair computers. Screen streaming and replay work independently.";
        }
        role.SelectionChanged+=(_,_)=>Update();Update();
    }
    void BuildMonitors()
    {
        var page=Page(4,"Desk","Arrange it like your desk.","Drag individual displays into place. Touching edges let the mouse pass between PCs.");
        page.Children.Add(Kit.Card(monitors));page.Children.Add(Kit.Text("Remote only removes the selected PC from edge switching while keeping it available in KVM. You can restore it from the computer card. Saved monitor positions are retained.",12,"#B0A9BC"));
    }
    void BuildUpdates(Settings v)
    {
        var page=Page(5,"Updates","Keep every PC in sync.",$"ScreenCapture {Updates.VersionText} · Signed releases from GitHub");var section=Section(page,"Update preferences");Row(section,"Startup",Toggle("checkUpdates","Check for a newer version",v.Updates.CheckOnStartup));Row(section,"Paired host",Toggle("remoteUpdates","Allow updates from the host",v.Updates.AllowFromHost));
        var manage=Kit.Button("Manage updates",()=>openUpdates?.Invoke(),true);manage.IsEnabled=openUpdates!=null;section.Children.Add(manage);
        page.Children.Add(Kit.Text("Update all paired computers from the host. Packages are verified, and the current replay is saved before restarting. Settings, pairing and saved clips are preserved.",12,"#B0A9BC"));
    }
    void BuildDiagnostics()
    {
        var page=Page(6,"Status","See what is happening.","Capture, recording, connections and delivery status in one place.");page.Children.Add(Kit.Card(state));page.Children.Add(Kit.Actions(Kit.AsyncButton("Refresh",RefreshDiagnostics),Kit.Button("Open logs",()=>Process.Start(new ProcessStartInfo(Log.Folder){UseShellExecute=true})),Kit.Button("Pending clips",()=>{var path=Path.Combine(ReplayTools.Root,"saved");Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});})));
    }
    void BuildProfile()
    {
        var page=Page(7,"Profile","Make yourself at home. Again.","Save your setup before reinstalling Windows. Use a separate profile for each computer.");var section=Section(page,"Encrypted backup","Includes capture, replay, audio, shortcuts, monitor layout and KVM pairing. Your password protects access keys.");section.Children.Add(Kit.Actions(Kit.Button("Export profile…",ExportProfile,true),Kit.Button("Import profile…",ImportProfile)));
        page.Children.Add(Kit.Text("Restore on the original computer and keep the same Windows PC name. If an audio device ID changes after reinstalling, select that device again.",12,"#B0A9BC"));
    }
    internal void SelectPage(int id,bool animate=true)
    {
        if(!pages.ContainsKey(id))id=0;SelectedPage=id;body.Content=pages[id];scroll.ScrollToTop();
        foreach(var pair in links){pair.Value.Background=Kit.Brush(pair.Key==id?"#302039":"Transparent");pair.Value.BorderBrush=Kit.Brush(pair.Key==id?"#7C3C83":"Transparent");}
        if(animate)Kit.Enter(body);if(id==6&&IsLoaded)_=RefreshDiagnostics();
    }
    async Task RefreshDiagnostics()
    {
        if(loadingState||diagnostics==null||IsClosed)return;loadingState=true;
        try{var text=await Task.Run(diagnostics).WaitAsync(TimeSpan.FromSeconds(5),lifetime.Token);if(!IsClosed)state.Text=text;}catch(OperationCanceledException){}catch(Exception e){if(!IsClosed)state.Text=e.Message;}finally{loadingState=false;}
    }
    string Id(string name)=>(choices[name].SelectedItem as Choice)?.Id??"";
    bool On(string name)=>toggles[name].IsChecked==true;
    internal Settings Collect()
    {
        var result=initial.Copy();result.SendOnLaunch=On("ndi");result.Device=Id("display");result.CaptureCursor=On("cursor");result.PreventIdleSleep=On("awake");result.NdiAudioDevice=Id("ndiAudio");result.NdiAudioVolume=(int)sliders["ndiVolume"].Value;
        int width=int.Parse(Id("resolution"),CultureInfo.InvariantCulture);int height=width switch{1920=>1080,2560=>1440,3840=>2160,_=>0};
        result.Replay=result.Replay with{Enabled=On("replay"),Minutes=int.Parse(Id("minutes")),Codec=Id("codec"),Quality=Id("quality"),Fps=int.Parse(Id("fps")),Width=width,Height=height,Folder=texts["folder"].Text.Trim(),GroupByApp=true,Silent=false,AudioMode=Id("audioMode"),GameAudio=Id("game"),Microphone=Id("mic"),ExtraAudio=Id("extra"),HotkeyModifiers=replayKey.Modifiers,HotkeyKey=replayKey.Key};
        result.Kvm=result.Kvm with{Role=Id("role"),Host=texts["host"].Text.Trim(),PairingCode=texts["pairing"].Text.Trim(),Port=int.Parse(texts["port"].Text,CultureInfo.InvariantCulture),Seamless=On("seamless"),ClipboardText=On("clipboardText"),ClipboardFiles=On("clipboardFiles"),AllowView=On("allowView"),RemoteViewOnly=On("remoteOnly"),AudioDevice=Id("remoteAudio"),OpenHotkeyModifiers=kvmKey.Modifiers,OpenHotkeyKey=kvmKey.Key,ToggleHotkeyModifiers=edgeKey.Modifiers,ToggleHotkeyKey=edgeKey.Key,Layout=monitors.Result,RemoteOnlyPeers=monitors.RemoteOnlyPeers};
        result.Updates=new(){CheckOnStartup=On("checkUpdates"),AllowFromHost=On("remoteUpdates")};
        result.Discord=result.Discord with{Enabled=On("discord")&&result.Kvm.Role!="Host",CameraName=texts["cameraName"].Text.Trim(),Source=texts["source"].Text.Trim(),AudioMode=Id("discordMode"),AudioDevice=Id("discordOutput"),CaptureDevice=Id("discordInput"),Volume=(int)sliders["discordVolume"].Value};
        Validate(result);return result;
    }
    static void Validate(Settings result)
    {
        result.Replay.Validate();result.Kvm.Validate();result.Discord.Validate();
        if(result.Replay.HotkeyModifiers==0||result.Replay.HotkeyKey==0)throw new ArgumentException("Choose a key and Ctrl, Alt or Shift for saving replays.");
        if(new[]{(result.Kvm.OpenHotkeyModifiers,result.Kvm.OpenHotkeyKey),(result.Kvm.ToggleHotkeyModifiers,result.Kvm.ToggleHotkeyKey)}.Contains((result.Replay.HotkeyModifiers,result.Replay.HotkeyKey)))throw new ArgumentException("The replay and KVM shortcuts must be different.");
        if(result.Kvm.Layout.Any(m=>m.Hotkey>0&&(int)result.Replay.HotkeyKey==(int)Keys.F1+m.Hotkey-1)&&result.Replay.HotkeyModifiers==3)throw new ArgumentException("The replay shortcut is also assigned to a monitor.");
    }
    void Save(){try{ResultSettings=Collect();Accepted=true;Close();}catch(Exception e){error.Text=e.Message;}}
    void ExportProfile()
    {
        try{var profile=ConfigurationBackup.Capture(Collect(),StartWithWindows,Log.Folder);var dialog=new Microsoft.Win32.SaveFileDialog{Filter="ScreenCapture profile|*.scprofile",FileName=Environment.MachineName+"-ScreenCapture.scprofile",DefaultExt=".scprofile"};if(dialog.ShowDialog(this)!=true)return;var password=PasswordWindow.Ask(this,true);if(password==null)return;File.WriteAllBytes(dialog.FileName,ConfigurationBackup.Encode(profile,password));Kit.Notify(this,"Profile saved. Keep the password safe; you need it to restore this backup.");}catch(Exception e){error.Text=e.Message;}
    }
    void ImportProfile()
    {
        try{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="ScreenCapture profile|*.scprofile"};if(dialog.ShowDialog(this)!=true)return;if(new FileInfo(dialog.FileName).Length>2097152)throw new IOException("The profile file is too large.");var password=PasswordWindow.Ask(this,false);if(password==null)return;var profile=ConfigurationBackup.Decode(File.ReadAllBytes(dialog.FileName),password);
            if(W.MessageBox.Show(this,$"Restore the profile for {profile.Computer} from {profile.Created.LocalDateTime:g}?\n\nSettings and pairing on this PC will be replaced. Saved clips stay in place.","Restore profile",W.MessageBoxButton.OKCancel,W.MessageBoxImage.Question)!=W.MessageBoxResult.OK)return;
            ImportedProfile=profile;ResultSettings=profile.Settings.Copy();toggles["startup"].IsChecked=profile.Autorun;Accepted=true;Close();
        }catch(Exception e){error.Text=e.Message;}
    }
    void BuildDiscord(Settings v)
    {
        var page=Page(8,"Discord","Your gaming screen. Ready to share.","A virtual camera on your streaming PC. 1080p at 60 FPS, without opening OBS.");
        var camera=Section(page,"Receive a screen");Row(camera,"Receiver",Toggle("discord","Receive the gaming PC screen",v.Discord.Enabled));Row(camera,"Device name",Input("cameraName",CameraInstallation.Name(v.Discord.CameraName)));var source=Input("source",v.Discord.Source);Row(camera,"NDI source",source);
        var found=new C.ComboBox{Visibility=W.Visibility.Collapsed,Margin=new(0,8,0,0)};found.SelectionChanged+=(_,_)=>{if(found.SelectedItem is string value)source.Text=value;};camera.Children.Add(found);
        var find=Kit.AsyncButton("Find screens",async()=>{try{var sources=await Task.Run(NdiDiscovery.Sources).WaitAsync(TimeSpan.FromSeconds(8),lifetime.Token);if(IsClosed)return;found.ItemsSource=sources;found.Visibility=W.Visibility.Visible;if(sources.Length==1)found.SelectedIndex=0;}catch(Exception e){if(!IsClosed)error.Text=e.Message;}});camera.Children.Add(find);
        var sound=Section(page,"Share the right sound");var mode=Choose("discordMode",[new("Network","Game audio via virtual cable"),new("Local","Local input or mixer"),new("Silent","Silent — no audio")],v.Discord.AudioMode);Row(sound,"Audio source",mode);
        var output=Audio("discordOutput",v.Discord.AudioDevice);var outputRow=Row(sound,"Cable output",output);var inputRow=Row(sound,"Discord input",Audio("discordInput",v.Discord.CaptureDevice,true));var volumeRow=Row(sound,"Volume",Volume("discordVolume",v.Discord.Volume));
        output.SelectionChanged+=(_,_)=>{if(replacingAudio||Id("discordMode")!="Network")return;try{Select(choices["discordInput"],DiscordDevices.Pair(Id("discordOutput"),endpoints).Capture.Id);}catch(IOException){}};
        var hint=Kit.Text("",12,"#B0A9BC");sound.Children.Add(hint);
        var setup=Section(page,"One-time device setup","Registers your camera and names the chosen audio input to match. Audio renaming and driver installation ask for administrator permission.");
        var pair=Kit.AsyncButton("Set up devices",async()=>{try{CameraInstallation.Install(Id("role"),texts["cameraName"].Text.Trim());if(Id("discordMode")!="Silent"&&Id("discordInput").Length>0)await DiscordDevices.PairInput(Id("role"),Id("discordInput"),texts["cameraName"].Text.Trim());await RefreshAudio();if(!IsClosed)Kit.Notify(this,"Devices are ready. Quit Discord from the tray and reopen it. Choose this camera and 60 FPS under Devices. Select the matching recording input if Discord remembers a different one.");}catch(Exception e){if(!IsClosed)error.Text=e.Message;}});pair.Style=(W.Style)FindResource("Primary");
        var install=Kit.AsyncButton("Install VB-CABLE…",async()=>{if(Id("role")=="Host")return;if(W.MessageBox.Show(this,"Install the signed VB-CABLE driver on this PC? Windows will ask for administrator permission. Choose Install Driver, then restart if needed. Previous default audio devices will be restored.\n\nVB-CABLE is donationware by VB-Audio.","Install audio cable",W.MessageBoxButton.OKCancel)!=W.MessageBoxResult.OK)return;try{await DiscordSetup.InstallCable("Client");await RefreshAudio();if(IsClosed)return;var cable=endpoints.FirstOrDefault(e=>e.Flow==DataFlow.Render&&DiscordDevices.IsCable(e));if(cable!=null)Select(output,cable.Id);Kit.Notify(this,"Driver installed. Restart if needed, then choose Set up devices and Save changes.");}catch(Exception e){if(!IsClosed)error.Text=e.Message;}});
        setup.Children.Add(Kit.Actions(pair,Kit.AsyncButton("Refresh devices",RefreshAudio)));install.Margin=new(0,12,0,0);setup.Children.Add(install);var terms=Kit.Button("VB-Audio licensing & donation",()=>Process.Start(new ProcessStartInfo("https://vb-audio.com/Services/licensing.htm"){UseShellExecute=true}));terms.Margin=new(0,8,0,0);setup.Children.Add(terms);
        void Update()
        {
            bool receiver=Id("role")!="Host",network=Id("discordMode")=="Network",silent=Id("discordMode")=="Silent";
            camera.IsEnabled=sound.IsEnabled=setup.IsEnabled=receiver;
            outputRow.Visibility=volumeRow.Visibility=install.Visibility=terms.Visibility=network?W.Visibility.Visible:W.Visibility.Collapsed;inputRow.Visibility=silent?W.Visibility.Collapsed:W.Visibility.Visible;
            hint.Text=!receiver?"Set this up on the streaming PC where Discord runs. The gaming host does not need virtual devices.":network?"Game audio enters the cable output and reaches its paired input in Discord. For another cable, select both ends. Choosing speakers plays sound locally.":silent?"Video only. Disable audio in your Discord share; ScreenCapture does not change Discord settings.":"Choose your mixer, such as RØDECaster, or any local recording input. Discord uses it directly; NDI audio and the cable are unused.";
        }
        mode.SelectionChanged+=(_,_)=>Update();choices["role"].SelectionChanged+=(_,_)=>Update();Update();
    }
    internal async Task RefreshAudio()
    {
        if(loadingAudio||IsClosed)return;loadingAudio=true;audioStatus.Text="Loading devices… You can keep editing.";
        try
        {
            var result=await discover().WaitAsync(TimeSpan.FromSeconds(8),lifetime.Token);if(IsClosed)return;endpoints=result;replacingAudio=true;
            try
            {
                var standard=new List<Choice>{new("","Silent — no audio"),new("default:capture","Default Windows microphone"),new("default:render","Default Windows playback")};standard.AddRange(result.Select(e=>new Choice(e.Id,$"{(e.Flow==DataFlow.Capture?"Input":"Output")}: {e.Name}")));
                foreach(var key in new[]{"ndiAudio","game","mic","remoteAudio"})Replace(key,standard);
                Replace("extra",standard.Concat((service?.Peers??[]).Select(p=>new Choice("kvm:"+p.Id,p.Name+" · KVM audio"))));
                Replace("discordOutput",result.Where(e=>e.Flow==DataFlow.Render).OrderByDescending(DiscordDevices.IsCable).Select(e=>new Choice(e.Id,e.Name)).Prepend(new Choice("","Silent — no audio")));
                Replace("discordInput",result.Where(e=>e.Flow==DataFlow.Capture).Select(e=>new Choice(e.Id,e.Name)).Prepend(new Choice("","Choose a recording input…")));
            }finally{replacingAudio=false;}
            audioStatus.Text="Audio devices are up to date.";
        }
        catch(OperationCanceledException){}
        catch(Exception e){if(!IsClosed){audioStatus.Text="Devices did not respond. Your selection is kept; try Refresh devices.";Log.Write("Settings audio discovery: "+e.Message);}}
        finally{loadingAudio=false;}
    }
    void Replace(string id,IEnumerable<Choice> items){string selected=Id(id);choices[id].ItemsSource=items.ToList();Select(choices[id],selected);}
}

sealed class PasswordWindow:ShellWindow
{
    readonly C.PasswordBox password=new();
    bool accepted;
    PasswordWindow(bool exporting):base(exporting?"Export profile":"Import profile",480,270)
    {
        ResizeMode=W.ResizeMode.NoResize;ShowInTaskbar=false;
        var text=Kit.Text(exporting?"Choose a password of at least 8 characters to protect this profile and its computer access keys.":"Enter the password for this backup.");text.Margin=new(0,0,0,20);
        var error=Kit.Text("",12,"#FF83AB");error.Margin=new(0,8,0,8);
        var save=Kit.Button(exporting?"Save profile":"Restore",()=>{if(exporting&&password.Password.Length<8){error.Text="Use at least 8 characters.";return;}accepted=true;Close();},true);save.IsDefault=true;
        var cancel=Kit.Button("Cancel",Close);cancel.IsCancel=true;
        Content=new C.Border{Padding=new(24),Child=Kit.Stack(text,password,error,Kit.Actions(cancel,save))};Loaded+=(_,_)=>password.Focus();
    }
    public static string? Ask(W.Window owner,bool exporting){var window=new PasswordWindow(exporting){Owner=owner,WindowStartupLocation=W.WindowStartupLocation.CenterOwner};window.ShowDialog();return window.accepted?window.password.Password:null;}
}
