using System.Diagnostics;

namespace SdrCapture;
class AppSettingsForm:Form
{
    readonly Settings initial;
    readonly KvmService? service;
    readonly Panel content=new(){Dock=DockStyle.Fill,Padding=new Padding(24,12,24,16),BackColor=Color.White};
    readonly ListBox navigation=new(){Dock=DockStyle.Left,Width=208,BorderStyle=BorderStyle.None,ItemHeight=62,DrawMode=DrawMode.OwnerDrawFixed,BackColor=Color.FromArgb(240,243,248)};
    readonly PageTransition transition=new();
    readonly ToolTip help=new(){AutoPopDelay=14000,InitialDelay=350,ReshowDelay=100};
    bool preview;
    readonly string[] subtitles=["OBS и Discord","Сохранить последние минуты","Игра, микрофон, второй ПК","Сопряжение компьютеров","Расположение и переходы","Все подключённые ПК","Диагностика и журнал"];
    readonly List<Control> pages=[];
    readonly CheckBox ndi=new(){Text="Передавать SDR-картинку по NDI"},cursor=new(){Text="Показывать курсор"},replay=new(){Text="Записывать последние минуты в фоне"},startup=new(){Text="Запускать вместе с Windows"};
    readonly ComboBox ndiAudio=Choice(),display=Choice(),quality=Choice(),codec=Choice(),fps=Choice(),resolution=Choice(),audioMode=Choice(),mic=Choice(),game=Choice(),extra=Choice(),role=Choice(),remoteAudio=Choice();
    readonly TrackBar ndiVolume=new(){Minimum=0,Maximum=100,TickFrequency=10,SmallChange=1,LargeChange=10};
    readonly CheckBox checkUpdates=new(){Text="Проверять новые версии при запуске"},remoteUpdates=new(){Text="Разрешить обновление с сопряжённого управляющего ПК"};
    readonly NumericUpDown minutes=new(){Minimum=5,Maximum=20};
    readonly TextBox folder=new(),host=new(),pairing=new(),hostCode=new(){ReadOnly=true};
    readonly NumericUpDown port=new(){Minimum=1024,Maximum=65535};
    readonly CheckBox seamless=new(){Text="Переходить мышью между мониторами"},textClipboard=new(){Text="Общий текстовый буфер обмена"},fileClipboard=new(){Text="Копировать файлы и папки"},allowView=new(){Text="Разрешить просмотр рабочего стола"};
    readonly CheckBox ctrl=new(){Text="Ctrl",AutoSize=true},alt=new(){Text="Alt",AutoSize=true},shift=new(){Text="Shift",AutoSize=true};
    readonly ComboBox hotkey=Choice();
    readonly MonitorLayoutEditor monitors;
    public Settings ResultSettings {get;private set;}
    public bool StartWithWindows=>startup.Checked;
    public AppSettingsForm(Settings value,KvmService? service=null,bool autorun=false,Func<string>? diagnostics=null,Action<KvmPeerInfo>? view=null,Action? openUpdates=null,int startPage=0)
    {
        initial=value.Copy();ResultSettings=value.Copy();this.service=service;
        Text=$"ScreenCapture {Updates.VersionText} · Настройки";Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);ClientSize=new(1020,760);MinimumSize=new(940,700);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.Dpi;BackColor=Color.White;
        var heading=new Panel{Dock=DockStyle.Top,Height=88,Padding=new Padding(24,14,24,8),BackColor=Color.FromArgb(24,35,54)};
        heading.Controls.Add(new Label{Text="ScreenCapture",ForeColor=Color.White,Font=new Font(Font.FontFamily,23,FontStyle.Bold),Dock=DockStyle.Top,Height=43});
        heading.Controls.Add(new Label{Text="Экран, мгновенный повтор и управление компьютерами",ForeColor=Color.FromArgb(195,211,234),Dock=DockStyle.Bottom,Height=22});
        var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=65,Padding=new Padding(16,12,20,10),FlowDirection=FlowDirection.RightToLeft,BackColor=Color.FromArgb(245,247,250)};
        var save=new Button{Text="Сохранить",Width=140,Height=36,BackColor=Color.FromArgb(33,105,211),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};save.FlatAppearance.BorderSize=0;
        var cancel=new Button{Text="Отмена",Width=110,Height=36,DialogResult=DialogResult.Cancel};
        footer.Controls.AddRange([save,cancel]);Controls.Add(content);Controls.Add(navigation);Controls.Add(footer);Controls.Add(heading);
        AcceptButton=save;CancelButton=cancel;
        navigation.DrawItem+=(_,e)=>{if(e.Index<0)return;bool selected=(e.State&DrawItemState.Selected)!=0;using var brush=new SolidBrush(selected?Color.FromArgb(221,232,249):navigation.BackColor);e.Graphics.FillRectangle(brush,e.Bounds);if(selected){using var accent=new SolidBrush(Color.FromArgb(33,105,211));e.Graphics.FillRectangle(accent,e.Bounds.X,e.Bounds.Y+9,3,e.Bounds.Height-18);}TextRenderer.DrawText(e.Graphics,navigation.Items[e.Index].ToString(),Font,new Point(e.Bounds.X+18,e.Bounds.Y+11),selected?Color.FromArgb(20,80,168):Color.FromArgb(50,61,78));using var small=new Font(Font.FontFamily,8);TextRenderer.DrawText(e.Graphics,subtitles[e.Index],small,new Point(e.Bounds.X+18,e.Bounds.Y+34),Color.FromArgb(96,111,133));};
        content.Controls.Add(transition);
        navigation.SelectedIndexChanged+=(_,_)=>{if(!preview)transition.CapturePage(content);for(int i=0;i<pages.Count;i++)pages[i].Visible=i==navigation.SelectedIndex;content.PerformLayout();if(!preview)transition.Play(content);};
        FormClosed+=(_,_)=>help.Dispose();

        var general=Page("Передача экрана","Из HDR и SDR — всегда SDR");
        ndi.Checked=value.SendOnLaunch;Row(general,"NDI → OBS / Discord",ndi);
        try{foreach(var d in DisplayInfo.All())display.Items.Add(new DisplayChoice(d.Device,$"{d.Device} · {d.Width} × {d.Height}"));}catch{}
        if(!display.Items.Cast<DisplayChoice>().Any(d=>d.Id==value.Device))display.Items.Add(new DisplayChoice(value.Device,value.Device+" (недоступен)"));
        display.SelectedItem=display.Items.Cast<DisplayChoice>().First(d=>d.Id==value.Device);Row(general,"Монитор",display);
        cursor.Checked=value.CaptureCursor;Row(general,"Курсор",cursor);
        startup.Checked=autorun;Row(general,"Запуск",startup);
        PopulateAudio(ndiAudio,value.NdiAudioDevice);Row(general,"Звук NDI / Discord",ndiAudio);
        ndiVolume.Value=Math.Clamp(value.NdiAudioVolume,0,100);
        var volumeLabel=new Label{Text=ndiVolume.Value+"%",AutoSize=true,Dock=DockStyle.Right,TextAlign=ContentAlignment.MiddleCenter};
        var volumePanel=new Panel();ndiVolume.Dock=DockStyle.Fill;volumePanel.Controls.Add(ndiVolume);volumePanel.Controls.Add(volumeLabel);
        ndiVolume.ValueChanged+=(_,_)=>volumeLabel.Text=ndiVolume.Value+"%";Row(general,"Громкость NDI / Discord",volumePanel,50);
        help.SetToolTip(ndiVolume,"Меняет только звук, отправляемый на стрим-ПК. 0% — тишина. Громкость записи и Windows не меняется.");
        Note(general,"NDI High Bandwidth · исходное разрешение · 60 FPS · SDR BT.709. Выберите выход с игрой для звука в Discord; Silent выключает звук NDI независимо от записи.");
        var obs=new Button{Text="Подключить к OBS",AutoSize=true};obs.Click+=(_,_)=>MessageBox.Show(this,TrayApp.ConnectionText,"OBS / DistroAV");Row(general,"OBS",obs);
        Note(general,"Discord без OBS: на стрим-ПК откройте NDI Webcam Input → выберите источник «"+Environment.MachineName+" (SdrCapture SDR)» → в Discord выберите NDI Webcam Video 1. Для звука выберите NDI Audio / NDI Webcam Audio в настройках звука трансляции Discord. Микрофон и звук стрим-ПК из записи сюда не подмешиваются.");

        var recording=Page("Мгновенный повтор","Запись на видеокарте NVIDIA");
        replay.Checked=value.Replay.Enabled;Row(recording,"Фоновая запись",replay);
        minutes.Value=Math.Clamp(value.Replay.Minutes,5,20);Row(recording,"Длина повтора, минут",minutes);
        Set(codec,["HEVC","H264","AV1"],value.Replay.Codec);Row(recording,"Кодек NVENC",codec);
        help.SetToolTip(codec,"HEVC — компактные записи. H264 — широкая совместимость. AV1 — требует поддержки видеокарты и проигрывателя.");
        Set(quality,["Ultra","High","Medium","Low"],value.Replay.Quality);Row(recording,"Качество",quality);
        Set(fps,["60 FPS","120 FPS"],value.Replay.Fps+" FPS");Row(recording,"Частота записи",fps);
        help.SetToolTip(fps,"60 FPS — обычный повтор. 120 FPS — больше кадров для плавного замедления; выше нагрузка. Частота передачи NDI остаётся 60 FPS.");
        help.SetToolTip(quality,"Ultra сохраняет больше деталей и создаёт самые большие файлы. High — исходный вариант для повседневной записи.");
        Set(resolution,["Разрешение монитора","1920 × 1080","2560 × 1440","3840 × 2160"],value.Replay.Width switch{1920=>"1920 × 1080",2560=>"2560 × 1440",3840=>"3840 × 2160",_=>"Разрешение монитора"});Row(recording,"Разрешение",resolution);
        folder.Text=value.Replay.Folder;
        var pathPanel=new TableLayoutPanel{ColumnCount=2,RowCount=1,Dock=DockStyle.Fill,Margin=Padding.Empty};
        pathPanel.ColumnStyles.Add(new(SizeType.Percent,100));pathPanel.ColumnStyles.Add(new(SizeType.Absolute,105));
        folder.Dock=DockStyle.Fill;folder.Margin=new(0,5,6,0);pathPanel.Controls.Add(folder,0,0);
        var browse=new Button{Text="Обзор…",Dock=DockStyle.Fill,Margin=new(0,1,0,4)};
        browse.Click+=(_,_)=>{using var picker=new FolderBrowserDialog{Description="Папка для записей",UseDescriptionForTitle=true,SelectedPath=folder.Text,ShowNewFolderButton=true};if(picker.ShowDialog(this)==DialogResult.OK)folder.Text=picker.SelectedPath;};
        pathPanel.Controls.Add(browse,1,0);Row(recording,"Папка для видео",pathPanel,48);
        Note(recording,@"Можно вставить путь вручную, включая \\STREAM-PC\Clips. Подпапки с именами игр создаются автоматически. Готовый клип сначала сохраняется локально, затем копируется в выбранную папку.");
        var verify=new Button{Text="Проверить доступ к папке",AutoSize=true};
        verify.Click+=async(_,_)=>{verify.Enabled=false;string destination=folder.Text.Trim();try{await Task.Run(()=>{if(!Path.IsPathFullyQualified(destination))throw new IOException("Нужен полный путь.");Directory.CreateDirectory(destination);string probe=Path.Combine(destination,".sdr-write-test-"+Guid.NewGuid().ToString("N"));try{File.WriteAllText(probe,"SDR Capture write test");}finally{File.Delete(probe);}}).WaitAsync(TimeSpan.FromSeconds(10));if(!IsDisposed)MessageBox.Show(this,"Папка доступна для записи.","Проверка");}catch(Exception e){if(!IsDisposed)MessageBox.Show(this,e.Message,"Папка недоступна");}finally{if(!IsDisposed)verify.Enabled=true;}};
        Row(recording,"",verify);
        ctrl.Checked=(value.Replay.HotkeyModifiers&2)!=0;alt.Checked=(value.Replay.HotkeyModifiers&1)!=0;shift.Checked=(value.Replay.HotkeyModifiers&4)!=0;
        Set(hotkey,Enumerable.Range((int)Keys.F1,12).Select(k=>((Keys)k).ToString()).ToArray(),((Keys)value.Replay.HotkeyKey).ToString());hotkey.Width=90;
        var shortcut=new FlowLayoutPanel{WrapContents=false,Margin=Padding.Empty};shortcut.Controls.AddRange([ctrl,alt,shift,hotkey]);Row(recording,"Сохранить повтор",shortcut,45);
        Note(recording,"Stream Deck может нажимать это сочетание. Смена кодека, качества, FPS, разрешения или аудиорежима очищает накопленный буфер.");

        var sound=Page("Звук записи","Одна общая дорожка или три отдельных");
        Set(audioMode,["Объединить в одну дорожку","Три отдельные дорожки","Silent — без звука"],value.Replay.IsSilent?"Silent — без звука":value.Replay.AudioMode=="Separate"?"Три отдельные дорожки":"Объединить в одну дорожку");Row(sound,"Режим",audioMode);
        PopulateAudio(game,value.Replay.GameAudio);PopulateAudio(mic,value.Replay.Microphone);PopulateAudio(extra,value.Replay.ExtraAudio,true);
        Row(sound,"1 · Звук игрового ПК",game);Row(sound,"2 · Микрофон",mic);Row(sound,"3 · Звук стрим-ПК",extra);
        audioMode.SelectedIndexChanged+=(_,_)=>game.Enabled=mic.Enabled=extra.Enabled=audioMode.SelectedIndex!=2;game.Enabled=mic.Enabled=extra.Enabled=audioMode.SelectedIndex!=2;
        Note(sound,"Silent доступен для каждого источника. Отдельные дорожки: игра, микрофон, стрим-ПК. Неиспользуемая дорожка будет тихой. Звук стрим-ПК можно получить по KVM: подключите второй ПК и выберите его здесь.");
        Note(sound,"На стрим-ПК: KVM → Управляемый ПК → «Передавать звук» → нужный вход или выход. Звук поступает только в запись на игровом ПК, без воспроизведения в колонках.");

        var network=Page("KVM / сеть","Клавиатура, мышь, буфер обмена и звук");
        Set(role,["Выключен","Управляющий ПК (клавиатура и мышь)","Управляемый ПК (стрим-ПК / сервер)"],value.Kvm.Role switch{"Host"=>"Управляющий ПК (клавиатура и мышь)","Client"=>"Управляемый ПК (стрим-ПК / сервер)",_=>"Выключен"});Row(network,"Роль этого ПК",role);
        var roleHelp=Note(network,"");
        var advancedNetwork=new CheckBox{Text="Дополнительные параметры сети"};Row(network,"",advancedNetwork);
        port.Value=value.Kvm.Port;Row(network,"Порт KVM",port);ShowRow(port,false);advancedNetwork.CheckedChanged+=(_,_)=>ShowRow(port,advancedNetwork.Checked);
        host.Text=value.Kvm.Host;Row(network,"Имя / IP управляющего ПК",host);
        pairing.Text=value.Kvm.PairingCode;Row(network,"Код с управляющего ПК",pairing);
        hostCode.Text=service?.PairingCode??"";
        var codePanel=new TableLayoutPanel{ColumnCount=2};codePanel.ColumnStyles.Add(new(SizeType.Percent,100));codePanel.ColumnStyles.Add(new(SizeType.Absolute,115));hostCode.Dock=DockStyle.Fill;codePanel.Controls.Add(hostCode);
        var copy=new Button{Text="Копировать",Dock=DockStyle.Fill};copy.Click+=(_,_)=>{try{if(hostCode.Text.Length==0){using var identity=new KvmIdentity(Path.Combine(Log.Folder,"Kvm"));hostCode.Text=identity.Code;}Clipboard.SetText(hostCode.Text);}catch(Exception e){MessageBox.Show(this,e.Message);}};codePanel.Controls.Add(copy);Row(network,"Код этого ПК",codePanel,46);
        seamless.Checked=value.Kvm.Seamless;Row(network,"Переходы",seamless);
        textClipboard.Checked=value.Kvm.ClipboardText;fileClipboard.Checked=value.Kvm.ClipboardFiles;
        var clipPanel=new FlowLayoutPanel{WrapContents=true};textClipboard.AutoSize=fileClipboard.AutoSize=true;clipPanel.Controls.AddRange([textClipboard,fileClipboard]);Row(network,"Буфер обмена",clipPanel,65);
        PopulateAudio(remoteAudio,value.Kvm.AudioDevice);Row(network,"Передавать звук",remoteAudio);
        allowView.Checked=value.Kvm.AllowView;Row(network,"Удалённое окно",allowView);
        void RoleChanged(){foreach(var field in new Control[]{host,pairing,remoteAudio,allowView})ShowRow(field,role.SelectedIndex==2);ShowRow(codePanel,role.SelectedIndex==1);ShowRow(seamless,role.SelectedIndex==1);ShowRow(clipPanel,role.SelectedIndex!=0);roleHelp.Text=role.SelectedIndex switch{1=>"Здесь подключены клавиатура и мышь. На втором ПК выберите «Управляемый ПК», затем введите имя «"+Environment.MachineName+"» и код этого компьютера. Для просмотра откройте отдельное окно KVM из трея.",2=>"Этот ПК принимает управление. Введите имя и код игрового ПК. «Передавать звук» отправляет выбранный звук в запись игрового ПК; Silent выключает отправку.",_=>"KVM выключен. Передача картинки NDI и запись работают независимо. Выберите роль, чтобы связать компьютеры."};}
        role.SelectedIndexChanged+=(_,_)=>RoleChanged();RoleChanged();
        Note(network,"Код даёт доступ к управлению: вводите его только на своих компьютерах. Соединения шифруются. При первом подключении разрешите SdrCapture в частной сети в запросе Windows. Ctrl+Alt+Esc — домой; Ctrl+Alt+Pause — включить/выключить переходы мышью.");
        Note(network,"Экран блокировки и окна с повышенными правами могут быть недоступны. На сервере без монитора Windows должна предоставлять рабочий экран; при его отсутствии потребуется виртуальный дисплей или HDMI-заглушка.");
        var layoutPage=Page("Мониторы","Каждый монитор располагается отдельно");
        monitors=new MonitorLayoutEditor(value.Kvm,KvmLayout.Merge(value.Kvm,new[]{new KvmPeerInfo(value.Kvm.Id,Environment.MachineName,KvmScreen.Local())}.Concat(service?.Peers??[]))){Dock=DockStyle.Fill,Height=390};
        layoutPage.Controls.Add(monitors,0,layoutPage.RowCount);layoutPage.SetColumnSpan(monitors,2);layoutPage.RowStyles.Add(new(SizeType.Absolute,420));layoutPage.RowCount++;
        Note(layoutPage,"Перетащите экраны так, как они стоят на столе. Границы соседних экранов должны касаться. Выберите левый экран стрим-ПК и нажмите «Слева», правый — «Справа». Ctrl+Alt+F1…F12 можно назначить конкретному экрану. Новые экраны появятся здесь после подключения ПК.");
        var updates=Page("Обновления","GitHub Releases · ScreenCapture "+Updates.VersionText);
        checkUpdates.Checked=value.Updates.CheckOnStartup;remoteUpdates.Checked=value.Updates.AllowFromHost;
        Row(updates,"Проверка",checkUpdates);Row(updates,"Обновлять с хоста",remoteUpdates,64);
        var updateButton=new Button{Text="Проверить и обновить ПК…",AutoSize=true};
        updateButton.Click+=(_,_)=>{if(openUpdates!=null){Close();openUpdates();}};updateButton.Enabled=openUpdates!=null;Row(updates,"",updateButton);
        Note(updates,"Обновление всех подключённых ПК запускается с управляющего ПК KVM. Пакеты проверяются по цифровой подписи. Перед перезапуском сохраняется текущий повтор; настройки и код сопряжения остаются. Старые сборки до 0.6 необходимо обновить вручную один раз.");
        var statePage=Page("Состояние","Захват, запись и подключения");
        var state=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Consolas",10),BackColor=Color.White,Text=diagnostics?.Invoke()??"Предпросмотр настроек"};
        statePage.Controls.Add(state,0,statePage.RowCount);statePage.SetColumnSpan(state,2);statePage.RowStyles.Add(new(SizeType.Absolute,390));statePage.RowCount++;
        var refresh=new Button{Text="Обновить",AutoSize=true};refresh.Click+=(_,_)=>state.Text=diagnostics?.Invoke()??"";Row(statePage,"",refresh);
        var logs=new Button{Text="Открыть журнал",AutoSize=true};logs.Click+=(_,_)=>Process.Start(new ProcessStartInfo(Log.Folder){UseShellExecute=true});Row(statePage,"",logs);
        var pending=new Button{Text="Повторы, ожидающие копирования",AutoSize=true};pending.Click+=(_,_)=>{string path=Path.Combine(ReplayTools.Root,"saved");Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});};Row(statePage,"",pending);
        navigation.SelectedIndex=Math.Clamp(startPage,0,pages.Count-1);
        save.Click+=(_,_)=>Save();
    }
    static ComboBox Choice()=>new(){DropDownStyle=ComboBoxStyle.DropDownList,IntegralHeight=false,DropDownHeight=280};
    public void RenderPreviews(string folder)
    {
        preview=true;transition.Finish();Directory.CreateDirectory(folder);Opacity=0;ShowInTaskbar=false;Show();Application.DoEvents();
        for(int i=0;i<pages.Count;i++)
        {
            navigation.SelectedIndex=i;Application.DoEvents();
            using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(Path.Combine(folder,$"settings-{i}.png"));
        }
        Close();
    }
    static void Set(ComboBox combo,string[] items,string selected){combo.Items.AddRange(items);combo.SelectedItem=selected;if(combo.SelectedIndex<0)combo.SelectedIndex=0;}
    TableLayoutPanel Page(string name,string title)
    {
        navigation.Items.Add(name);
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,ColumnCount=2,RowCount=0,Visible=false,BackColor=Color.White};
        panel.ColumnStyles.Add(new(SizeType.Absolute,190));panel.ColumnStyles.Add(new(SizeType.Percent,100));
        var heading=new Label{Text=title,Font=new Font(Font.FontFamily,15,FontStyle.Bold),AutoSize=true,Margin=new(0,6,0,16)};
        panel.Controls.Add(heading,0,0);panel.SetColumnSpan(heading,2);panel.RowStyles.Add(new(SizeType.AutoSize));panel.RowCount=1;
        content.Controls.Add(panel);pages.Add(panel);return panel;
    }
    static void Row(TableLayoutPanel panel,string text,Control control,int height=44)
    {
        int row=panel.RowCount++;panel.RowStyles.Add(new(SizeType.Absolute,height));
        panel.Controls.Add(new Label{Text=text,AutoSize=true,Anchor=AnchorStyles.Left,Margin=new(0,0,8,0)},0,row);
        control.Dock=DockStyle.Fill;control.Margin=new(0,5,0,5);panel.Controls.Add(control,1,row);
    }
    static void ShowRow(Control control,bool visible)
    {
        if(control.Parent is not TableLayoutPanel panel)return;int row=panel.GetRow(control);
        control.Tag??=panel.RowStyles[row].Height;
        panel.RowStyles[row].Height=visible?(float)control.Tag:0;
        control.Visible=visible;var label=panel.GetControlFromPosition(0,row);if(label!=null)label.Visible=visible;
    }
    static Label Note(TableLayoutPanel panel,string text)
    {
        int row=panel.RowCount++;panel.RowStyles.Add(new(SizeType.AutoSize));
        var note=new Label{Text=text,AutoSize=true,MaximumSize=new(715,0),ForeColor=Color.FromArgb(91,104,121),Margin=new(0,10,0,14),Dock=DockStyle.Fill};
        panel.Controls.Add(note,0,row);panel.SetColumnSpan(note,2);return note;
    }
    void PopulateAudio(ComboBox box,string id,bool includeRemote=false)
    {
        List<AudioChoice> devices;try{devices=AudioChoice.All();}catch{devices=[new("","Silent — без звука")];}
        if(includeRemote)foreach(var peer in service?.Peers??[])devices.Add(new("kvm:"+peer.Id,peer.Name+" · звук по KVM"));
        if(!devices.Any(d=>d.Id==id))devices.Add(new(id,id.StartsWith("kvm:")?"KVM · ранее выбранный ПК (не подключён)":"Недоступное устройство"));
        box.Items.AddRange(devices.ToArray());box.SelectedItem=devices.First(d=>d.Id==id);
    }
    void Save()
    {
        try
        {
            var size=resolution.SelectedIndex switch{1=>(1920,1080),2=>(2560,1440),3=>(3840,2160),_=>(0,0)};
            var result=initial.Copy();
            result.NdiAudioVolume=ndiVolume.Value;result.Updates=new(){CheckOnStartup=checkUpdates.Checked,AllowFromHost=remoteUpdates.Checked};result.NdiAudioDevice=((AudioChoice)ndiAudio.SelectedItem!).Id;result.SendOnLaunch=ndi.Checked;result.CaptureCursor=cursor.Checked;result.Device=((DisplayChoice)display.SelectedItem!).Id;
            result.Replay=initial.Replay with{Enabled=replay.Checked,Minutes=(int)minutes.Value,Codec=(string)codec.SelectedItem!,Quality=(string)quality.SelectedItem!,Fps=fps.SelectedIndex==1?120:60,Width=size.Item1,Height=size.Item2,Folder=folder.Text.Trim(),GroupByApp=true,Silent=false,AudioMode=audioMode.SelectedIndex switch{1=>"Separate",2=>"Silent",_=>"Mixed"},Microphone=((AudioChoice)mic.SelectedItem!).Id,GameAudio=((AudioChoice)game.SelectedItem!).Id,ExtraAudio=((AudioChoice)extra.SelectedItem!).Id,HotkeyModifiers=(uint)((ctrl.Checked?2:0)|(alt.Checked?1:0)|(shift.Checked?4:0)),HotkeyKey=(uint)Enum.Parse<Keys>((string)hotkey.SelectedItem!)};
            if(result.Replay.HotkeyModifiers==0)throw new ArgumentException("Для сохранения повтора выберите Ctrl, Alt или Shift.");
            result.Kvm=initial.Kvm with{Role=role.SelectedIndex switch{1=>"Host",2=>"Client",_=>"Off"},Port=(int)port.Value,Host=host.Text.Trim(),PairingCode=pairing.Text.Trim(),Seamless=seamless.Checked,ClipboardText=textClipboard.Checked,ClipboardFiles=fileClipboard.Checked,AllowView=allowView.Checked,AudioDevice=((AudioChoice)remoteAudio.SelectedItem!).Id,Layout=monitors.Result};
            result.Replay.Validate();result.Kvm.Validate();
            if(result.Kvm.Layout.Any(m=>m.Hotkey>0&&(int)result.Replay.HotkeyKey==(int)Keys.F1+m.Hotkey-1)&&result.Replay.HotkeyModifiers==3)throw new ArgumentException("Клавиша сохранения повтора совпадает с клавишей переключения монитора.");
            ResultSettings=result;DialogResult=DialogResult.OK;Close();
        }
        catch(Exception e){MessageBox.Show(this,e.Message,"Проверьте настройки",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    }
    sealed record DisplayChoice(string Id,string Text){public override string ToString()=>Text;}
    sealed record PeerChoice(KvmPeerInfo Peer){public override string ToString()=>Peer.Name;}
}
