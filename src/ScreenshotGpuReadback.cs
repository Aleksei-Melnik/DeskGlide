using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace SdrCapture;

// Native-resolution screenshot conversion only. The FP16 source stays on the
// GPU until HDR tone mapping is complete; no intermediate 8-bit HDR clipping.
sealed class ScreenshotGpuReadback:IDisposable
{
    static readonly Lazy<byte[]> code=new(()=>Compiler.Compile(Shader,"main","ScreenshotSdr.hlsl","cs_5_0").Span.ToArray());
    readonly ID3D11Device device;readonly ID3D11DeviceContext context;readonly int width,height;
    readonly ID3D11ComputeShader shader;readonly ID3D11Buffer constants,output,staging;readonly ID3D11UnorderedAccessView uav;
    [StructLayout(LayoutKind.Sequential)]struct Parameters{public uint Width,Height,Rotation,Hdr;public float Exposure,Pad1,Pad2,Pad3;}
    const string Shader="""
        Texture2D<float4> source : register(t0); RWByteAddressBuffer result : register(u0);
        cbuffer Settings : register(b0) { uint width,height,rotation,hdr; float exposure; float3 unused; }
        float srgb(float c){return c<=0.0031308?c*12.92:1.055*pow(c,1.0/2.4)-.055;}
        [numthreads(8,8,1)] void main(uint3 id:SV_DispatchThreadID) {
            if(id.x>=width||id.y>=height)return; uint w,h;source.GetDimensions(w,h);
            uint2 p=id.xy;
            if(rotation==2)p=uint2(id.y,h-1-id.x);
            else if(rotation==3)p=uint2(w-1-id.x,h-1-id.y);
            else if(rotation==4)p=uint2(w-1-id.y,id.x);
            float3 c=source.Load(int3(p,0)).rgb;
            c=float3(isfinite(c.r)?c.r:0,isfinite(c.g)?c.g:0,isfinite(c.b)?c.b:0);
            if(hdr!=0){c=max(c*exposure,0);float peak=max(c.r,max(c.g,c.b));float mapped=peak<=.75?peak:.75+.25*(1-exp(-(peak-.75)/.25));if(peak>0)c*=mapped/peak;}
            c=float3(srgb(c.r),srgb(c.g),srgb(c.b));
            uint3 v=(uint3)round(saturate(c)*255);result.Store((id.y*width+id.x)*4,v.b|(v.g<<8)|(v.r<<16)|0xff000000);
        }
        """;
    internal ScreenshotGpuReadback(ID3D11Device device,ID3D11DeviceContext context,int width,int height)
    {
        this.device=device;this.context=context;this.width=width;this.height=height;
        try
        {
            shader=device.CreateComputeShader(code.Value);constants=device.CreateBuffer(new BufferDescription(32,BindFlags.ConstantBuffer,ResourceUsage.Default));
            uint bytes=checked((uint)(width*height*4));output=device.CreateBuffer(bytes,BindFlags.UnorderedAccess,ResourceUsage.Default,CpuAccessFlags.None,ResourceOptionFlags.BufferAllowRawViews);
            uav=device.CreateUnorderedAccessView(output,new UnorderedAccessViewDescription(output,Format.R32_Typeless,0,bytes/4,BufferUnorderedAccessViewFlags.Raw));staging=device.CreateBuffer(bytes,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read);
        }
        catch{Dispose();throw;}
    }
    internal unsafe byte[] Read(ID3D11Texture2D texture,ModeRotation rotation,bool hdr,double whiteNits)
    {
        var d=texture.Description;d.BindFlags=BindFlags.ShaderResource;d.CPUAccessFlags=CpuAccessFlags.None;d.Usage=ResourceUsage.Default;d.MiscFlags=ResourceOptionFlags.None;
        using var input=device.CreateTexture2D(d);using var srv=device.CreateShaderResourceView(input);
        context.CopyResource(input,texture);var parameters=new Parameters{Width=(uint)width,Height=(uint)height,Rotation=(uint)rotation,Hdr=hdr?1u:0,Exposure=(float)(80/Math.Clamp(whiteNits,1,10000))};context.UpdateSubresource(in parameters,constants);
        context.CSSetShader(shader);context.CSSetConstantBuffer(0,constants);context.CSSetShaderResource(0,srv);context.CSSetUnorderedAccessView(0,uav);
        context.Dispatch(((uint)width+7)/8,((uint)height+7)/8,1);context.CSSetShaderResource(0,null);context.CSSetUnorderedAccessView(0,null);
        context.CopyResource(staging,output);var map=context.Map(staging,MapMode.Read);
        try{byte[] bytes=new byte[checked(width*height*4)];new ReadOnlySpan<byte>((void*)map.DataPointer,bytes.Length).CopyTo(bytes);return bytes;}
        finally{context.Unmap(staging,0);}
    }
    public void Dispose(){staging?.Dispose();uav?.Dispose();output?.Dispose();constants?.Dispose();shader?.Dispose();}
}
