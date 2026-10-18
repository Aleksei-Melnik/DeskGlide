namespace SdrCapture;

static class RecordingSdr
{
    // Rawvideo must carry these on decoded frames as well as on the output
    // stream. NVENC otherwise writes unspecified primaries/transfer into AV1
    // sequence headers even when the MP4 container is tagged BT.709.
    internal static readonly string[] Tags=["-color_range","tv","-colorspace","bt709","-color_primaries","bt709","-color_trc","bt709"];
    internal static string[] RawInput(string pipe,int width,int height,int fps)=>
        ["-f","rawvideo","-pixel_format","nv12","-video_size",$"{width}x{height}","-framerate",fps.ToString(),..Tags,"-i",@"\\.\pipe\"+pipe];
}
