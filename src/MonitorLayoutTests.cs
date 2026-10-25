using System.Text.Json;

namespace SdrCapture;

static class MonitorLayoutTests
{
    const string Local="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",Remote="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",Server="cccccccccccccccccccccccccccccccc";
    const string Old=@"\\.\DISPLAY5",Current=@"\\.\DISPLAY1";
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static MonitorPlacement Placement(string peer,string device,int x=0,int y=0,int width=2560,int height=1440,int hotkey=0)=>
        new(){Peer=peer,Device=device,Name=peer+" · "+device,X=x,Y=y,Width=width,Height=height,Hotkey=hotkey};
    public static void Run()
    {
        var monitor=new Rectangle(0,0,1920,1080);
        Require(!WindowActivation.IsFullscreenClient("WorkerW",monitor,monitor)&&!WindowActivation.IsFullscreenClient("Progman",monitor,monitor),"The Windows desktop was classified as a fullscreen application.");
        Require(WindowActivation.IsFullscreenClient("GameWindow",monitor,monitor)&&WindowActivation.IsFullscreenClient("Chrome_WidgetWin_1",monitor,monitor),"Borderless game or browser fullscreen was not protected.");
        Require(!WindowActivation.IsFullscreenClient("Chrome_WidgetWin_1",new(0,31,1920,1009),monitor),"Ordinary maximized browser client area was classified as fullscreen.");
        var secondary=new Rectangle(-1920,0,1920,1080);
        Require(WindowActivation.IsFullscreenClient("GameWindow",secondary,secondary),"Fullscreen protection failed on a secondary monitor.");
        // A driver reinstall changed DISPLAY5 to DISPLAY1; the old merge saved both.
        var old=Placement(Local,Old,2133,-3859);
        var current=Placement(Local,Current,2133,-1292,hotkey:1);
        var left=Placement(Remote,"LEFT",213,-1292,1920,1080,2);
        var right=Placement(Remote,"RIGHT",4693,-1874,1080,1920,3);
        var server=Placement(Server,"SERVER",width:1920,height:1080);
        var options=new KvmOptions{Id=Local,Layout=[old,current,left,right,server],RemoteOnlyPeers=[Server]};
        var peers=new[]{new KvmPeerInfo(Local,"MAINGEAR",[new(Current,0,0,2560,1440,true)])};
        string before=JsonSerializer.Serialize(options);
        var merged=KvmLayout.Merge(options,peers);
        Require(merged.Count==3&&!merged.Any(m=>m.Key==old.Key),"Disconnected local monitor remains in the layout after a driver reinstall");
        Require(merged.Single(m=>m.Key==current.Key).Bounds==current.Bounds,"Current monitor moved while removing its stale duplicate");
        Require(merged.Count(m=>m.Peer==Remote)==2,"Offline PC lost its saved monitor positions");
        var canvas=new Ui.MonitorCanvas(options,merged,peers);
        Require(!canvas.Result.Any(m=>m.Key==old.Key),"Stale monitor was reintroduced through Available monitors");
        canvas.AddMonitor(old.Key,0,0);
        Require(!canvas.Result.Any(m=>m.Key==old.Key),"Disconnected monitor can be dragged back into the layout");
        Require(canvas.Result.Any(m=>m.Key==server.Key),"Offline remote-only server lost its saved layout");
        Require(JsonSerializer.Serialize(options)==before,"Opening the editor mutated saved settings");

        // With one saved and one active screen, preserve the desk geometry/shortcut
        // and exclusion even if the driver name and fullscreen resolution changed.
        options=new(){Id=Local,Layout=[old with{Hotkey=4},left,right]};
        var smallPeers=new[]{peers[0] with{Screens=[new(Current,0,0,1280,720,true)]}};
        merged=KvmLayout.Merge(options,smallPeers);
        var migrated=merged.Single(m=>m.Peer==Local);
        Require(migrated.Device==Current&&migrated.Bounds==old.Bounds&&migrated.Hotkey==4,"Renumbered monitor lost its geometry or shortcut");
        Require(migrated.Name=="MAINGEAR · "+Current,"Renumbered monitor retained its obsolete display name");
        var edgeOptions=new KvmOptions{Id=Local,Layout=[Placement(Local,Old,hotkey:4),Placement(Remote,"RIGHT",2560,0,1920,1080)]};
        var edgeLayout=KvmLayout.Merge(edgeOptions,smallPeers);
        var crossing=KvmLayout.EdgeCrossing(edgeLayout,edgeLayout.Single(m=>m.Peer==Local),smallPeers[0].Screens[0],new(1279,360));
        Require(crossing?.Target.Device=="RIGHT","Renumbering/fullscreen resolution change broke a shared edge");
        var excludedOptions=new KvmOptions{Id=Remote,Layout=[old],ExcludedMonitors=[old.Key]};
        var excludedCanvas=new Ui.MonitorCanvas(excludedOptions,KvmLayout.Merge(excludedOptions,peers),peers);
        Require(excludedCanvas.ExcludedMonitors.Contains(Local+"|"+Current)&&!excludedCanvas.ExcludedMonitors.Contains(old.Key),"Renumbered monitor lost its exclusion");
        Require(!KvmLayout.Merge(excludedOptions,peers).Any(),"Renumbered excluded monitor returned to edge switching");
        excludedCanvas.AddMonitor(Local+"|"+Current,-2560,0);
        Require(!excludedCanvas.ExcludedMonitors.Any()&&excludedCanvas.Result.Single().Device==Current,"Renumbered monitor cannot be restored from Available monitors");

        // The current inventory is authoritative only for PCs in this snapshot.
        // Equal resolution is not an identity: two real screens must both remain.
        var remotePeers=new[]{peers[0],new KvmPeerInfo(Remote,"AURORA",[new("LEFT",0,0,1920,1080,true),new("RIGHT",1920,0,1920,1080,false)])};
        options=new(){Id=Local,Layout=[current,left,right,Placement(Remote,"UNPLUGGED")]};
        merged=KvmLayout.Merge(options,remotePeers);
        Require(merged.Count==3&&merged.Count(m=>m.Peer==Remote)==2&&!merged.Any(m=>m.Device=="UNPLUGGED"),"Connected peer retained a removed monitor or merged two real screens");
        options.ExcludedMonitors=[left.Key];
        canvas=new(options,KvmLayout.Merge(options,remotePeers),remotePeers);
        Require(canvas.Result.Count==3&&canvas.ExcludedMonitors.Contains(left.Key),"Active excluded monitor disappeared from Available monitors");
        canvas.AddMonitor(left.Key,left.X,left.Y);
        Require(!canvas.ExcludedMonitors.Contains(left.Key)&&canvas.Result.Count==3,"Restoring one active monitor changed its sibling");
        var emptyPeer=remotePeers[1] with{Screens=[]};
        Require(!KvmLayout.Merge(options,[peers[0],emptyPeer]).Any(m=>m.Peer==Remote),"Connected headless peer retained phantom monitors");
        var remoteServer=remotePeers[1] with{RemoteViewOnly=true,Screens=[new("RIGHT",0,0,1080,1920,true)]};
        canvas=new(options,KvmLayout.Merge(options,[peers[0],remoteServer]),[peers[0],remoteServer]);
        Require(!canvas.Result.Any(m=>m.Key==left.Key)&&canvas.Result.Any(m=>m.Key==right.Key),"Remote-only peer retained stale screens or lost its current screen");

        Program.Write("monitor-layout-tests.json",new{Pass=true,DriverReinstallDuplicateRemoved=true,RenumberedGeometryPreserved=true,
            OfflineLayoutsPreserved=true,ActiveExclusionsPreserved=true,TwoRealMonitorsPreserved=true,NoUserSettingsChanged=true,NoWindowsOpened=true});
    }
}
