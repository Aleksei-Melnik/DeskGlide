using System.Reflection;
using System.Runtime.InteropServices;
namespace SdrCapture;

static class UiTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static IEnumerable<Control> All(Control control)=>control.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(All(c)));
    public static void Run()
    {
        Application.EnableVisualStyles();
        Ui.WpfUiTests.Run();
        string folder=Path.Combine(AppContext.BaseDirectory,"wpf-preview");
        var stream=new KvmPeerInfo(Guid.NewGuid().ToString("N"),"STREAM-PC",[new("LEFT",0,0,1920,1080,true),new("RIGHT",1920,0,1920,1080,false)],"0.8.0",1);
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
            var renderWait=System.Diagnostics.Stopwatch.StartNew();while(picture.Image==null&&renderWait.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(5);}
            Require(picture.Image?.Width==640&&picture.Image.Height==360,"Received JPEG not displayed");
            var current=(KvmScreen?)typeof(KvmViewer).GetField("screen",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(viewer);
            Require(current?.X==1920,"Remote image lost second monitor origin");
            using var rendered=new Bitmap(viewer.Width,viewer.Height);viewer.DrawToBitmap(rendered,new(0,0,viewer.Width,viewer.Height));rendered.Save(Path.Combine(folder,"kvm-viewer.png"));viewer.Close();
        }
        Program.Write("ui-tests.json",new{Pass=true,WpfInterface=true,NativeViewportKeyboardFocus=true,KeyReleasesPreserved=true,FullscreenRepeatGuard=true});
    }
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
}
