using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
namespace SdrCapture;

// Separate from the established NDI pipeline. Captures, rotates and scales on the GPU.
sealed class KvmGpuCapture:IDisposable
{
    readonly IDXGIFactory1 factory;readonly IDXGIAdapter1 adapter;readonly IDXGIOutput6 output;
    readonly ID3D11Device device;readonly ID3D11DeviceContext context;readonly IDXGIOutputDuplication duplication;
    readonly KvmGpuReadback readback;readonly DisplayInfo display;readonly ModeRotation rotation;
    public KvmGpuCapture(DisplayInfo display,int width,int height)
    {
        this.display=display;
        try
        {
            factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();factory.EnumAdapters1(display.Adapter,out adapter).CheckError();adapter.EnumOutputs(display.Output,out var raw).CheckError();
            using(raw)output=raw.QueryInterface<IDXGIOutput6>();rotation=output.Description1.Rotation;
            D3D11.D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport,[FeatureLevel.Level_11_0],out device,out context).CheckError();
            using var output5=output.QueryInterface<IDXGIOutput5>();duplication=output5.DuplicateOutput1(device,display.Hdr?[Format.R16G16B16A16_Float]:[Format.B8G8R8A8_UNorm]);
            readback=new(device,context,width,height);
        }
        catch{Dispose();throw;}
    }
    public bool Read(Bitmap target)
    {
        var current=output.Description1;
        if(!current.AttachedToDesktop||current.Rotation!=rotation||(current.ColorSpace==ColorSpaceType.RgbFullG2084NoneP2020)!=display.Hdr||current.DesktopCoordinates.Right-current.DesktopCoordinates.Left!=display.Width||current.DesktopCoordinates.Bottom-current.DesktopCoordinates.Top!=display.Height)throw new CaptureResetException("Размер или режим экрана изменился. Переподключаю просмотр.");
        var result=duplication.AcquireNextFrame(0,out _,out var resource);if(result.Code==unchecked((int)0x887A0027))return false;result.CheckError();
        try{using(resource)using(var texture=resource.QueryInterface<ID3D11Texture2D>())readback.Read(texture,target,(int)rotation,display.Hdr?(float)(80/WhiteLevel.Read(display.Device)):1);return true;}
        finally{duplication.ReleaseFrame().CheckError();}
    }
    public void Dispose(){readback?.Dispose();duplication?.Dispose();context?.Dispose();device?.Dispose();output?.Dispose();adapter?.Dispose();factory?.Dispose();}
}

