using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;

static class GpuReadbackTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        using var device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.BgraSupport,FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;
        using var asyncColor=new ColorPipeline(device,context);using var reference=new ColorPipeline(device,context);
        foreach(int size in new[]{8,64})foreach(bool hdr in new[]{false,true})
        {
            var expected=new List<byte[]>();using var texture=device.CreateTexture2D(new Texture2DDescription(hdr?Format.R16G16B16A16_Float:Format.R8G8B8A8_UNorm,(uint)size,(uint)size,1,1,BindFlags.ShaderResource));
            for(int index=0;index<3;index++)
            {
                if(hdr)
                {
                    Half[] data=new Half[size*size*4];for(int at=0;at<data.Length;at+=4){data[at]=(Half)(index*.6);data[at+1]=(Half).1;data[at+2]=(Half)(.8-index*.3);data[at+3]=(Half)1;}
                    context.UpdateSubresource(data,texture,0,(uint)(size*8));
                }
                else
                {
                    byte[] data=new byte[size*size*4];for(int at=0;at<data.Length;at+=4){data[at]=(byte)(index*100);data[at+1]=50;data[at+2]=(byte)(250-index*100);data[at+3]=255;}
                    context.UpdateSubresource(data,texture,0,(uint)(size*4));
                }
                Require(asyncColor.QueueNdi(texture,hdr,1,100+index),"GPU queue rejected an available slot.");
                using var comparison=reference.ConvertNdi(texture,hdr,1);expected.Add(comparison.Data.ToArray());
            }
            Require(!asyncColor.QueueNdi(texture,hdr,1,999),"GPU readback queue was not bounded.");
            for(int index=0;index<3;index++)
            {
                VideoBuffer? frame=null;var clock=Stopwatch.StartNew();
                while(frame==null&&clock.ElapsedMilliseconds<2000){frame=asyncColor.TryReadNdi();if(frame==null)Thread.Yield();}
                using(frame){Require(frame!=null&&frame.CapturedAt==100+index&&frame.Data.AsSpan(0,frame.Length).SequenceEqual(expected[index]),"Asynchronous SDR readback changed frame pixels, order or timestamps.");}
            }
            Require(asyncColor.TryReadNdi()==null,"GPU queue repeated a consumed frame.");
            Require(asyncColor.ReadbackBytes==size*size*2*3,"GPU readback memory was not bounded to three surfaces.");
        }
        using(var small=device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,8,8,1,1,BindFlags.ShaderResource)))
        using(var resized=device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,16,16,1,1,BindFlags.ShaderResource)))
        {
            context.UpdateSubresource(Enumerable.Repeat((byte)255,16*16*4).ToArray(),resized,0,64);
            Require(asyncColor.QueueNdi(small,false,1,1)&&asyncColor.QueueNdi(small,false,1,2),"Pending-resize fixture failed.");
            Require(asyncColor.QueueNdi(resized,false,1,99),"Resized frame was not accepted.");
            VideoBuffer? frame=null;var clock=Stopwatch.StartNew();
            while(frame==null&&clock.ElapsedMilliseconds<2000){frame=asyncColor.TryReadNdi();if(frame==null)Thread.Yield();}
            using(frame)Require(frame is {Width:16,Height:16,CapturedAt:99}&&frame.Data[1]==235,"Resize returned a stale pending frame or changed its pixels.");
            Require(asyncColor.TryReadNdi()==null,"Resize retained old-format pending readbacks.");
        }
        Program.Write("gpu-readback-tests.json",new{Pass=true,RealD3D11=true,SameSdrPixelsAsNdi=true,HdrAndSdr=true,FrameOrderAndTimestamps=true,QueueCapacity=3,ResizeAndFormatReset=true});
    }
}
