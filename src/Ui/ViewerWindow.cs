using W=System.Windows;
using C=System.Windows.Controls;
using System.Windows.Threading;

namespace SdrCapture.Ui;

sealed class ViewerWindow:ShellWindow
{
    readonly KvmViewer surface;
    internal KvmViewer NativeSurface=>surface;
    readonly System.Windows.Forms.Integration.WindowsFormsHost host=new();
    readonly C.ComboBox monitors=new(){Width=150},quality=new(){Width=150},fps=new(){Width=85};
    readonly C.CheckBox control=new(){Content=Kit.Text("Control"),IsChecked=true};
    readonly C.TextBlock status=Kit.Text("Connecting…",12,"#B0A9BC");
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    bool changing,fullscreen;W.Rect previous;
    public ViewerWindow(KvmService service,KvmPeerInfo peer):base(peer.Name,1160,780)
    {
        surface=new(service,peer);surface.Embed();host.Child=surface;
        var root=new C.DockPanel();var toolbar=Kit.Actions(monitors,quality,fps,control,Kit.Button("Full screen · F11",ToggleFullscreen),Kit.Button("Disconnect",Close));toolbar.Margin=new(16,12,8,12);C.DockPanel.SetDock(toolbar,C.Dock.Top);root.Children.Add(toolbar);
        status.Margin=new(16,10,16,10);C.DockPanel.SetDock(status,C.Dock.Bottom);root.Children.Add(status);root.Children.Add(host);Content=root;
        monitors.ItemsSource=surface.Devices;monitors.SelectedItem=surface.SelectedDevice;quality.ItemsSource=new[]{"Crisp text · 95","High · 88","Efficient · 1080p"};quality.SelectedIndex=0;fps.ItemsSource=new[]{"30 FPS","60 FPS"};fps.SelectedIndex=1;
        void Apply(){if(!changing)surface.Configure((string?)monitors.SelectedItem,quality.SelectedIndex,fps.SelectedIndex==0?30:60,control.IsChecked==true);}
        monitors.SelectionChanged+=(_,_)=>Apply();quality.SelectionChanged+=(_,_)=>Apply();fps.SelectionChanged+=(_,_)=>Apply();control.Checked+=(_,_)=>Apply();control.Unchecked+=(_,_)=>Apply();
        surface.FullscreenRequested+=ToggleFullscreen;surface.ReturnRequested+=ReturnControl;
        timer.Tick+=(_,_)=>{status.Text=surface.StatusText;var devices=surface.Devices;if(!monitors.Items.Cast<string>().SequenceEqual(devices)){changing=true;monitors.ItemsSource=devices;monitors.SelectedItem=surface.SelectedDevice;changing=false;}};
        Loaded+=(_,_)=>{surface.Show();surface.Focus();timer.Start();};Deactivated+=(_,_)=>surface.ReleaseControl();Closed+=(_,_)=>{timer.Stop();surface.Close();host.Dispose();};
    }
    void ToggleFullscreen()
    {
        surface.ReleaseControl();
        if(!fullscreen){previous=new(Left,Top,Width,Height);WindowStyle=W.WindowStyle.None;WindowState=W.WindowState.Maximized;}
        else{WindowState=W.WindowState.Normal;WindowStyle=W.WindowStyle.SingleBorderWindow;Left=previous.Left;Top=previous.Top;Width=previous.Width;Height=previous.Height;}fullscreen=!fullscreen;
    }
    public void ReturnControl(){surface.ReleaseControl();WindowState=W.WindowState.Minimized;}
}
