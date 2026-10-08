using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using System.Runtime.InteropServices;

namespace SdrCapture;

public sealed class ColorPipeline : IDisposable
{
    static readonly Lazy<byte[]> colorCode=new(()=>Compiler.Compile(Shader,"main","SdrColor.hlsl","cs_5_0").Span.ToArray());
    static readonly Lazy<byte[]> p010Code=new(()=>Compiler.Compile(PackShader,"main","P010.hlsl","cs_5_0").Span.ToArray());
    static readonly Lazy<byte[]> ndiCode=new(()=>Compiler.Compile(NdiShader,"main","NDI-UYVY.hlsl","cs_5_0").Span.ToArray());
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11ComputeShader shader;
    readonly ID3D11Buffer constants;
    readonly ID3D11ComputeShader packShader;
    readonly ID3D11ComputeShader ndiShader;
    ID3D11Buffer? ndiBuffer, ndiStaging;
    ID3D11UnorderedAccessView? ndiUav;
    ID3D11Buffer? p010, p010Staging;
    ID3D11UnorderedAccessView? p010Uav;
    ID3D11ShaderResourceView? outputSrv;
    byte[] packed = [];
    public CursorOverlay Cursor { get; }
    ID3D11Texture2D? input, output, staging;
    ID3D11ShaderResourceView? srv;
    ID3D11UnorderedAccessView? uav;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; } = [];
    Format inputFormat;
    readonly int canvasWidth,canvasHeight;
    int inputWidth,inputHeight;
    [StructLayout(LayoutKind.Sequential)] struct Parameters { public float Exposure; public uint Linear, Hdr, Padding; }
    // Input is linear scRGB for FP16. No PQ/HLG texture is accepted here.
    // Output is bounded SDR BT.709 RGB16, never an HDR pass-through.
    const string Shader = """
        Texture2D<float4> src : register(t0);
        Texture2D<uint> mouse : register(t1);
        RWTexture2D<float4> dst : register(u0);
        cbuffer Settings : register(b0) { float exposure; uint linearInput; uint hdr; uint pad; }
        cbuffer MouseSettings : register(b1) { int mouseX; int mouseY; uint mouseW; uint mouseH; uint mouseMode; uint3 unused; }
        float encode(float x) { return x < 0.018 ? 4.5*x : 1.099*pow(x,0.45)-0.099; }
        float decodeSrgb(float x) { return x <= 0.04045 ? x/12.92 : pow((x+0.055)/1.055,2.4); }
        float decode709(float x) { return x < 0.081 ? x/4.5 : pow((x+0.099)/1.099,1.0/0.45); }
        float encodeSrgb(float x) { return x <= 0.0031308 ? 12.92*x : 1.055*pow(x,1.0/2.4)-0.055; }
        float3 fromSrgb(float3 v) { return v; }
        float3 pointerBlend(float3 c, uint2 position) {
            int2 q = int2(position)-int2(mouseX,mouseY);
            if(mouseMode==0 || any(q<0) || q.x>=mouseW || q.y>=mouseH) return c;
            uint p = mouse.Load(int3(q,0));
            float3 rgb = float3((p>>16)&255,(p>>8)&255,p&255)/255.0;
            if(mouseMode==2) return lerp(c,fromSrgb(rgb),((p>>24)&255)/255.0);
            uint3 background = (uint3)round(saturate(c)*255);
            if(mouseMode==1) {
                uint a=(p&1)!=0?255:0, x=(p&2)!=0?255:0;
                return fromSrgb(((background&a)^x)/255.0);
            }
            uint3 value=uint3((p>>16)&255,(p>>8)&255,p&255);
            if((p>>24)!=0) value ^= background;
            return fromSrgb(value/255.0);
        }
        [numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) {
            uint w,h; dst.GetDimensions(w,h); if(id.x>=w || id.y>=h) return;
            uint sw,sh;src.GetDimensions(sw,sh);
            float2 p=(float2(id.xy)+.5)*float2(sw,sh)/float2(w,h)-.5;
            int2 q=(int2)floor(p),maximum=int2(sw-1,sh-1);float2 f=frac(p);
            float3 a=src.Load(int3(clamp(q,int2(0,0),maximum),0)).rgb;
            float3 b=src.Load(int3(clamp(q+int2(1,0),int2(0,0),maximum),0)).rgb;
            float3 c0=src.Load(int3(clamp(q+int2(0,1),int2(0,0),maximum),0)).rgb;
            float3 d=src.Load(int3(clamp(q+int2(1,1),int2(0,0),maximum),0)).rgb;
            float3 c=lerp(lerp(a,b,f.x),lerp(c0,d,f.x),f.y);
            c = float3(isfinite(c.r)?c.r:0, isfinite(c.g)?c.g:0, isfinite(c.b)?c.b:0);
            if(linearInput != 0) {
                c = max(c*exposure,0);
                if(hdr != 0) {
                    float peak = max(c.r,max(c.g,c.b));
                    // Hue-preserving shoulder, fixed in time (no automatic exposure pumping).
                    float mapped = peak <= 0.75 ? peak : 0.75+0.25*(1-exp(-(peak-0.75)/0.25));
                    if(peak>0) c *= mapped/peak;
                }
            }
            else c = float3(decodeSrgb(c.r),decodeSrgb(c.g),decodeSrgb(c.b));
            // Desktop pixels are display referred. Preserve sRGB shadow brightness;
            // applying the camera BT.709 OETF here was darkening SDR screen content.
            c = float3(encodeSrgb(c.r),encodeSrgb(c.g),encodeSrgb(c.b));
            c = pointerBlend(saturate(c), (uint2)clamp(p+0.5,float2(0,0),float2(sw-1,sh-1)));
            dst[id.xy] = float4(saturate(c),1);
        }
        """;
    public ColorPipeline(ID3D11Device device, ID3D11DeviceContext context,int canvasWidth=0,int canvasHeight=0)
    {
        this.canvasWidth=canvasWidth;this.canvasHeight=canvasHeight;
        this.device = device; this.context = context;
        shader = device.CreateComputeShader(colorCode.Value);
        constants = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
        Cursor = new(device, context);
        packShader = device.CreateComputeShader(p010Code.Value);
        ndiShader = device.CreateComputeShader(ndiCode.Value);
    }
    // The established SDR RGB transform above is unchanged. Pack its result as BT.709
    // limited UYVY, the native 4:2:2 input to NDI High Bandwidth.
    const string NdiShader = """
        Texture2D<float4> rgb : register(t0);
        RWByteAddressBuffer packed : register(u0);
        [numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) {
            uint w,h; rgb.GetDimensions(w,h); uint2 p=uint2(id.x*2,id.y);
            if(p.x>=w || p.y>=h) return;
            float3 a=rgb.Load(int3(p,0)).rgb, b=rgb.Load(int3(p+uint2(1,0),0)).rgb;
            float ya=dot(a,float3(0.2126,0.7152,0.0722)), yb=dot(b,float3(0.2126,0.7152,0.0722));
            float3 c=(a+b)*0.5; float y=(ya+yb)*0.5;
            uint y0=(uint)round(clamp(16+219*ya,16,235)), y1=(uint)round(clamp(16+219*yb,16,235));
            uint u=(uint)round(clamp(128+224*(c.b-y)/1.8556,16,240));
            uint v=(uint)round(clamp(128+224*(c.r-y)/1.5748,16,240));
            packed.Store((p.y*w+p.x)*2,u|(y0<<8)|(v<<16)|(y1<<24));
        }
        """;
    // RGB16 BT.709 -> limited-range P010. Centered 2x2 chroma; one thread owns a whole block.
    const string PackShader = """
        Texture2D<float4> rgb : register(t0);
        RWByteAddressBuffer packed : register(u0);
        uint yCode(float3 c) { return (uint)round(64+876*dot(c,float3(0.2126,0.7152,0.0722))) << 6; }
        [numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) {
            uint w,h; rgb.GetDimensions(w,h); uint2 p=id.xy*2;
            if(p.x>=w || p.y>=h) return;
            float3 a=rgb.Load(int3(p,0)).rgb, b=rgb.Load(int3(p+uint2(1,0),0)).rgb;
            float3 c=rgb.Load(int3(p+uint2(0,1),0)).rgb, d=rgb.Load(int3(p+uint2(1,1),0)).rgb;
            packed.Store((p.y*w+p.x)*2, yCode(a)|(yCode(b)<<16));
            packed.Store(((p.y+1)*w+p.x)*2, yCode(c)|(yCode(d)<<16));
            float3 avg=(a+b+c+d)*0.25; float y=dot(avg,float3(0.2126,0.7152,0.0722));
            uint u=(uint)round(clamp(512+896*(avg.b-y)/1.8556,64,960))<<6;
            uint v=(uint)round(clamp(512+896*(avg.r-y)/1.5748,64,960))<<6;
            packed.Store(w*h*2+(id.y*w+p.x)*2, u|(v<<16));
        }
        """;
    void Allocate(Texture2DDescription d)
    {
        if (input != null && inputWidth == d.Width && inputHeight == d.Height && inputFormat == d.Format) return;
        FreeTextures();inputWidth=(int)d.Width;inputHeight=(int)d.Height;
        Width=canvasWidth>0?canvasWidth:inputWidth;Height=canvasHeight>0?canvasHeight:inputHeight;inputFormat=d.Format;
        d.BindFlags = BindFlags.ShaderResource; d.Usage = ResourceUsage.Default; d.CPUAccessFlags = CpuAccessFlags.None; d.MiscFlags = ResourceOptionFlags.None;
        input = device.CreateTexture2D(d); srv = device.CreateShaderResourceView(input);
        d.Width=(uint)Width;d.Height=(uint)Height;
        d.Format = Format.R16G16B16A16_UNorm; d.BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource;
        output = device.CreateTexture2D(d); uav = device.CreateUnorderedAccessView(output);
        outputSrv = device.CreateShaderResourceView(output);
        d.BindFlags = BindFlags.None; d.Usage = ResourceUsage.Staging; d.CPUAccessFlags = CpuAccessFlags.Read;
        staging = device.CreateTexture2D(d); Pixels = new byte[checked(Width * Height * 8)];
        if ((Width & 1) == 0)
        {
            uint count=checked((uint)(Width*Height*2));
            ndiBuffer=device.CreateBuffer(count,BindFlags.UnorderedAccess,ResourceUsage.Default,CpuAccessFlags.None,ResourceOptionFlags.BufferAllowRawViews);
            ndiUav=device.CreateUnorderedAccessView(ndiBuffer,new UnorderedAccessViewDescription(ndiBuffer,Format.R32_Typeless,0,count/4,BufferUnorderedAccessViewFlags.Raw));
            ndiStaging=device.CreateBuffer(count,BindFlags.None,ResourceUsage.Staging,CpuAccessFlags.Read);
        }
        if ((Width & 1) == 0 && (Height & 1) == 0)
        {
            uint count = checked((uint)(Width * Height * 3));
            p010 = device.CreateBuffer(count, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews);
            p010Uav = device.CreateUnorderedAccessView(p010, new UnorderedAccessViewDescription(p010, Format.R32_Typeless, 0, count / 4, BufferUnorderedAccessViewFlags.Raw));
            p010Staging = device.CreateBuffer(count, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            packed = new byte[count];
        }
    }
    public unsafe byte[] Convert(ID3D11Texture2D source, bool hdr, float exposure)
    {
        Render(source, hdr, exposure);
        context.CopyResource(staging!, output!);
        var map = context.Map(staging!, 0, MapMode.Read);
        try
        {
            fixed (byte* dst = Pixels)
            for (int y = 0; y < Height; y++)
                System.Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, dst + y * Width * 8, Width * 8, Width * 8);
        }
        finally { context.Unmap(staging!, 0); }
        return Pixels;
    }
    public unsafe byte[] ConvertP010(ID3D11Texture2D source, bool hdr, float exposure)
    {
        Render(source, hdr, exposure);
        if (p010 == null) throw new NotSupportedException("P010 requires even dimensions.");
        context.CSSetShader(packShader); context.CSSetShaderResource(0, outputSrv); context.CSSetUnorderedAccessView(0, p010Uav);
        context.Dispatch(((uint)Width + 15) / 16, ((uint)Height + 15) / 16, 1);
        context.CSSetShaderResource(0, null); context.CSSetUnorderedAccessView(0, null);
        context.CopyResource(p010Staging!, p010);
        var map = context.Map(p010Staging!, MapMode.Read);
        try { fixed(byte* dst=packed) System.Buffer.MemoryCopy((void*)map.DataPointer, dst, packed.Length, packed.Length); }
        finally { context.Unmap(p010Staging!, 0); }
        return packed;
    }
    public unsafe VideoBuffer ConvertNdi(ID3D11Texture2D source,bool hdr,float exposure)
    {
        Render(source,hdr,exposure);
        if(ndiBuffer==null) throw new NotSupportedException("NDI requires an even monitor width.");
        context.CSSetShader(ndiShader);context.CSSetShaderResource(0,outputSrv);context.CSSetUnorderedAccessView(0,ndiUav);
        context.Dispatch(((uint)Width+15)/16,((uint)Height+7)/8,1);
        context.CSSetShaderResource(0,null);context.CSSetUnorderedAccessView(0,null);
        context.CopyResource(ndiStaging!,ndiBuffer);
        var frame=new VideoBuffer(Width,Height);
        try
        {
            var map=context.Map(ndiStaging!,MapMode.Read);
            try{fixed(byte* dst=frame.Data) System.Buffer.MemoryCopy((void*)map.DataPointer,dst,frame.Length,frame.Length);}
            finally{context.Unmap(ndiStaging!,0);}
            return frame;
        }
        catch{frame.Dispose();throw;}
    }
    void Render(ID3D11Texture2D source, bool hdr, float exposure)
    {
        var d = source.Description;
        bool linear = d.Format == Format.R16G16B16A16_Float;
        if ((!linear && d.Format != Format.B8G8R8A8_UNorm && d.Format != Format.R8G8B8A8_UNorm) || (hdr && !linear))
            throw new InvalidOperationException($"Unsafe capture format {d.Format} for HDR={hdr}; capture stopped.");
        if (!float.IsFinite(exposure) || exposure <= 0) throw new ArgumentOutOfRangeException(nameof(exposure));
        Allocate(d);
        context.CopyResource(input!, source);
        var parameters = new Parameters { Exposure = exposure, Linear = linear ? 1u : 0u, Hdr = hdr ? 1u : 0u };
        context.UpdateSubresource(in parameters, constants);
        context.CSSetShader(shader); context.CSSetConstantBuffer(0, constants);
        Cursor.Bind();
        context.CSSetShaderResource(0, srv); context.CSSetUnorderedAccessView(0, uav);
        context.Dispatch(((uint)Width + 7) / 8, ((uint)Height + 7) / 8, 1);
        context.CSSetShaderResource(0, null); context.CSSetShaderResource(1, null); context.CSSetUnorderedAccessView(0, null);
    }
    void FreeTextures() { srv?.Dispose(); uav?.Dispose(); outputSrv?.Dispose(); input?.Dispose(); output?.Dispose(); staging?.Dispose(); p010Uav?.Dispose(); p010?.Dispose(); p010Staging?.Dispose(); ndiUav?.Dispose();ndiBuffer?.Dispose();ndiStaging?.Dispose();ndiBuffer=null;p010=null; input = null; }
    public void Dispose() { FreeTextures(); Cursor.Dispose(); ndiShader.Dispose();packShader.Dispose(); constants.Dispose(); shader.Dispose(); }
}
