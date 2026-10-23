using System.Text.Json;
using System.Text.RegularExpressions;
namespace SdrCapture;

sealed record RecordingOptions
{
    public string Computer {get;set;}="";
    public bool AllowRemoteEncoding {get;set;}
    public ReplayOptions Profile {get;set;}=new(){Enabled=true,HotkeyKey=(uint)Keys.F9,Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"DeskGlide","Recordings")};
    public void Validate()
    {
        if(Computer==null||Computer.Length>0&&!Guid.TryParseExact(Computer,"N",out _))throw new ArgumentException("Choose a paired encoding computer.");
        if(Profile==null)throw new ArgumentException("Recording profile is missing.");Profile.Validate();
        if(Profile.HotkeyModifiers is 0 or >7||Profile.HotkeyKey<(uint)Keys.F1||Profile.HotkeyKey>(uint)Keys.F12)throw new ArgumentException("Choose a recording shortcut with Ctrl, Alt or Shift and F1–F12.");
    }
    public RecordingOptions Copy()=>this with{Profile=Profile with{}};
}

sealed class ContinuousRecordingLease
{
    internal readonly string Id=Guid.NewGuid().ToString("N");
    internal readonly double Start=ReplayRecorder.Now;
    internal double End=double.MaxValue;
    internal readonly ReplayOptions Options;
    internal readonly string App,Journal;
    internal readonly List<ReplayChunk> Chunks=[];
    internal bool Finishing,Completed;
    internal ContinuousRecordingLease(ReplayOptions options,string app,string folder)
    {Options=options with{};App=ReplayAppContext.SafeName(app);Journal=Path.Combine(folder,"recording-"+Id+".json");}
}
sealed record RecordingJournal(int Format,string Id,double Start,double? End,ReplayOptions Options,string App,RecordingJournalChunk[] Chunks);
sealed record RecordingJournalChunk(string File,double Start,double End,long Bytes,string Codec,int Fps,int Width,int Height);

public sealed partial class ReplayRecorder
{
    readonly FileStream runOwnership;
    readonly List<ContinuousRecordingLease> recordings=[];
    internal ContinuousRecordingLease BeginRecording(ReplayOptions destination,string app)
    {
        destination.Validate();ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        lock(chunksGate)
        {
            if(!Volatile.Read(ref options).Enabled)throw new InvalidOperationException("The recording encoder is disabled.");
            var recording=new ContinuousRecordingLease(destination,app,runFolder);recordings.Add(recording);
            try{WriteJournal(recording);}catch{recordings.Remove(recording);throw;}
            return recording;
        }
    }
    void AddChunk(ReplayChunk chunk)
    {
        lock(chunksGate)
        {
            // Scan retries a CSV entry after a transient journal failure. Reuse
            // its committed object so retries cannot duplicate frames or pins.
            var existing=chunks.FirstOrDefault(c=>c.Path==chunk.Path);
            if(existing==null)chunks.Add(chunk);else chunk=existing;
            foreach(var recording in recordings.Where(r=>!r.Completed&&chunk.End>r.Start&&chunk.Start<r.End))
            {
                if(!recording.Chunks.Contains(chunk)){chunk.Pins++;recording.Chunks.Add(chunk);}
                WriteJournal(recording);
            }
        }
    }
    void WriteJournal(ContinuousRecordingLease recording)
    {
        var journal=new RecordingJournal(1,recording.Id,recording.Start,recording.End==double.MaxValue?null:recording.End,recording.Options,recording.App,
            recording.Chunks.Select(c=>new RecordingJournalChunk(Path.GetRelativePath(runFolder,c.Path),c.Start,c.End,c.Bytes,c.Codec,c.Fps,c.Width,c.Height)).ToArray());
        string temp=recording.Journal+".tmp";
        try{File.WriteAllBytes(temp,JsonSerializer.SerializeToUtf8Bytes(journal));File.Move(temp,recording.Journal,true);}
        finally{TryDelete(temp);}
    }
    internal async Task<ReplayResult> StopRecordingAsync(ContinuousRecordingLease recording,bool allowEmpty=false)
    {
        lock(chunksGate)
        {
            if(!recordings.Contains(recording)||recording.Completed)throw new InvalidOperationException("This recording session has already ended.");
            if(recording.Finishing)throw new InvalidOperationException("The recording is already being finalized.");
            recording.Finishing=true;if(recording.End==double.MaxValue)recording.End=Now;
        }
        try
        {
            lock(chunksGate)WriteJournal(recording);
            double deadline=Now+Math.Max(5,recording.Options.KeyframeSeconds+3);
            while(Now<deadline&&!worker.IsCompleted)
            {
                lock(chunksGate)if(recording.Chunks.Count>0&&recording.Chunks[^1].End>=recording.End)break;
                if(!Volatile.Read(ref options).Enabled&&Volatile.Read(ref liveEnd)==0)break;
                await Task.Delay(50).ConfigureAwait(false);
            }
            ReplayChunk[] selected;lock(chunksGate)selected=recording.Chunks.ToArray();
            if(selected.Length==0&&allowEmpty)
            {
                lock(chunksGate){recording.Completed=true;recordings.Remove(recording);TryDelete(recording.Journal);}
                return new("","",0,"Recording stopped before a complete video segment was available.");
            }
            var exported=await ReplayClipExporter.ExportAsync(selected,recording.Options,recording.Start,recording.End,recording.App,"Recording").ConfigureAwait(false);
            lock(chunksGate)
            {
                recording.Completed=true;recordings.Remove(recording);foreach(var chunk in recording.Chunks)chunk.Pins--;TryDelete(recording.Journal);
            }
            return exported.Result;
        }
        catch{lock(chunksGate){recording.Finishing=false;WriteJournal(recording);}throw;}
    }
    internal void SealRecording(ContinuousRecordingLease recording)
    {lock(chunksGate){if(!recording.Completed){if(recording.End==double.MaxValue)recording.End=Now;WriteJournal(recording);}}}
    internal string RecordingJournalPath(ContinuousRecordingLease recording)=>recording.Journal;
}

