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
        string codec=options.Codec switch{"H264"=>"h264","HEVC"=>"hevc","AV1"=>"av1",_=>throw new ArgumentException("Unknown recording codec.")};
        int cq=QualityTarget(options.Quality);
        int maxRate=MaximumBitrate(options.Quality,width,height,options.Fps);
        string[] common=["-g",(options.Fps*2).ToString(),"-bf","0","-pix_fmt","nv12"];
        var nvidia=new List<string>{"-c:v",codec+"_nvenc","-preset","p4","-tune","hq","-rc","vbr","-cq",cq.ToString(),"-b:v","0","-maxrate",$"{maxRate}M","-bufsize",$"{maxRate*2}M","-rc-lookahead","0"};
        // Preserve the working NVIDIA path, including its AV1 decoder limit.
        if(options.Codec=="AV1")nvidia.AddRange(["-level","6.2","-tier","1"]);
        nvidia.AddRange(common);
        int amdQp=options.Codec=="AV1"?cq*4:cq;
        var amd=new List<string>{"-c:v",codec+"_amf","-usage","transcoding","-quality","balanced","-rc","cqp","-qp_i",amdQp.ToString(),"-qp_p",amdQp.ToString()};amd.AddRange(common);
        var intel=new List<string>{"-c:v",codec+"_qsv","-preset","veryfast","-global_quality",cq.ToString()};intel.AddRange(common);
        return [new(codec+"_nvenc","NVIDIA NVENC",nvidia.ToArray()),new(codec+"_amf","AMD AMF",amd.ToArray()),new(codec+"_qsv","Intel Quick Sync",intel.ToArray())];
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
        if(sourceWidth==0){sourceWidth=width;sourceHeight=height;}
        string key=$"{ReplayTools.Ffmpeg}|{File.GetLastWriteTimeUtc(ReplayTools.Ffmpeg).Ticks}|{options.Codec}|{options.Quality}|{sourceWidth}x{sourceHeight}>{width}x{height}|{options.Fps}";
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
            string error=UiStrings.F("No GPU encoder supports {0} at {1} × {2}, {3} FPS. Try H.264, lower the resolution or frame rate, and update your graphics driver.",options.Codec,width,height,options.Fps);
            cache[key]=(null,DateTimeOffset.UtcNow.AddSeconds(30),error);throw new IOException(error);
        }
        finally{gate.Release();}
    }
}
