using System.Reflection;
using System.Runtime.InteropServices;
namespace SdrCapture;

// Render real WinForms controls using synthetic peers; no desktop capture or input injection.
static class UiTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static IEnumerable<Control> All(Control control)=>control.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(All(c)));
    public static void Run()
    {
        Application.EnableVisualStyles();
        string folder=Path.Combine(AppContext.BaseDirectory,"ui-preview");Directory.CreateDirectory(folder);
        foreach(string role in new[]{"Off","Host","Client"})
        {
            using var form=new AppSettingsForm(new Settings{Kvm=new(){Role=role,Host="GAMING-PC"}});
            Require(form.FormBorderStyle==FormBorderStyle.FixedSingle&&!form.MaximizeBox,"Settings resizable");
            form.RenderPreviews(Path.Combine(folder,role));
        }
        var stream=new KvmPeerInfo(Guid.NewGuid().ToString("N"),"STREAM-PC",[new("LEFT",0,0,1920,1080,true),new("RIGHT",1920,0,1920,1080,false)],"0.6.1",1);
        var server=new KvmPeerInfo(Guid.NewGuid().ToString("N"),"SERVER",[new("DISPLAY",0,0,1920,1080,true)],"0.6.1",1,true);
        using(var hub=new KvmHub(()=>null,()=>true,_=>{},()=>{},_=>{},_=>{}))hub.RenderPreview(Path.Combine(folder,"kvm-connected.png"),[stream,server]);
        using(var hub=new KvmHub(()=>null,()=>false,_=>{},()=>{},_=>{},_=>{}))hub.RenderPreview(Path.Combine(folder,"kvm-empty.png"));
        var options=new KvmOptions();
        var layout=new List<MonitorPlacement>{new(){Peer=options.Id,Device="LOCAL",Width=2560,Height=1440},new(){Peer=stream.Id,Device="LEFT",X=-1920,Width=1920,Height=1080},new(){Peer=stream.Id,Device="RIGHT",X=2560,Width=1920,Height=1080}};
        using(var editor=new MonitorLayoutEditor(options,layout))
        {
            using var host=new Form{Opacity=0,ShowInTaskbar=false};host.Controls.Add(editor);host.Show();
            var field=typeof(MonitorLayoutEditor).GetField("monitors",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var actual=(List<MonitorPlacement>)field.GetValue(editor)!;
            actual[1].X=-1900;
            typeof(MonitorLayoutEditor).GetField("selected",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(editor,actual[1]);
            All(editor).OfType<Button>().Single(b=>b.Text=="Только KVM").PerformClick();
            Require(actual.Count==1&&editor.RemoteOnlyPeers.Contains(stream.Id),"Remove peer did not update displayed layout");
            Require(editor.Result.Single(m=>m.Device=="LEFT").X==-1900,"Excluded monitor position lost");host.Close();
        }
        using var service=new KvmService(new KvmOptions());
        using(var viewer=new KvmViewer(service,stream){Opacity=0,ShowInTaskbar=false})
        {
            viewer.Show();Application.DoEvents();
            var picture=All(viewer).OfType<KvmViewer.Viewport>().Single();
            Require(picture.CanSelect&&picture.Focus()&&picture.Focused,"Remote viewport cannot receive keyboard focus");
            var key=typeof(KvmViewer).GetMethod("Key",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var down=new List<Keys>();var released=new List<Keys>();
            viewer.KeyDown+=(_,e)=>{if(e.Handled)down.Add(e.KeyCode);};viewer.KeyUp+=(_,e)=>{if(e.Handled)released.Add(e.KeyCode);};
            Keys[] codes=[Keys.Tab,Keys.Left,Keys.ControlKey,Keys.A];
            foreach(var code in codes){PostMessage(picture.Handle,0x100,(IntPtr)(int)code,IntPtr.Zero);PostMessage(picture.Handle,0x101,(IntPtr)(int)code,IntPtr.Zero);}
            Application.DoEvents();
            Require(down.SequenceEqual(codes)&&released.SequenceEqual(codes),"Native viewer key down/up routing lost events");
            key.Invoke(viewer,[new KeyEventArgs(Keys.F11),false]);
            key.Invoke(viewer,[new KeyEventArgs(Keys.F11),false]);
            Require(viewer.FormBorderStyle==FormBorderStyle.None,"F11 repeat toggled fullscreen twice");
            key.Invoke(viewer,[new KeyEventArgs(Keys.F11),true]);key.Invoke(viewer,[new KeyEventArgs(Keys.F11),false]);key.Invoke(viewer,[new KeyEventArgs(Keys.F11),true]);
            Require(viewer.FormBorderStyle==FormBorderStyle.FixedSingle,"Viewer did not restore fixed border");
            using var image=new Bitmap(640,360);using(var graphics=Graphics.FromImage(image)){graphics.Clear(Color.FromArgb(24,35,54));graphics.DrawString("Remote display · test pattern",viewer.Font,Brushes.White,25,25);}
            using var bytes=new MemoryStream();image.Save(bytes,System.Drawing.Imaging.ImageFormat.Jpeg);
            var receive=typeof(KvmViewer).GetMethod("Receive",BindingFlags.Instance|BindingFlags.NonPublic)!;
            All(viewer).OfType<ComboBox>().Single(c=>c.Items.Contains("LEFT")).SelectedIndex=1;
            receive.Invoke(viewer,[stream.Id,new KvmMessage{Type="view-frame",Device="LEFT",X=1920,Y=1080,Data=bytes.ToArray()}]);Application.DoEvents();
            Require(picture.Image==null,"Late frame undid monitor selection");
            receive.Invoke(viewer,[stream.Id,new KvmMessage{Type="view-frame",Device="RIGHT",X=1920,Y=1080,Data=bytes.ToArray()}]);Application.DoEvents();
            Require(picture.Image?.Width==640&&picture.Image.Height==360,"Received JPEG not displayed");
            var current=(KvmScreen?)typeof(KvmViewer).GetField("screen",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(viewer);
            Require(current?.X==1920,"Remote image lost second monitor origin");
            using var rendered=new Bitmap(viewer.Width,viewer.Height);viewer.DrawToBitmap(rendered,new(0,0,viewer.Width,viewer.Height));rendered.Save(Path.Combine(folder,"kvm-viewer.png"));viewer.Close();
        }
        Program.Write("ui-tests.json",new{Pass=true,RolesRendered=3,VisiblePeerCards=true,FixedWindows=true,RemoteOnlyLayoutRoundtrip=true,ViewerKeyboardFocus=true,KeyReleasesPreserved=true,FullscreenRepeatGuard=true});
    }
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
}
