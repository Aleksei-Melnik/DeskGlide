using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;
static class GameStabilityTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    public static void Run()
    {
        using var device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.BgraSupport,FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;
        // Real shader: 5:4 game -> 16:9 native canvas. Every corner must contain
        // image content; letterboxing or stretching only at the camera fails here.
        using var stretch=new ColorPipeline(device,context,2560,1440);
        using var source=device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,1280,1024,1,1,BindFlags.ShaderResource));
        var pixels=new byte[1280*1024*4];Array.Fill(pixels,(byte)255);context.UpdateSubresource(pixels,source,0,1280*4);
        using(var frame=stretch.ConvertNdi(source,false,1))
        {
            Require(frame.Width==2560&&frame.Height==1440,"Native canvas changed to game resolution");
            foreach(int offset in new[]{0,frame.Length-4,2560*2*719})Require(frame.Data[offset+1]==235&&frame.Data[offset+3]==235,"Black border added during stretch");
        }
        using var colors=new ColorPipeline(device,context);
        using var ramp=device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,256,2,1,1,BindFlags.ShaderResource));
        var gray=new byte[256*2*4];for(int y=0;y<2;y++)for(int x=0;x<256;x++){int p=(y*256+x)*4;gray[p]=gray[p+1]=gray[p+2]=(byte)x;gray[p+3]=255;}
        context.UpdateSubresource(gray,ramp,0,256*4);
        var result=MemoryMarshal.Cast<byte,ushort>(colors.Convert(ramp,false,1));
        for(int x=0;x<256;x++)Require(Math.Abs(result[x*4]/257.0-x)<1,"SDR shadow ramp was darkened at "+x);
        Require(result[0]==0&&result[255*4]==65535,"Black or white endpoint altered");
        var command=RecordingFolder.Command(@"\\Aurora\f\Record\With spaces");
        Require(Path.GetFileName(command.FileName)=="explorer.exe"&&command.ArgumentList.Single()==@"\\Aurora\f\Record\With spaces"&&!command.UseShellExecute,"UNC folder shell command corrupted");
        var monitor=DisplayInfo.All().First();var native=PreferredResolution.Read(monitor.Device,monitor.Width,monitor.Height);
        Program.Write("game-stability-tests.json",new{Pass=true,Stretch="1280x1024 -> 2560x1440",NoBorders=true,GrayRamp=256,WhitePreserved=true,NetworkFolder=true,NativeWidth=native.Width,NativeHeight=native.Height});
    }
}
