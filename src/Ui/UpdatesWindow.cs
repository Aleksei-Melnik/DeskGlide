using System.Diagnostics;
using W=System.Windows;
using C=System.Windows.Controls;

namespace SdrCapture.Ui;

sealed class UpdatesWindow:ShellWindow
{
    readonly UpdateCoordinator updater;
    readonly Func<KvmService?> network;
    readonly C.TextBlock status=Kit.Text("",15),details=Kit.Text("",13,"#B0A9BC");
    readonly C.Button check,here,all;
    public UpdatesWindow(UpdateCoordinator updater,Func<KvmService?> network):base("Updates",820,620)
    {
        this.updater=updater;this.network=network;
        var root=new C.DockPanel();var header=Header("One update. Every computer.",$"ScreenCapture {Updates.VersionText} · Verified releases from GitHub");C.DockPanel.SetDock(header,C.Dock.Top);root.Children.Add(header);
        check=Kit.AsyncButton("Check for updates",Check);here=Kit.AsyncButton("Update this PC",()=>updater.InstallHere());all=Kit.AsyncButton("Update all PCs",()=>updater.InstallAll());all.Style=(W.Style)FindResource("Primary");
        var footer=new C.Border{Padding=new(24),Child=Kit.Actions(check,here,all),BorderBrush=Kit.Brush("#302B3E"),BorderThickness=new(0,1,0,0)};C.DockPanel.SetDock(footer,C.Dock.Bottom);root.Children.Add(footer);
        var stack=new C.StackPanel{Margin=new(28,24,28,24)};status.Margin=new(0,0,0,16);stack.Children.Add(status);stack.Children.Add(Kit.Card(details));
        stack.Children.Add(Kit.Button("View release notes",()=>Process.Start(new ProcessStartInfo(Updates.RepositoryUrl+"/releases"){UseShellExecute=true})));root.Children.Add(new C.ScrollViewer{Content=stack,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto});Content=root;
        updater.Changed+=Changed;Closed+=(_,_)=>updater.Changed-=Changed;Loaded+=async(_,_)=>{Refresh();if(!updater.Busy)await Check();};Refresh();
    }
    void Changed(){if(!IsClosed)Dispatcher.BeginInvoke(Refresh);}
    async Task Check(){try{await updater.Check();}catch(Exception e){updater.SetStatus("Could not check for updates: "+e.Message);}finally{if(!IsClosed)Refresh();}}
    void Refresh()
    {
        if(IsClosed)return;status.Text=updater.Status;check.IsEnabled=!updater.Busy;here.IsEnabled=!updater.Busy&&updater.Latest!=null&&Updates.ParseVersion(updater.Latest.Manifest.Version)>Updates.Current;all.IsEnabled=!updater.Busy&&network()?.Options.Role=="Host"&&updater.Latest!=null;
        var computers=new List<string>{Environment.MachineName+"  ·  "+Updates.VersionText+"  ·  This PC"};
        foreach(var peer in network()?.Peers??[])computers.Add(updater.Peers.TryGetValue(peer.Id,out string? state)?state:$"{peer.Name}  ·  {(peer.Version.Length==0?"0.5.x":peer.Version)}  ·  {(peer.UpdateProtocol>0?"Ready for updates":"Needs a manual update to 0.6 or later")}");
        details.Text=string.Join("\n\n",computers)+"\n\n"+(updater.Latest?.Notes??"Packages are digitally verified. Settings, pairing and saved clips are kept. The current replay is saved before restarting.");
    }
}
