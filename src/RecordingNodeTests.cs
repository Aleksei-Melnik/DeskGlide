using System.Diagnostics;
using System.Text.Json;

namespace SdrCapture;

static class RecordingNodeTests
{
    public static void Run()=>RunAsync().GetAwaiter().GetResult();
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static string Id()=>Guid.NewGuid().ToString("N");
    static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeskGlide-NodeTests-"+Id());
        string? previous=Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT");Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var nodes=new List<RecordingNode>();
        try
        {
            Require(RecordingNode.CapabilitySource(new(SourceWidth:2560,SourceHeight:1440),0,0)==(2560,1440,false),"Capability probes lost the gaming PC's native source dimensions before NDI connected.");
            Require(RecordingNode.CapabilitySource(null,3840,2160)==(3840,2160,false)&&RecordingNode.CapabilitySource(null,0,0)==(1920,1080,true),"Capability source dimensions did not use live NDI data or an explicit fallback.");
            var denied=new RecordingNode(()=>new(),()=>new());nodes.Add(denied);
            var result=await denied.Handle(Id(),new(Id(),"configureReplay",Id(),"Gaming PC (Desktop)",new()));
            Require(!result.Ok&&result.Code=="denied","A receiver without explicit opt-in accepted remote encoding.");
            Require(!Directory.Exists(Path.Combine(root,"buffer")),"A denied request created a recorder.");
            var prefs=new RecordingOptions{AllowRemoteEncoding=true,Profile=new(){Folder=Path.Combine(root,"receiver-recordings"),Microphone="receiver microphone",GameAudio="receiver desktop",ExtraAudio="receiver extra"}};
            var replay=new ReplayOptions{Enabled=true,Folder=Path.Combine(root,"receiver-replays")};
            var sources=new List<FixtureSource>();var coordinators=new List<RecordingCoordinator>();var initialProfiles=new List<ReplayOptions>();
            RecordingNode Create()=>new(()=>prefs,()=>replay,null,(name,offer,availability)=>
            {var source=new FixtureSource(name,availability);sources.Add(source);return source;},profile=>
            {initialProfiles.Add(profile);var coordinator=new RecordingCoordinator(profile,true);coordinators.Add(coordinator);return coordinator;});
            var node=Create();nodes.Add(node);string first=Id(),other=Id(),replayId=Id(),recordingId=Id();const string sourceName="Gaming PC (Desktop)";
            Require((await node.Handle(first,new(Id(),"configureReplay",replayId,sourceName,new()))).Ok,"Permitted replay session was rejected.");
            Require(node.Snapshot.ReplayEnabled&&node.Snapshot.Error?.Contains("first NDI")==true&&node.Snapshot.InputFps==0,"Receiver claimed an available source before the first video packet.");
            Require(initialProfiles.Single().Folder==replay.Folder&&initialProfiles[0].GameAudio=="ndi:"+sourceName&&initialProfiles[0].Microphone==""&&initialProfiles[0].ExtraAudio=="","Receiver replay settings were replaced by remote paths or local audio capture.");
            Require((await node.Handle(other,new(Id(),"status"))).Code=="busy"&&(await node.Handle(other,new(Id(),"configureReplay",Id(),sourceName,new()))).Code=="busy"&&sources.Count==1,"Another peer acquired an occupied receiver.");
            Require((await node.Handle(first,new(Id(),"startRecording",recordingId,sourceName,new(Fps:120),"Counter-Strike 2"))).Ok,"Logical recording could not wait for an offline source.");
            Require(node.Snapshot.Recording&&node.Snapshot.RecordingSession==recordingId&&node.Snapshot.RequiredFps==120&&!coordinators[0].HasRecording,"Offline recording lost its logical session, required source rate, or opened a premature encoder lease.");
            Require((await node.Handle(first,new(Id(),"disableReplay",Id()))).Code=="session"&&node.Snapshot.ReplayEnabled,"A mismatched session disabled replay.");
            Require((await node.Handle(first,new(Id(),"disableReplay",replayId))).Ok&&!node.Snapshot.ReplayEnabled&&node.Snapshot.Recording&&!sources[0].Disposed,"Disabling replay stopped the independent recording session or its NDI source.");
            Require((await node.Handle(first,new(Id(),"configureReplay",replayId,sourceName,new()))).Ok&&node.Snapshot.ReplayEnabled&&sources.Count==1,"Replay could not resume alongside the waiting recording.");
            Require((await node.Handle(first,new(Id(),"configureReplay",replayId,"Other PC (Desktop)",new()))).Code=="conflict"&&sources.Count==1,"An active logical recording changed its NDI source.");
            Require((await node.Handle(first,new(Id(),"stopRecording",Id()))).Code=="session"&&node.Snapshot.Recording,"A mismatched session stopped the recording.");
            Require((await node.Handle(first,new(Id(),"stopRecording",recordingId))).Ok&&!node.Snapshot.Recording&&node.Snapshot.ReplayEnabled,"Stopping a waiting recording stopped replay too.");
            Require(node.Snapshot.RequiredFps==60,"A stopped 120-FPS recording kept the source rate above its remaining 60-FPS replay.");
            Require((await node.Handle(first,new(Id(),"startRecording",recordingId,sourceName,new()))).Code=="completed","A stopped logical session started again under a new request ID.");
            Require((await node.Handle(first,new(Id(),"release",replayId))).Ok&&sources[0].Disposed,"Releasing replay left its NDI receiver alive.");
            string otherReplay=Id(),otherRecording=Id();
            Require((await node.Handle(other,new(Id(),"configureReplay",otherReplay,sourceName,new()))).Ok,"Receiver ownership was not released.");
            Require((await node.Handle(other,new(Id(),"startRecording",otherRecording,sourceName,new()))).Ok,"New owner could not start a logical session.");
            sources[0].SetAvailable(true);await Task.Delay(30);
            Require(!coordinators[^1].HasRecording&&node.Snapshot.Error?.Contains("unavailable")==true,"A disposed source callback activated the replacement receiver.");
            prefs.AllowRemoteEncoding=false;await node.RefreshPreferencesAsync();
            Require(!node.Snapshot.ReplayEnabled&&!node.Snapshot.Recording&&sources[^1].Disposed,"Revoking permission left remote encoding active.");
            Require((await node.Handle(other,new(Id(),"configureReplay",Id(),sourceName,new()))).Code=="denied","Permission revocation was ignored by later commands.");
            node.Dispose();await node.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(10));

