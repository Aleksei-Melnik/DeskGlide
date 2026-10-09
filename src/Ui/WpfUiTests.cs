using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace SdrCapture.Ui;

static class WpfUiTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Pump(){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(()=>frame.Continue=false,System.Windows.Threading.DispatcherPriority.ApplicationIdle);System.Windows.Threading.Dispatcher.PushFrame(frame);}
    internal static void Render(W.Window window,string path)
    {
        window.UpdateLayout();Pump();var frame=(W.FrameworkElement?)window.Template.FindName("WindowFrame",window)??(W.FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth),(int)Math.Ceiling(frame.ActualHeight),96,96,M.PixelFormats.Pbgra32);bitmap.Render(frame);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    public static void Run()
    {
        if(EventWaitHandle.TryOpenExisting("Local\\SdrCapture.OpenKvm",out var running))
        {running.Dispose();throw new InvalidOperationException("Close DeskGlide before running UI tests: they activate test windows and can interrupt keyboard focus.");}
        WindowsFormsSynchronizationContext.AutoInstall=false;
        var app=new W.Application{ShutdownMode=W.ShutdownMode.OnExplicitShutdown};Exception? failure=null;
        app.Dispatcher.BeginInvoke(()=>{try{RunTests();}catch(Exception e){failure=e;}finally{foreach(W.Window window in app.Windows.Cast<W.Window>().ToArray())window.Close();app.Shutdown();}});
        app.Run();if(failure!=null)throw failure;
    }
    static IEnumerable<W.DependencyObject> Children(W.DependencyObject root)
    {for(int i=0;i<M.VisualTreeHelper.GetChildrenCount(root);i++){var child=M.VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Children(child))yield return nested;}}
    static void Invoke(C.Button button)
    {
        var root=W.Window.GetWindow(button);var centre=button.TranslatePoint(new(button.ActualWidth/2,button.ActualHeight/2),(W.UIElement)root.Content);
        var hit=((W.UIElement)root.Content).InputHitTest(centre) as W.DependencyObject;
        bool reached=false;while(hit!=null){if(hit==button){reached=true;break;}hit=M.VisualTreeHelper.GetParent(hit);}
        Require(reached,"Button covered by another hit-test surface: "+button.Tag);
        var peer=new System.Windows.Automation.Peers.ButtonAutomationPeer(button);((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();Pump();
    }
    static C.Button ActionButton(W.Window window,string tag)=>Children((W.DependencyObject)window.Content).OfType<C.Button>().Single(b=>(string?)b.Tag==tag);
    static int NativeHit(W.Window window,W.Point point)
    {
        var physical=window.PointToScreen(point);long position=(((long)Math.Round(physical.Y)&0xffff)<<16)|((long)Math.Round(physical.X)&0xffff);
        return (int)SendMessage(new System.Windows.Interop.WindowInteropHelper(window).Handle,0x84,IntPtr.Zero,(IntPtr)position);
    }
    static void ChromeWorks(ShellWindow window)
    {
        Require(window.Template.FindName("WindowFrame",window) is C.Border {BorderThickness:var border}&&border==new W.Thickness(0),"Window perimeter border is visible");
        Require(NativeHit(window,new(window.ActualWidth/2,25))==2,"Header cannot drag the window");
        foreach(string tag in new[]{"Minimize window","Close window"})
        {
            var button=ActionButton(window,tag);var point=button.TranslatePoint(new(button.ActualWidth/2,button.ActualHeight/2),window);
            Require(NativeHit(window,point)==1,"Caption intercepts custom button input: "+tag);
        }
        var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;GetWindowRect(handle,out var bounds);var origin=new NativePoint();ClientToScreen(handle,ref origin);
        Require(Math.Abs(origin.Y-bounds.Top)<=2,"Native title bar still reserves space above app content");
    }
    static void RunTests()
    {
        UiStrings.Shared.Language="en";
        string folder=Path.Combine(AppContext.BaseDirectory,"wpf-preview");Directory.CreateDirectory(folder);
        var pending=new TaskCompletionSource<DiscordDevices.Endpoint[]>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
        var settings=new Settings{Device=Screen.PrimaryScreen?.DeviceName??"DISPLAY1",NdiAudioDevice="saved",Replay=new(){GameAudio="game",Microphone="mic",ExtraAudio="extra"},Discord=new(){AudioDevice="cable",CaptureDevice="input"}};
        var watch=Stopwatch.StartNew();
        var form=new SettingsWindow(settings,discoverAudio:()=>{calls++;return pending.Task;}){Opacity=0,ShowInTaskbar=false};
        Require(calls==0,"WPF constructor called audio discovery");form.Reveal();Pump();long opening=watch.ElapsedMilliseconds;
        bool responding=false;form.Dispatcher.BeginInvoke(()=>responding=true);Pump();Require(form.IsVisible&&responding&&calls==1&&!pending.Task.IsCompleted,"WPF settings blocked by device discovery");
        ChromeWorks(form);Invoke(ActionButton(form,"Minimize window"));Require(form.WindowState==W.WindowState.Minimized,"Custom minimize failed");form.Reveal();Pump();Require(form.WindowState==W.WindowState.Normal,"Custom minimize could not restore");
        var before=form.Collect();Require(before.NdiAudioDevice=="saved"&&before.Replay.GameAudio=="game"&&before.Discord.CaptureDevice=="input","WPF loading lost device selection");
        var audioChoice=form.AudioChoice("ndiAudio");Require(audioChoice.ActualWidth>150&&audioChoice.IsVisible,"Audio selector is hidden or covered by refresh button");
        form.AudioChoice("game").SelectedIndex=0;form.Reveal();Require(calls==1,"WPF repeated activation restarted discovery");
        pending.SetResult([new("mic",NAudio.CoreAudioApi.DataFlow.Capture,"Microphone","Microphone")]);watch.Restart();while(form.AudioStatus!="Audio devices are up to date."&&watch.ElapsedMilliseconds<3000){Pump();Thread.Sleep(5);}
        Require(form.AudioStatus=="Audio devices are up to date.","WPF devices did not populate");var after=form.Collect();Require(after.Replay.GameAudio==""&&after.Replay.Microphone=="mic"&&after.NdiAudioDevice=="saved"&&after.Discord.CaptureDevice=="input","WPF discovery replaced edits or missing devices");
        Invoke(Children((W.DependencyObject)form.Content).OfType<C.Button>().Single(b=>(string?)b.Tag=="General"));Require(form.SelectedPage==9,"Navigation click failed");
        form.AudioChoice("language").SelectedItem=form.AudioChoice("language").Items.Cast<SettingsWindow.Choice>().Single(c=>c.Id=="ru");Pump();
        Require(UiStrings.Shared.Language=="ru"&&Children((W.DependencyObject)form.Content).OfType<C.TextBlock>().Any(t=>t.Text=="Сохранить"),"Live language switch failed");
        Require(form.Collect().NdiAudioDevice=="saved"&&form.Collect().Language=="ru","Language switch lost edits or preference");
        form.AudioChoice("language").SelectedItem=form.AudioChoice("language").Items.Cast<SettingsWindow.Choice>().Single(c=>c.Id=="en");Pump();
        foreach(int page in Enumerable.Range(0,32).Select(i=>new[]{0,1,2,3,4,7,9}[i%7])){form.SelectPage(page);Pump();}
        watch.Restart();while(watch.ElapsedMilliseconds<300){Pump();Thread.Sleep(5);}Pump();
        var pageSurface=Children((W.DependencyObject)form.Content).OfType<C.ContentControl>().Single(c=>c.GetType()==typeof(C.ContentControl));
        Require(pageSurface.Opacity>.999&&pageSurface.RenderTransform.Value.IsIdentity,"Rapid navigation left an incomplete transition");
        form.SelectPage(1,false);Pump();
        var pageScroll=Children((W.DependencyObject)form.Content).OfType<C.ScrollViewer>().Single(s=>ReferenceEquals(s.Content,pageSurface));
        pageScroll.ScrollToEnd();Pump();double offset=pageScroll.VerticalOffset;
        Require(offset>0,"Replay page must scroll to check repeated selection");
        var selectedContent=pageSurface.Content;var selectedTransform=pageSurface.RenderTransform;
        Invoke(ActionButton(form,"Instant replay"));
        Require(form.SelectedPage==1&&ReferenceEquals(pageSurface.Content,selectedContent)&&ReferenceEquals(pageSurface.RenderTransform,selectedTransform)&&Math.Abs(pageScroll.VerticalOffset-offset)<.1,"Clicking the selected tab restarted its transition or reset scrolling");
        form.SelectPage(2);form.SelectPage(1);var activeTransition=pageSurface.RenderTransform;
        Invoke(ActionButton(form,"Instant replay"));
        Require(ReferenceEquals(pageSurface.RenderTransform,activeTransition),"Clicking the selected tab replaced its active transition");
        Require(form.Collect().NdiAudioDevice=="saved"&&form.Collect().Replay.Microphone=="mic","Navigation lost edits");
        form.WindowState=W.WindowState.Minimized;form.Reveal();Require(form.WindowState==W.WindowState.Normal,"WPF window did not restore");
        Invoke(Children((W.DependencyObject)form.Content).OfType<C.Button>().Single(b=>(string?)b.Tag=="Save changes"));Require(form.Accepted&&form.IsClosed,"Save button failed");
        var delayed=new TaskCompletionSource<DiscordDevices.Endpoint[]>(TaskCreationOptions.RunContinuationsAsynchronously);var closing=new SettingsWindow(settings,discoverAudio:()=>delayed.Task){Opacity=0,ShowInTaskbar=false};closing.Reveal();Pump();Invoke(ActionButton(closing,"Close window"));Require(closing.IsClosed&&!closing.Accepted,"Custom close saved or left settings open");delayed.SetResult([]);Pump();
        foreach(var role in new[]{"Off","Host","Client"})
        {
            var preview=new SettingsWindow(new(){Device=Screen.PrimaryScreen?.DeviceName??"DISPLAY1",Kvm=new(){Role=role,Host="GAMING-PC"}},discoverAudio:()=>Task.FromResult<DiscordDevices.Endpoint[]>([new("game",NAudio.CoreAudioApi.DataFlow.Render,"Game speakers","Speakers"),new("mic",NAudio.CoreAudioApi.DataFlow.Capture,"Microphone","Microphone")]),overview:()=>[new("Screen streaming",("State","On"),("Output","2560 × 1440"),("Delivery rate","60.0 FPS")),new("Instant replay",("State","On"),("Buffered","5 / 5 min")),new("KVM & pairing",("Connected computers","STREAM-PC, SERVER"))]){Opacity=0,ShowInTaskbar=false};preview.Reveal();Pump();
            Require(preview.ResizeMode==W.ResizeMode.CanMinimize,"WPF settings resizable");preview.SelectPage(8,false);Require(role!="Host"||preview.SelectedPage==0,"Receiver page shown on host");
            for(int i=0;i<10;i++){preview.SelectPage(i,false);Render(preview,Path.Combine(folder,$"{role}-{i}.png"));}
            UiStrings.Shared.Language="ru";Pump();preview.SelectPage(0,false);Render(preview,Path.Combine(folder,$"{role}-ru.png"));preview.Close();UiStrings.Shared.Language="en";
        }
        var options=new KvmOptions();var peer=new KvmPeerInfo(Guid.NewGuid().ToString("N"),"STREAM-PC",[new("LEFT",0,0,1920,1080,true),new("RIGHT",1920,0,1080,1920,false)],"0.8.0",1);var server=new KvmPeerInfo(Guid.NewGuid().ToString("N"),"SERVER",[new("DISPLAY",0,0,1920,1080,true)],"0.8.0",1,true);
        var layout=new List<MonitorPlacement>{new(){Peer=options.Id,Name="GAMING-PC",Device="LOCAL",X=0,Y=0,Width=2560,Height=1440,Hotkey=1},new(){Peer=peer.Id,Name="STREAM-PC",Device="LEFT",X=-1920,Y=180,Width=1920,Height=1080,Hotkey=2},new(){Peer=peer.Id,Name="STREAM-PC",Device="RIGHT",X=2560,Y=-240,Width=1080,Height=1920,Hotkey=3}};
        var canvas=new MonitorCanvas(options,layout);var layoutWindow=new ShellWindow("Monitor test",820,680){Content=Kit.Card(canvas),Opacity=0,ShowInTaskbar=false};layoutWindow.Reveal();Pump();Render(layoutWindow,Path.Combine(folder,"monitors.png"));canvas.SelectMonitor("LEFT");Pump();Invoke(Children(canvas).OfType<C.Button>().First(b=>(string?)b.Tag=="Remove from layout"));Require(canvas.ExcludedMonitors.Contains(peer.Id+"|LEFT")&&!canvas.ExcludedMonitors.Contains(peer.Id+"|RIGHT")&&canvas.Result.Count==3,"Removing one monitor removed its sibling or lost position");canvas.AddMonitor(peer.Id+"|LEFT",-1920,0);Require(!canvas.ExcludedMonitors.Contains(peer.Id+"|LEFT")&&canvas.Result.Single(m=>m.Device=="LEFT").X==-1920,"Monitor could not return from available list");layoutWindow.Close();
        var hub=new KvmWindow(()=>null,()=>true,_=>{},()=>{},_=>{},_=>{}){Opacity=0,ShowInTaskbar=false};hub.Reveal();Pump();ChromeWorks(hub);hub.RenderPeers("Host",[peer,server]);Render(hub,Path.Combine(folder,"kvm-connected.png"));Invoke(ActionButton(hub,"Close window"));Require(hub.IsClosed,"KVM custom close failed");
        var empty=new KvmWindow(()=>null,()=>false,_=>{},()=>{},_=>{},_=>{}){Opacity=0,ShowInTaskbar=false};empty.Reveal();Pump();Render(empty,Path.Combine(folder,"kvm-empty.png"));empty.Close();
        using var service=new KvmService(new KvmOptions());var viewer=new ViewerWindow(service,peer){Opacity=0,ShowInTaskbar=false};viewer.Reveal();Pump();Require(viewer.IsVisible,"WPF viewer did not open");
        var surface=viewer.NativeSurface;var picture=surface.Controls.OfType<KvmViewer.Viewport>().Single();Require(picture.Focus()&&picture.Focused,"Embedded viewport cannot receive keyboard focus");
        var down=new List<Keys>();var up=new List<Keys>();surface.KeyDown+=(_,e)=>{if(e.Handled)down.Add(e.KeyCode);};surface.KeyUp+=(_,e)=>{if(e.Handled)up.Add(e.KeyCode);};
        foreach(var key in new[]{Keys.Tab,Keys.Left,Keys.ControlKey,Keys.A}){PostMessage(picture.Handle,0x100,(IntPtr)(int)key,IntPtr.Zero);PostMessage(picture.Handle,0x101,(IntPtr)(int)key,IntPtr.Zero);}Pump();
        Require(down.SequenceEqual(new[]{Keys.Tab,Keys.Left,Keys.ControlKey,Keys.A})&&up.SequenceEqual(down),"WPF hosting swallowed a remote key or key release");
        ChromeWorks(viewer);PostMessage(picture.Handle,0x100,(IntPtr)(int)Keys.F11,IntPtr.Zero);PostMessage(picture.Handle,0x100,(IntPtr)(int)Keys.F11,IntPtr.Zero);Pump();Require(viewer.WindowStyle==W.WindowStyle.None&&System.Windows.Shell.WindowChrome.GetWindowChrome(viewer)==null&&!ActionButton(viewer,"Close window").IsVisible,"WPF fullscreen repeat guard failed");
        PostMessage(picture.Handle,0x101,(IntPtr)(int)Keys.F11,IntPtr.Zero);PostMessage(picture.Handle,0x100,(IntPtr)(int)Keys.F11,IntPtr.Zero);PostMessage(picture.Handle,0x101,(IntPtr)(int)Keys.F11,IntPtr.Zero);Pump();Require(viewer.WindowStyle==W.WindowStyle.SingleBorderWindow&&System.Windows.Shell.WindowChrome.GetWindowChrome(viewer)!=null,"WPF fullscreen did not restore");ChromeWorks(viewer);viewer.Close();
        Program.Write("wpf-ui-tests.json",new{Pass=true,SettingsOpenMs=opening,PagesRendered=33,ProductionWpfMessageLoop=true,NavigationAndSaveInvoked=true,LiveLanguageSwitch=true,HostReceiverHidden=true,IndividualMonitorRemoveAndRestore=true,SettingsResponsiveDuringDiscovery=true,SettingsSelectionsPreserved=true,ClosedDuringDiscovery=true,WpfKvmViewerOpened=true,HostedKeyboardAndKeyRelease=true,WpfFullscreenRepeatGuard=true,NativeCaptionHitTesting=true,NativeTitleBarRemoved=true,CustomMinimizeClose=true,RapidAnimatedNavigationPreservedEdits=true});
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct NativePoint{public int X,Y;}
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct NativeRect{public int Left,Top,Right,Bottom;}
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out NativeRect rectangle);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window,ref NativePoint point);
}
