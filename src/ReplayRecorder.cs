using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SdrCapture;

sealed class ReplayChunk(string path,double start,double end,long bytes,int generation,string codec)
{
    public string Path=path;
    public double Start=start,End=end;
    public long Bytes=bytes;
    public int Pins;
    public int Generation=generation;
    public string Codec=codec;
}
public sealed record ReplayResult(string LocalPath,string Destination,double Seconds,string Message);

public sealed class ReplayRecorder:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly object chunksGate=new();
    readonly List<ReplayChunk> chunks=[];
    readonly SemaphoreSlim saveGate=new(1,1);
    readonly Task worker;
    readonly string runFolder;
    readonly bool syntheticAudio;
    readonly int? testWindowSeconds;
    ReplayOptions options;
    readonly object framesGate=new();
    readonly List<VideoBuffer> frames=[];
    ReplayAudio? audio;
    int version,disposed;
    long warmupUntil;
    public string Status {get;private set;}="Подготовка буфера";
    public string? Error {get;private set;}
    public string AudioStatus=>audio?.Status??"Звук не запущен";
    public int Width {get;private set;}
    public int Height {get;private set;}
    public long EncodedFrames {get;private set;}
    public long RepeatedInputFrames {get;private set;}
    public long HistoryMisses {get;private set;}
    public double MaxWriterLateMs {get;private set;}
    public int EncoderStarts {get;private set;}
    public double BufferedSeconds {get{lock(chunksGate)return Math.Min(WindowSeconds,chunks.Sum(c=>c.End-c.Start));}}
    double WindowSeconds=>testWindowSeconds??Volatile.Read(ref options).Minutes*60;
    public long CacheBytes {get{lock(chunksGate)return chunks.Sum(c=>c.Bytes);}}
    public static double Now=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
    public ReplayRecorder(ReplayOptions value,bool syntheticAudio=false,int? testWindowSeconds=null)
    {
        options=value with {};this.syntheticAudio=syntheticAudio;this.testWindowSeconds=testWindowSeconds;
        runFolder=Path.Combine(ReplayTools.Root,"buffer",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runFolder);
        CleanOldBuffers();
        worker=Task.Run(Run);
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
    public void Offer(VideoBuffer frame)
    {
        if(Volatile.Read(ref disposed)!=0||!Volatile.Read(ref options).Enabled)return;
        var reference=frame.Retain();
        if(reference==null)return;
        if(reference.CapturedAt<=0)reference.CapturedAt=Stopwatch.GetTimestamp();
        lock(framesGate)
        {
            if(Volatile.Read(ref disposed)!=0){reference.Dispose();return;}
            frames.Add(reference);
            var config=Volatile.Read(ref options);
            int steady=Math.Max(12,(int)Math.Ceiling((ReplayAudio.WaitSeconds(config)+1.0/60+.04)*config.Fps)+3);
            int wanted=Stopwatch.GetTimestamp()<Volatile.Read(ref warmupUntil)?Math.Max(steady,32):steady;
            int limit=Math.Min(wanted,Math.Max(12,536870912/Math.Max(1,reference.Data.Length)));
            while(frames.Count>limit){frames[0].Dispose();frames.RemoveAt(0);}
        }
    }
    VideoBuffer? GetFrame(long timestamp=long.MaxValue)
    {
        lock(framesGate)
        {
            for(int i=frames.Count-1;i>=0;i--)if(frames[i].CapturedAt<=timestamp)return frames[i].Retain();
            if(frames.Count>0){HistoryMisses++;return frames[0].Retain();}return null;
        }
    }
    public void Update(ReplayOptions value)
    {
        value.Validate();var old=Volatile.Read(ref options);Volatile.Write(ref options,value with {});
        if(!value.Enabled)lock(framesGate){foreach(var frame in frames)frame.Dispose();frames.Clear();}
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
                using var first=GetFrame();
                if(first==null){Status="Ожидание SDR-кадра";await Task.Delay(100,stop.Token);continue;}
                if(activeVersion!=currentVersion)
                {
                    lock(chunksGate)foreach(var chunk in chunks.Where(c=>c.Pins==0).ToArray()){TryDelete(chunk.Path);chunks.Remove(chunk);}
                    activeVersion=currentVersion;
                    Width=config.Width==0?first.Width:config.Width;Height=config.Width==0?(first.Height&~1):config.Height;
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
                using var captureAudio=new ReplayAudio(config,syntheticAudio);audio=captureAudio;
                await RecordSession(config,currentVersion,captureAudio);
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
    async Task RecordSession(ReplayOptions config,int currentVersion,ReplayAudio captureAudio)
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
        int cq=config.Quality switch{"Ultra"=>18,"Medium"=>25,"Low"=>29,_=>21};
        int baseRate=config.Quality switch{"Ultra"=>60,"Medium"=>24,"Low"=>14,_=>40};
        int maxRate=Math.Clamp((int)Math.Ceiling(baseRate*Width*(double)Height/(2560*1440)*config.Fps/60),6,200);
        var args=new List<string>{"-hide_banner","-loglevel","warning","-nostdin","-y",
            "-thread_queue_size","4","-probesize","32","-analyzeduration","0","-f","rawvideo","-pixel_format","uyvy422","-video_size",$"{Width}x{Height}","-framerate",config.Fps.ToString(),"-i",@"\\.\pipe\"+videoName,
            "-thread_queue_size","16","-probesize","32","-analyzeduration","0","-f","f32le","-ar","48000","-ac","6","-i",@"\\.\pipe\"+audioName};
        if(!config.IsSilent)
        {
            if(config.AudioMode=="Separate")args.AddRange(["-filter_complex","[1:a]asplit=3[g][m][e];[g]pan=stereo|c0=c2|c1=c3[game];[m]pan=stereo|c0=c0|c1=c1[mic];[e]pan=stereo|c0=c4|c1=c5[extra]","-map","0:v","-map","[game]","-map","[mic]","-map","[extra]","-metadata:s:a:0","title=Game PC","-metadata:s:a:1","title=Microphone","-metadata:s:a:2","title=Stream PC"]);
            else args.AddRange(["-filter_complex","[1:a]pan=stereo|c0=c0+c2+c4|c1=c1+c3+c5,alimiter=limit=0.95:level=0:latency=1[mix]","-map","0:v","-map","[mix]","-metadata:s:a:0","title=Mixed audio"]);
        }
        else args.AddRange(["-map","0:v"]);
        // Some drivers choose AV1 level 7.3 automatically, which common decoders reject.
        if(config.Codec=="AV1")args.AddRange(["-level","6.2","-tier","1"]);
        args.AddRange([
            "-c:v",config.Codec switch{"H264"=>"h264_nvenc","AV1"=>"av1_nvenc",_=>"hevc_nvenc"},"-preset","p4","-tune","hq","-rc","vbr","-cq",cq.ToString(),"-b:v","0","-maxrate",$"{maxRate}M","-bufsize",$"{maxRate*2}M","-g",(config.Fps*2).ToString(),"-bf","0","-rc-lookahead","0","-pix_fmt","nv12",
            "-color_range","tv","-colorspace","bt709","-color_primaries","bt709","-color_trc","bt709",
            "-c:a","aac","-b:a","192k","-ar","48000",
            "-f","segment","-segment_format","matroska","-segment_time","2","-reset_timestamps","1","-segment_list_size","8","-segment_list_type","csv","-segment_list",Path.Combine(folder,"chunks.csv"),Path.Combine(folder,"seg-%06d.mkv")]);
        using var process=Process.Start(ReplayTools.StartInfo(ReplayTools.Ffmpeg,args))??throw new IOException("FFmpeg не запущен.");
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
                    lock(chunksGate)chunks.Add(new ReplayChunk(path,epoch+start,epoch+end,new FileInfo(path).Length,currentVersion,config.Codec));
                    parsed=index;
                }
            }
            catch(IOException){}
            Cleanup(config.Minutes);
        }
        var videoTask=Task.Factory.StartNew(()=>
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            using var pacing=new Pacer();
            byte[] resized=ArrayPool<byte>.Shared.Rent(Width*Height*2);
            try
            {
                videoPipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                epoch=Now;lastVideo=epoch;started.TrySetResult(epoch);
                // Keep recording video/audio on the same delayed clock. NDI has its own sender.
                double playout=Math.Max(2.0/config.Fps,ReplayAudio.WaitSeconds(config)+1.0/60);
                long frameIndex=0,previousCapture=-1;
                while(!token.IsCancellationRequested&&Volatile.Read(ref version)==currentVersion)
                {
                    double due=epoch+frameIndex/(double)config.Fps;
                    pacing.WaitUntil((long)((due+playout)*Stopwatch.Frequency),token);
                    token.ThrowIfCancellationRequested();
                    MaxWriterLateMs=Math.Max(MaxWriterLateMs,(Now-due)*1000);
                    if(Now-due>1)throw new IOException($"NVENC не успевает за {config.Fps} FPS. Уменьшите разрешение или FPS записи.");
                    using var frame=GetFrame((long)(due*Stopwatch.Frequency));if(frame==null){Thread.Sleep(1);continue;}
                    if(frame.CapturedAt==previousCapture)RepeatedInputFrames++;previousCapture=frame.CapturedAt;
                    byte[] data=frame.Data;
                    if(frame.Width!=Width||frame.Height!=Height){Resize(frame,resized,Width,Height);data=resized;}
                    videoPipe.WriteAsync(data.AsMemory(0,Width*Height*2),token).AsTask().GetAwaiter().GetResult();
                    frameIndex++;EncodedFrames++;lastVideo=Now;
                }
            }
            finally{ArrayPool<byte>.Shared.Return(resized);videoPipe.Dispose();}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        var audioTask=Task.Run(async()=>
        {
            try
            {
                await audioPipe.WaitForConnectionAsync(token);
                double origin=await started.Task.WaitAsync(token);long sampleOrigin=(long)(origin*48000);
                long frameIndex=0;byte[] pcm=new byte[800*6*4];
                while(!token.IsCancellationRequested&&Volatile.Read(ref version)==currentVersion)
                {
                    // Give WASAPI packets time to arrive, without delaying NDI or changing A/V timestamps.
                    double delay=origin+(frameIndex+1)/60.0+ReplayAudio.WaitSeconds(config)-Now;if(delay>.001)await Task.Delay(TimeSpan.FromSeconds(delay),token);
                    captureAudio.Read(sampleOrigin+frameIndex*800,pcm);
                    await audioPipe.WriteAsync(pcm,token);frameIndex++;
                }
            }
            finally{audioPipe.Dispose();}
        });
        Exception? failure=null;
        try
        {
            while(!stop.IsCancellationRequested&&Volatile.Read(ref version)==currentVersion)
            {
                await Task.Delay(200,stop.Token);Scan();
                if(stop.IsCancellationRequested||Volatile.Read(ref version)!=currentVersion)break;
                if(process.HasExited){lock(errors)throw new IOException(config.Codec+": "+string.Join(" ",errors));}
                if(videoTask.IsCompleted)await videoTask;
                if(!config.IsSilent&&audioTask.IsCompleted)await audioTask;
                if(Now-lastVideo>4)throw new IOException("Кодировщик записи не отвечает.");
                Error=null;Status=$"{config.Codec} NVENC · {Width}×{Height} · {config.Fps} FPS · буфер {TimeSpan.FromSeconds(BufferedSeconds):mm\\:ss} / {Volatile.Read(ref options).Minutes}:00";
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        catch(Exception e){failure=e;}
        finally
        {
            sessionStop.Cancel();videoPipe.Dispose();audioPipe.Dispose();
            try{await Task.WhenAll(videoTask,audioTask);}catch{}
            using var timeout=new CancellationTokenSource(3500);
            try{await process.WaitForExitAsync(timeout.Token);}catch{try{process.Kill(true);}catch{}}
            await stderr;await stdout;Scan();
        }
        if(failure!=null)throw failure;
    }
    void Cleanup(int minutes)
    {
        lock(chunksGate)
        {
            long bytes=chunks.Sum(c=>c.Bytes);double before=Now-WindowSeconds-8;
            foreach(var chunk in chunks.ToArray())
            {
                if(chunk.Pins!=0)continue;
                if(chunk.End>=before&&bytes<=32L*1024*1024*1024)break;
                TryDelete(chunk.Path);chunks.Remove(chunk);bytes-=chunk.Bytes;
            }
        }
        var drive=new DriveInfo(Path.GetPathRoot(runFolder)!);
        if(drive.AvailableFreeSpace<512L*1024*1024)throw new IOException("Для буфера повтора осталось менее 512 МБ. Освободите место на локальном диске.");
    }
    public async Task<ReplayResult> SaveAsync(string appFolder="Desktop")
    {
        if(!await saveGate.WaitAsync(0))throw new InvalidOperationException("Предыдущий повтор ещё сохраняется.");
        ReplayChunk[] selected=[];
        string? listPath=null;
        try
        {
            var config=Volatile.Read(ref options);double pressed=Now;double deadline=pressed+5;
            while(Now<deadline&&!stop.IsCancellationRequested)
            {
                lock(chunksGate)if(chunks.Count>0&&chunks[^1].End>=pressed)break;
                if(!config.Enabled)break;
                await Task.Delay(100);
            }
            lock(chunksGate)
            {
                int generation=chunks.Count>0?chunks[^1].Generation:-1;
                selected=chunks.Where(c=>c.Generation==generation&&c.Start>=pressed-WindowSeconds&&c.Start<pressed).ToArray();
                if(selected.Length==0)throw new InvalidOperationException("Буфер ещё пуст. Подождите несколько секунд после включения записи.");
                foreach(var chunk in selected)chunk.Pins++;
            }
            string exports=Path.Combine(ReplayTools.Root,"saved");Directory.CreateDirectory(exports);
            string name=$"Replay-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-{Guid.NewGuid().ToString("N")[..6]}.mp4";
            string output=Path.Combine(exports,name),partial=output+".partial";
            listPath=output+".ffconcat";
            File.WriteAllLines(listPath,selected.SelectMany(c=>new[]{"file '"+c.Path.Replace("\\","/").Replace("'","'\\''")+"'","duration "+ReplayTools.Number(c.End-c.Start)}));
            double duration=selected.Sum(c=>c.End-c.Start)-Math.Max(0,selected[^1].End-pressed);
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var exportArgs=new List<string>{"-hide_banner","-loglevel","error","-nostdin","-y","-f","concat","-safe","0","-i",listPath,"-t",ReplayTools.Number(duration),"-map","0","-c","copy","-movflags","+faststart"};
            if(selected[0].Codec=="HEVC")exportArgs.AddRange(["-tag:v","hvc1"]);
            if(config.IsSilent)exportArgs.Add("-an");
            exportArgs.AddRange(["-f","mp4",partial]);
            await ReplayTools.RunAsync(ReplayTools.Ffmpeg,exportArgs,timeout.Token);
            if(new FileInfo(partial).Length<1024)throw new IOException("Пустой файл повтора.");
            File.Move(partial,output);
            string destination=Path.Combine(config.Folder,ReplayAppContext.SafeName(appFolder),name);
            File.WriteAllText(output+".delivery.tmp",JsonSerializer.Serialize(new DeliveryJob(output,destination,duration)));
            File.Move(output+".delivery.tmp",output+".delivery.json");
            return new ReplayResult(output,destination,duration,"Повтор готов; сохранение в выбранную папку.");
        }
        finally
        {
            lock(chunksGate)foreach(var chunk in selected)chunk.Pins--;
            if(listPath!=null)TryDelete(listPath);
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
        stop.Cancel();try{worker.Wait(7000);}catch{}
        lock(framesGate){foreach(var frame in frames)frame.Dispose();frames.Clear();}
        // Completed clips and delivery jobs are never part of ring cleanup.
        if(worker.IsCompleted)lock(chunksGate)foreach(var chunk in chunks.Where(c=>c.Pins==0))TryDelete(chunk.Path);
    }
}
