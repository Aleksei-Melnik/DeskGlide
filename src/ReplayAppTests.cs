namespace SdrCapture;

static class ReplayAppTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        PreservePendingRecordingBuffers();
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
            OrdinaryAppsRecognized=true,NoGroupingRespected=true,UnsafeNamesSanitized=true,NoLiveProfileChanges=true,PendingNewerRecordingsPreserved=true});
    }
    static void PreservePendingRecordingBuffers()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeskGlide-RollbackTest-"+Guid.NewGuid().ToString("N"));
        string? previous=Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT");
        try
        {
            string pending=Path.Combine(root,"buffer",Guid.NewGuid().ToString("N")),stale=Path.Combine(root,"buffer",Guid.NewGuid().ToString("N"));
            string Fixture(string folder)
            {
                string session=Path.Combine(folder,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(session);
                File.WriteAllText(Path.Combine(session,"seg-000.mkv"),"recording payload");File.WriteAllText(Path.Combine(session,"chunks.csv"),"segment index");
                return session;
            }
            string protectedSession=Fixture(pending),staleSession=Fixture(stale);
            string journal=Path.Combine(pending,"recording-"+Guid.NewGuid().ToString("N")+".json");File.WriteAllText(journal,"pending newer-version journal");
            Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
            using var recorder=new ReplayRecorder(new(){Enabled=false},true,null,null,startEncoder:false);
            Require(File.Exists(journal)&&File.Exists(Path.Combine(protectedSession,"seg-000.mkv"))&&File.ReadAllText(Path.Combine(protectedSession,"seg-000.mkv"))=="recording payload"&&File.Exists(Path.Combine(protectedSession,"chunks.csv")),"Rollback cleanup deleted fragments belonging to a pending newer recording.");
            Require(!Directory.Exists(staleSession),"Rollback protection disabled ordinary stale replay cleanup.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);
            string resolved=Path.GetFullPath(root),prefix=Path.GetFullPath(Path.GetTempPath())+"DeskGlide-RollbackTest-";
            if(!resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid rollback test directory.");
            if(Directory.Exists(resolved))Directory.Delete(resolved,true);
        }
    }
}
