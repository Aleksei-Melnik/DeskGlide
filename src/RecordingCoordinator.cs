namespace SdrCapture;

// Owns encoders, never a desktop duplication. Equal replay/recording profiles
// share one segment stream; a distinct profile can use a second encoder fed
// by the same immutable source frames.
sealed class RecordingCoordinator:IDisposable
{
    sealed class Session(ReplayRecorder recorder,ContinuousRecordingLease lease,ReplayOptions profile)
    {internal readonly ReplayRecorder Recorder=recorder;internal readonly ContinuousRecordingLease Lease=lease;internal readonly ReplayOptions Profile=profile;internal bool Stopping;}
    readonly object gate=new();
    readonly bool syntheticAudio;readonly RecordingEncoder? validationEncoder;readonly int? testWindowSeconds;
    ReplayRecorder replay;ReplayOptions desiredReplay;Session? active;bool paused,disposed;
    internal RecordingCoordinator(ReplayOptions initial,bool syntheticAudio=false,RecordingEncoder? validationEncoder=null,int? testWindowSeconds=null)
    {
        initial.Validate();this.syntheticAudio=syntheticAudio;this.validationEncoder=validationEncoder;this.testWindowSeconds=testWindowSeconds;
        desiredReplay=initial with{};replay=Create(initial);
    }
    ReplayRecorder Create(ReplayOptions profile)=>new(profile,syntheticAudio,testWindowSeconds,validationEncoder);
    internal ReplayRecorder Replay{get{lock(gate)return replay;}}
    internal ReplayRecorder MetricsRecorder{get{lock(gate)return active?.Recorder??replay;}}
    internal bool HasRecording{get{lock(gate)return active!=null;}}
    internal bool IsRecording{get{lock(gate)return active!=null&&active.Lease.End==double.MaxValue&&!paused;}}
    internal string RecordingId{get{lock(gate)return active?.Lease.Id??"";}}
    internal bool NeedsFrames{get{lock(gate)return !disposed&&!paused&&(desiredReplay.Enabled||active?.Lease.End==double.MaxValue);}}
    internal int CaptureFps{get{lock(gate)return Math.Max(desiredReplay.Enabled?desiredReplay.Fps:1,active?.Lease.End==double.MaxValue?active.Profile.Fps:1);}}
    internal string Status{get{lock(gate)return active==null?"Recording is stopped.":active.Stopping?"Finalizing recording…":paused?"Waiting for video source…":active.Lease.End!=double.MaxValue?"Recording export failed; retry Stop or recover the journal.":active.Recorder.Status;}}
    internal string? Error{get{lock(gate)return active?.Recorder.Error??replay.Error;}}
    internal void Offer(VideoBuffer frame)
    {
        ReplayRecorder primary;ReplayRecorder? other;
        lock(gate){if(disposed||paused)return;primary=replay;other=active?.Lease.End==double.MaxValue&&active.Recorder!=replay?active.Recorder:null;}
        primary.Offer(frame);other?.Offer(frame);
    }
    internal void UpdateReplay(ReplayOptions profile)
    {
        profile.Validate();
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(active?.Recorder==replay&&!RecordingCapabilities.EncodingEquivalent(active.Profile,profile))
            {
                // The old encoder continues the recording unchanged. A new
                // replay profile owns its own encoder instead of cutting it.
                replay=Create(profile with{Enabled=!paused&&profile.Enabled});
            }
            else replay.Update(profile with{Enabled=!paused&&(profile.Enabled||active?.Recorder==replay&&active.Lease.End==double.MaxValue)});
            desiredReplay=profile with{};
        }
    }
    internal void StartRecording(ReplayOptions profile,string app)
    {
        profile.Validate();
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(active!=null)throw new InvalidOperationException("A recording is already running or waiting for export.");
            bool share=desiredReplay.Enabled&&RecordingCapabilities.EncodingEquivalent(desiredReplay,profile);
            var recorder=share?replay:Create(profile with{Enabled=true});
            try
            {
                if(share&&paused)recorder.Update(profile with{Enabled=true});
                var lease=recorder.BeginRecording(profile,app);active=new(recorder,lease,profile with{Enabled=true});
                if(paused)recorder.Update(profile with{Enabled=false});
            }
            catch{if(!share)recorder.Dispose();throw;}
        }
    }
    internal async Task<ReplayResult> StopRecordingAsync(bool allowEmpty=false)
    {
        Session session;lock(gate){session=active??throw new InvalidOperationException("No recording is running.");if(session.Stopping)throw new InvalidOperationException("The recording is already being finalized.");session.Stopping=true;}
        try
        {
            var result=await session.Recorder.StopRecordingAsync(session.Lease,allowEmpty).ConfigureAwait(false);
            lock(gate)
            {
                if(active==session)active=null;
                if(session.Recorder==replay)replay.Update(desiredReplay with{Enabled=!paused&&desiredReplay.Enabled});
            }
            if(session.Recorder!=Replay)session.Recorder.Dispose();return result;
        }
        catch
        {
            lock(gate){session.Stopping=false;if(session.Recorder!=replay)session.Recorder.Update(session.Profile with{Enabled=false});else replay.Update(desiredReplay with{Enabled=!paused&&desiredReplay.Enabled});}
            throw;
        }
    }
    internal void PauseInput()
    {
        lock(gate)
        {
            if(disposed)return;paused=true;replay.Update(desiredReplay with{Enabled=false});
            if(active?.Recorder!=null&&active.Recorder!=replay)active.Recorder.Update(active.Profile with{Enabled=false});
        }
    }
    internal void ResumeInput()
    {
        lock(gate)
        {
            if(disposed)return;paused=false;replay.Update(desiredReplay with{Enabled=desiredReplay.Enabled||active?.Recorder==replay&&active.Lease.End==double.MaxValue});
            if(active?.Recorder!=null&&active.Recorder!=replay&&active.Lease.End==double.MaxValue)active.Recorder.Update(active.Profile with{Enabled=true});
        }
    }
    public void Dispose()
    {
        ReplayRecorder[] owned;
        lock(gate)
        {
            if(disposed)return;disposed=true;
            if(active!=null)active.Recorder.SealRecording(active.Lease);
            owned=active!=null&&active.Recorder!=replay?[replay,active.Recorder]:[replay];
        }
        foreach(var recorder in owned)recorder.Dispose();
    }
}