static class RecordingRecovery
{
    internal static IEnumerable<string> Pending()
    {
        string root=Path.Combine(ReplayTools.Root,"buffer");if(!Directory.Exists(root))return [];
        return Directory.GetDirectories(root).Where(p=>Guid.TryParseExact(Path.GetFileName(p),"N",out _)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0)
            .SelectMany(p=>Directory.GetFiles(p,"recording-*.json")).ToArray();
    }
    internal static async Task<ReplayResult> RecoverAsync(string journalPath,CancellationToken token=default)
    {
        string folder=Path.GetFullPath(Path.GetDirectoryName(journalPath)!);
        string root=Path.GetFullPath(Path.Combine(ReplayTools.Root,"buffer"))+Path.DirectorySeparatorChar;
        if(!folder.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(folder),"N",out _)||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0||
            (File.GetAttributes(journalPath)&FileAttributes.ReparsePoint)!=0||new FileInfo(journalPath).Length>32*1024*1024)throw new IOException("Invalid recording recovery journal.");
        using var ownership=new FileStream(Path.Combine(folder,"active.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var journal=JsonSerializer.Deserialize<RecordingJournal>(await File.ReadAllTextAsync(journalPath,token).ConfigureAwait(false))??throw new IOException("Empty recording journal.");
        if(journal.Format!=1||!Guid.TryParseExact(journal.Id,"N",out _)||Path.GetFileName(journalPath)!="recording-"+journal.Id+".json"||!double.IsFinite(journal.Start)||journal.Options==null||journal.Chunks==null||journal.Chunks.Length>100000)throw new IOException("Invalid recording journal fields.");
        journal.Options.Validate();var chunks=new List<ReplayChunk>();double last=double.MinValue;
        foreach(var part in journal.Chunks)
        {
            token.ThrowIfCancellationRequested();
            if(!Regex.IsMatch(part.File,@"^[a-fA-F0-9]{32}[\\/]seg-\d+\.mkv$")||!double.IsFinite(part.Start)||!double.IsFinite(part.End)||part.End<=part.Start||part.Start<last||part.Fps is not(60 or 120)||part.Codec is not("H264" or "HEVC" or "AV1"))throw new IOException("Invalid recording segment journal.");
            string path=Path.GetFullPath(Path.Combine(folder,part.File));string directory=Path.GetDirectoryName(path)!;
            if(!path.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0||
                !File.Exists(path)||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||new FileInfo(path).Length!=part.Bytes)throw new IOException("A recording segment is missing or changed; recovery data was retained.");
            chunks.Add(new(path,part.Start,part.End,part.Bytes,0,part.Codec,part.Fps,part.Width,part.Height));last=part.Start;
        }
        double end=journal.End??chunks.LastOrDefault()?.End??journal.Start;
        if(!double.IsFinite(end)||end<journal.Start)throw new IOException("Invalid recording end time.");
        var exported=await ReplayClipExporter.ExportAsync(chunks.ToArray(),journal.Options,journal.Start,end,journal.App,"Recovered-recording").ConfigureAwait(false);
        File.Delete(journalPath);return exported.Result;
    }
}
