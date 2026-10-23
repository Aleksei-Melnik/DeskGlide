using System.Collections.Concurrent;
using System.Text.Json;

namespace SdrCapture;

// UI-thread orchestration. The paired recording receiver owns its files and
// encoders; the gaming PC supplies one SDR capture and explicit session commands.
sealed partial class TrayApp
{
    readonly ReplayHotkey recordingHotkey=new();
    readonly CancellationTokenSource recordingLifetime=new();
    readonly SemaphoreSlim recordingCommands=new(1,1);
    readonly ConcurrentDictionary<string,RecordingNodeState> recordingStates=new();
    readonly ConcurrentDictionary<string,string> recordingProblems=new();
    readonly Dictionary<string,string> retiringReplays=[];
    RecordingRemote? recordingRemote;
    RecordingNode? recordingNode;
    RecordingPerformanceMonitor? recordingPerformance;
    string? remoteReplayPeer,remoteReplayProfile;
    string remoteReplaySession=Guid.NewGuid().ToString("N");
    string? remoteRecordingPeer,remoteRecordingSession;
    ReplayOptions? remoteRecordingProfile;
    ReplayOptions? localRecordingProfile;
    long remoteRecordingGeneration;
    bool recordingPolling,recordingReconciling,recordingBusy;
    string recordingStatus="Recording is stopped.";
    string? recordingError;
    ReplayOptions LocalReplayProfile()=>settings.Replay with{Enabled=settings.Replay.Enabled&&settings.Replay.EncodingComputer.Length==0};
    bool PeerConnected(string peer)=>kvm?.Peers.Any(p=>p.Id==peer)==true;
    RecordingNodeState? ReplayRemoteState=>recordingStates.GetValueOrDefault(settings.Replay.EncodingComputer);
    double ReplayBufferedSeconds=>settings.Replay.EncodingComputer.Length==0?replay.DisplayBufferedSeconds:PeerConnected(settings.Replay.EncodingComputer)?ReplayRemoteState?.BufferedSeconds??0:0;
    string? ReplayProblem=>settings.Replay.EncodingComputer.Length==0?replay.Error:!PeerConnected(settings.Replay.EncodingComputer)?"The encoding PC is disconnected.":recordingProblems.GetValueOrDefault(settings.Replay.EncodingComputer)??ReplayRemoteState?.Error;
    bool ReplayAdvancing=>settings.Replay.EncodingComputer.Length==0?replay.BufferAdvancing:settings.Replay.Enabled&&PeerConnected(settings.Replay.EncodingComputer)&&ReplayRemoteState is {ReplayEnabled:true,InputFps:>0,Error:null};
    bool RecordingRunning=>recordings.HasRecording||remoteRecordingSession!=null;

