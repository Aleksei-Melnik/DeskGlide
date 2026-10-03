using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;
public sealed class CursorOverlay : IDisposable
{
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11Buffer constants;
    ID3D11Texture2D? texture;
    ID3D11ShaderResourceView? view;
    byte[] shape = [];
    uint[] pixels = [];
    [StructLayout(LayoutKind.Sequential)] struct Params { public int X,Y; public uint W,H,Mode,P1,P2,P3; }
    Params parameters;
    bool visible;
    public bool Enabled { get; set; } = true;
    public CursorOverlay(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device=device; this.context=context;
        constants=device.CreateBuffer(new BufferDescription(32,BindFlags.ConstantBuffer,ResourceUsage.Default));
    }
    public unsafe void Update(IDXGIOutputDuplication duplication, OutduplFrameInfo frame)
    {
        if(frame.LastMouseUpdateTime != 0)
        {
            parameters.X=frame.PointerPosition.Position.X; parameters.Y=frame.PointerPosition.Position.Y;
            visible=frame.PointerPosition.Visible;
        }
        if(frame.PointerShapeBufferSize==0) return;
        if(shape.Length<frame.PointerShapeBufferSize) shape=new byte[frame.PointerShapeBufferSize];
        fixed(byte* data=shape)
        {
            duplication.GetFramePointerShape((uint)shape.Length,(IntPtr)data,out _,out var info).CheckError();
            UploadShape(info, shape);
        }
    }
    public void UploadShape(OutduplPointerShapeInfo info, byte[] data)
    {
        uint w=info.Width, h=info.Type==(uint)PointerShapeType.Monochrome?info.Height/2:info.Height;
        if(w==0 || h==0 || w>4096 || h>4096) throw new InvalidOperationException("Invalid cursor dimensions.");
        if (data.Length < checked((int)(info.Pitch * info.Height))) throw new InvalidOperationException("Truncated cursor shape.");
        if(parameters.W!=w || parameters.H!=h || texture==null)
        {
            view?.Dispose(); texture?.Dispose();
            texture=device.CreateTexture2D(new Texture2DDescription(Format.R32_UInt,w,h,1,1,BindFlags.ShaderResource));
            view=device.CreateShaderResourceView(texture); pixels=new uint[w*h];
        }
        for(uint y=0;y<h;y++) for(uint x=0;x<w;x++)
        {
            if(info.Type==(uint)PointerShapeType.Monochrome)
            {
                int bit=7-(int)(x%8);
                uint and=(uint)(data[y*info.Pitch+x/8]>>bit)&1;
                uint xor=(uint)(data[(y+h)*info.Pitch+x/8]>>bit)&1;
                pixels[y*w+x]=and|(xor<<1);
            }
            else pixels[y*w+x]=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(y*info.Pitch+x*4),4));
        }
        context.UpdateSubresource(pixels,texture,0,w*4);
        parameters.W=w; parameters.H=h; parameters.Mode=(uint)info.Type;
    }
    public void SetTestPosition(int x,int y,bool show) { parameters.X=x; parameters.Y=y; visible=show; }
    public void Bind()
    {
        var p=parameters;
        if(!visible || !Enabled || texture==null) p.Mode=0;
        context.UpdateSubresource(in p,constants); context.CSSetConstantBuffer(1,constants); context.CSSetShaderResource(1,view);
    }
    public void Dispose() { view?.Dispose(); texture?.Dispose(); constants.Dispose(); }
}