            prefs.AllowRemoteEncoding=true;bool failOnce=true;
            var transactional=new RecordingNode(()=>prefs,()=>replay,null,(name,offer,availability)=>
            {if(failOnce){failOnce=false;throw new IOException("Fixture source failed to open.");}return new FixtureSource(name,availability);},profile=>new(profile,true));nodes.Add(transactional);
            Require(!(await transactional.Handle(first,new(Id(),"configureReplay",Id(),sourceName,new()))).Ok,"A failed source reported a configured replay.");
            Require((await transactional.Handle(other,new(Id(),"configureReplay",Id(),sourceName,new()))).Ok,"A failed source factory permanently claimed receiver ownership.");
            transactional.Dispose();await transactional.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(10));

            prefs.AllowRemoteEncoding=true;sources.Clear();coordinators.Clear();initialProfiles.Clear();
            var lifecycle=Create();nodes.Add(lifecycle);string logical=Id(),lifecycleReplay=Id();
            Require((await lifecycle.Handle(first,new(Id(),"configureReplay",lifecycleReplay,sourceName,new()))).Ok,"Lifecycle replay fixture could not start.");
            Require((await lifecycle.Handle(first,new(Id(),"startRecording",logical,sourceName,new(),"Remote game"))).Ok,"Recording lifecycle fixture could not start.");
            sources.Single().SetAvailable(true);
            await Wait(()=>coordinators[0].HasRecording,3,"A live source did not open a continuous lease.");
            string journal=RecordingRecovery.Pending().Single();var metadata=JsonSerializer.Deserialize<RecordingJournal>(File.ReadAllText(journal))!;
            Require(metadata.Options.Folder==prefs.Profile.Folder&&metadata.Options.Microphone==""&&metadata.Options.ExtraAudio==""&&metadata.Options.GameAudio=="ndi:"+sourceName&&metadata.Options.AudioMode=="Mixed"&&metadata.App=="Remote game","Continuous journal did not use receiver-owned paths and the incoming NDI audio source.");
            Require(metadata.Id!=logical,"Internal segment lease replaced the logical recording session ID.");
            Require((await lifecycle.Handle(first,new(Id(),"disableReplay",lifecycleReplay))).Ok&&coordinators[0].HasRecording&&lifecycle.Snapshot.Recording&&!lifecycle.Snapshot.ReplayEnabled&&!sources.Single().Disposed,"Disabling replay interrupted an open continuous lease.");
            sources.Single().SetAvailable(false);
            await Wait(()=>JsonSerializer.Deserialize<RecordingJournal>(File.ReadAllText(journal))?.End!=null,3,"Source loss did not seal the interrupted recording.");
            await Wait(()=>RecordingRecovery.Pending().Contains(journal)&&lifecycle.Snapshot.Recording,3,"Source loss discarded the logical session or its journal.");
            Require(lifecycle.Snapshot.RecordingSession==logical&&lifecycle.Snapshot.OutputFps==0,"Source loss reported an unrelated recording or continued encoded output.");
            sources.Single().SetAvailable(true);
            await Wait(()=>coordinators.Count>=2&&coordinators[^1].HasRecording,10,"Source reconnect did not create a new continuous part after a failed interrupted export.");
            Require(lifecycle.Snapshot.RecordingSession==logical&&RecordingRecovery.Pending().Count()==2&&File.Exists(journal),"Reconnect lost the interrupted journal or changed the logical session.");
            lifecycle.Dispose();await lifecycle.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Require(RecordingRecovery.Pending().Count()==2,"Disposal deleted unfinished recording journals.");

            long leaseClock=Stopwatch.GetTimestamp();FixtureSource? leasedSource=null;RecordingCoordinator? leasedCoordinator=null;
            var leased=new RecordingNode(()=>prefs,()=>replay,null,(name,offer,availability)=>leasedSource=new(name,availability),
                profile=>leasedCoordinator=new(profile,true),()=>Interlocked.Read(ref leaseClock),TimeSpan.FromSeconds(30));nodes.Add(leased);
            string leasedReplay=Id(),leasedRecording=Id();
            Require((await leased.Handle(first,new(Id(),"configureReplay",leasedReplay,sourceName,new()))).Ok,"Control lease fixture did not configure replay.");
            Require((await leased.Handle(first,new(Id(),"startRecording",leasedRecording,sourceName,new()))).Ok,"Control lease fixture did not start recording.");
            Interlocked.Add(ref leaseClock,Stopwatch.Frequency*20);await leased.Handle(first,new(Id(),"status",leasedRecording));
            Interlocked.Add(ref leaseClock,Stopwatch.Frequency*20);await leased.RefreshPreferencesAsync();
            Require(leased.Snapshot.ReplayEnabled&&leased.Snapshot.Recording&&!leasedSource!.Disposed,"An owner status keepalive did not extend the control lease.");
            leasedSource!.SetAvailable(true);await Wait(()=>leasedCoordinator!.HasRecording,3,"Leased recording did not open its part.");
            int beforeExpiry=RecordingRecovery.Pending().Count();
            Interlocked.Add(ref leaseClock,Stopwatch.Frequency*11);
            await leased.Handle(first,new(Id(),"status"));await leased.Handle(first,new(Id(),"status",Id()));
            await leased.RefreshPreferencesAsync();
            Require(!leased.Snapshot.ReplayEnabled&&!leased.Snapshot.Recording&&leasedSource.Disposed&&RecordingRecovery.Pending().Count()==beforeExpiry,"Expired control lease kept encoding active or discarded its interrupted journal.");
            Require((await leased.Handle(first,new(Id(),"startRecording",leasedRecording,sourceName,new()))).Code=="completed","An expired continuous session silently restarted.");
            Require((await leased.Handle(first,new(Id(),"configureReplay",leasedReplay,sourceName,new()))).Ok,"Replay could not be configured again after its controller lease expired.");
            leased.Dispose();await leased.DisposalCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Program.Write("recording-node-tests.json",new{Pass=true,OptInRequired=true,SinglePeerOwner=true,ReceiverOwnsPathsAndAudio=true,
                LogicalSessionBeforeFirstVideo=true,SourceChangeRejectedDuringRecording=true,StoppedSessionCannotRestart=true,
                StaleReceiverCallbackIgnored=true,PermissionRevocationStopsReceiver=true,SourceLossSealsPart=true,
                InterruptedJournalRetained=true,ReconnectStartsNewPartWithSameLogicalSession=true,DisposePreservesJournals=true,
                ReplayDisablePreservesContinuousSession=true,
                OwnerStatusExtendsControlLease=true,PassiveOrWrongSessionStatusCannotRenewLease=true,ExpiredControlLeaseReleasesReceiver=true,ExpiredRecordingCannotRestart=true,
                HardwareEncoderStarted=false,MediaFileClaimed=false,DesktopOrInputCaptured=false});
        }
        finally
        {
            foreach(var node in nodes)node.Dispose();
            await Task.WhenAll(nodes.Select(n=>n.DisposalCompletion)).WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);
            string temp=Path.GetFullPath(Path.GetTempPath());
            if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("DeskGlide-NodeTests-",StringComparison.Ordinal))throw new IOException("Unsafe test cleanup path.");
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    static async Task Wait(Func<bool> condition,int seconds,string message)
    {var watch=Stopwatch.StartNew();while(!condition()&&watch.Elapsed.TotalSeconds<seconds)await Task.Delay(20);Require(condition(),message);}
    sealed class FixtureSource(string name,Action<bool> availability):IRecordingNodeSource
    {
        public bool Disposed{get;private set;}
        public string SourceAudioId=>"ndi:"+name;
        public string Status=>"No native source is started in this policy fixture.";
        public double FramesPerSecond=>0;
        public long SourceGaps=>0;
        public void SetAvailable(bool value)=>availability(value);
        public void Dispose()=>Disposed=true;
    }
}
