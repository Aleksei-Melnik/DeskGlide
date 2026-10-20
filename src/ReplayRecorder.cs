using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdrCapture;

sealed class ReplayChunk(string path,double start,double end,long bytes,int generation,string codec,int fps)
{
    public string Path=path;
    public double Start=start,End=end;
    public long Bytes=bytes;
    public int Pins;
    public int Generation=generation;
    public string Codec=codec;
    public int Fps=fps;
}
public sealed record ReplayResult(string LocalPath,string Destination,double Seconds,string Message);

public sealed class ReplayRecorder:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly object chunksGate=new();
    readonly List<ReplayChunk> chunks=[];
    readonly SemaphoreSlim saveGate=new(1,1);
    readonly Task worker;
    readonly RecordingFrameQueue incoming;
    readonly string runFolder;
    readonly bool syntheticAudio;
    readonly int? testWindowSeconds;
    readonly RecordingEncoder? validationEncoder;
    ReplayOptions options;
    readonly object framesGate=new();
    readonly List<RecordingFrame> frames=[];
    readonly RecordingFramePool framePool=new();
    ReplayAudio? audio;
    int version,disposed;
    long warmupUntil;
    double savedUntil;
    double liveStart,liveEnd,liveUpdated;
    public string Status {get;private set;}="Подготовка буфера";
    public string? Error {get;private set;}
    public string AudioStatus=>audio?.Status??"Звук не запущен";
    public string AudioDiagnostics=>audio?.Diagnostics??"";
    public long AudioMissingSamples=>audio?.MissingSamples??0;
    public long AudioReadSamples=>audio?.ReadSamples??0;
    public int Width {get;private set;}
    public int Height {get;private set;}
    public long EncodedFrames {get;private set;}
    public long RepeatedInputFrames {get;private set;}
    public long HistoryMisses {get;private set;}
    public double MaxWriterLateMs {get;private set;}
    public double MaxConvertMs {get;private set;}
    public double MaxFramePreparationAgeMs {get;private set;}
    public long SessionEpochTicks {get;private set;}
    public long AudioReadyTicks {get;private set;}
    long duplicateSourceFrames;
    public long DuplicateSourceFrames=>Interlocked.Read(ref duplicateSourceFrames);
    internal int SourceHistoryCount {get{lock(framesGate)return frames.Count;}}
    internal Action<long>? FramePreparedForTest;
    internal Action<long,long>? FrameSelectedForTest;
    internal RecordingFrame? SelectForTest(long due,long previous,long tolerance)=>GetFrame(due,previous,tolerance);
    internal bool SynchronousFrameConversionForTest;
    public long ConversionQueueDrops=>incoming.Dropped;
    public long ConversionQueueBytes=>incoming.QueuedBytes;
    public double MaxPipeWriteMs {get;private set;}
    public long FrameMemoryBytes {get{lock(framesGate)return frames.Sum(f=>(long)f.Data.Length)+framePool.CachedBytes+incoming.QueuedBytes;}}
    public int EncoderStarts {get;private set;}
    public double BufferedSeconds {get{lock(chunksGate)return Math.Min(WindowSeconds,chunks.Sum(c=>Math.Max(0,c.End-Math.Max(c.Start,savedUntil))));}}
    // Display the written part of the open segment too; closed segments arrive
    // every two seconds. Export selection still uses only completed segments.
    public double DisplayBufferedSeconds
    {
        get{lock(chunksGate){double tail=Math.Max(0,Volatile.Read(ref liveEnd)-Math.Max(Math.Max(Volatile.Read(ref liveStart),savedUntil),chunks.Count>0?chunks[^1].End:0));return Math.Min(WindowSeconds,chunks.Sum(c=>Math.Max(0,c.End-Math.Max(c.Start,savedUntil)))+tail);}}
    }
    public bool BufferAdvancing=>Volatile.Read(ref options).Enabled&&Error==null&&Volatile.Read(ref liveEnd)>0&&Now-Volatile.Read(ref liveUpdated)<1;
    double WindowSeconds=>testWindowSeconds??Volatile.Read(ref options).BufferSeconds;
    public long CacheBytes {get{lock(chunksGate)return chunks.Sum(c=>c.Bytes);}}
    public static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    public ReplayRecorder(ReplayOptions value,bool syntheticAudio=false,int? testWindowSeconds=null):this(value,syntheticAudio,testWindowSeconds,null){}
    internal ReplayRecorder(ReplayOptions value,bool syntheticAudio,int? testWindowSeconds,RecordingEncoder? validationEncoder,bool startEncoder=true)
    {
        this.validationEncoder=validationEncoder;
        options=value with {};this.syntheticAudio=syntheticAudio;this.testWindowSeconds=testWindowSeconds;
        runFolder=Path.Combine(ReplayTools.Root,"buffer",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runFolder);
        CleanOldBuffers();
        incoming=new(ConvertFrame,e=>{Error=e.Message;Log.Write("Recording frame conversion: "+e.Message);});
        worker=startEncoder?Task.Run(Run):Task.CompletedTask;
    }
    void CleanOldBuffers()
    {
        // Only our GUID-named buffer/session folders, never saved clips or arbitrary paths.
        string bufferRoot=Path.GetFullPath(Path.Combine(ReplayTools.Root,"buffer"))+Path.DirectorySeparatorChar;
        foreach(string old in Directory.GetDirectories(bufferRoot))
        {
            if(old==runFolder||!Guid.TryParseExact(Path.GetFileName(old),"N",out _)||(File.GetAttributes(old)&FileAttributes.ReparsePoint)!=0)continue;
            if(!Path.GetFullPath(old).StartsWith(bufferRoot,StringComparison.OrdinalIgnoreCase))continue;
            try
            {
                foreach(string session in Directory.GetDirectories(old))
                {
                    if(!Guid.TryParseExact(Path.GetFileName(session),"N",out _)||(File.GetAttributes(session)&FileAttributes.ReparsePoint)!=0)continue;
                    foreach(string file in Directory.GetFiles(session))
                        if(Regex.IsMatch(Path.GetFileName(file),@"^(seg-\d+\.mkv|chunks\.csv)$"))TryDelete(file);
                    if(!Directory.EnumerateFileSystemEntries(session).Any())Directory.Delete(session,false);
                }
                if(!Directory.EnumerateFileSystemEntries(old).Any())Directory.Delete(old,false);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    // The frame's pixels are immutable after publication. Retaining its storage
    // allows the producer to dispose its own reference immediately.
    public void Offer(VideoBuffer frame)
    {
        if(Volatile.Read(ref disposed)!=0||!Volatile.Read(ref options).Enabled)return;
        if(SynchronousFrameConversionForTest)ConvertFrame(frame,Volatile.Read(ref version));
        else incoming.Offer(frame,Volatile.Read(ref version));
    }
    void ConvertFrame(VideoBuffer frame,int generation)
    {
        if(Volatile.Read(ref disposed)!=0||!Volatile.Read(ref options).Enabled)return;
        long begin=Stopwatch.GetTimestamp();
        var reference=new RecordingFrame(frame,framePool);
        MaxConvertMs=Math.Max(MaxConvertMs,Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        MaxFramePreparationAgeMs=Math.Max(MaxFramePreparationAgeMs,Stopwatch.GetElapsedTime(reference.CapturedAt).TotalMilliseconds);
        lock(framesGate)
        {
            if(Volatile.Read(ref disposed)!=0||!Volatile.Read(ref options).Enabled||generation!=Volatile.Read(ref version)){reference.Dispose();return;}
            // DWM can publish a new desktop timestamp for unchanged pixels.
            // Such an entry is not a new video image: retaining it lets the
            // CFR selector repeat that image despite a fresh one being ready.
            // Compare exact encoded-input pixels; keep the original image time.
            if(frames.Count>0&&frames[^1].Width==reference.Width&&frames[^1].Height==reference.Height&&frames[^1].Data.SequenceEqual(reference.Data))
            {reference.Dispose();Interlocked.Increment(ref duplicateSourceFrames);return;}
            frames.Add(reference);
            FramePreparedForTest?.Invoke(reference.CapturedAt);
            var config=Volatile.Read(ref options);
            int steady=Math.Max(12,(int)Math.Ceiling((ReplayAudio.WaitSeconds(config)+1.0/60+.50)*config.Fps)+3);
            bool warming=Stopwatch.GetTimestamp()<Volatile.Read(ref warmupUntil);
            int wanted=warming?Math.Max(steady,config.Fps):steady;
            // Encoder initialization can hold the first pipe write for hundreds
            // of milliseconds. Keep its source frames until it catches up.
            int budget=512*1024*1024;
            int limit=Math.Min(wanted,Math.Max(12,budget/Math.Max(1,reference.Data.Length)));
            while(frames.Count>limit){frames[0].Dispose();frames.RemoveAt(0);}
        }
    }
    RecordingFrame? GetFrame(long timestamp=long.MaxValue,long previous=-1,long tolerance=0)
    {
        lock(framesGate)
        {
            if(frames.Count==0)return null;
            Span<long> times=stackalloc long[frames.Count];for(int n=0;n<frames.Count;n++)times[n]=frames[n].CapturedAt;
            if(timestamp<long.MaxValue-tolerance&&times[0]>timestamp+tolerance)HistoryMisses++;
            int i=RecordingFrameSelection.Index(times,timestamp,previous,tolerance,Stopwatch.Frequency/Volatile.Read(ref options).Fps);
            if(i>=0)
            {
                var selected=frames[i].Retain();
                // Once a recording timestamp is consumed, older frames cannot
                // be selected again. Keep the selected image for a slow source.
                if(timestamp!=long.MaxValue&&i>0)
                {for(int old=0;old<i;old++)frames[old].Dispose();frames.RemoveRange(0,i);}
                return selected;
            }
            return null;
        }
    }
    public void Update(ReplayOptions value)
    {
        value.Validate();var old=Volatile.Read(ref options);Volatile.Write(ref options,value with {});
        if(!value.Enabled){incoming.Clear();lock(framesGate){foreach(var frame in frames)frame.Dispose();frames.Clear();}}
        if(old.Enabled!=value.Enabled||old.Quality!=value.Quality||old.Codec!=value.Codec||old.AudioMode!=value.AudioMode||old.Fps!=value.Fps||old.Width!=value.Width||old.Height!=value.Height||old.Microphone!=value.Microphone||old.GameAudio!=value.GameAudio||old.ExtraAudio!=value.ExtraAudio||old.Silent!=value.Silent)Interlocked.Increment(ref version);
    }
    async Task Run()
    {
        int activeVersion=-1;
        while(!stop.IsCancellationRequested)
        {
            try
            {
                var config=Volatile.Read(ref options);int currentVersion=Volatile.Read(ref version);
                if(!config.Enabled){Status="Буфер выключен";await Task.Delay(200,stop.Token);continue;}
                int inputWidth,inputHeight;
                using(var first=GetFrame())
                {
                    if(first==null){Status="Ожидание SDR-кадра";await Task.Delay(100,stop.Token);continue;}
                    inputWidth=first.Width;inputHeight=first.Height;
                    if(activeVersion!=currentVersion)
                    {
                        lock(chunksGate)foreach(var chunk in chunks.Where(c=>c.Pins==0).ToArray()){TryDelete(chunk.Path);chunks.Remove(chunk);}
                        activeVersion=currentVersion;
                        Width=config.Width==0?first.Width:config.Width;Height=config.Width==0?(first.Height&~1):config.Height;
                    }
                }
                config.Validate();
                if(!RecordingTools.Ready)
                {
                    Error=null;Status=UiStrings.T("Setting up recording tools…");
                }
                // Finish legacy migration before an encoder can lock its old EXE.
                await RecordingTools.EnsureAsync(message=>Status=message,stop.Token);
                // Setup may outlive a settings change. Re-read on the next pass.
                if(!Volatile.Read(ref options).Enabled||Volatile.Read(ref version)!=currentVersion)continue;
                Status=UiStrings.T("Checking GPU recording support…");
                var encoder=validationEncoder??await RecordingEncoders.SelectAsync(config,Width,Height,stop.Token,inputWidth,inputHeight);
                if(!Volatile.Read(ref options).Enabled||Volatile.Read(ref version)!=currentVersion)continue;
                using var captureAudio=new ReplayAudio(config,syntheticAudio,200);audio=captureAudio;
                await RecordSession(config,currentVersion,captureAudio,encoder,inputWidth,inputHeight);
                audio=null;
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
            catch(Exception e)
            {
                Error=e.Message;Status="Запись восстанавливается: "+e.Message;Log.Write("Replay: "+e);
                try{await Task.Delay(2000,stop.Token);}catch(OperationCanceledException){break;}
            }
        }
    }
    async Task RecordSession(ReplayOptions config,int currentVersion,ReplayAudio captureAudio,RecordingEncoder encoder,int inputWidth,int inputHeight)
    {
        Volatile.Write(ref warmupUntil,Stopwatch.GetTimestamp()+Stopwatch.Frequency*2);
        string folder=Path.Combine(runFolder,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string videoName="SdrCapture-video-"+Guid.NewGuid().ToString("N"),audioName="SdrCapture-audio-"+Guid.NewGuid().ToString("N");
        using var videoPipe=new NamedPipeServerStream(videoName,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,1024*1024);
        using var audioPipe=new NamedPipeServerStream(audioName,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,19200*4);
        using var sessionStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var token=sessionStop.Token;
        double epoch=0,lastVideo=Now;int parsed=-1;
        var started=new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var videoEncoder=config.IsSilent&&inputWidth==Width&&inputHeight==Height?null:new RecordingVideoEncoder(videoName,inputWidth,inputHeight,Width,Height,config.Fps,encoder,token);
        var args=new List<string>{"-hide_banner","-loglevel","warning","-nostdin","-y"};
        if(videoEncoder!=null)
            // These are compressed packets: enough for delayed local/KVM audio,
            // without filling another queue with multi-megabyte raw frames.
            args.AddRange(["-thread_queue_size","128","-probesize","32","-analyzeduration","0","-f","nut","-i",@"\\.\pipe\"+videoEncoder.PipeName]);
        else
        {
            args.AddRange(["-thread_queue_size","8","-threads","1","-probesize","32","-analyzeduration","0"]);
            args.AddRange(RecordingSdr.RawInput(videoName,Width,Height,config.Fps));
        }
        args.AddRange(["-thread_queue_size","16","-probesize","32","-analyzeduration","0","-f","f32le","-ar","48000","-ac","6","-i",@"\\.\pipe\"+audioName]);
        if(!config.IsSilent)
        {
            if(config.AudioMode=="Separate")args.AddRange(["-filter_complex","[1:a]asplit=3[g][m][e];[g]pan=stereo|c0=c2|c1=c3[game];[m]pan=stereo|c0=c0|c1=c1[mic];[e]pan=stereo|c0=c4|c1=c5[extra]","-map","0:v","-map","[game]","-map","[mic]","-map","[extra]","-metadata:s:a:0","title=Game PC","-metadata:s:a:1","title=Microphone","-metadata:s:a:2","title=Stream PC"]);
            else args.AddRange(["-filter_complex","[1:a]pan=stereo|c0=c0+c2+c4|c1=c1+c3+c5,alimiter=limit=0.95:level=0:latency=1[mix]","-map","0:v","-map","[mix]","-metadata:s:a:0","title=Mixed audio"]);
        }
        else args.AddRange(["-map","0:v"]);
        if(videoEncoder==null)args.AddRange(encoder.Arguments);
        else args.AddRange(["-c:v","copy"]);
        args.AddRange([
            "-color_range","tv","-colorspace","bt709","-color_primaries","bt709","-color_trc","bt709",
            "-c:a","aac","-b:a","192k","-ar","48000",
            // AAC priming must not shift the video/frame grid by 21 ms. MP4
            // exports retain the necessary audio preroll through their edit list.
            "-avoid_negative_ts","disabled",
            "-f","segment","-segment_format","matroska","-segment_time","2","-reset_timestamps","1","-segment_list_size","8","-segment_list_type","csv","-segment_list",Path.Combine(folder,"chunks.csv"),Path.Combine(folder,"seg-%06d.mkv")]);
        using var process=Process.Start(ReplayTools.StartInfo(ReplayTools.Ffmpeg,args))??throw new IOException("FFmpeg не запущен.");
        try{process.PriorityClass=ProcessPriorityClass.AboveNormal;}catch(System.ComponentModel.Win32Exception){}
        EncoderStarts++;
        var errors=new Queue<string>();
        var stderr=Task.Run(async()=>{while(await process.StandardError.ReadLineAsync() is string line){lock(errors){errors.Enqueue(line);while(errors.Count>12)errors.Dequeue();}}});
        var stdout=process.StandardOutput.ReadToEndAsync();
        void Scan()
        {
            if(epoch==0)return;
            try
            {
                using var file=new FileStream(Path.Combine(folder,"chunks.csv"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(file);
                while(reader.ReadLine() is string line)
                {
                    var match=Regex.Match(line,"(?:^|[/\\\\])?(seg-(\\d+)\\.mkv)\"?,([0-9.]+),([0-9.]+)$");
                    if(!match.Success)continue;
                    int index=int.Parse(match.Groups[2].Value);if(index<=parsed)continue;
                    string path=Path.Combine(folder,match.Groups[1].Value);
                    double start=double.Parse(match.Groups[3].Value,CultureInfo.InvariantCulture),end=double.Parse(match.Groups[4].Value,CultureInfo.InvariantCulture);
                    if(end<=start||!File.Exists(path))continue;
                    lock(chunksGate)chunks.Add(new ReplayChunk(path,epoch+start,epoch+end,new FileInfo(path).Length,currentVersion,config.Codec,config.Fps));
                    parsed=index;
                }
            }
            catch(IOException){}
            Cleanup();
        }
        var videoTask=Task.Factory.StartNew(()=>
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            using var pacing=new Pacer();
            using var writer=new RecordingPipeWriter(videoPipe);
            byte[]? resized=null;
            try
            {
                videoPipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                // Align the CFR grid to an actual source image rather than
                // the arbitrary instant the encoder connected its pipe.
                // This keeps nearest-image selection away from the midpoint
                // between source presentations and gives audio the same epoch.
                AudioReadyTicks=captureAudio.WaitForCaptureStart(token);
                var anchorWait=Stopwatch.StartNew();
                while(true)
                {
                    using var anchor=GetFrame();
                    if(anchor!=null&&anchor.CapturedAt>=AudioReadyTicks){SessionEpochTicks=anchor.CapturedAt;break;}
                    // A static/silent desktop need not publish another image.
                    // Start its held image at the current (audio-ready) time.
                    if(anchorWait.ElapsedMilliseconds>=500){SessionEpochTicks=Stopwatch.GetTimestamp();break;}
                    token.ThrowIfCancellationRequested();token.WaitHandle.WaitOne(1);
                }
                epoch=SessionEpochTicks/(double)Stopwatch.Frequency;
                lastVideo=Now;Volatile.Write(ref liveEnd,0);Volatile.Write(ref liveStart,epoch);started.TrySetResult(epoch);
                // Both inputs still begin at epoch, with continuous frame/sample
                // timestamps. Encode video promptly and let the muxer interleave
                // its small compressed packets while WASAPI catches up, instead
                // of retaining hundreds of MB of raw video for the audio delay.
                // Keep a short capture-jitter allowance: GPU readback/packing
                // can finish after the image's presentation timestamp. This
                // is independent of the longer local/network audio wait.
                double playout=Math.Max(2.0/config.Fps,.10);
                long frameIndex=0,previousCapture=-1;
                while(!token.IsCancellationRequested&&Volatile.Read(ref version)==currentVersion)
                {
                    double due=epoch+frameIndex/(double)config.Fps;
                    pacing.WaitUntil((long)((due+playout)*Stopwatch.Frequency),token);
                    token.ThrowIfCancellationRequested();
                    MaxWriterLateMs=Math.Max(MaxWriterLateMs,(Now-due)*1000);
                    if(Now-epoch>5&&Now-due>1)throw new IOException(UiStrings.F("{0} cannot keep up with {1} FPS. Lower recording resolution or frame rate.",encoder.Label,config.Fps));
                    // Match distinct pictures through short DWM/render timing
                    // variations. Bound image-time deviation to 20 ms,
                    // using the existing 100 ms playout without adding latency.
                    long tolerance=Stopwatch.Frequency/50;
                    using var frame=GetFrame((long)(due*Stopwatch.Frequency),previousCapture,tolerance);if(frame==null){Thread.Sleep(1);continue;}
                    if(frame.CapturedAt==previousCapture)RepeatedInputFrames++;previousCapture=frame.CapturedAt;
                    FrameSelectedForTest?.Invoke((long)(due*Stopwatch.Frequency),frame.CapturedAt);
                    var data=frame.Data;
                    if(frame.Width!=inputWidth||frame.Height!=inputHeight)
                    {resized??=new byte[inputWidth*inputHeight*3/2];RecordingPixels.ResizeNv12(data,frame.Width,frame.Height,resized,inputWidth,inputHeight);data=resized;}
                    long written=Stopwatch.GetTimestamp();
                    writer.Write(data[..(inputWidth*inputHeight*3/2)],token);
                    MaxPipeWriteMs=Math.Max(MaxPipeWriteMs,Stopwatch.GetElapsedTime(written).TotalMilliseconds);
                    frameIndex++;EncodedFrames++;lastVideo=Now;Volatile.Write(ref liveUpdated,lastVideo);Volatile.Write(ref liveEnd,epoch+frameIndex/(double)config.Fps);
                }
            }
            finally{videoPipe.Dispose();}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        var audioTask=Task.Factory.StartNew(()=>
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            using var pacing=new Pacer();
            using var writer=new RecordingPipeWriter(audioPipe);
            try
            {
                audioPipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                double origin=started.Task.WaitAsync(token).GetAwaiter().GetResult();long sampleOrigin=(long)(origin*48000);
                long frameIndex=0;byte[] pcm=new byte[800*6*4];
                while(!token.IsCancellationRequested&&Volatile.Read(ref version)==currentVersion)
                {
                    // Give WASAPI packets time to arrive, without delaying NDI or changing A/V timestamps.
                    pacing.WaitUntil((long)((origin+(frameIndex+1)/60.0+ReplayAudio.WaitSeconds(config))*Stopwatch.Frequency),token);
                    token.ThrowIfCancellationRequested();
                    captureAudio.Read(sampleOrigin+frameIndex*800,pcm);
                    writer.Write(pcm,pcm.Length,token);frameIndex++;
                }
            }
            finally{audioPipe.Dispose();}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        Exception? failure=null;
        try
        {
            while(!stop.IsCancellationRequested&&Volatile.Read(ref version)==currentVersion)
            {
                await Task.Delay(200,stop.Token);Scan();
                if(stop.IsCancellationRequested||Volatile.Read(ref version)!=currentVersion)break;
                videoEncoder?.Check();
                if(process.HasExited){lock(errors)throw new IOException(config.Codec+": "+string.Join(" ",errors));}
                if(videoTask.IsCompleted)await videoTask;
                if(!config.IsSilent&&audioTask.IsCompleted)await audioTask;
                if(Now-lastVideo>4)throw new IOException("Кодировщик записи не отвечает.");
                Error=null;Status=$"{config.Codec} · {encoder.Label} · {Width}×{Height} · {config.Fps} FPS · буфер {ReplayTime.Format(BufferedSeconds)} / {ReplayTime.Format(WindowSeconds)}";
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        catch(Exception e){failure=e;}
        finally
        {
            sessionStop.Cancel();videoPipe.Dispose();audioPipe.Dispose();
            try{await Task.WhenAll(videoTask,audioTask);}catch{}
            videoEncoder?.Dispose();
            using var timeout=new CancellationTokenSource(3500);
            try{await process.WaitForExitAsync(timeout.Token);}catch{try{process.Kill(true);}catch{}}
            await stderr;await stdout;Scan();Volatile.Write(ref liveEnd,0);
            Log.Write($"Replay session: {encoder.Label}, {config.Codec}, {Width}x{Height}@{config.Fps}; frames={EncodedFrames}, repeated={RepeatedInputFrames}, historyMisses={HistoryMisses}, maxWriterLateMs={MaxWriterLateMs:F1}, starts={EncoderStarts}; {captureAudio.Diagnostics}");
        }
        if(failure!=null)throw failure;
    }
    void Cleanup()
    {
        lock(chunksGate)
        {
            double before=Now-WindowSeconds-8;
            foreach(var chunk in chunks.ToArray())
            {
                if(chunk.Pins!=0)continue;
                if(chunk.End>=before)break;
                TryDelete(chunk.Path);chunks.Remove(chunk);
            }
        }
        var drive=new DriveInfo(Path.GetPathRoot(runFolder)!);
        if(drive.AvailableFreeSpace<512L*1024*1024)throw new IOException("Для буфера повтора осталось менее 512 МБ. Освободите место на локальном диске.");
    }
    public async Task<ReplayResult> SaveAsync(string? appFolder=null)
    {
        if(!await saveGate.WaitAsync(0))throw new InvalidOperationException("Предыдущий повтор ещё сохраняется.");
        ReplayChunk[] selected=[];
        string? listPath=null,partialPath=null;
        try
        {
            var config=Volatile.Read(ref options);double pressed=Now;double deadline=pressed+5;
            while(Now<deadline&&!stop.IsCancellationRequested)
            {
                lock(chunksGate)if(chunks.Count>0&&chunks[^1].End>=pressed)break;
                if(!config.Enabled)break;
                await Task.Delay(100);
            }
            double wantedStart=Math.Max(pressed-WindowSeconds,savedUntil);
            lock(chunksGate)
            {
                int generation=chunks.Count>0?chunks[^1].Generation:-1;
                selected=chunks.Where(c=>c.Generation==generation&&c.End>wantedStart&&c.Start<pressed).ToArray();
                if(selected.Length==0)throw new InvalidOperationException("Буфер ещё пуст. Подождите несколько секунд после включения записи.");
                foreach(var chunk in selected)chunk.Pins++;
            }
            string exports=Path.Combine(ReplayTools.Root,"saved");Directory.CreateDirectory(exports);
            string name=$"Replay-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-{Guid.NewGuid().ToString("N")[..6]}.mp4";
            string output=Path.Combine(exports,name),partial=output+".partial";partialPath=partial;
            listPath=output+".ffconcat";
            // AAC preroll can give a chunk a negative container start time.
            // Anchor concat to video time zero so it cannot shift the frame grid.
            File.WriteAllLines(listPath,selected.SelectMany(c=>new[]{"file '"+c.Path.Replace("\\","/").Replace("'","'\\''")+"'","inpoint 0","duration "+ReplayTools.Number(c.End-c.Start)}));
            double offset=0;
            foreach(var chunk in selected)
            {
                if(wantedStart>=chunk.End)offset+=chunk.End-chunk.Start;
                else{offset+=Math.Max(0,wantedStart-chunk.Start);break;}
            }
            // Snap both sides to the same frame grid. MP4 edit lists retain the
            // keyframe preroll for decoding but hide already exported frames.
            int fps=selected[0].Fps;
            offset=ReplaySaveTime.Boundary(offset,fps);
            double end=selected.Sum(c=>c.End-c.Start)-Math.Max(0,selected[^1].End-pressed);
            end=ReplaySaveTime.Boundary(end,fps);
            double duration=end-offset;
            if(duration<1.0/fps-.001)throw new InvalidOperationException("Буфер ещё пуст. Подождите несколько секунд после включения записи.");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(180,selected.Sum(c=>c.Bytes)/(16.0*1024*1024)+60)));
            var exportArgs=new List<string>{"-hide_banner","-loglevel","error","-nostdin","-y","-ss",ReplayTools.Number(ReplaySaveTime.Packet(offset)),"-f","concat","-safe","0","-i",listPath,"-t",ReplayTools.Number(ReplaySaveTime.Packet(end)-ReplaySaveTime.Packet(offset)-.001),"-map","0","-c","copy","-avoid_negative_ts","disabled","-use_editlist","1","-movflags","+faststart"};
            if(selected[0].Codec=="HEVC")exportArgs.AddRange(["-tag:v","hvc1"]);
            if(config.IsSilent)exportArgs.Add("-an");
            exportArgs.AddRange(["-f","mp4",partial]);
            await ReplayTools.RunAsync(ReplayTools.Ffmpeg,exportArgs,timeout.Token);
            if(new FileInfo(partial).Length<1024)throw new IOException("Пустой файл повтора.");
            File.Move(partial,output);
            appFolder??=ReplayAppContext.ForInterval(selected[0].Start+offset,selected[0].Start+end);
            string destination=ReplayAppContext.Destination(config,appFolder,name);
            File.WriteAllText(output+".delivery.tmp",JsonSerializer.Serialize(new DeliveryJob(output,destination,duration)));
            File.Move(output+".delivery.tmp",output+".delivery.json");
            lock(chunksGate)savedUntil=selected[^1].End-(selected.Sum(c=>c.End-c.Start)-end);
            return new ReplayResult(output,destination,duration,"Повтор готов; сохранение в выбранную папку.");
        }
        finally
        {
            lock(chunksGate)foreach(var chunk in selected)chunk.Pins--;
            if(listPath!=null)TryDelete(listPath);
            if(partialPath!=null)TryDelete(partialPath);
            saveGate.Release();
        }
    }
    internal static void Resize(VideoBuffer input,byte[] target,int width,int height)
    {
        var output=MemoryMarshal.Cast<byte,uint>(target.AsSpan(0,width*height*2));output.Fill(0x10801080);
        double factor=Math.Min(width/(double)input.Width,height/(double)input.Height);
        int w=Math.Max(2,((int)(input.Width*factor))&~1),h=Math.Max(1,(int)(input.Height*factor));
        int left=((width-w)/2)&~1,top=(height-h)/2;
        var source=MemoryMarshal.Cast<byte,uint>(input.Data.AsSpan(0,input.Length));
        for(int y=0;y<h;y++)
        {
            int src=(int)((long)y*input.Height/h)*(input.Width/2),dst=(top+y)*(width/2)+left/2;
            for(int x=0;x<w/2;x++)output[dst+x]=source[src+(int)((long)x*(input.Width/2)/(w/2))];
        }
    }
    static void TryDelete(string path){try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        incoming.Dispose();
        stop.Cancel();try{worker.Wait(7000);}catch{}
        lock(framesGate){foreach(var frame in frames)frame.Dispose();frames.Clear();}
        framePool.Dispose();
        // Completed clips and delivery jobs are never part of ring cleanup.
        if(worker.IsCompleted)lock(chunksGate)foreach(var chunk in chunks.Where(c=>c.Pins==0))TryDelete(chunk.Path);
    }
}
