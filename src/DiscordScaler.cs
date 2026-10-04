using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace SdrCapture;
// Resize decoded SDR on the receiving GPU. No HDR transform or changes to the sending pipeline.
sealed class DiscordScaler:IDisposable
{
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11ComputeShader shader;
    readonly ID3D11Buffer output,staging;
    readonly ID3D11UnorderedAccessView uav;
    ID3D11Texture2D? input;
    ID3D11ShaderResourceView? srv;
    int width,height;
    const string Code="""
        Texture2D<float4> image : register(t0);
        RWByteAddressBuffer result : register(u0);
        [numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) {
            if(id.x>=1920 || id.y>=1080)return;
            uint w,h;image.GetDimensions(w,h);
            float scale=min(1920.0/w,1080.0/h);
            float2 extent=float2(w,h)*scale, offset=(float2(1920,1080)-extent)*.5;
            float2 local=float2(id.xy)+.5-offset;
            float3 color=0;
            if(all(local>=0)&&all(local<extent)) {
                float2 p=local/scale-.5;int2 q=(int2)floor(p);float2 f=frac(p);int2 maximum=int2(w-1,h-1);
                float3 a=image.Load(int3(clamp(q,int2(0,0),maximum),0)).rgb;
                float3 b=image.Load(int3(clamp(q+int2(1,0),int2(0,0),maximum),0)).rgb;
                float3 c=image.Load(int3(clamp(q+int2(0,1),int2(0,0),maximum),0)).rgb;
                float3 d=image.Load(int3(clamp(q+int2(1,1),int2(0,0),maximum),0)).rgb;
                color=lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
            }
            uint3 v=(uint3)round(saturate(color)*255);
            result.Store((id.y*1920+id.x)*4,v.b|(v.g<<8)|(v.r<<16)|0xff000000);
        }
        """;
    public DiscordScaler()
    {
        device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.BgraSupport,FeatureLevel.Level_11_0);context=device.ImmediateContext;
        shader=device.CreateComputeShader(Compiler.Compile(Code,"main","DiscordResize.hlsl","cs_5_0").Span);
        output=device.CreateBuffer(CameraFrames.Bytes,BindFlags.UnorderedAccess,ResourceUsage.Default,CpuAccessFlags.None,ResourceOptionFlags.BufferAllowRawViews);
        uav=device.CreateUnorderedAccessView(output,new UnorderedAccessViewDescription(output,Format.R32_Typeless,0,CameraFrames.Bytes/4,BufferUnorderedAccessViewFlags.Raw));
        staging=device.CreateBuffer(CameraFrames.Bytes,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read);
    }
    public void Write(NdiNative.Video frame,CameraFrames frames)
    {
        if(input==null||width!=frame.Width||height!=frame.Height)
        {
            srv?.Dispose();input?.Dispose();width=frame.Width;height=frame.Height;
            input=device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)width,(uint)height,1,1,BindFlags.ShaderResource));srv=device.CreateShaderResourceView(input);
        }
        context.UpdateSubresource(new MappedSubresource(frame.Data,(uint)frame.Stride,0),input);
        context.CSSetShader(shader);context.CSSetShaderResource(0,srv);context.CSSetUnorderedAccessView(0,uav);
        context.Dispatch(240,135,1);context.CSSetShaderResource(0,null);context.CSSetUnorderedAccessView(0,null);
        context.CopyResource(staging,output);var data=context.Map(staging,MapMode.Read);
        try{DiscordReceiver.TestFrame?.Invoke(System.Runtime.InteropServices.Marshal.ReadInt32(frame.Data,(height/2)*frame.Stride+width/2*4),System.Runtime.InteropServices.Marshal.ReadInt32(data.DataPointer,(1080/2)*1920*4+1920/2*4));frames.Write(data.DataPointer,1920*4);}
        finally{context.Unmap(staging,0);}
    }
    public void Dispose(){srv?.Dispose();input?.Dispose();staging.Dispose();uav.Dispose();output.Dispose();shader.Dispose();context.Dispose();device.Dispose();}
}
