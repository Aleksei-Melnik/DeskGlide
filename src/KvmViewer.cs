using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SdrCapture;
static class KvmViewerCapture
{
    static readonly SemaphoreSlim gate=new(1,1);
    public static async Task Reply(KvmWire wire,KvmMessage request,CancellationToken token)
    {
        if(!await gate.WaitAsync(0,token))return;
        try
        {
            var screen=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==request.Device)??Screen.PrimaryScreen;
            if(screen==null||screen.Bounds.Width<1||screen.Bounds.Height<1)throw new IOException("Windows не предоставила рабочий экран. Для этого сервера нужен активный виртуальный дисплей или HDMI-заглушка.");
            if((long)screen.Bounds.Width*screen.Bounds.Height>40000000)throw new IOException("Разрешение экрана слишком велико для просмотра.");
            using var original=new Bitmap(screen.Bounds.Width,screen.Bounds.Height,PixelFormat.Format32bppRgb);
            using(var graphics=Graphics.FromImage(original))graphics.CopyFromScreen(screen.Bounds.Location,Point.Empty,screen.Bounds.Size);
            var encoded=KvmImageCodec.Encode(original,request.Flags==2?request.Code:-1);
            await wire.SendAsync(new(){Type="view-frame",Id=request.Id,Device=screen.DeviceName,X=screen.Bounds.Width,Y=screen.Bounds.Height,Data=encoded.Data,Text=encoded.Description},token);
        }
        catch(Exception e)when(e is not OperationCanceledException){await wire.SendAsync(new(){Type="view-error",Text="Не удалось получить рабочий стол: "+e.Message},token);}
        finally{gate.Release();}
    }
}
static class KvmImageCodec
{
    // Leave room for JSON/base64 inside the existing 4 MiB wire limit.
    const int MaximumBytes=2800000;
    public static (byte[] Data,string Description) Encode(Bitmap original,int quality)
    {
        int maxWidth=quality<0?1280:quality==2?1920:Math.Max(original.Width,original.Height);
        int maxHeight=quality<0?720:quality==2?1080:maxWidth;
        double scale=Math.Min(1,Math.Min(maxWidth/(double)original.Width,maxHeight/(double)original.Height));
        Bitmap? resized=null;
        try
        {
            var image=original;
            if(scale<1){resized=Resize(original,scale);image=resized;}
            if(quality==0)
            {
                using var png=new MemoryStream();image.Save(png,ImageFormat.Png);
                if(png.Length<=MaximumBytes)return(png.ToArray(),$"{image.Width} × {image.Height} · PNG без потерь");
            }
            var codec=ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid);
            foreach(long level in quality<0?new long[]{75}:quality==2?new long[]{85,75}:new long[]{95,90,85,75})
            {
                using var bytes=new MemoryStream();using var parameters=new EncoderParameters(1);
                parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,level);image.Save(bytes,codec,parameters);
                if(bytes.Length<=MaximumBytes)return(bytes.ToArray(),$"{image.Width} × {image.Height} · JPEG {level}");
            }
            // Very noisy large desktops must not disconnect the control/clipboard channel.
            using var smaller=Resize(image,.5);return Encode(smaller,1);
        }
        finally{resized?.Dispose();}
    }
    static Bitmap Resize(Bitmap source,double scale)
    {
        var result=new Bitmap(Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)),PixelFormat.Format24bppRgb);
        using var graphics=Graphics.FromImage(result);graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source,new Rectangle(0,0,result.Width,result.Height));return result;
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
    readonly ComboBox quality=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=170,Margin=new(4,7,4,0)};
    readonly ComboBox frameRate=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=82,Margin=new(4,7,4,0)};
    readonly System.Windows.Forms.Timer timer=new(){Interval=100};
    readonly CheckBox control=new(){Text="Управление",Checked=true,AutoSize=true,Margin=new(8,10,8,0)};
    FormBorderStyle previousBorder;Rectangle previousBounds;bool fullscreen;
    bool waiting,closing,f11Down;long requested;
    volatile string generation="",selectedDevice="";
    bool streaming;long lastFrame,retryAt,statsAt=Environment.TickCount64;int displayed;double actualFps;
    readonly CancellationTokenSource decodeStop=new();readonly SemaphoreSlim decodeReady=new(0,1);
    readonly object frameGate=new();KvmMessage? pendingEncoded; (Bitmap Image,KvmMessage Message)? pendingDecoded;bool presenting;
    readonly Task decoder;
    KvmScreen? screen;
    public KvmViewer(KvmService service,KvmPeerInfo peer)
    {
        this.service=service;this.peer=peer;
        Text="ScreenCapture · "+peer.Name;Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);ClientSize=new(1100,740);MinimumSize=new(860,450);KeyPreview=true;Font=new("Segoe UI",10);StartPosition=FormStartPosition.CenterScreen;
        UiStyle.FixedWindow(this);
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=50,Padding=new(8,5,8,5),WrapContents=false,BackColor=Color.FromArgb(242,246,251)};
        monitors.Dock=DockStyle.None;monitors.Width=160;monitors.Margin=new(0,7,0,0);
        quality.Items.AddRange(["Чёткий текст · 95","Высокое · 88","Экономный · 1080p"]);quality.SelectedIndex=0;
        frameRate.Items.AddRange(["30 FPS","60 FPS"]);frameRate.SelectedIndex=1;
        toolbar.Controls.AddRange([monitors,quality,frameRate,control,UiStyle.Button("Полный экран · F11",ToggleFullscreen),UiStyle.Button("Отключиться",Close)]);
        Controls.Add(picture);Controls.Add(status);Controls.Add(toolbar);control.CheckedChanged+=(_,_)=>Release();
        foreach(var item in peer.Screens)monitors.Items.Add(item.Device);
        if(monitors.Items.Count>0)monitors.SelectedIndex=0;
        monitors.SelectedIndexChanged+=(_,_)=>{screen=CurrentScreens.FirstOrDefault(s=>s.Device==(string?)monitors.SelectedItem);Release();var old=picture.Image;picture.Image=null;old?.Dispose();RestartStream();};
        screen=peer.Screens.FirstOrDefault();
        selectedDevice=screen?.Device??"";decoder=Task.Run(DecodeLoop);
        quality.SelectedIndexChanged+=(_,_)=>RestartStream();frameRate.SelectedIndexChanged+=(_,_)=>RestartStream();
        service.Received+=Receive;
        timer.Tick+=async(_,_)=>
        {
            RefreshScreens();long now=Environment.TickCount64;
            if(now-statsAt>=1000){actualFps=displayed*1000d/(now-statsAt);displayed=0;statsAt=now;}
            if(peer.ViewProtocol>=1)
            {
                if(now<retryAt)return;
                if(streaming&&now-lastFrame>4000)RestartStream();
                if(!streaming){if(string.IsNullOrEmpty(generation))generation=Guid.NewGuid().ToString("N");streaming=service.SendVideo(peer.Id,new(){Type="view-start",Id=generation,Device=selectedDevice,Code=frameRate.SelectedIndex==0?30:60,Flags=quality.SelectedIndex});lastFrame=now;}
                return;
            }
            if(waiting&&now-requested<3000)return;waiting=true;requested=now;
            try{await service.SendBulk(peer.Id,new(){Type="view-request",Device=screen?.Device??"",Flags=2,Code=quality.SelectedIndex==0?1:quality.SelectedIndex});}catch(Exception e){waiting=false;status.Text=e.Message;}
        };
        Shown+=(_,_)=>{picture.Focus();RestartStream();timer.Start();};
        picture.MouseMove+=(_,e)=>Mouse(e,0);
        picture.MouseDown+=(_,e)=>{picture.Focus();Mouse(e,e.Button switch{MouseButtons.Left=>2,MouseButtons.Right=>8,MouseButtons.Middle=>32,MouseButtons.XButton1 or MouseButtons.XButton2=>128,_=>0});};
        picture.MouseUp+=(_,e)=>Mouse(e,e.Button switch{MouseButtons.Left=>4,MouseButtons.Right=>16,MouseButtons.Middle=>64,MouseButtons.XButton1 or MouseButtons.XButton2=>256,_=>0});
        picture.MouseWheel+=(_,e)=>Mouse(e,2048);
        KeyDown+=(_,e)=>Key(e,false);KeyUp+=(_,e)=>Key(e,true);
        // Forward physical down/up independently; text must not activate local controls.
        KeyPress+=(_,e)=>{if(picture.Focused)e.Handled=true;};
        picture.LostFocus+=(_,_)=>Release();
        Deactivate+=(_,_)=>{f11Down=false;Release();};
        FormClosed+=(_,_)=>{closing=true;service.SendVideo(peer.Id,new(){Type="view-stop",Id=generation});timer.Stop();timer.Dispose();service.Received-=Receive;decodeStop.Cancel();Release();picture.Image?.Dispose();lock(frameGate){pendingDecoded?.Image.Dispose();pendingDecoded=null;pendingEncoded=null;}_=decoder.ContinueWith(_=>{decodeStop.Dispose();decodeReady.Dispose();});};
        status.Text="Просмотр рабочего стола · для выхода из управления переключитесь на другое окно.";
    }
    void RestartStream()
    {
        if(streaming)service.SendVideo(peer.Id,new(){Type="view-stop",Id=generation});
        streaming=false;generation=Guid.NewGuid().ToString("N");selectedDevice=screen?.Device??"";
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
        if(peer.ViewProtocol>=1&&message.Id!=generation)return;
        if(message.Type=="view-error"){try{BeginInvoke(()=>{if(!closing){waiting=false;status.Text=message.Text;RestartStream();retryAt=Environment.TickCount64+1000;}});}catch(InvalidOperationException){}return;}
        if(message.Data==null||message.Device!=selectedDevice)return;
        lock(frameGate){if(closing)return;pendingEncoded=message;if(decodeReady.CurrentCount==0)decodeReady.Release();}
    }
    async Task DecodeLoop()
    {
        try
        {
            using var jpeg=new KvmJpeg();
            while(!decodeStop.IsCancellationRequested)
            {
                await decodeReady.WaitAsync(decodeStop.Token);KvmMessage? message;
                lock(frameGate){message=pendingEncoded;pendingEncoded=null;}if(message==null)continue;
                try
                {
                    Bitmap image;
                    if(peer.ViewProtocol>=1)image=jpeg.Decode(message.Data!);
                    else{using var stream=new MemoryStream(message.Data!);using var decoded=Image.FromStream(stream);if((long)decoded.Width*decoded.Height>40000000)throw new IOException("Кадр слишком большой.");image=new Bitmap(decoded);}
                    lock(frameGate)
                    {
                        if(closing){image.Dispose();return;}pendingDecoded?.Image.Dispose();pendingDecoded=(image,message);
                        if(!presenting){presenting=true;try{BeginInvoke(Present);}catch(InvalidOperationException){presenting=false;pendingDecoded?.Image.Dispose();pendingDecoded=null;}}
                    }
                }
                catch(Exception e)when(e is not OperationCanceledException){Log.Write("KVM decode: "+e.Message);}
            }
        }catch(OperationCanceledException){}
    }
    void Present()
    {
        (Bitmap Image,KvmMessage Message)? frame;lock(frameGate){frame=pendingDecoded;pendingDecoded=null;presenting=false;}
        if(frame is not {} current)return;
        if(closing||current.Message.Device!=selectedDevice||(peer.ViewProtocol>=1&&current.Message.Id!=generation)){current.Image.Dispose();return;}
        waiting=false;lastFrame=Environment.TickCount64;displayed++;
        var old=picture.Image;picture.Image=current.Image;old?.Dispose();
        screen=CurrentScreens.FirstOrDefault(s=>s.Device==current.Message.Device)??new(current.Message.Device,0,0,current.Message.X,current.Message.Y,true);
        string frequency=peer.ViewProtocol>=1?$"{actualFps:F1} FPS / {(frameRate.SelectedIndex==0?30:60)}":"Старый режим: обновите оба ПК до 0.7.2";
        status.Text=$"{peer.Name} · {current.Message.Text} · {frequency} · Ctrl+Alt+Esc — вернуть управление";
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