    void InitializeRecording()
    {
        recordingHotkey.SaveRequested+=ToggleRecording;
        if(!RecordingRouting.DistinctHotkeys(settings)||!recordingHotkey.Set(settings.Recording.Profile.HotkeyModifiers,settings.Recording.Profile.HotkeyKey))
            notifications.Enqueue("Горячая клавиша записи занята. Выберите другое сочетание в настройках записи видео.");
        recordingPerformance=new(()=>recordings.NeedsFrames||engine.NdiEnabled||recordingNode?.Snapshot is {ReplayEnabled:true} or {Recording:true});
        _=Task.Run(RecoverRecordings);
    }
    void RegisterRecordingHotkeys(Settings next)
    {
        next.Recording.Validate();
        if(!RecordingRouting.DistinctHotkeys(next))throw new ArgumentException("Для записи видео и сохранения повтора нужны разные сочетания клавиш.");
        hotkey.Clear();recordingHotkey.Clear();
        if(hotkey.Set(next.Replay.HotkeyModifiers,next.Replay.HotkeyKey)&&recordingHotkey.Set(next.Recording.Profile.HotkeyModifiers,next.Recording.Profile.HotkeyKey))return;
        hotkey.Clear();recordingHotkey.Clear();
        hotkey.Set(settings.Replay.HotkeyModifiers,settings.Replay.HotkeyKey);recordingHotkey.Set(settings.Recording.Profile.HotkeyModifiers,settings.Recording.Profile.HotkeyKey);
        screenshotHotkey.Set(settings.Screenshots.Enabled);
        throw new InvalidOperationException("Сочетание клавиш записи или повтора занято другой программой.");
    }
    void BindRecordingRemote(KvmService service)
    {
        recordingNode??=new(()=>settings.Recording.Copy(),()=>settings.Replay with{},_=>delivery.Wake());
        recordingRemote=new(service,recordingNode.Handle);
        // Re-send the desired replay profile after rebuilding the control channel.
        remoteReplayProfile=null;recordingStates.Clear();recordingProblems.Clear();
    }
    void UpdateRecordingCaptureDemand()
    {
        bool remoteReplay=settings.Replay.Enabled&&settings.Replay.EncodingComputer.Length>0&&PeerConnected(settings.Replay.EncodingComputer);
        bool remoteRecord=remoteRecordingPeer!=null&&remoteRecordingSession!=null&&PeerConnected(remoteRecordingPeer);
        var demand=RecordingRouting.CaptureDemand(recordings.NeedsFrames,recordings.CaptureFps,settings.SendOnLaunch,
            remoteReplay,settings.Replay.Fps,remoteRecord,remoteRecordingProfile?.Fps??60);
        engine.ReplayEnabled=demand.Capture;engine.ReplayFrameRate=demand.CaptureFps;
        engine.NdiEnabled=demand.Ndi;engine.NdiFrameRate=demand.NdiFps;
        engine.NdiAudioVolume=settings.NdiAudioVolume;
        // Remote recording uses the existing NDI audio stream. With no explicit
        // streaming source, use the system playback mix; the microphone stays local.
        engine.NdiAudioDevice=settings.NdiAudioDevice.Length>0?settings.NdiAudioDevice:remoteReplay||remoteRecord?"default:render":"";
    }
    void RecordingTick()
    {
        if(closing)return;UpdateRecordingCaptureDemand();
        if(!recordingPolling)_=PollRecordingNodes();
        if(!recordingReconciling)_=ReconcileRemoteReplay();
        if(recordings.HasRecording)recordingStatus=recordings.Status;
    }
    async Task PollRecordingNodes()
    {
        recordingPolling=true;
        try
        {
            var targets=new[]{remoteReplayPeer,remoteRecordingPeer,settings.Replay.Enabled?settings.Replay.EncodingComputer:null,settings.Recording.Computer}
                .Where(p=>!string.IsNullOrEmpty(p)).Cast<string>().Distinct().ToArray();
            // Heartbeats bypass recordingCommands. Saving a long clip must not
            // expire the receiver's control lease while FFmpeg is exporting it.
            await Task.WhenAll(targets.Select(async peer=>
            {
                var remote=recordingRemote;
                if(remote==null||!PeerConnected(peer)){recordingStates.TryRemove(peer,out _);return;}
                long generation=remoteRecordingGeneration;
                string? observedSession=remoteRecordingSession;
                string? lease=peer==remoteRecordingPeer?remoteRecordingSession:peer==remoteReplayPeer?remoteReplaySession:null;
                var reply=await remote.ExecuteAsync(peer,Command("status",lease),recordingLifetime.Token);
                if(closing)return;
                if(reply.Ok&&reply.State!=null)
                {
                    if(!RecordingRouting.CurrentPoll(generation,remoteRecordingGeneration,observedSession,remoteRecordingSession))return;
                    recordingStates[peer]=reply.State;recordingProblems.TryRemove(peer,out _);
                    if(peer==settings.Recording.Computer&&remoteRecordingSession==null&&reply.State.Recording&&reply.State.RecordingSession!=null)
                    {
                        // Reconnect to an existing session using its explicit ID;
                        // never toggle or start a second session after a timeout.
                        remoteRecordingPeer=peer;remoteRecordingSession=reply.State.RecordingSession;remoteRecordingProfile=settings.Recording.Profile with{Fps=reply.State.RequiredFps};remoteRecordingGeneration++;
                    }
                    if(peer==remoteRecordingPeer&&!recordingBusy&&remoteRecordingSession!=null&&!reply.State.Recording)
                    {
                        recordingStatus=reply.State.LastSaved==null?"The remote recording ended. Check recovery on the encoding PC.":"Remote recording saved: "+reply.State.LastSaved;
                        notifications.Enqueue(recordingStatus);remoteRecordingPeer=null;remoteRecordingSession=null;remoteRecordingProfile=null;remoteRecordingGeneration++;
                    }
                    else if(peer==remoteRecordingPeer)recordingStatus=reply.State.Error??"Recording on the selected encoding PC.";
                }
                else recordingProblems[peer]=reply.Message;
            }));
        }
        catch(OperationCanceledException){}
        catch(Exception e){Log.Write("Recording status: "+e.Message);}
        finally{recordingPolling=false;if(!closing)UpdateRecordingCaptureDemand();}
    }
    static RecordingCommand Command(string command,string? session=null,string? source=null,ReplayOptions? profile=null,string? app=null)=>
        new(Guid.NewGuid().ToString("N"),command,session,source,profile==null?null:RecordingProfile.FromReplay(profile),app);
    async Task<RecordingResponse> RemoteCommand(string peer,RecordingCommand command)
    {
        var remote=recordingRemote??throw new IOException("Pair and connect the encoding PC in KVM first.");
        if(!PeerConnected(peer))throw new RecordingCommandFailure("offline","The selected encoding PC is disconnected.");
        var reply=await remote.ExecuteAsync(peer,command,recordingLifetime.Token);
        if(reply.State!=null)recordingStates[peer]=reply.State;
        if(!reply.Ok){recordingProblems[peer]=reply.Message;throw new RecordingCommandFailure(reply.Code,reply.Message,reply.State);}
        recordingProblems.TryRemove(peer,out _);return reply;
    }
    async Task<string> RecordingSource()
    {
        UpdateRecordingCaptureDemand();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(recordingLifetime.Token);timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while(engine.SourceName=="SdrCapture SDR")await Task.Delay(100,timeout.Token);
        return engine.SourceName;
    }
    async Task ReconcileRemoteReplay()
    {
        recordingReconciling=true;
        bool entered=false;
        try
        {
            await recordingCommands.WaitAsync(recordingLifetime.Token);entered=true;
            await ReconcileRemoteReplayCore();
        }
        catch(OperationCanceledException){}
        catch(Exception e){Log.Write("Remote replay: "+e.Message);}
        finally{if(entered)recordingCommands.Release();recordingReconciling=false;}
    }
    async Task ReconcileRemoteReplayCore()
    {
        string desired=settings.Replay.Enabled?settings.Replay.EncodingComputer:"";
        if(remoteReplayPeer!=null&&remoteReplayPeer!=desired)
        {
            string old=remoteReplayPeer;remoteReplayPeer=null;remoteReplayProfile=null;
            retiringReplays[old]=remoteReplaySession;
            remoteReplaySession=Guid.NewGuid().ToString("N");
        }
        foreach(var retired in retiringReplays.ToArray())
        {
            if(!PeerConnected(retired.Key))continue;
            try{await RemoteCommand(retired.Key,Command("disableReplay",retired.Value));retiringReplays.Remove(retired.Key);}
            catch(RecordingCommandFailure e)when(e.Code=="session"||e.State is {ReplayEnabled:false}){retiringReplays.Remove(retired.Key);}
            catch(Exception e){Log.Write("Remote replay retirement will retry: "+e.Message);}
        }
        if(desired.Length==0||!PeerConnected(desired))return;
        var profile=settings.Replay with{};
        string fingerprint=JsonSerializer.Serialize(RecordingProfile.FromReplay(profile))+"|"+settings.Device;
        if(remoteReplayPeer==desired&&remoteReplayProfile==fingerprint&&recordingStates.GetValueOrDefault(desired)?.ReplayEnabled==true)return;
        remoteReplayPeer=desired;
        string source=await RecordingSource();
        // A settings change during runtime setup will be reconciled on the next tick.
        if(!settings.Replay.Enabled||settings.Replay.EncodingComputer!=desired)return;
        await RemoteCommand(desired,Command("configureReplay",remoteReplaySession,source,profile));
        remoteReplayProfile=fingerprint;
    }
    async Task SaveRemoteReplay()
    {
        await recordingCommands.WaitAsync(recordingLifetime.Token);
        try
        {
            await ReconcileRemoteReplayCore();
            string peer=remoteReplayPeer??throw new IOException("No remote replay session is available.");
            string app=ReplayAppContext.ForInterval(ReplayRecorder.Now-settings.Replay.BufferSeconds,ReplayRecorder.Now);
            var reply=await RemoteCommand(peer,Command("saveReplay",remoteReplaySession,app:ReplayAppContext.SafeName(app)));
            notifications.Enqueue("Повтор сохранён на ПК кодирования: "+reply.Message);
        }
        finally{recordingCommands.Release();}
    }
    async void ToggleRecording()
    {
        try{await ToggleRecordingAsync();}
        catch(Exception e){notifications.Enqueue("Запись видео: "+e.Message);Log.Write("Recording: "+e);}
    }
    async Task ToggleRecordingAsync()
    {
        if(closing||recordingBusy||updater.Busy)throw new InvalidOperationException("A recording operation or update is already in progress.");
        recordingBusy=true;recordingError=null;remoteRecordingGeneration++;
        try
        {
            await recordingCommands.WaitAsync(recordingLifetime.Token);
            try
            {
                if(recordings.HasRecording)
                {
                    var result=await recordings.StopRecordingAsync(true);delivery.Wake();localRecordingProfile=null;
                    recordingStatus=result.LocalPath.Length>0?"Recording saved: "+Path.GetFileName(result.LocalPath):result.Message;
                }
                else if(remoteRecordingPeer!=null&&remoteRecordingSession!=null)
                {
                    var reply=await RemoteCommand(remoteRecordingPeer,Command("stopRecording",remoteRecordingSession));
                    recordingStatus="Remote recording saved: "+reply.Message;remoteRecordingPeer=null;remoteRecordingSession=null;remoteRecordingProfile=null;
                }
                else
                {
                    var options=settings.Recording.Copy();options.Validate();ReplayAppContext.Capture();
                    string app=ReplayAppContext.SafeName(ReplayAppContext.ForInterval(ReplayRecorder.Now-5,ReplayRecorder.Now));
                    if(options.Computer.Length==0)
                    {
                        await RecordingTools.EnsureAsync(message=>notifications.Enqueue(message),recordingLifetime.Token);
                        var display=await Task.Run(()=>DisplayInfo.All().FirstOrDefault(d=>d.Device==settings.Device),recordingLifetime.Token)??throw new IOException("The selected capture monitor is disconnected.");
                        var profile=options.Profile with{Enabled=true,EncodingComputer=""};
                        await RecordingEncoders.SelectAsync(profile,profile.Width==0?display.Width:profile.Width,profile.Height==0?display.Height:profile.Height,recordingLifetime.Token,display.Width,display.Height);
                        recordings.StartRecording(profile,app);localRecordingProfile=profile;recordingStatus="Recording on this PC.";
                    }
                    else
                    {
                        if(!PeerConnected(options.Computer))throw new IOException("The selected encoding PC is disconnected.");
                        remoteRecordingPeer=options.Computer;remoteRecordingSession=Guid.NewGuid().ToString("N");remoteRecordingProfile=options.Profile with{};
                        bool attempted=false;
                        try
                        {
                            string source=await RecordingSource();
                            attempted=true;
                            await RemoteCommand(options.Computer,Command("startRecording",remoteRecordingSession,source,options.Profile,app));
                            recordingStatus="Recording on the selected encoding PC.";
                        }
                        catch(Exception e)
                        {
                            // Preserve the session until a status check determines
                            // whether an unconfirmed request actually started.
                            var failure=e as RecordingCommandFailure;
                            bool keep=attempted&&RecordingRouting.PreserveUnconfirmedStart(failure?.Code??"unknown",failure?.State,remoteRecordingSession!);
                            if(!keep){remoteRecordingPeer=null;remoteRecordingSession=null;remoteRecordingProfile=null;}
                            recordingStatus=keep?"Start was not confirmed. Checking the encoding PC before another action.":"Recording did not start.";throw;
                        }
                    }
                }
                notifications.Enqueue(UiStrings.T(recordingStatus));
            }
            finally{recordingCommands.Release();}
        }
        catch(Exception e){recordingError=e.Message;throw;}
        finally{remoteRecordingGeneration++;recordingBusy=false;if(!closing){UpdateRecordingCaptureDemand();SelectRemoteAudio();PublishDeckState();}}
    }
    async Task<IReadOnlyList<RecordingCapability>> GetRecordingCapabilities(string peer,ReplayOptions profile,string device,CancellationToken token)
    {
        var display=await Task.Run(()=>DisplayInfo.All().FirstOrDefault(d=>d.Device==device),token)??throw new IOException("The selected capture monitor is disconnected.");
        if(peer.Length==0)return await RecordingCapabilities.DiscoverAsync(profile,display.Width,display.Height,token);
        var remote=recordingRemote??throw new IOException("Connect the selected encoding PC first.");
        var request=Command("capabilities",profile:profile);
        request=request with{Profile=request.Profile! with{SourceWidth=display.Width,SourceHeight=display.Height}};
        var reply=await remote.ExecuteAsync(peer,request,token);
        if(!reply.Ok)throw new IOException(reply.Message);return reply.Capabilities??[];
    }
    async Task RecoverRecordings()
    {
        try
        {
            foreach(string journal in RecordingRecovery.Pending())
            {
                if(recordingLifetime.IsCancellationRequested)break;
                try{await RecordingRecovery.RecoverAsync(journal,recordingLifetime.Token);delivery.Wake();notifications.Enqueue("Незавершённая запись восстановлена.");}
                catch(OperationCanceledException){break;}
                catch(Exception e){Log.Write("Recording recovery retained journal: "+e.Message);}
            }
        }
        catch(Exception e){Log.Write("Recording recovery scan: "+e.Message);}
    }
    string RecordingDiagnostics()
    {
        var local=recordings.MetricsRecorder;
        return $"\r\nRecording: {recordingStatus}\r\n{recordings.Error}\r\nLocal encoder: {local.Status}\r\nConversion drops: {local.ConversionQueueDrops}; history misses: {local.HistoryMisses}; repeated frames: {local.RepeatedInputFrames}\r\nMax conversion: {local.MaxConvertMs:F1} ms; frame age: {local.MaxFramePreparationAgeMs:F1} ms; encoder write: {local.MaxPipeWriteMs:F1} ms\r\nReceiver: {recordingNode?.Snapshot}\r\n{string.Join("\r\n",recordingStates.Select(p=>$"Peer {p.Key}: {p.Value}"))}\r\n{recordingPerformance?.Summary}";
    }
    async Task FinishRecordingBeforeUpdate()
    {
        if(recordingBusy||savingReplay)throw new IOException("Дождитесь завершения сохранения записи перед обновлением.");
        recordingBusy=true;
        try
        {
            if(recordings.HasRecording){await recordings.StopRecordingAsync(true);localRecordingProfile=null;delivery.Wake();}
            if(remoteRecordingPeer!=null&&remoteRecordingSession!=null)
            {
                await RemoteCommand(remoteRecordingPeer,Command("stopRecording",remoteRecordingSession));
                remoteRecordingPeer=null;remoteRecordingSession=null;remoteRecordingProfile=null;
            }
        }
        finally{remoteRecordingGeneration++;recordingBusy=false;}
    }
    void CloseRecording()
    {
        recordingLifetime.Cancel();recordingHotkey.Dispose();recordingPerformance?.Dispose();recordingRemote?.Dispose();recordingNode?.Dispose();
        // Encoder disposal seals active journals. Startup recovery exports closed
        // segments rather than losing a recording when Windows terminates us.
    }
}