sealed class KvmGpuReadback:IDisposable
{
    static readonly Lazy<byte[]> code=new(()=>Compiler.Compile(Shader,"main","KvmBgra.hlsl","cs_5_0").Span.ToArray());
    readonly ID3D11Device device;readonly ID3D11DeviceContext context;readonly ID3D11ComputeShader shader;readonly ID3D11Buffer constants,output,staging;readonly ID3D11UnorderedAccessView uav;
    ID3D11Texture2D? input;ID3D11ShaderResourceView? srv;readonly int width,height;
    [StructLayout(LayoutKind.Sequential)]struct Parameters{public uint Width,Height,Rotation,Linear;public float Exposure,Pad1,Pad2,Pad3;}
    const string Shader="""
        Texture2D<float4> source : register(t0); RWByteAddressBuffer result : register(u0);
        cbuffer Settings : register(b0) { uint width,height,rotation,linearInput; float exposure; float3 unused; }
        float srgb(float c){return c<=0.0031308?c*12.92:1.055*pow(c,1.0/2.4)-.055;}
        [numthreads(8,8,1)] void main(uint3 id:SV_DispatchThreadID) {
            if(id.x>=width||id.y>=height)return; uint w,h;source.GetDimensions(w,h);
            bool quarter=rotation==2||rotation==4;
            float2 p=(float2(id.xy)+.5)*float2(quarter?h:w,quarter?w:h)/float2(width,height)-.5;
            if(rotation==2)p=float2(p.y,h-1-p.x);
            else if(rotation==3)p=float2(w-1-p.x,h-1-p.y);
            else if(rotation==4)p=float2(w-1-p.y,p.x);
            int2 q=(int2)floor(p),maximum=int2(w-1,h-1);float2 f=frac(p);
            float3 a=source.Load(int3(clamp(q,int2(0,0),maximum),0)).rgb;
            float3 b=source.Load(int3(clamp(q+int2(1,0),int2(0,0),maximum),0)).rgb;
            float3 c=source.Load(int3(clamp(q+int2(0,1),int2(0,0),maximum),0)).rgb;
            float3 d=source.Load(int3(clamp(q+int2(1,1),int2(0,0),maximum),0)).rgb;
            float3 color=lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
            if(linearInput!=0){color=max(color*exposure,0);float peak=max(color.r,max(color.g,color.b));float mapped=peak<=.75?peak:.75+.25*(1-exp(-(peak-.75)/.25));if(peak>0)color*=mapped/peak;color=float3(srgb(color.r),srgb(color.g),srgb(color.b));}
            uint3 v=(uint3)round(saturate(color)*255);result.Store((id.y*width+id.x)*4,v.b|(v.g<<8)|(v.r<<16)|0xff000000);
        }
        """;
    public KvmGpuReadback(ID3D11Device device,ID3D11DeviceContext context,int width,int height)
    {
        this.device=device;this.context=context;this.width=width;this.height=height;
        shader=device.CreateComputeShader(code.Value);constants=device.CreateBuffer(new BufferDescription(32,BindFlags.ConstantBuffer,ResourceUsage.Default));
        uint bytes=checked((uint)(width*height*4));output=device.CreateBuffer(bytes,BindFlags.UnorderedAccess,ResourceUsage.Default,CpuAccessFlags.None,ResourceOptionFlags.BufferAllowRawViews);
        uav=device.CreateUnorderedAccessView(output,new UnorderedAccessViewDescription(output,Format.R32_Typeless,0,bytes/4,BufferUnorderedAccessViewFlags.Raw));staging=device.CreateBuffer(bytes,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read);
    }
    public unsafe void Read(ID3D11Texture2D texture,Bitmap target,int rotation,float exposure)
    {
        var description=texture.Description;
        if(input==null||input.Description.Width!=description.Width||input.Description.Height!=description.Height||input.Description.Format!=description.Format)
        {srv?.Dispose();input?.Dispose();description.BindFlags=BindFlags.ShaderResource;description.CPUAccessFlags=CpuAccessFlags.None;description.Usage=ResourceUsage.Default;description.MiscFlags=ResourceOptionFlags.None;input=device.CreateTexture2D(description);srv=device.CreateShaderResourceView(input);}
        context.CopyResource(input,texture);var parameters=new Parameters{Width=(uint)width,Height=(uint)height,Rotation=(uint)rotation,Linear=description.Format==Format.R16G16B16A16_Float?1u:0,Exposure=exposure};context.UpdateSubresource(in parameters,constants);
        context.CSSetShader(shader);context.CSSetConstantBuffer(0,constants);context.CSSetShaderResource(0,srv);context.CSSetUnorderedAccessView(0,uav);context.Dispatch(((uint)width+7)/8,((uint)height+7)/8,1);context.CSSetShaderResource(0,null);context.CSSetUnorderedAccessView(0,null);
        context.CopyResource(staging,output);var map=context.Map(staging,MapMode.Read);
        try{var bits=target.LockBits(new(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);try{for(int y=0;y<height;y++)Buffer.MemoryCopy((byte*)map.DataPointer+y*width*4,(byte*)bits.Scan0+y*bits.Stride,width*4,width*4);}finally{target.UnlockBits(bits);}}
        finally{context.Unmap(staging,0);}
    }
    public void Dispose(){srv?.Dispose();input?.Dispose();staging.Dispose();uav.Dispose();output.Dispose();constants.Dispose();shader.Dispose();}
}
