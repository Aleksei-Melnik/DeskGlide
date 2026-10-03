using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SdrCapture;
static class KvmViewerCapture
{
    static readonly SemaphoreSlim gate=new(1,1);
    public static async Task Reply(KvmWire wire,string device,CancellationToken token)
    {
        if(!await gate.WaitAsync(0,token))return;
        try
        {
            var screen=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==device)??Screen.PrimaryScreen;
            if(screen==null||screen.Bounds.Width<1||screen.Bounds.Height<1)throw new IOException("Windows не предоставила рабочий экран. Для этого сервера нужен активный виртуальный дисплей или HDMI-заглушка.");
            if((long)screen.Bounds.Width*screen.Bounds.Height>40000000)throw new IOException("Разрешение экрана слишком велико для просмотра.");
            using var original=new Bitmap(screen.Bounds.Width,screen.Bounds.Height,PixelFormat.Format32bppRgb);
            using(var graphics=Graphics.FromImage(original))graphics.CopyFromScreen(screen.Bounds.Location,Point.Empty,screen.Bounds.Size);
            double scale=Math.Min(1,Math.Min(1280.0/original.Width,720.0/original.Height));
            using var preview=new Bitmap(original,new Size(Math.Max(1,(int)(original.Width*scale)),Math.Max(1,(int)(original.Height*scale))));
            using var bytes=new MemoryStream();using var parameters=new EncoderParameters(1);
            parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,75L);
            preview.Save(bytes,ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid),parameters);
            await wire.SendAsync(new(){Type="view-frame",Device=screen.DeviceName,X=screen.Bounds.Width,Y=screen.Bounds.Height,Data=bytes.ToArray()},token);
        }
        catch(Exception e)when(e is not OperationCanceledException){await wire.SendAsync(new(){Type="view-error",Text="Не удалось получить рабочий стол: "+e.Message},token);}
        finally{gate.Release();}
    }
}
sealed class KvmViewer:Form
{
    internal sealed class Viewport:PictureBox
    {
        public Viewport(){SetStyle(ControlStyles.Selectable,true);TabStop=true;}
        protected override bool IsInputKey(Keys keyData)=>true;
        protected override bool IsInputChar(char charCode)=>true;
    }
    readonly KvmService service;
    readonly KvmPeerInfo peer;
    readonly Viewport picture=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.Black};
    readonly Label status=new(){Dock=DockStyle.Bottom,Height=30,TextAlign=ContentAlignment.MiddleLeft};
    readonly ComboBox monitors=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top};
    readonly System.Windows.Forms.Timer timer=new(){Interval=100};
    readonly CheckBox control=new(){Text="Управлять мышью и клавиатурой",Checked=true,AutoSize=true,Margin=new(12,10,8,0)};
    FormBorderStyle previousBorder;Rectangle previousBounds;bool fullscreen;
    bool waiting,closing,f11Down;long requested;
    KvmScreen? screen;
    public KvmViewer(KvmService service,KvmPeerInfo peer)
    {
        this.service=service;this.peer=peer;
        Text="ScreenCapture · "+peer.Name;Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);ClientSize=new(1100,740);MinimumSize=new(860,450);KeyPreview=true;Font=new("Segoe UI",10);StartPosition=FormStartPosition.CenterScreen;
        UiStyle.FixedWindow(this);
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=50,Padding=new(8,5,8,5),WrapContents=false,BackColor=Color.FromArgb(242,246,251)};
        monitors.Dock=DockStyle.None;monitors.Width=160;monitors.Margin=new(0,7,0,0);
        toolbar.Controls.AddRange([monitors,control,UiStyle.Button("Полный экран · F11",ToggleFullscreen),UiStyle.Button("Отключиться",Close)]);
        Controls.Add(picture);Controls.Add(status);Controls.Add(toolbar);control.CheckedChanged+=(_,_)=>Release();
        foreach(var item in peer.Screens)monitors.Items.Add(item.Device);
        if(monitors.Items.Count>0)monitors.SelectedIndex=0;
        monitors.SelectedIndexChanged+=(_,_)=>{screen=CurrentScreens.FirstOrDefault(s=>s.Device==(string?)monitors.SelectedItem);Release();var old=picture.Image;picture.Image=null;old?.Dispose();};
        screen=peer.Screens.FirstOrDefault();
        service.Received+=Receive;
        timer.Tick+=async(_,_)=>{if(waiting&&Environment.TickCount64-requested<3000)return;RefreshScreens();waiting=true;requested=Environment.TickCount64;try{await service.SendBulk(peer.Id,new(){Type="view-request",Device=screen?.Device??""});}catch(Exception e){waiting=false;status.Text=e.Message;}};
        Shown+=(_,_)=>{picture.Focus();timer.Start();};
        picture.MouseMove+=(_,e)=>Mouse(e,0);
        picture.MouseDown+=(_,e)=>{picture.Focus();Mouse(e,e.Button switch{MouseButtons.Left=>2,MouseButtons.Right=>8,MouseButtons.Middle=>32,MouseButtons.XButton1 or MouseButtons.XButton2=>128,_=>0});};
        picture.MouseUp+=(_,e)=>Mouse(e,e.Button switch{MouseButtons.Left=>4,MouseButtons.Right=>16,MouseButtons.Middle=>64,MouseButtons.XButton1 or MouseButtons.XButton2=>256,_=>0});
        picture.MouseWheel+=(_,e)=>Mouse(e,2048);
        KeyDown+=(_,e)=>Key(e,false);KeyUp+=(_,e)=>Key(e,true);
        // Forward physical down/up independently; text must not activate local controls.
        KeyPress+=(_,e)=>{if(picture.Focused)e.Handled=true;};
        picture.LostFocus+=(_,_)=>Release();
        Deactivate+=(_,_)=>{f11Down=false;Release();};
        FormClosed+=(_,_)=>{closing=true;timer.Stop();timer.Dispose();service.Received-=Receive;Release();picture.Image?.Dispose();};
        status.Text="Просмотр рабочего стола · для выхода из управления переключитесь на другое окно.";
    }
    KvmScreen[] CurrentScreens=>service.Peers.FirstOrDefault(p=>p.Id==peer.Id)?.Screens??peer.Screens;
    void RefreshScreens()
    {
        var screens=CurrentScreens;
        if(monitors.Items.Cast<string>().SequenceEqual(screens.Select(s=>s.Device)))return;
        string? selected=screen?.Device;Release();monitors.Items.Clear();
        foreach(var item in screens)monitors.Items.Add(item.Device);
        if(monitors.Items.Count>0)monitors.SelectedIndex=Math.Max(0,Array.FindIndex(screens,s=>s.Device==selected));
        else screen=null;
    }
    void Receive(string id,KvmMessage message)
    {
        if(id!=peer.Id||closing||message.Type is not ("view-frame" or "view-error"))return;
        try{BeginInvoke(()=>
        {
            if(closing)return;waiting=false;
            if(message.Type=="view-error"){status.Text=message.Text;return;}
            if(message.Data==null)return;
            // A previous monitor's in-flight frame must not undo a user's selection.
            if(monitors.SelectedItem is string selected&&message.Device!=selected)return;
            try
            {
                using var stream=new MemoryStream(message.Data);using var decoded=Image.FromStream(stream);
                var old=picture.Image;picture.Image=new Bitmap(decoded);old?.Dispose();
                screen=CurrentScreens.FirstOrDefault(s=>s.Device==message.Device)??new(message.Device,0,0,message.X,message.Y,true);
                status.Text=$"{peer.Name} · {screen.Width} × {screen.Height} · Ctrl+Alt+Esc — вернуть управление";
            }
            catch(Exception e){status.Text=e.Message;}
        });}catch(InvalidOperationException){}
    }
    void Mouse(MouseEventArgs e,int flags)
    {
        if(!control.Checked||screen==null||picture.Image==null)return;
        double scale=Math.Min(picture.Width/(double)picture.Image.Width,picture.Height/(double)picture.Image.Height);
        double x=(e.X-(picture.Width-picture.Image.Width*scale)/2)/scale/picture.Image.Width;
        double y=(e.Y-(picture.Height-picture.Image.Height*scale)/2)/scale/picture.Image.Height;
        // Do not click a remote edge when the user clicks a black letterbox bar.
        // Button releases still go through, including after dragging outside the image.
        if((x<0||y<0||x>=1||y>=1)&&(flags&(4|16|64|256))==0)return;
        service.Send(peer.Id,new(){Type="mouse",X=screen.X+(int)(Math.Clamp(x,0,.999999)*screen.Width),Y=screen.Y+(int)(Math.Clamp(y,0,.999999)*screen.Height),Flags=flags,Code=flags==2048?e.Delta:e.Button==MouseButtons.XButton2?2:e.Button==MouseButtons.XButton1?1:0});
    }
    void Key(KeyEventArgs e,bool up)
    {
        if(e.KeyCode==Keys.F11){if(!up&&!f11Down)ToggleFullscreen();f11Down=!up;e.Handled=true;return;}
        if(e.Control&&e.Alt&&e.KeyCode==Keys.Escape){ReturnControl();e.Handled=true;return;}
        if(!control.Checked||!picture.Focused)return;
        uint scan=MapVirtualKey((uint)e.KeyValue,4);int extended=(scan&0xFF00)!=0?1:0;
        service.Send(peer.Id,new(){Type="key",Code=e.KeyValue,X=(int)(scan&255),Flags=(up?2:0)|extended});e.Handled=true;
    }
    void Release()=>service.Send(peer.Id,new(){Type="release"});
    void ToggleFullscreen()
    {
        Release();
        if(!fullscreen){previousBounds=Bounds;previousBorder=FormBorderStyle;WindowState=FormWindowState.Normal;FormBorderStyle=FormBorderStyle.None;Bounds=Screen.FromControl(this).Bounds;}
        else{FormBorderStyle=previousBorder;Bounds=previousBounds;}
        fullscreen=!fullscreen;
    }
    public void ReturnControl(){Release();WindowState=FormWindowState.Minimized;}
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code,uint type);
}
