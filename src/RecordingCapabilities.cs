namespace SdrCapture;

// A successful preflight of this complete format, not a GPU-model promise.
sealed record RecordingCapability(string Codec,string Encoder,string Label,int SourceWidth,int SourceHeight,int Width,int Height,int Fps,string Quality,string RateControl,int BitrateMbps,int KeyframeSeconds,string EncoderPreset,string Backend="GPU");

static class RecordingCapabilities
{
    public static Task<IReadOnlyList<RecordingCapability>> DiscoverAsync(ReplayOptions options,int sourceWidth,int sourceHeight,CancellationToken token)
        =>DiscoverAsync(options,sourceWidth,sourceHeight,token,RecordingEncoders.SelectAsync);

    // Inject only the hardware-selection boundary; production always uses real FFmpeg.
    internal static async Task<IReadOnlyList<RecordingCapability>> DiscoverAsync(ReplayOptions options,int sourceWidth,int sourceHeight,CancellationToken token,
        Func<ReplayOptions,int,int,CancellationToken,int,int,Task<RecordingEncoder>> select)
    {
        ArgumentNullException.ThrowIfNull(options);ArgumentNullException.ThrowIfNull(select);
        token.ThrowIfCancellationRequested();
        options=options with{};options.Validate();
        if(sourceWidth is <2 or >32768||sourceHeight is <2 or >32768||(sourceWidth&1)!=0||(sourceHeight&1)!=0)
            throw new ArgumentException("Invalid recording source dimensions.");
        int width=options.Width==0?sourceWidth:options.Width,height=options.Width==0?sourceHeight:options.Height;
        var result=new List<RecordingCapability>();
        foreach(string codec in options.Encoder=="CPU"?new[]{"H264"}:new[]{"H264","HEVC","AV1"})
        {
            token.ThrowIfCancellationRequested();
            var profile=options with{Codec=codec};
            try
            {
                var encoder=await select(profile,width,height,token,sourceWidth,sourceHeight);
                token.ThrowIfCancellationRequested();
                result.Add(new(codec,encoder.Name,encoder.Label,sourceWidth,sourceHeight,width,height,profile.Fps,profile.Quality,profile.RateControl,profile.BitrateMbps,profile.KeyframeSeconds,profile.EncoderPreset,profile.Encoder));
            }
            catch(IOException){token.ThrowIfCancellationRequested();}
        }
        return result.AsReadOnly();
    }

    public static bool EncodingEquivalent(ReplayOptions first,ReplayOptions second)
    {
        ArgumentNullException.ThrowIfNull(first);ArgumentNullException.ThrowIfNull(second);
        // Record equality keeps new encoder/audio settings significant by default.
        static ReplayOptions Encoding(ReplayOptions value)=>value with
        {
            Enabled=false,Minutes=0,DurationSeconds=0,Folder="",HotkeyModifiers=0,HotkeyKey=0,
            SaveSoundEnabled=false,SaveSoundVolume=0,GroupByApp=false,EncodingComputer=""
        };
        return Encoding(first)==Encoding(second);
    }
}
