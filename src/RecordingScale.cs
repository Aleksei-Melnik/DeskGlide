namespace SdrCapture;

static class RecordingScale
{
    internal static string[] Arguments(RecordingEncoder encoder,int sourceWidth,int sourceHeight,int width,int height)
    {
        if(sourceWidth==width&&sourceHeight==height)return [];
        bool cuda=encoder.GpuScale&&encoder.Name.EndsWith("_nvenc",StringComparison.Ordinal);
        string backend=cuda?"scale_cuda":"scale";
        string algorithm=cuda?"interp_algo=bicubic:format=nv12":"flags=bicubic";
        string filter=(cuda?"hwupload_cuda,":"")+$"{backend}={width}:{height}:{algorithm}:force_original_aspect_ratio=decrease:force_divisible_by=2";
        if((long)sourceWidth*height!=(long)sourceHeight*width)
            filter+=$",{(cuda?"pad_cuda":"pad")}={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black";
        filter+=",setsar=1";
        return cuda?["-vf",filter,"-pix_fmt","cuda"]:["-vf",filter];
    }
}
