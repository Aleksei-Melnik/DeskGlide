using System.Diagnostics;
namespace SdrCapture;
static class RecordingPixelTests
{
    public static void Run()
    {
        var random=new Random(73);
        foreach(int width in new[]{6,8,10,16,18,30,32,34,62,64,66,2560})
        {
            int height=width==2560?1440:6;byte[] input=new byte[width*height*2],output=new byte[width*height*3/2];random.NextBytes(input);
            RecordingPixels.Nv12(input,output,width,height);
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                if(output[y*width+x]!=input[(y*width+x)*2+1])throw new Exception("Luma code changed");
            for(int y=0;y<height;y+=2)for(int x=0;x<width;x+=2)for(int c=0;c<2;c++)
                if(output[width*height+y/2*width+x+c]!=(input[(y*width+x)*2+c*2]+input[((y+1)*width+x)*2+c*2]+1)/2)throw new Exception("Wrong chroma resampling");
        }
        byte[] full=new byte[2560*1440*2],packed=new byte[2560*1440*3/2];
        foreach(int fps in new[]{60,120})for(int frame=0;frame<fps*120;frame++)
        {
            double boundary=ReplaySaveTime.Boundary((frame+.4)/fps,fps);
            if(Math.Abs(boundary-(frame+1.0)/fps)>1e-9||Math.Abs(ReplaySaveTime.Boundary(boundary+1e-8,fps)-boundary)>1e-9)throw new Exception("Save boundary consumed or repeated a frame");
            if(Math.Abs(ReplaySaveTime.Packet(boundary)-boundary)>.000501)throw new Exception("Packet boundary lost precision");
        }
        var clock=Stopwatch.StartNew();for(int i=0;i<120;i++)RecordingPixels.Nv12(full,packed,2560,1440);
        Program.Write("recording-pixels-test.json",new{Pass=true,Frames=120,MeanMilliseconds=clock.Elapsed.TotalMilliseconds/120,PipeBytes=packed.Length,ScalarTail=true,SaveBoundarySeconds=120,SaveBoundaryFps=new[]{60,120}});
    }
}
