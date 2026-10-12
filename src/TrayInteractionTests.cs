using System.Reflection;
using System.Runtime.InteropServices;
using W=System.Windows;
using D=System.Windows.Threading;

namespace SdrCapture;

static class TrayInteractionTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Pump()
    {
        var frame=new D.DispatcherFrame();
        D.Dispatcher.CurrentDispatcher.BeginInvoke(()=>frame.Continue=false,D.DispatcherPriority.ApplicationIdle);
        D.Dispatcher.PushFrame(frame);
    }
    public static void Run()
    {
        if(EventWaitHandle.TryOpenExisting("Local\\SdrCapture.OpenKvm",out var running))
        {running.Dispose();throw new InvalidOperationException("Run tray UI tests on an isolated desktop: test menus can interrupt focus.");}
        Application.EnableVisualStyles();DesktopMessageLoop.EnableFormsInterop();
        var app=new W.Application{ShutdownMode=W.ShutdownMode.OnExplicitShutdown};Exception? failure=null;
        app.Dispatcher.BeginInvoke(()=>{try{Check();}catch(Exception e){failure=e;}finally{app.Shutdown();}});
        app.Run();if(failure!=null)throw failure;
    }
    static void Check()
    {
        using var dispatcher=new Control();_=dispatcher.Handle;
        // This must work before any settings window or WindowsFormsHost exists.
        var filter=new ProbeFilter(dispatcher.Handle);Application.AddMessageFilter(filter);
        try
        {
            Require(PostMessage(dispatcher.Handle,ProbeFilter.Marker,IntPtr.Zero,IntPtr.Zero),"Could not post interop probe");Pump();
            Require(filter.Seen==1,"WPF startup loop bypassed WinForms message filters");
        }
        finally{Application.RemoveMessageFilter(filter);}
        using var menu=new ContextMenuStrip();TrayMenuStyle.Apply(menu);
        // Reproduce the original empty-menu cancellation, on the actual framework.
        using(var original=new ContextMenuStrip())
        {
            original.Opening+=(_,_)=>original.Items.Add("Settings");
            original.Show(new Point(20,20));
            Require(!original.Visible&&original.Items.Count==1,"Expected original first-opening cancellation was not reproduced");
        }
        using var icon=new NotifyIcon{Icon=SystemIcons.Application,ContextMenuStrip=menu};
        int settings=0,menuSettings=0,refreshes=0;
        bool queued=false,actionRanWithMenuOpen=false;
        void OpenSettings()
        {
            if(queued)return;queued=true;
            dispatcher.BeginInvoke(()=>{try{settings++;actionRanWithMenuOpen|=menu.Visible;}finally{queued=false;}});
        }
        TrayInteraction.Configure(icon,OpenSettings,()=>
        {
            refreshes++;menu.Items.Clear();
            menu.Items.Add("Settings",null,(_,_)=>{menuSettings++;OpenSettings();});
            menu.Items.Add("KVM",null,(_,_)=>{});
        });
        Require(menu.Items.Count==2&&refreshes==1,"Tray was published without a populated menu");
        icon.Visible=true;
        var native=(NativeWindow?)typeof(NotifyIcon).GetField("_window",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(icon);
        Require(native!=null&&native.Handle!=IntPtr.Zero,"NotifyIcon callback window missing");
        void Mouse(uint message)
        {
            Require(PostMessage(native!.Handle,0x400+1024,IntPtr.Zero,(IntPtr)message),"Could not post tray mouse callback");
            Pump();
        }
        Mouse(0x201);Mouse(0x202);
        Require(settings==1&&!menu.Visible,"First single left click did not dispatch Settings");
        Mouse(0x204);Mouse(0x205);
        Require(menu.Visible&&settings==1,"First right click failed or opened Settings");
        Require(PostMessage(menu.Handle,0x100,(IntPtr)Keys.Escape,IntPtr.Zero),"Could not post menu Escape");Pump();
        Require(!menu.Visible,"Escape could not dismiss tray menu under WPF");
        for(int i=0;i<6;i++)
        {
            Mouse(0x204);Mouse(0x205);Require(menu.Visible,"Repeated right click did not show menu");
            menu.Items[0].PerformClick();Pump();
            Require(!menu.Visible&&menuSettings==i+1&&settings==i+2,"Menu Settings action was lost or left popup open");
        }
        // Even a refresh that starts with zero displayed items must open on this attempt.
        menu.Items.Clear();Mouse(0x204);Mouse(0x205);Require(menu.Visible,"A pre-cancelled Opening was not reset after refresh");
        Mouse(0x201);Mouse(0x202);Require(!menu.Visible&&settings==8,"Left click did not close the menu and open Settings");
        Mouse(0x203);Mouse(0x202);Require(settings==8,"Double-click notification opened an extra Settings window");
        Require(!actionRanWithMenuOpen,"Settings activated before tray menu closed");
        icon.Visible=false;
        Program.Write("tray-ui-tests.json",new{Pass=true,OriginalFirstRightClickFailureReproduced=true,SingleLeftClick=true,FirstRightClick=true,RepeatedMenuActions=6,EmptyMenuRefreshRecovery=true,MenuClosesBeforeSettings=true,DoubleClickDoesNotDuplicate=true,WpfStartupMessageFiltering=true,KeyboardDismissal=true,NoGlobalInputInjected=true});
    }
    sealed class ProbeFilter(IntPtr handle):IMessageFilter
    {
        public const uint Marker=0x8000+73;
        public int Seen;
        public bool PreFilterMessage(ref Message message)
        {
            if(message.HWnd!=handle||message.Msg!=Marker)return false;
            Seen++;return true;
        }
    }
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
}
