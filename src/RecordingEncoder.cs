namespace SdrCapture;

sealed record RecordingEncoder(string Name,string Label,string[] Arguments,bool GpuScale=true);

static class RecordingEncoders
{
    static readonly SemaphoreSlim gate=new(1,1);
    static readonly Dictionary<string,(RecordingEncoder? Encoder,DateTimeOffset Retry,string? Error)> cache=[];
    internal static int QualityTarget(string quality)=>quality switch{"Ultra"=>18,"Medium"=>25,"Low"=>29,_=>21};
    internal static int MaximumBitrate(string quality,int width,int height,int fps)
    {
        int baseRate=quality switch{"Ultra"=>60,"Medium"=>24,"Low"=>14,_=>40};
        return Math.Clamp((int)Math.Ceiling(baseRate*width*(double)height/(2560*1440)*fps/60),6,200);
    }
    internal static RecordingEncoder[] Candidates(ReplayOptions options,int width,int height)
    {
        if(options.Encoder=="CPU")
        {
            if(options.Codec!="H264")throw new ArgumentException("CPU x264 encoding requires the H.264 codec.");
            return [Cpu(options,width,height)];
        }
        if(options.Encoder!="GPU")throw new ArgumentException("Unknown recording encoder backend.");
        string codec=options.Codec switch{"H264"=>"h264","HEVC"=>"hevc","AV1"=>"av1",_=>throw new ArgumentException("Unknown recording codec.")};
        int cq=QualityTarget(options.Quality);
        int maxRate=options.BitrateMbps==0?MaximumBitrate(options.Quality,width,height,options.Fps):options.BitrateMbps;
        string[] common=["-g",(options.Fps*options.KeyframeSeconds).ToString(),"-bf","0","-pix_fmt","nv12"];
        string nvidiaPreset=options.EncoderPreset switch{"Performance"=>"p2","Quality"=>"p6",_=>"p4"};
        string amdPreset=options.EncoderPreset switch{"Performance"=>"speed","Quality"=>"quality",_=>"balanced"};
        // Keep the previously shipped veryfast preset for the Balanced default.
        string intelPreset=options.EncoderPreset=="Quality"?"medium":"veryfast";
        var nvidia=new List<string>{"-c:v",codec+"_nvenc","-preset",nvidiaPreset,"-tune","hq"};
        if(options.RateControl=="Quality")nvidia.AddRange(["-rc","vbr","-cq",cq.ToString(),"-b:v","0","-maxrate",$"{maxRate}M","-bufsize",$"{maxRate*2}M"]);
        else{nvidia.AddRange(["-rc",options.RateControl=="CBR"?"cbr":"vbr"]);nvidia.AddRange(BitrateArguments(options,maxRate));}
        nvidia.AddRange(["-rc-lookahead","0"]);
        // Preserve the working NVIDIA path, including its AV1 decoder limit.
        if(options.Codec=="AV1")nvidia.AddRange(["-level","6.2","-tier","1"]);
        nvidia.AddRange(common);
        int amdQp=options.Codec=="AV1"?cq*4:cq;
        var amd=new List<string>{"-c:v",codec+"_amf","-usage","transcoding","-quality",amdPreset};
        if(options.RateControl=="Quality")amd.AddRange(["-rc","cqp","-qp_i",amdQp.ToString(),"-qp_p",amdQp.ToString()]);
        else{amd.AddRange(["-rc",options.RateControl=="CBR"?"cbr":"vbr_peak"]);amd.AddRange(BitrateArguments(options,maxRate));}
        amd.AddRange(common);
        var intel=new List<string>{"-c:v",codec+"_qsv","-preset",intelPreset};
        if(options.RateControl=="Quality")intel.AddRange(["-global_quality",cq.ToString()]);
        else intel.AddRange(BitrateArguments(options,maxRate));
        intel.AddRange(common);
        return [new(codec+"_nvenc","NVIDIA NVENC",nvidia.ToArray()),new(codec+"_amf","AMD AMF",amd.ToArray()),new(codec+"_qsv","Intel Quick Sync",intel.ToArray())];
    }
    static RecordingEncoder Cpu(ReplayOptions options,int width,int height)
    {
        int maximum=options.BitrateMbps==0?MaximumBitrate(options.Quality,width,height,options.Fps):options.BitrateMbps;
        string preset=options.EncoderPreset switch{"Performance"=>"ultrafast","Quality"=>"medium",_=>"veryfast"};
        var args=new List<string>{"-c:v","libx264","-preset",preset,"-tune","zerolatency","-threads",Math.Clamp(Environment.ProcessorCount/2,1,16).ToString(),
            "-g",(options.Fps*options.KeyframeSeconds).ToString(),"-keyint_min",(options.Fps*options.KeyframeSeconds).ToString(),"-sc_threshold","0","-bf","0","-pix_fmt","yuv420p"};
        if(options.RateControl=="Quality")args.AddRange(["-crf",QualityTarget(options.Quality).ToString(),"-maxrate",$"{maximum}M","-bufsize",$"{maximum*2}M"]);
        else args.AddRange(BitrateArguments(options,maximum));
        if(options.RateControl=="CBR")args.AddRange(["-x264-params","nal-hrd=cbr:force-cfr=1"]);
        return new("libx264","CPU x264",args.ToArray(),GpuScale:false);
    }
    static string[] BitrateArguments(ReplayOptions options,int maximum)
    {
        // QSV infers VBR when the target differs from the maximum, CBR when equal.
        string target=ReplayTools.Number(options.RateControl=="CBR"?maximum:maximum*.75);
        return options.RateControl=="CBR"
            ?["-b:v",target+"M","-minrate",$"{maximum}M","-maxrate",$"{maximum}M","-bufsize",$"{maximum*2}M"]
            :["-b:v",target+"M","-maxrate",$"{maximum}M","-bufsize",$"{maximum*2}M"];
    }
    internal static string[] ProbeArguments(RecordingEncoder encoder,int width,int height,int fps,int sourceWidth=0,int sourceHeight=0)
    {
        if(sourceWidth==0){sourceWidth=width;sourceHeight=height;}
        string source=$"color=c=black:s={sourceWidth}x{sourceHeight}:r={fps}";
        if(sourceWidth!=width||sourceHeight!=height)source+=",format=nv12";
        return ["-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i",source,"-frames:v","2","-an",..encoder.Arguments,..RecordingScale.Arguments(encoder,sourceWidth,sourceHeight,width,height),"-f","null","-"];
    }
    internal static async Task<RecordingEncoder?> FindAsync(IEnumerable<RecordingEncoder> candidates,Func<RecordingEncoder,CancellationToken,Task<bool>> probe,CancellationToken token)
    {
        foreach(var encoder in candidates){token.ThrowIfCancellationRequested();if(await probe(encoder,token))return encoder;}
        return null;
    }
    public static async Task<RecordingEncoder> SelectAsync(ReplayOptions options,int width,int height,CancellationToken token,int sourceWidth=0,int sourceHeight=0)
    {
        options=options with{};
        options.Validate();
        if(sourceWidth==0){sourceWidth=width;sourceHeight=height;}
        string key=CacheKey(options,width,height,sourceWidth,sourceHeight);
        await gate.WaitAsync(token);
        try
        {
            if(cache.TryGetValue(key,out var saved))
            {
                if(saved.Encoder!=null)return saved.Encoder;
                if(DateTimeOffset.UtcNow<saved.Retry)throw new IOException(saved.Error);
            }
            // An imported FFmpeg may lack CUDA filters even when NVENC works.
            // Preflight the actual resize path, with optimized software scaling
            // as a fallback before trying a different vendor's encoder.
            var candidates=Candidates(options,width,height).SelectMany(e=>(sourceWidth!=width||sourceHeight!=height)&&e.Name.EndsWith("_nvenc",StringComparison.Ordinal)?new[]{e,e with{GpuScale=false}}:new[]{e});
            var found=await FindAsync(candidates,async(encoder,parent)=>
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(parent);timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try{await ReplayTools.RunAsync(ReplayTools.Ffmpeg,ProbeArguments(encoder,width,height,options.Fps,sourceWidth,sourceHeight),timeout.Token);return true;}
                catch(OperationCanceledException)when(!parent.IsCancellationRequested){Log.Write("Recording encoder probe timed out: "+encoder.Name);return false;}
                catch(IOException e){Log.Write("Recording encoder unavailable: "+encoder.Name+" · "+e.Message);return false;}
            },token);
            if(found!=null){cache[key]=(found,default,null);return found;}
            string error=options.Encoder=="CPU"?UiStrings.F("CPU x264 could not encode {0} × {1}, {2} FPS with this profile. Check the recording tools or reduce the resolution and frame rate.",width,height,options.Fps):
                UiStrings.F("No GPU encoder supports {0} at {1} × {2}, {3} FPS. Try H.264, lower the resolution or frame rate, and update your graphics driver.",options.Codec,width,height,options.Fps);
            cache[key]=(null,DateTimeOffset.UtcNow.AddSeconds(30),error);throw new IOException(error);
        }
        finally{gate.Release();}
    }
    internal static string CacheKey(ReplayOptions options,int width,int height,int sourceWidth,int sourceHeight)
        =>$"{ReplayTools.Ffmpeg}|{File.GetLastWriteTimeUtc(ReplayTools.Ffmpeg).Ticks}|{options.Encoder}|{sourceWidth}x{sourceHeight}>{width}x{height}|{options.Fps}|"+string.Join('|',Candidates(options,width,height).SelectMany(e=>e.Arguments));
}
