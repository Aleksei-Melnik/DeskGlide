namespace SdrCapture;

static class DesktopMessageLoop
{
    public static void EnableFormsInterop()
    {
        WindowsFormsSynchronizationContext.AutoInstall=false;
        // NotifyIcon and ToolStrip need WinForms message filtering even before
        // the first KVM viewer creates a WindowsFormsHost inside our WPF app.
        System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
    }
}

static class TrayInteraction
{
    public static void Configure(NotifyIcon icon,Action openSettings,Action refreshMenu)
    {
        var menu=icon.ContextMenuStrip??throw new ArgumentException("Tray menu is required.",nameof(icon));
        void Refresh()
        {
            menu.SuspendLayout();
            try{refreshMenu();}
            finally{menu.ResumeLayout(true);}
        }
        // WinForms pre-cancels Opening for an empty menu. Populate it before
        // publishing the icon, then explicitly evaluate cancellation after refresh.
        Refresh();
        menu.Opening+=(_,e)=>{Refresh();e.Cancel=menu.Items.Count==0;};
        icon.MouseClick+=(_,e)=>
        {
            if(e.Button!=MouseButtons.Left)return;
            menu.Close();
            openSettings();
        };
    }
}
