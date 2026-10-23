using System.Globalization;
using W=System.Windows;
using C=System.Windows.Controls;

namespace SdrCapture.Ui;

sealed partial class SettingsWindow
{
    readonly Func<string,ReplayOptions,CancellationToken,Task<IReadOnlyList<RecordingCapability>>>? recordingCapabilities;
    readonly Dictionary<string,EncodingEditor> recordingEditors=[];
    sealed class EncodingEditor(string prefix,ReplayOptions original,C.ComboBox codec,C.TextBlock status,C.Button probe)
    {
        public readonly string Prefix=prefix;
        public readonly ReplayOptions Original=original with{};
        public readonly C.ComboBox Codec=codec;
        public readonly C.TextBlock Status=status;
        public readonly C.Button Probe=probe;
        public string SavedCodec=original.Codec;
        public int Revision;
        public bool Replacing;
        public string StatusKey="Check encoder support to choose a codec for this profile.";
        public object[] StatusArguments=[];
        public CancellationTokenSource? Pending;
    }
    static string RecordingField(string prefix,string name)=>prefix.Length==0?name:prefix+char.ToUpperInvariant(name[0])+name[1..];

    void BuildRecording(Settings value)
    {
        var page=Page(12,"Recording","Video recording","Start a full recording independently of the instant replay buffer.");
        var main=Section(page,"Recording controls");Row(main,"Start / stop shortcut",recordingKey);
        var action=Kit.Button("Apply and start/stop recording",()=>{ToggleRecordingAfterSave=true;Save();if(!Accepted)ToggleRecordingAfterSave=false;},true);
        action.HorizontalAlignment=W.HorizontalAlignment.Left;main.Children.Add(action);
        main.Children.Add(Kit.Text("The shortcut and tray menu start or stop the recording. Replay can stay enabled at the same time.",12,"#B0A9BC"));
        BuildEncodingProfile(page,value.Recording.Profile,"recording",value.Recording.Computer,12);
        var audio=Section(page,"Recording audio","Choose audio sources for local recordings independently of replay. A paired encoder uses the audio in this PC's NDI stream.");
        var mode=Choose("recordingAudioMode",[new("Mixed","Mix into one track"),new("Separate","Three separate tracks"),new("Silent","Silent — no audio")],value.Recording.Profile.IsSilent?"Silent":value.Recording.Profile.AudioMode);
        Row(audio,"Track mode",mode);Row(audio,"Game / PC audio",Audio("recordingGame",value.Recording.Profile.GameAudio));
        Row(audio,"Remote PC audio",Audio("recordingExtra",value.Recording.Profile.ExtraAudio));Row(audio,"Microphone",Audio("recordingMic",value.Recording.Profile.Microphone));
        void UpdateRecordingAudio(){foreach(string name in new[]{"recordingGame","recordingMic","recordingExtra"})choices[name].IsEnabled=Id("recordingAudioMode")!="Silent";}
        mode.SelectionChanged+=(_,_)=>UpdateRecordingAudio();UpdateRecordingAudio();
        var storage=Section(page,"Recording save location","For a paired encoder, its own recording folder is used. Change that folder on the receiving PC.");
        RecordingFolder(storage,"recordingFolder",value.Recording.Profile.Folder);
        var receiver=Section(page,"Paired recording requests");
        Row(receiver,"Receive recordings",Toggle("allowRemoteEncoding","Allow paired PCs to encode on this PC",value.Recording.AllowRemoteEncoding));
        receiver.Children.Add(Kit.Text("Only paired PCs can send an incoming NDI source for recording. This PC controls its output folder and audio devices. Allowing requests uses this PC's GPU, CPU and storage.",12,"#B0A9BC"));
        page.Children.Add(Kit.Text("The encoding PC must stay online. Source loss pauses or ends recording; completed segments remain recoverable if saving fails.",12,"#B0A9BC"));
    }
    void RecordingFolder(C.StackPanel section,string id,string folder)
    {
        var path=Input(id,folder);var row=new C.Grid();row.ColumnDefinitions.Add(new());row.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});row.Children.Add(path);
        var browse=Kit.Button("Browse…",()=>{var picker=new Microsoft.Win32.OpenFolderDialog{Title=UiStrings.T("Choose a recording folder"),InitialDirectory=path.Text};if(picker.ShowDialog(this)==true)path.Text=picker.FolderName;});
        browse.Margin=new(8,0,0,0);C.Grid.SetColumn(browse,1);row.Children.Add(browse);Row(section,"Folder",row);
    }
    IEnumerable<Choice> RecordingComputers(string saved)
    {
        var values=new List<Choice>{new("","This PC")};
        foreach(var peer in (service?.Peers??[]).Where(p=>Guid.TryParseExact(p.Id,"N",out _)&&p.Id!=initial.Kvm.Id).OrderBy(p=>p.Name))values.Add(new(peer.Id,peer.Name));
        foreach(var peer in initial.Kvm.Layout.Where(p=>p.Peer!=initial.Kvm.Id&&Guid.TryParseExact(p.Peer,"N",out _)).GroupBy(p=>p.Peer))
            if(!values.Any(v=>v.Id==peer.Key))values.Add(new(peer.Key,UiStrings.F("{0} · offline",peer.First().Name)));
        if(saved.Length>0&&!values.Any(v=>v.Id==saved))values.Add(new(saved,"Saved computer · offline"));
        return values;
    }
    void TrackRecordingPeers()
    {
        if(service==null)return;
        void Changed()
        {
            if(IsClosed||Dispatcher.HasShutdownStarted)return;
            Dispatcher.BeginInvoke(()=>
            {
                if(IsClosed)return;
                foreach(var editor in recordingEditors.Values)
                {
                    RefreshRecordingComputerChoices(editor);
                    InvalidateRecordingCapabilities(editor);
                }
            });
        }
        service.PeersChanged+=Changed;Closed+=(_,_)=>service.PeersChanged-=Changed;
    }
    void RefreshRecordingComputerChoices(EncodingEditor editor)
    {
        string field=RecordingField(editor.Prefix,"encodingComputer"),selected=Id(field);bool replacing=editor.Replacing;editor.Replacing=true;
        try{choices[field].ItemsSource=RecordingComputers(selected).ToList();Select(choices[field],selected);}
        finally{editor.Replacing=replacing;}
    }
    void BuildEncodingProfile(C.StackPanel page,ReplayOptions original,string prefix,string computer,int pageId)
    {
        string Field(string name)=>RecordingField(prefix,name);
        var section=Section(page,"Recording quality","Choose GPU hardware encoding or CPU x264. CPU encoding uses the processor and supports H.264 only.");
        Row(section,"Encoding computer",Choose(Field("encodingComputer"),RecordingComputers(computer),computer));
        Row(section,"Encoding method",Choose(Field("encoder"),[new("GPU","GPU hardware · NVENC / AMF / Quick Sync"),new("CPU","CPU x264 · H.264")],original.Encoder));
        section.Children.Add(Kit.Text("A paired encoding PC records this PC's NDI stream. Keep the selected PC connected and enable paired recording requests there.",12,"#B0A9BC"));
        var codec=new C.ComboBox{IsEditable=true,IsReadOnly=true,IsEnabled=false,ItemsSource=Array.Empty<Choice>(),ItemTemplate=Kit.ChoiceTemplate(),Text=original.Codec};choices[Field("codec")]=codec;
        Row(section,"Codec",codec);Row(section,"Quality",Choose(Field("quality"),Options("Ultra","High","Medium","Low"),original.Quality));
        Row(section,"Frame rate",Choose(Field("fps"),[new("60","60 FPS"),new("120","120 FPS")],original.Fps.ToString(CultureInfo.InvariantCulture)));
        var resolutions=new List<Choice>{new("0","Monitor resolution"),new("1280","1280 × 720 · 720p"),new("1920","1920 × 1080 · 1080p"),new("2560","2560 × 1440 · 1440p"),new("3840","3840 × 2160 · 4K")};
        string resolution=RecordingResolutionId(original.Width,original.Height);
        if(!resolutions.Any(r=>r.Id==resolution))resolutions.Add(new(resolution,$"{original.Width} × {original.Height}"));
        Row(section,"Resolution",Choose(Field("resolution"),resolutions,resolution));
        Row(section,"Rate control",Choose(Field("rateControl"),[new("Quality","Quality target"),new("VBR","Variable bitrate (VBR)"),new("CBR","Constant bitrate (CBR)")],original.RateControl));
        Row(section,"Bitrate limit (Mbps)",Input(Field("bitrate"),original.BitrateMbps.ToString(CultureInfo.InvariantCulture)));
        section.Children.Add(Kit.Text("0 uses an automatic bitrate limit. VBR targets 75% of the limit; CBR targets the full limit. Quality mode uses x264 CRF on the CPU and vendor quality controls on the GPU.",12,"#B0A9BC"));
        Row(section,"Encoder preset",Choose(Field("preset"),Options("Performance","Balanced","Quality"),original.EncoderPreset));
        Row(section,"Keyframe interval (s)",Input(Field("keyframe"),original.KeyframeSeconds.ToString(CultureInfo.InvariantCulture)));
        section.Children.Add(Kit.Text("Choose 1–10 seconds between keyframes. Changing the encoding format starts a fresh replay buffer.",12,"#B0A9BC"));
        section.Children.Add(Kit.Text("High is recommended; Ultra creates larger files. CPU presets use ultrafast, veryfast or medium. GPU mode keeps hardware encoding and does not fall back to the CPU.",12,"#B0A9BC"));
        var status=Kit.Text("Check encoder support to choose a codec for this profile.",12,"#B0A9BC");
        C.Button probe=Kit.Button("Check encoder support",()=>_ =RefreshRecordingCapabilities(prefix));probe.HorizontalAlignment=W.HorizontalAlignment.Left;
        var editor=new EncodingEditor(prefix,original,codec,status,probe);recordingEditors[prefix]=editor;section.Children.Add(probe);status.Margin=new(0,10,0,0);section.Children.Add(status);
        codec.SelectionChanged+=(_,_)=>{if(!editor.Replacing&&codec.SelectedItem is Choice selected)editor.SavedCodec=selected.Id;};
        void RefreshQuality()
        {
            bool wasReplacing=editor.Replacing;editor.Replacing=true;
            try
            {
            string selected=Id(Field("quality"));var (width,height)=ParseRecordingResolution(Id(Field("resolution")));
            if(width==0&&SelectedRecordingDisplay() is {} display){width=display.Width;height=display.Height;}
            int fps=int.Parse(Id(Field("fps")),CultureInfo.InvariantCulture);
            choices[Field("quality")].ItemsSource=new[]{("Ultra","maximum detail"),("High","recommended"),("Medium","smaller files"),("Low","minimum size")}.Select(level=>new Choice(level.Item1,
                Id(Field("encoder"))=="CPU"?(Id(Field("rateControl"))=="Quality"?UiStrings.F("{0} ({1} · x264 CRF {2})",UiStrings.T(level.Item1),UiStrings.T(level.Item2),RecordingEncoders.QualityTarget(level.Item1)):UiStrings.F("{0} ({1})",UiStrings.T(level.Item1),UiStrings.T(level.Item2))):
                width>0?UiStrings.F("{0} ({1} · NVIDIA ≤{2} Mbps)",UiStrings.T(level.Item1),UiStrings.T(level.Item2),RecordingEncoders.MaximumBitrate(level.Item1,width,height,fps)):UiStrings.F("{0} ({1})",UiStrings.T(level.Item1),UiStrings.T(level.Item2)))).ToArray();
            Select(choices[Field("quality")],selected);
            }
            finally{editor.Replacing=wasReplacing;}
        }
        foreach(string field in new[]{"resolution","fps","rateControl"})choices[Field(field)].SelectionChanged+=(_,_)=>RefreshQuality();
        choices["display"].SelectionChanged+=(_,_)=>RefreshQuality();
        RefreshQuality();
        choices[Field("encoder")].SelectionChanged+=(_,_)=>
        {
            if(editor.Replacing)return;
            if(Id(Field("encoder"))=="CPU")
            {
                editor.Replacing=true;
                try{editor.Codec.SelectedItem=null;editor.SavedCodec="H264";editor.Codec.Text="H264";}
                finally{editor.Replacing=false;}
            }
            RefreshQuality();InvalidateRecordingCapabilities(editor);
        };
        foreach(string field in new[]{"encodingComputer","quality","resolution","fps","rateControl","preset"})choices[Field(field)].SelectionChanged+=(_,_)=>{if(!editor.Replacing)InvalidateRecordingCapabilities(editor);};
        foreach(string field in new[]{"bitrate","keyframe"})texts[Field(field)].TextChanged+=(_,_)=>InvalidateRecordingCapabilities(editor);
        choices["display"].SelectionChanged+=(_,_)=>InvalidateRecordingCapabilities(editor);
        System.ComponentModel.PropertyChangedEventHandler languageChanged=(_,_)=>{RefreshQuality();RefreshRecordingComputerChoices(editor);SetRecordingStatus(editor,editor.StatusKey,editor.StatusArguments);};UiStrings.Shared.PropertyChanged+=languageChanged;Closed+=(_,_)=>UiStrings.Shared.PropertyChanged-=languageChanged;
        InvalidateRecordingCapabilities(editor);
        Loaded+=async(_,_)=>{if(SelectedPage==pageId&&CanProbeRecording(editor))await RefreshRecordingCapabilities(prefix);};
    }
    MonitorNames.Entry? SelectedRecordingDisplay()=>availableDisplays.FirstOrDefault(d=>d.Device==Id("display"))??availableDisplays.FirstOrDefault();
    MonitorNames.Entry? CurrentRecordingSource()
    {
        var screen=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==Id("display"))??Screen.PrimaryScreen;
        return screen==null?null:new(screen.DeviceName,screen.DeviceName,screen.Bounds.Width,screen.Bounds.Height);
    }
    static void SetRecordingStatus(EncodingEditor editor,string key,params object[] arguments)
    {editor.StatusKey=key;editor.StatusArguments=arguments;editor.Status.Text=UiStrings.F(key,arguments);}
    bool CanProbeRecording(EncodingEditor editor)=>recordingCapabilities!=null||(Id(RecordingField(editor.Prefix,"encodingComputer")).Length==0&&RecordingTools.Ready);
    void InvalidateRecordingCapabilities(EncodingEditor editor)
    {
        editor.Revision++;editor.Pending?.Cancel();ResetRecordingCodec(editor,[]);
        SetRecordingStatus(editor,CanProbeRecording(editor)?"Check encoder support to choose a codec for this profile.":Id(RecordingField(editor.Prefix,"encodingComputer")).Length>0?"Connect the selected encoding PC to check its codecs.":"Recording tools are not installed. Set them up on the Instant replay page to check encoder support.");
        editor.Probe.IsEnabled=CanProbeRecording(editor)&&editor.Pending==null;
    }
    void ResetRecordingCodec(EncodingEditor editor,IReadOnlyList<Choice> supported)
    {
        editor.Replacing=true;
        try
        {
            if(editor.Codec.SelectedItem is Choice previous)editor.SavedCodec=previous.Id;
            editor.Codec.ItemsSource=supported;editor.Codec.SelectedItem=supported.FirstOrDefault(c=>c.Id==editor.SavedCodec);
            editor.Codec.Text=editor.Codec.SelectedItem is Choice selected?selected.Label:editor.SavedCodec;
            editor.Codec.IsEnabled=supported.Count>0;
        }
        finally{editor.Replacing=false;}
    }
    internal async Task RefreshRecordingCapabilities(string prefix)
    {
        var editor=recordingEditors[prefix];if(editor.Pending!=null||IsClosed)return;
        int revision=++editor.Revision;
        using var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);pending.CancelAfter(TimeSpan.FromMinutes(2));editor.Pending=pending;
        editor.Probe.IsEnabled=false;ResetRecordingCodec(editor,[]);SetRecordingStatus(editor,"Checking the selected encoder with this recording profile…");
        try
        {
            var profile=CollectEncodingProfile(prefix,editor.Original);profile.Validate();
            string computer=Id(RecordingField(prefix,"encodingComputer"));
            var display=CurrentRecordingSource()??throw new IOException(UiStrings.T("Select an available monitor before checking encoder support."));
            IReadOnlyList<RecordingCapability> result;
            if(recordingCapabilities!=null)result=await recordingCapabilities(computer,profile,pending.Token).WaitAsync(pending.Token);
            else if(computer.Length==0&&RecordingTools.Ready)result=await RecordingCapabilities.DiscoverAsync(profile,display.Width,display.Height,pending.Token);
            else throw new IOException(UiStrings.T(computer.Length>0?"Connect the selected encoding PC to check its codecs.":"Recording tools are not installed. Set them up on the Instant replay page to check encoder support."));
            if(IsClosed||revision!=editor.Revision)return;
            var supported=SupportedRecordingCodecs(result,profile,display.Width,display.Height);ResetRecordingCodec(editor,supported);
            if(supported.Length==0)SetRecordingStatus(editor,"The selected encoder does not support this profile. Try another frame rate, resolution or encoding method.");
            else if(supported.Any(c=>c.Id==editor.SavedCodec))SetRecordingStatus(editor,"Encoder support checked for the selected profile.");
            else SetRecordingStatus(editor,"The saved codec {0} is unsupported for this profile. Choose a supported codec.",editor.SavedCodec);
        }
        catch(OperationCanceledException){if(!IsClosed&&revision==editor.Revision)SetRecordingStatus(editor,"Encoder check was cancelled or timed out. Your saved codec is kept.");}
        catch(Exception failure){if(!IsClosed&&revision==editor.Revision){ResetRecordingCodec(editor,[]);SetRecordingStatus(editor,"Encoder check failed: {0}",failure.Message);}}
        finally{editor.Pending=null;if(!IsClosed)editor.Probe.IsEnabled=CanProbeRecording(editor);}
    }
    internal static Choice[] SupportedRecordingCodecs(IReadOnlyList<RecordingCapability> capabilities,ReplayOptions profile,int sourceWidth,int sourceHeight)
    {
        int width=profile.Width==0?sourceWidth:profile.Width,height=profile.Width==0?sourceHeight:profile.Height;
        return capabilities.Where(c=>(c.Codec is "H264" or "HEVC" or "AV1")&&c.Backend==profile.Encoder&&
            (c.Backend=="CPU"?c.Codec=="H264"&&c.Encoder=="libx264":c.Encoder.EndsWith("_nvenc",StringComparison.Ordinal)||c.Encoder.EndsWith("_amf",StringComparison.Ordinal)||c.Encoder.EndsWith("_qsv",StringComparison.Ordinal))&&
            c.SourceWidth==sourceWidth&&c.SourceHeight==sourceHeight&&c.Width==width&&c.Height==height&&c.Fps==profile.Fps&&c.Quality==profile.Quality&&c.RateControl==profile.RateControl&&c.BitrateMbps==profile.BitrateMbps&&c.KeyframeSeconds==profile.KeyframeSeconds&&c.EncoderPreset==profile.EncoderPreset).Select(c=>c.Codec).Distinct().Select(codec=>new Choice(codec,codec)).ToArray();
    }
    internal static string RecordingResolutionId(int width,int height)=>width switch{0 when height==0=>"0",1280 when height==720=>"1280",1920 when height==1080=>"1920",2560 when height==1440=>"2560",3840 when height==2160=>"3840",_=>$"{width}x{height}"};
    internal static (int Width,int Height) ParseRecordingResolution(string id)
    {
        if(id.Contains('x')){string[] pair=id.Split('x');if(pair.Length==2&&int.TryParse(pair[0],NumberStyles.None,CultureInfo.InvariantCulture,out int width)&&int.TryParse(pair[1],NumberStyles.None,CultureInfo.InvariantCulture,out int height))return(width,height);throw new ArgumentException(UiStrings.T("Choose a valid recording resolution."));}
        return id switch{"0"=>(0,0),"1280"=>(1280,720),"1920"=>(1920,1080),"2560"=>(2560,1440),"3840"=>(3840,2160),_=>throw new ArgumentException(UiStrings.T("Choose a valid recording resolution."))};
    }
    internal static int RecordingNumber(string value,string label,int minimum,int maximum)
    {
        if(!int.TryParse(value.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int number)||number<minimum||number>maximum)throw new ArgumentException(UiStrings.F("{0} must be a whole number between {1} and {2}.",UiStrings.T(label),minimum,maximum));
        return number;
    }
    ReplayOptions CollectEncodingProfile(string prefix,ReplayOptions original)
    {
        string Field(string name)=>RecordingField(prefix,name);var editor=recordingEditors[prefix];var (width,height)=ParseRecordingResolution(Id(Field("resolution")));
        return original with{Codec=(choices[Field("codec")].SelectedItem as Choice)?.Id??editor.SavedCodec,Encoder=Id(Field("encoder")),Quality=Id(Field("quality")),Fps=int.Parse(Id(Field("fps")),CultureInfo.InvariantCulture),Width=width,Height=height,RateControl=Id(Field("rateControl")),BitrateMbps=RecordingNumber(texts[Field("bitrate")].Text,"Bitrate limit (Mbps)",0,1000),EncoderPreset=Id(Field("preset")),KeyframeSeconds=RecordingNumber(texts[Field("keyframe")].Text,"Keyframe interval (s)",1,10),EncodingComputer=Id(Field("encodingComputer"))};
    }
    void CollectRecording(Settings result)
    {
        result.Recording=result.Recording with{Computer=Id("recordingEncodingComputer"),AllowRemoteEncoding=On("allowRemoteEncoding"),Profile=CollectEncodingProfile("recording",result.Recording.Profile) with{Enabled=true,EncodingComputer="",Folder=texts["recordingFolder"].Text.Trim(),HotkeyModifiers=recordingKey.Modifiers,HotkeyKey=recordingKey.Key,
            AudioMode=Id("recordingAudioMode"),Silent=false,GameAudio=Id("recordingGame"),Microphone=Id("recordingMic"),ExtraAudio=Id("recordingExtra")}};
    }
    static void ValidateRecordingShortcut(Settings result)
    {
        var shortcut=(result.Recording.Profile.HotkeyModifiers,result.Recording.Profile.HotkeyKey);
        if(new[]{(result.Replay.HotkeyModifiers,result.Replay.HotkeyKey),(result.Kvm.OpenHotkeyModifiers,result.Kvm.OpenHotkeyKey),(result.Kvm.ToggleHotkeyModifiers,result.Kvm.ToggleHotkeyKey)}.Contains(shortcut)||result.Kvm.Layout.Where(m=>m.Hotkey>0).Any(m=>shortcut==(3u,(uint)Keys.F1+(uint)m.Hotkey-1)))throw new ArgumentException(UiStrings.T("The recording shortcut must differ from replay, KVM and monitor shortcuts."));
    }
}