static class RecordingRouting
{
    internal static bool PreserveUnconfirmedStart(string code,RecordingNodeState? state,string session)=>code=="unknown"||state is {Recording:true}&&state.RecordingSession==session;
    internal static bool CurrentPoll(long sentGeneration,long currentGeneration,string? sentSession,string? currentSession)=>sentGeneration==currentGeneration&&sentSession==currentSession;
    internal static bool DistinctHotkeys(Settings settings)=>settings.Replay.HotkeyModifiers!=settings.Recording.Profile.HotkeyModifiers||settings.Replay.HotkeyKey!=settings.Recording.Profile.HotkeyKey;
    internal static (bool Capture,int CaptureFps,bool Ndi,int NdiFps) CaptureDemand(bool local,int localFps,bool streaming,bool remoteReplay,int replayFps,bool remoteRecording,int recordingFps)
    {
        int remoteFps=Math.Max(remoteReplay?replayFps:1,remoteRecording?recordingFps:1);
        return(local||remoteReplay||remoteRecording,Math.Max(local?localFps:1,remoteFps),streaming||remoteReplay||remoteRecording,Math.Max(60,remoteFps));
    }
}
sealed class RecordingCommandFailure(string code,string message,RecordingNodeState? state=null):IOException(message)
{
    internal string Code=>code;
    internal RecordingNodeState? State=>state;
}
