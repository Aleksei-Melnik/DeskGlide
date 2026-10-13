using System.Diagnostics;
using W=System.Windows;
using C=System.Windows.Controls;

namespace SdrCapture.Ui;

sealed class UpdatesWindow:ShellWindow
{
    readonly UpdateCoordinator updater;
    readonly Func<KvmService?> network;
    readonly KvmService? subscribedNetwork;
    readonly C.TextBlock status=Kit.Text("",15),details=Kit.Text("",13,"#B0A9BC");
    readonly C.Button check,here,all;
    public UpdatesWindow(UpdateCoordinator updater,Func<KvmService?> network,bool checkOnOpen=true):base("Updates",820,620)
    {
        AnimateOnReveal=true;
        this.updater=updater;this.network=network;subscribedNetwork=network();
        var root=new C.DockPanel();var header=Header("Updates",$"DeskGlide {Updates.VersionText} · GitHub");C.DockPanel.SetDock(header,C.Dock.Top);root.Children.Add(header);
        check=Kit.AsyncButton("Check for updates",Check);here=Kit.AsyncButton("Update this PC",()=>updater.InstallHere());all=Kit.AsyncButton("Update all PCs",()=>updater.InstallAll());all.Style=(W.Style)FindResource("Primary");
        var footer=new C.Border{Padding=new(24),Child=Kit.Actions(check,here,all),BorderBrush=Kit.Brush("#302B3E"),BorderThickness=new(0,1,0,0)};C.DockPanel.SetDock(footer,C.Dock.Bottom);root.Children.Add(footer);
        var stack=new C.StackPanel{Margin=new(28,24,28,24)};status.Margin=new(0,0,0,16);stack.Children.Add(status);stack.Children.Add(Kit.Card(details));
        stack.Children.Add(Kit.Button("View release notes",()=>Process.Start(new ProcessStartInfo(Updates.RepositoryUrl+"/releases"){UseShellExecute=true})));root.Children.Add(new C.ScrollViewer{Content=stack,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto});Content=root;
        updater.Changed+=Changed;if(subscribedNetwork!=null)subscribedNetwork.PeersChanged+=Changed;UiStrings.Shared.PropertyChanged+=LanguageChanged;Closed+=(_,_)=>{updater.Changed-=Changed;if(subscribedNetwork!=null)subscribedNetwork.PeersChanged-=Changed;UiStrings.Shared.PropertyChanged-=LanguageChanged;};Loaded+=async(_,_)=>{Refresh();if(checkOnOpen&&!updater.Busy)await Check();};Refresh();
    }
    void LanguageChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)=>Changed();
    void Changed(){if(!IsClosed)Dispatcher.BeginInvoke(Refresh);}
    async Task Check(){try{await updater.Check();}catch(Exception e){updater.SetStatus(UiStrings.F("Could not check for updates: {0}",UpdateStatusText.Translate(e.Message)));}finally{if(!IsClosed)Refresh();}}
    void Refresh()
    {
        if(IsClosed)return;status.Text=UpdateStatusText.Translate(updater.Status);check.IsEnabled=!updater.Busy;here.IsEnabled=!updater.Busy&&updater.Latest!=null&&Updates.ParseVersion(updater.Latest.Manifest.Version)>Updates.Current;all.IsEnabled=!updater.Busy&&network()?.Options.Role=="Host"&&updater.Latest!=null;
        var computers=new List<string>{Environment.MachineName+"  ·  "+Updates.VersionText+"  ·  "+UiStrings.T("This PC")};
        foreach(var peer in network()?.Peers??[])computers.Add(updater.PeerStatus(peer));
        details.Text=string.Join("\n\n",computers)+"\n\n"+(updater.Latest?.Notes??UiStrings.T("Packages are digitally verified. Settings, pairing and saved clips are kept. Unsaved replay buffers are discarded on restart."));
    }
}
