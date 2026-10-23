using System.Diagnostics;

namespace SdrCapture;

internal interface IRecordingNodeSource:IDisposable
{
    string SourceAudioId{get;}
    string Status{get;}
    double FramesPerSecond{get;}
    long SourceGaps{get;}
}

sealed class RecordingNodeSource:IRecordingNodeSource
{
    readonly RecordingNdiSource source;
    public RecordingNodeSource(string name,Action<VideoBuffer> offer,Action<bool> availability)=>source=new(name,offer,availability);
    public string SourceAudioId=>source.SourceAudioId;
    public string Status=>source.Status;
    public double FramesPerSecond=>source.FramesPerSecond;
    public long SourceGaps=>source.SourceGaps;
    public void Dispose()=>source.Dispose();
}

// A paired peer controls a logical session. Encoders and destination folders
// remain local, and an interrupted logical recording can have several files.
sealed class RecordingNode:IDisposable
{
    readonly Func<RecordingOptions> preferences;
    readonly Func<ReplayOptions> replayPreferences;
    readonly Action<ReplayResult>? saved;
    readonly Func<string,Action<VideoBuffer>,Action<bool>,IRecordingNodeSource> createSource;
    readonly Func<ReplayOptions,RecordingCoordinator> createCoordinator;
    readonly Func<long> clock;
    readonly long controlLeaseTicks;
    readonly SemaphoreSlim gate=new(1,1);
    readonly CancellationTokenSource stop=new();
    readonly Dictionary<(string Peer,string Session),DateTimeOffset> completed=new();
    RecordingCoordinator? coordinator;
    IRecordingNodeSource? source;
    string? owner,sourceName,replaySession,recordingSession,recordingApp,error,lastSaved;
    ReplayOptions? replayProfile,recordingProfile;
    bool replayWanted,recordingWanted;
    int sourceVersion,availabilityVersion,availabilityWorker,available,disposed,sourceWidth,sourceHeight;
    long metricsStarted=Stopwatch.GetTimestamp();
    long lastControl;
    Task disposal=Task.CompletedTask;
    internal Task DisposalCompletion=>Volatile.Read(ref disposal);
    public RecordingNode(Func<RecordingOptions> preferences,Func<ReplayOptions> replayPreferences,Action<ReplayResult>? saved=null)
        :this(preferences,replayPreferences,saved,(name,offer,availability)=>new RecordingNodeSource(name,offer,availability),profile=>new(profile)){}
    internal RecordingNode(Func<RecordingOptions> preferences,Func<ReplayOptions> replayPreferences,Action<ReplayResult>? saved,
        Func<string,Action<VideoBuffer>,Action<bool>,IRecordingNodeSource> createSource,Func<ReplayOptions,RecordingCoordinator> createCoordinator,
        Func<long>? clock=null,TimeSpan? controlLease=null)
    {
        this.preferences=preferences;this.replayPreferences=replayPreferences;this.saved=saved;this.createSource=createSource;this.createCoordinator=createCoordinator;
        this.clock=clock??Stopwatch.GetTimestamp;controlLeaseTicks=(long)((controlLease??TimeSpan.FromSeconds(30)).TotalSeconds*Stopwatch.Frequency);
        if(controlLeaseTicks<=0)throw new ArgumentOutOfRangeException(nameof(controlLease));
        _=Task.Run(PermissionLoop);
    }
    public RecordingNodeState Snapshot
    {
        get
        {
            var active=Volatile.Read(ref coordinator);var input=Volatile.Read(ref source);bool connected=Volatile.Read(ref available)!=0;
            var metrics=active?.MetricsRecorder;
            string? problem=Volatile.Read(ref error);
            if(!Allowed())problem="Remote encoding is disabled on this PC.";
            else if(input!=null&&!connected)problem=recordingWanted?"The NDI source is unavailable. Recording will continue in a new file when video returns.":"Waiting for the first NDI video packet.";
            if(problem==null&&active?.Error!=null)problem="The encoder reported an error. Check recording diagnostics on this PC.";
            double elapsed=Math.Max(1,Stopwatch.GetElapsedTime(Interlocked.Read(ref metricsStarted)).TotalSeconds);
            return new(replayWanted,Math.Clamp(active?.Replay.DisplayBufferedSeconds??0,0,7200),recordingWanted,recordingWanted?recordingSession:null,
                problem,lastSaved,Math.Clamp(input?.FramesPerSecond??0,0,1000),connected?Math.Clamp((metrics?.EncodedFrames??0)/elapsed,0,1000):0,
                Math.Max(0,(metrics?.ConversionQueueDrops??0)+(metrics?.HistoryMisses??0)+(input?.SourceGaps??0)),Math.Max(0,metrics?.RepeatedInputFrames??0),
                Math.Max(replayWanted?replayProfile?.Fps??60:60,recordingWanted?recordingProfile?.Fps??60:60));
        }
    }
    bool Allowed(){try{return preferences().AllowRemoteEncoding;}catch{return false;}}
    RecordingResponse Reply(RecordingCommand command,bool ok=true,string code="",string message="")=>new(command.Id,ok,code,message,Snapshot);
    public async Task<RecordingResponse> Handle(string peer,RecordingCommand command)
    {
        if(!RecordingProtocol.GuidId(peer)||!RecordingProtocol.Valid(command))return new(command.Id,false,"invalid","Invalid recording command.");
        peer=Guid.ParseExact(peer,"N").ToString("N");command=RecordingProtocol.Normalize(command);
        if(Volatile.Read(ref disposed)!=0)return new(command.Id,false,"offline","The recording receiver has stopped.");
        if(command.Command=="capabilities")return await Capabilities(command).ConfigureAwait(false);
        if(command.Command=="status")
        {
            string? current=Volatile.Read(ref owner);
            if(current==peer&&command.Session!=null&&
                (replayWanted&&command.Session==replaySession||recordingWanted&&command.Session==recordingSession))TouchControl();
            return current!=null&&current!=peer?new(command.Id,false,"busy","This recording receiver is in use by another paired PC."):Reply(command);
        }
        if(!Allowed())return new(command.Id,false,"denied","Allow remote encoding in this PC's recording settings first.");
        bool entered=false;
        try
        {
            await gate.WaitAsync(stop.Token).ConfigureAwait(false);entered=true;
            if(!Allowed())return new(command.Id,false,"denied","Remote encoding is disabled on this PC.");
            if(owner!=null&&owner!=peer)return new(command.Id,false,"busy","This recording receiver is in use by another paired PC.");
            if(owner==peer)TouchControl();
            switch(command.Command)
            {
                case "configureReplay":
                    if(recordingWanted&&sourceName!=command.Source)return Reply(command,false,"conflict","Stop the current recording before changing its NDI source.");
                    var replay=NetworkProfile(command.Profile!,replayPreferences(),command.Source!);
                    EnsureSource(peer,command.Source!,replay);
                    replayProfile=replay;replaySession=command.Session;replayWanted=true;
                    coordinator!.UpdateReplay(replay with{Enabled=true});ApplyAvailability();error=null;return Reply(command);
                case "disableReplay":
                    if(!replayWanted||replaySession!=command.Session)return Reply(command,false,"session","The replay session does not match this receiver.");
                    replayWanted=false;
                    if(replayProfile!=null)coordinator?.UpdateReplay(replayProfile with{Enabled=false});
                    replaySession=null;replayProfile=null;
                    if(!recordingWanted&&coordinator?.HasRecording!=true)ReleaseResources();
                    return Reply(command);
                case "startRecording":
                    PruneCompleted();
                    if(completed.ContainsKey((peer,command.Session!)))return Reply(command,false,"completed","This recording session has already stopped. Use a new session ID to start another recording.");
                    if(recordingWanted)
                        return recordingSession==command.Session&&sourceName==command.Source&&recordingProfile==NetworkProfile(command.Profile!,preferences().Profile,command.Source!)?
                            Reply(command):Reply(command,false,"conflict","Another recording session is already active.");
                    if(completed.Count>=256)return Reply(command,false,"busy","Too many recent recording sessions. Check status later.");
                    if(replayWanted&&sourceName!=command.Source)return Reply(command,false,"conflict","Release the current replay source before recording a different NDI source.");
                    var recording=NetworkProfile(command.Profile!,preferences().Profile,command.Source!);
                    EnsureSource(peer,command.Source!,replayProfile??NetworkProfile(RecordingProfile.FromReplay(replayPreferences()),replayPreferences(),command.Source!) with{Enabled=false});
                    recordingProfile=recording;recordingSession=command.Session;recordingApp=ReplayAppContext.SafeName(command.App??"Desktop");recordingWanted=true;error=null;
                    ApplyAvailability();
                    if(Volatile.Read(ref available)!=0&&!coordinator!.HasRecording)coordinator.StartRecording(recording,recordingApp);
                    return Reply(command);
                case "stopRecording":
                    if(recordingSession!=command.Session)
                        return completed.ContainsKey((peer,command.Session!))?Reply(command):Reply(command,false,"session","The recording session does not match this receiver.");
                    if(coordinator?.HasRecording==true)await FinalizePart(true).ConfigureAwait(false);
                    recordingWanted=false;
                    completed[(peer,command.Session!)]=DateTimeOffset.UtcNow;
                    recordingSession=null;recordingProfile=null;recordingApp=null;
                    if(!replayWanted)ReleaseResources();
                    error=null;return Reply(command,message:lastSaved??"Recording stopped before the source supplied a complete segment.");
                case "saveReplay":
                    if(!replayWanted||replaySession!=command.Session||coordinator==null)return Reply(command,false,"session","The replay session does not match this receiver.");
                    if(Volatile.Read(ref available)==0&&coordinator.Replay.BufferedSeconds<=0)return Reply(command,false,"source","The NDI source has not supplied a replay buffer yet.");
                    Publish(await coordinator.Replay.SaveAsync(ReplayAppContext.SafeName(command.App??"Desktop")).ConfigureAwait(false));
                    error=null;return Reply(command,message:lastSaved??"");
                case "release":
                    if(command.Session!=replaySession&&command.Session!=recordingSession)return Reply(command,false,"session","The recording session does not match this receiver.");
                    await ReleaseCore().ConfigureAwait(false);return Reply(command);
                default:return Reply(command,false,"invalid","Unsupported recording command.");
            }
        }
        catch(OperationCanceledException){return new(command.Id,false,"unknown","The receiver stopped before confirming the command. Check status before retrying.");}
        catch(Exception)
        {
            error="The recording operation failed. Completed segments and recovery journals remain on this PC.";
            return Reply(command,false,"failed",error);
        }
        finally{if(entered)gate.Release();}
    }
    static ReplayOptions NetworkProfile(RecordingProfile profile,ReplayOptions local,string name)
    {
        bool silent=local.AudioMode=="Silent"||local.Silent;
        return profile.ToReplay(local) with{Enabled=true,Microphone="",GameAudio=silent?"":"ndi:"+name,ExtraAudio="",AudioMode=silent?"Silent":"Mixed",Silent=silent,EncodingComputer=""};
    }
    void EnsureSource(string peer,string name,ReplayOptions initial)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        if(sourceName==name&&source!=null){owner=peer;TouchControl();return;}
        if(coordinator?.HasRecording==true||recordingWanted)throw new InvalidOperationException("An active recording owns its source.");
        coordinator?.PauseInput();Interlocked.Increment(ref sourceVersion);source?.Dispose();source=null;
        Volatile.Write(ref available,0);Volatile.Write(ref sourceWidth,0);Volatile.Write(ref sourceHeight,0);
        coordinator??=createCoordinator(initial with{Enabled=false});coordinator.PauseInput();
        int generation=Volatile.Read(ref sourceVersion);
        try{source=createSource(name,frame=>Offer(generation,frame),value=>Availability(generation,value));owner=peer;sourceName=name;TouchControl();}
        catch{if(!replayWanted&&!recordingWanted)ReleaseResources();throw;}
        Interlocked.Exchange(ref metricsStarted,Stopwatch.GetTimestamp());
    }
    void Offer(int generation,VideoBuffer frame)
    {
        if(Volatile.Read(ref disposed)!=0||generation!=Volatile.Read(ref sourceVersion))return;
        Volatile.Write(ref sourceWidth,frame.Width);Volatile.Write(ref sourceHeight,frame.Height);
        if(Volatile.Read(ref available)!=0)Volatile.Read(ref coordinator)?.Offer(frame);
    }
    void Availability(int generation,bool value)
    {
        if(Volatile.Read(ref disposed)!=0||generation!=Volatile.Read(ref sourceVersion))return;
        Volatile.Write(ref available,value?1:0);Interlocked.Increment(ref availabilityVersion);
        if(Interlocked.CompareExchange(ref availabilityWorker,1,0)==0)_=Task.Run(AvailabilityLoop);
    }
    async Task AvailabilityLoop()
    {
        int observed=-1;
        try
        {
            do
            {
                observed=Volatile.Read(ref availabilityVersion);
                if(Volatile.Read(ref available)==0)Volatile.Read(ref coordinator)?.PauseInput();
                await gate.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    if(!Allowed()){await ReleaseCore().ConfigureAwait(false);continue;}
                    if(Volatile.Read(ref available)==0)
                    {
                        coordinator?.PauseInput();
                        if(coordinator?.HasRecording==true)
                        {
                            try{await FinalizePart().ConfigureAwait(false);}
                            catch
                            {
                                error="The interrupted recording part is retained for recovery on this PC.";
                                var retired=coordinator;coordinator=null;retired.Dispose();
                            }
                        }
                    }
                    else
                    {
                        coordinator??=createCoordinator((replayProfile??replayPreferences()) with{Enabled=false});
                        ApplyAvailability();
                        if(recordingWanted&&!coordinator.HasRecording)coordinator.StartRecording(recordingProfile!,recordingApp??"Desktop");
                    }
                }
                finally{gate.Release();}
            }while(observed!=Volatile.Read(ref availabilityVersion)&&!stop.IsCancellationRequested);
        }
        catch(OperationCanceledException){}
        catch(Exception){error="The NDI recording source could not resume. Recovery data remains on this PC.";}
        finally
        {
            Interlocked.Exchange(ref availabilityWorker,0);
            if(!stop.IsCancellationRequested&&observed!=Volatile.Read(ref availabilityVersion)&&Interlocked.CompareExchange(ref availabilityWorker,1,0)==0)_=Task.Run(AvailabilityLoop);
        }
    }
    void ApplyAvailability()
    {
        if(coordinator==null)return;
        if(replayProfile!=null)coordinator.UpdateReplay(replayProfile with{Enabled=replayWanted});
        if(Volatile.Read(ref available)==0)coordinator.PauseInput();else coordinator.ResumeInput();
    }
    async Task FinalizePart(bool allowEmpty=false)
    {
        if(coordinator?.HasRecording!=true)return;
        var result=await coordinator.StopRecordingAsync(allowEmpty).ConfigureAwait(false);
        if(result.LocalPath.Length>0)Publish(result);else lastSaved=null;
    }
    void Publish(ReplayResult result)
    {
        lastSaved=Path.GetFileName(string.IsNullOrEmpty(result.Destination)?result.LocalPath:result.Destination);
        try{saved?.Invoke(result);}catch(Exception){/* Delivery callback cannot undo a saved clip. */}
    }
    void PruneCompleted(){foreach(var key in completed.Where(p=>p.Value<DateTimeOffset.UtcNow.AddMinutes(-15)).Select(p=>p.Key).ToArray())completed.Remove(key);}
    async Task ReleaseCore()
    {
        replayWanted=false;recordingWanted=false;
        coordinator?.PauseInput();
        try{if(coordinator?.HasRecording==true)await FinalizePart().ConfigureAwait(false);}
        finally
        {
            if(owner!=null&&recordingSession!=null){PruneCompleted();if(completed.Count<256)completed[(owner,recordingSession)]=DateTimeOffset.UtcNow;}
            ReleaseResources();replaySession=null;recordingSession=null;replayProfile=null;recordingProfile=null;
        }
    }
    void ReleaseResources()
    {
        Interlocked.Increment(ref sourceVersion);Volatile.Write(ref available,0);
        var retiredSource=source;source=null;sourceName=null;owner=null;retiredSource?.Dispose();
        var retiredCoordinator=coordinator;coordinator=null;retiredCoordinator?.Dispose();
    }
    async Task<RecordingResponse> Capabilities(RecordingCommand command)
    {
        try
        {
            var local=replayPreferences();var profile=command.Profile?.ToReplay(local)??local with{};
            var (width,height,assumed)=CapabilitySource(command.Profile,Volatile.Read(ref sourceWidth),Volatile.Read(ref sourceHeight));
            var capabilities=await RecordingCapabilities.DiscoverAsync(profile,width,height,stop.Token).ConfigureAwait(false);
            return new(command.Id,true,Message:assumed?"No NDI source dimensions are available; probes used a 1920 x 1080 source.":"",Capabilities:capabilities.ToArray());
        }
        catch(OperationCanceledException){return new(command.Id,false,"offline","The recording receiver stopped during capability probing.");}
        catch(Exception){return new(command.Id,false,"failed","Hardware recording capabilities could not be probed on this PC.");}
    }
    internal static (int Width,int Height,bool Assumed) CapabilitySource(RecordingProfile? profile,int actualWidth,int actualHeight)
    {
        if(profile is {SourceWidth:>0,SourceHeight:>0})return(profile.SourceWidth,profile.SourceHeight,false);
        if(actualWidth>0&&actualHeight>0)return(actualWidth,actualHeight,false);
        return(1920,1080,true);
    }
    public async Task RefreshPreferencesAsync()
    {
        if(Allowed()&&!ControlExpired()||Volatile.Read(ref disposed)!=0)return;
        bool entered=false;
        try
        {
            await gate.WaitAsync(stop.Token).ConfigureAwait(false);entered=true;
            if(owner!=null&&(!Allowed()||ControlExpired()))
            {
                bool expired=Allowed();
                try{await ReleaseCore().ConfigureAwait(false);if(expired)error="The controller connection expired. Its recording session ended on this PC.";}
                catch(Exception){error="An interrupted recording remains available for recovery on this PC.";}
            }
        }
        catch(OperationCanceledException){}
        finally{if(entered)gate.Release();}
    }
    void TouchControl()=>Interlocked.Exchange(ref lastControl,clock());
    bool ControlExpired()=>Volatile.Read(ref owner)!=null&&clock()-Interlocked.Read(ref lastControl)>=controlLeaseTicks;
    async Task PermissionLoop()
    {
        try{while(!stop.IsCancellationRequested){await Task.Delay(1000,stop.Token).ConfigureAwait(false);if(Volatile.Read(ref owner)!=null&&(!Allowed()||ControlExpired()))await RefreshPreferencesAsync().ConfigureAwait(false);}}
        catch(OperationCanceledException){}
        catch(Exception){/* An unavailable settings provider disables future commands. */}
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();Interlocked.Increment(ref sourceVersion);Volatile.Write(ref available,0);
        disposal=Task.Run(async()=>
        {
            try
            {
                Volatile.Read(ref coordinator)?.PauseInput();Volatile.Read(ref source)?.Dispose();
                await gate.WaitAsync().ConfigureAwait(false);
                try{replayWanted=false;recordingWanted=false;ReleaseResources();}finally{gate.Release();}
            }
            catch(Exception){/* Coordinator disposal preserves unfinished journals. */}
        });
    }
}
