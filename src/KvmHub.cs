namespace SdrCapture;

sealed class KvmHub:Form
{
    readonly Func<KvmService?> service;
    readonly Func<bool> seamless;
    readonly CheckBox transitions=new(){Text="Переходить мышью между физическими мониторами",AutoSize=true,Margin=new(0,12,0,12)};
    readonly FlowLayoutPanel computers=new(){Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new(22,10,22,12),BackColor=Color.FromArgb(246,248,252)};
    readonly Label status=new(){Dock=DockStyle.Bottom,Height=40,Padding=new(24,8,12,0),ForeColor=Color.FromArgb(85,101,122)};
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly Action<KvmPeerInfo> view;
    readonly Action<int> settings;
    string signature="";bool refreshing;
    public KvmHub(Func<KvmService?> service,Func<bool> seamless,Action<bool> setSeamless,Action returnHome,Action<KvmPeerInfo> view,Action<int> settings)
    {
        this.service=service;this.seamless=seamless;this.view=view;this.settings=settings;
        Text="ScreenCapture · KVM";Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);ClientSize=new(820,580);MinimumSize=new(720,480);Font=new("Segoe UI",10);BackColor=Color.White;StartPosition=FormStartPosition.CenterScreen;
        var header=new Panel{Dock=DockStyle.Top,Height=110,Padding=new(24,15,24,10),BackColor=Color.FromArgb(24,35,54)};
        header.Controls.Add(new Label{Text="Какой компьютер открыть?",Dock=DockStyle.Top,Height=44,Font=new(Font.FontFamily,22,FontStyle.Bold),ForeColor=Color.White});
        header.Controls.Add(new Label{Text="Экран удалённого ПК откроется в отдельном окне. NDI и запись продолжат работать.",Dock=DockStyle.Bottom,Height=40,ForeColor=Color.FromArgb(200,215,236)});
        var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=110,Padding=new(24,4,12,10),FlowDirection=FlowDirection.TopDown,WrapContents=false};
        actions.Controls.Add(transitions);
        var buttons=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Margin=Padding.Empty};
        buttons.Controls.Add(UiStyle.Button("Подключить ПК…",()=>settings(3)));
        buttons.Controls.Add(UiStyle.Button("Раскладка мониторов…",()=>settings(4)));
        buttons.Controls.Add(UiStyle.Button("Вернуть управление",returnHome));actions.Controls.Add(buttons);
        Controls.Add(computers);Controls.Add(status);Controls.Add(actions);Controls.Add(header);
        transitions.CheckedChanged+=(_,_)=>{if(!refreshing)setSeamless(transitions.Checked);};
        timer.Tick+=(_,_)=>RefreshPeers();Shown+=(_,_)=>{RefreshPeers();timer.Start();};FormClosed+=(_,_)=>timer.Dispose();
    }
    void RefreshPeers()
    {
        var network=service();var peers=network?.Peers.OrderBy(p=>p.Name).ToArray()??[];
        refreshing=true;transitions.Enabled=network?.Options.Role=="Host";transitions.Checked=seamless();refreshing=false;
        status.Text=network?.Status??"KVM выключен. Начните с «Подключить ПК».";
        string next=(network?.Options.Role??"Off")+string.Join('|',peers.Select(p=>p.Id+p.Name+p.Version+string.Join(',',p.Screens.Select(s=>s.Device))));
        if(signature==next)return;signature=next;computers.SuspendLayout();
        foreach(Control c in computers.Controls.Cast<Control>().ToArray())c.Dispose();
        if(network?.Options.Role!="Host"||peers.Length==0)
        {
            computers.Controls.Add(new Label{Text=network?.Options.Role=="Client"?"Этот ПК управляемый. Открывайте его экран с управляющего компьютера.":"Пока нет подключённых компьютеров.\n\n1. Здесь выберите роль «Управляющий ПК».\n2. На втором ПК выберите «Управляемый ПК».\n3. Введите имя этого ПК и код сопряжения.\n\nПодключённый компьютер появится здесь автоматически.",AutoSize=true,MaximumSize=new(720,0),Padding=new(16),Margin=new(0,8,0,0),BackColor=Color.White});
        }
        else foreach(var peer in peers)
        {
            var card=new Panel{Width=computers.ClientSize.Width-55,Height=110,Margin=new(0,0,0,12),Padding=new(16),BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle,Anchor=AnchorStyles.Left|AnchorStyles.Right};
            var title=new Label{Text=peer.Name,Location=new(16,12),AutoSize=true,Font=new(Font.FontFamily,14,FontStyle.Bold)};
            var detail=new Label{Text=$"В сети · экранов: {peer.Screens.Length}"+(peer.Version.Length>0?$" · версия {peer.Version}":""),Location=new(16,48),AutoSize=true,ForeColor=Color.FromArgb(89,106,126)};
            var open=UiStyle.Button("Открыть экран",()=>view(peer),true);open.Location=new(card.Width-open.PreferredSize.Width-18,32);open.Anchor=AnchorStyles.Top|AnchorStyles.Right;
            card.Controls.AddRange([title,detail,open]);computers.Controls.Add(card);
        }
        computers.ResumeLayout();
    }
    public void RenderPreview(string path){Opacity=0;ShowInTaskbar=false;Show();Application.DoEvents();RefreshPeers();using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new(0,0,Width,Height));bitmap.Save(path);Close();}
}
