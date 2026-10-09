using W=System.Windows;
using C=System.Windows.Controls;
using System.Windows.Threading;

namespace SdrCapture.Ui;

sealed class KvmWindow:ShellWindow
{
    readonly Func<KvmService?> network;
    readonly Func<bool> seamless;
    readonly Action<KvmPeerInfo> open;
    readonly Func<KvmPeerInfo,bool> remoteOnly;
    readonly Action<KvmPeerInfo,bool>? setRemoteOnly;
    readonly C.WrapPanel computers=new();
    readonly C.CheckBox edges=new(){Content=Kit.Text("Move freely between physical displays")};
    readonly C.TextBlock status=Kit.Text("",12,"#B0A9BC");
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
    bool refreshing;
    string signature="";
    public KvmWindow(Func<KvmService?> network,Func<bool> seamless,Action<bool> setSeamless,Action returnHome,Action<KvmPeerInfo> open,Action<int> settings,Func<KvmPeerInfo,bool>? remoteOnly=null,Action<KvmPeerInfo,bool>? setRemoteOnly=null):base("KVM",900,660)
    {
        AnimateOnReveal=true;
        this.network=network;this.seamless=seamless;this.open=open;this.remoteOnly=remoteOnly??(p=>p.RemoteViewOnly);this.setRemoteOnly=setRemoteOnly;
        var root=new C.DockPanel();var header=Header("Your computers","Choose a computer to open its desktop.");C.DockPanel.SetDock(header,C.Dock.Top);root.Children.Add(header);
        var footer=new C.StackPanel{Margin=new(28,12,28,20)};status.Margin=new(0,0,0,12);footer.Children.Add(status);footer.Children.Add(edges);
        var actions=Kit.Actions(Kit.Button("Pair a computer…",()=>settings(3)));actions.Margin=new(0,14,0,0);footer.Children.Add(actions);C.DockPanel.SetDock(footer,C.Dock.Bottom);root.Children.Add(footer);
        root.Children.Add(new C.ScrollViewer{Content=computers,Padding=new(28,24,14,4),VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled});Content=root;
        edges.Checked+=(_,_)=>{if(!refreshing)setSeamless(true);};edges.Unchecked+=(_,_)=>{if(!refreshing)setSeamless(false);};
        timer.Tick+=(_,_)=>Refresh();Loaded+=(_,_)=>{Refresh();timer.Start();};Closed+=(_,_)=>timer.Stop();
    }
    void Refresh()
    {
        var service=network();refreshing=true;edges.IsChecked=seamless();edges.IsEnabled=service?.Options.Role=="Host";refreshing=false;
        status.Text=service?.Options.Role switch{"Host"=>UiStrings.F("{0} connected computers",service.Peers.Length),"Client"=>service.Peers.FirstOrDefault() is {} host?UiStrings.F("Connected to {0}",host.Name):UiStrings.T("Waiting for the host…"),_=>UiStrings.T("KVM is off. Start by pairing a computer.")};RenderPeers(service?.Options.Role??"Off",service?.Peers.OrderBy(p=>p.Name).ToArray()??[]);
    }
    internal void RenderPeers(string role,KvmPeerInfo[] peers)
    {
        string next=role+string.Join('|',peers.Select(p=>p.Id+p.Name+p.Version+remoteOnly(p)+string.Join(',',p.Screens.Select(s=>s.Device))));if(signature==next)return;signature=next;computers.Children.Clear();
        if(role!="Host"||peers.Length==0)
        {
            var title=Kit.Text(role=="Client"?"This is a client computer":"Your workspace starts here",21);title.FontWeight=W.FontWeights.SemiBold;
            var detail=Kit.Text(role=="Client"?"Open this desktop from the computer you paired as Host.":"Choose Host on this PC and Client on the other. Enter this PC name and pairing code on the client.\n\nConnected computers will appear here automatically.",14,"#B0A9BC");detail.Margin=new(0,14,0,0);var card=Kit.Card(Kit.Stack(title,detail));card.Width=790;computers.Children.Add(card);return;
        }
        foreach(var peer in peers)
        {
            var badge=Kit.Text(peer.RemoteViewOnly?"REMOTE SERVER":"CONNECTED COMPUTER",10,"#31D1DB");badge.FontWeight=W.FontWeights.SemiBold;
            var title=Kit.Text(peer.Name,24);title.FontWeight=W.FontWeights.SemiBold;title.Margin=new(0,12,0,6);
            var detail=Kit.Text(UiStrings.F("{0} displays",peer.Screens.Length)+(peer.Version.Length>0?$"  ·  v{peer.Version}":""),12,"#B0A9BC");detail.Margin=new(0,0,0,20);
            var button=Kit.Button("Open desktop  →",()=>open(peer),true);button.HorizontalAlignment=W.HorizontalAlignment.Stretch;
            var card=Kit.Card(Kit.Stack(badge,title,detail,button));card.Width=391;card.Margin=new(0,0,14,14);computers.Children.Add(card);
        }
    }
}
