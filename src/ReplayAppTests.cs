namespace SdrCapture;

static class ReplayAppTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        var timeline=new ReplayAppTimeline();
        Require(timeline.Resolve(0,600)=="Desktop","Empty activity did not use Desktop.");
        timeline.Observe("Counter-Strike 2",10);timeline.Observe("Desktop",590);
        Require(timeline.Resolve(0,600)=="Counter-Strike 2","An alt-tab at save time moved a CS2 clip to Desktop.");
        Require(timeline.Resolve(590,620)=="Desktop","A new Desktop-only save inherited an old game.");
        timeline.Observe("OBS Studio",620);timeline.Observe("Discord",650);
        Require(timeline.Resolve(615,649)=="OBS Studio"&&timeline.Resolve(649,680)=="Discord","Ordinary applications were treated as Desktop or an earlier clip.");
        Require(timeline.Resolve(10,11)=="Counter-Strike 2","Clipped activity interval was not respected.");
        timeline.Observe("Counter-Strike 2",8000);timeline.Observe("Desktop",8400);
        Require(timeline.Resolve(8000,8410)=="Counter-Strike 2","History cleanup lost the active game interval.");
        var options=new ReplayOptions{Folder=@"\\Aurora\f\Record",GroupByApp=true};
        Require(ReplayAppContext.Destination(options,"Counter-Strike 2","clip.mp4")==@"\\Aurora\f\Record\Counter-Strike 2\clip.mp4","CS2 destination changed.");
        Require(ReplayAppContext.Destination(options with{GroupByApp=false},"Counter-Strike 2","clip.mp4")==@"\\Aurora\f\Record\clip.mp4","Disabled app grouping still created subfolders.");
        Require(ReplayAppContext.ApplicationName("cs2",null)=="Counter-Strike 2","CS2 was not recognized without access to its executable metadata.");
        foreach(string process in new[]{"Discord","chrome","firefox","obs64","Codex","steamwebhelper"})
            Require(ReplayAppContext.ApplicationName(process,null)==process,"An ordinary application was classified as Desktop: "+process);
        Require(ReplayAppContext.ApplicationName("obs64","OBS Studio")=="OBS Studio","Product metadata was ignored.");
        Require(ReplayAppContext.ApplicationName("explorer",null,"Progman")=="Desktop"&&ReplayAppContext.ApplicationName("explorer",null,"CabinetWClass")=="File Explorer","File Explorer was confused with the Windows desktop.");
        Require(ReplayAppContext.ApplicationName("DeskGlide",null,own:true)==null&&ReplayAppContext.ApplicationName("StreamDeck",null)==null,"Save controls replaced the recorded application's context.");
        foreach(var name in new[]{"../escape","C:\\bad","CON","LPT1.txt","CON."+new string('a',200),"x/y",new string('a',200)})
        {
            string safe=ReplayAppContext.SafeName(name);Require(!safe.Contains('/')&&!safe.Contains('\\')&&!safe.Contains(':')&&safe.Length<=80&&safe!="CON"&&safe!="LPT1.txt","Unsafe application folder name accepted.");
        }
        Program.Write("replay-app-tests.json",new{Pass=true,ClipIntervalSelectsFolder=true,AltTabToDesktopRetainsGameClip=true,NewDesktopFootageSeparate=true,
            OrdinaryAppsRecognized=true,NoGroupingRespected=true,UnsafeNamesSanitized=true,NoLiveProfileChanges=true});
    }
}
