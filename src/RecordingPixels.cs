using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SdrCapture;

// NVENC/AMF/QSV consume NV12 directly. Avoid FFmpeg's per-frame software scaler
// and transfer 25% fewer bytes through the video pipe; preserve limited BT.709 codes.
static class RecordingPixels
{
    internal static void ResizeNv12(ReadOnlySpan<byte> source,int sourceWidth,int sourceHeight,Span<byte> target,int width,int height)
    {
        target[..(width*height)].Fill(16);target.Slice(width*height,width*height/2).Fill(128);
        double factor=Math.Min(width/(double)sourceWidth,height/(double)sourceHeight);
        int w=Math.Max(2,(int)(sourceWidth*factor)&~1),h=Math.Max(2,(int)(sourceHeight*factor)&~1);
        int left=((width-w)/2)&~1,top=((height-h)/2)&~1;
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
            target[(top+y)*width+left+x]=source[(int)((long)y*sourceHeight/h)*sourceWidth+(int)((long)x*sourceWidth/w)];
        for(int y=0;y<h/2;y++)for(int x=0;x<w;x+=2)
        {
            int from=sourceWidth*sourceHeight+(int)((long)y*(sourceHeight/2)/(h/2))*sourceWidth+((int)((long)x*sourceWidth/w)&~1);
            int to=width*height+(top/2+y)*width+left+x;
            target[to]=source[from];target[to+1]=source[from+1];
        }
    }
    static readonly Vector128<byte> yMask=Vector128.Create((byte)1,3,5,7,9,11,13,15,128,128,128,128,128,128,128,128);
    static readonly Vector128<byte> uvMask=Vector128.Create((byte)0,2,4,6,8,10,12,14,128,128,128,128,128,128,128,128);
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Nv12(ReadOnlySpan<byte> uyvy,Span<byte> nv12,int width,int height)
    {
        if((width&1)!=0||(height&1)!=0||uyvy.Length<width*height*2||nv12.Length<width*height*3/2)throw new ArgumentException("NV12 requires complete even-sized frames.");
        ref byte source=ref MemoryMarshal.GetReference(uyvy);ref byte target=ref MemoryMarshal.GetReference(nv12);
        for(int row=0;row<height;row+=2)
        {
            int a=row*width*2,b=a+width*2,ya=row*width,yb=ya+width,uv=width*height+row/2*width;
            int x=0;
            if(Ssse3.IsSupported)for(;x+8<=width;x+=8)
            {
                var top=Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref source,a+x*2));
                var bottom=Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref source,b+x*2));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target,ya+x),Ssse3.Shuffle(top,yMask).AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target,yb+x),Ssse3.Shuffle(bottom,yMask).AsUInt64().GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target,uv+x),Ssse3.Shuffle(Sse2.Average(top,bottom),uvMask).AsUInt64().GetElement(0));
            }
            for(;x<width;x+=2)
            {
                nv12[ya+x]=uyvy[a+x*2+1];nv12[ya+x+1]=uyvy[a+x*2+3];
                nv12[yb+x]=uyvy[b+x*2+1];nv12[yb+x+1]=uyvy[b+x*2+3];
                nv12[uv+x]=(byte)((uyvy[a+x*2]+uyvy[b+x*2]+1)/2);
                nv12[uv+x+1]=(byte)((uyvy[a+x*2+2]+uyvy[b+x*2+2]+1)/2);
            }
        }
    }
}
