using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PixelFormats=System.Windows.Media.PixelFormats;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;

sealed record ScreenshotOptions
{
    public bool Enabled {get;init;}=true;
    public string Folder {get;init;}=Path.Combine(PicturesFolder(),"DeskGlide","Screenshots");
    static string PicturesFolder()
    {
        string folder=Environment.GetFolderPath(Environment.SpecialFolder.MyPictures,Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(folder)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Pictures"):folder;
    }
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Folder)||!Path.IsPathFullyQualified(Folder)||Folder.IndexOfAny(Path.GetInvalidPathChars())>=0)
            throw new ArgumentException(UiStrings.T("Choose an absolute screenshots folder."));
    }
}

// RegisterHotKey runs after the KVM low-level routing decision. A PrintScreen
// suppressed and forwarded by KVM must never start a screenshot on the Host too.
sealed class ScreenshotHotkey:NativeWindow,IDisposable
{
    bool registered;
    internal bool Registered=>registered;
    public event Action? Requested;
    public ScreenshotHotkey()=>CreateHandle(new CreateParams{Caption="DeskGlide Screenshot",Parent=new IntPtr(-3)});
    public bool Set(bool enabled)
    {
        if(enabled&&registered)return true;
        UnregisterHotKey(Handle,1);
        registered=enabled&&RegisterHotKey(Handle,1,0x4000,(uint)Keys.PrintScreen);
        return !enabled||registered;
    }
    protected override void WndProc(ref Message m)
    {if(m.Msg==0x312&&m.WParam.ToInt32()==1)Requested?.Invoke();base.WndProc(ref m);}
    public void Dispose(){UnregisterHotKey(Handle,1);DestroyHandle();}
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd,int id);
}

// A tile owns a single unrotated DXGI desktop image. Preserve FP16 scRGB until
// tone mapping; capturing HDR through an 8-bit GDI bitmap irreversibly clips it.
sealed class ScreenshotTile(Rectangle bounds,int width,int height,ModeRotation rotation,bool linear,bool hdr,double whiteNits,byte[] pixels)
{
    internal Rectangle Bounds {get;}=bounds;
    internal bool Hdr {get;}=hdr;
    internal double WhiteNits {get;}=whiteNits;
    internal void Read(int x,int y,out float r,out float g,out float b)
    {
        (int sx,int sy)=Source(x,y,width,height,rotation);
        int p=(sy*width+sx)*(linear?8:4);
        if(linear)
        {
            r=Finite((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(pixels,p)));
            g=Finite((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(pixels,p+2)));
            b=Finite((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(pixels,p+4)));
        }
        else {b=Decode(pixels[p]/255f);g=Decode(pixels[p+1]/255f);r=Decode(pixels[p+2]/255f);}
    }
    internal static (int X,int Y) Source(int x,int y,int width,int height,ModeRotation rotation)=>rotation switch
    {
        ModeRotation.Rotate90=>(y,height-1-x),
        ModeRotation.Rotate180=>(width-1-x,height-1-y),
        ModeRotation.Rotate270=>(width-1-y,x),
        _=>(x,y)
    };
    internal static float Finite(float v)=>float.IsFinite(v)?v:0;
    internal static float Decode(float v)=>v<=.04045f?v/12.92f:MathF.Pow((v+.055f)/1.055f,2.4f);
    internal static byte Encode(float v)=>(byte)Math.Clamp((int)MathF.Round(255*(v<=.0031308f?12.92f*v:1.055f*MathF.Pow(v,1/2.4f)-.055f)),0,255);
    internal static void ToneMap(ref float r,ref float g,ref float b,double white)
    {
        float exposure=(float)(80/Math.Clamp(white,1,10000));
        r=Math.Max(0,r*exposure);g=Math.Max(0,g*exposure);b=Math.Max(0,b*exposure);
        float peak=Math.Max(r,Math.Max(g,b));
        float mapped=peak<=.75f?peak:.75f+.25f*(1-MathF.Exp(-(peak-.75f)/.25f));
        if(peak>0){float scale=mapped/peak;r*=scale;g*=scale;b*=scale;}
    }
}

sealed class ScreenshotFrame:IDisposable
{
    const long MaximumPixels=40_000_000;
    internal IReadOnlyList<ScreenshotTile> Tiles {get;}
    internal Rectangle Bounds {get;}
    internal Bitmap Preview {get;}
    internal bool HasHdr=>Tiles.Any(t=>t.Hdr);
    internal ScreenshotFrame(IReadOnlyList<ScreenshotTile> tiles)
    {
        if(tiles.Count==0)throw new IOException("No active displays.");
        Tiles=tiles;Bounds=tiles.Select(t=>t.Bounds).Aggregate(Rectangle.Union);
        CheckSize(Bounds);Preview=new(Bounds.Width,Bounds.Height,PixelFormat.Format32bppArgb);
        try{BuildPreview();}catch{Preview.Dispose();throw;}
    }
    internal static void CheckSize(Rectangle bounds)
    {if(bounds.Width<=0||bounds.Height<=0||(long)bounds.Width*bounds.Height>MaximumPixels)throw new IOException(UiStrings.T("The selected desktop is too large to capture safely."));}
    unsafe void BuildPreview()
    {
        var data=Preview.LockBits(new(0,0,Preview.Width,Preview.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        try
        {
            nint address=data.Scan0;int stride=data.Stride;
            Parallel.For(0,Bounds.Height,y=>
            {
                byte* row=(byte*)address+y*stride;
                for(int x=0;x<Bounds.Width;x++){row[x*4]=row[x*4+1]=row[x*4+2]=0;row[x*4+3]=255;}
                foreach(var tile in Tiles)
                {
                    int ty=y+Bounds.Y-tile.Bounds.Y;if(ty<0||ty>=tile.Bounds.Height)continue;
                    for(int tx=0;tx<tile.Bounds.Width;tx++)
                    {
                        tile.Read(tx,ty,out float r,out float g,out float b);
                        if(tile.Hdr)ScreenshotTile.ToneMap(ref r,ref g,ref b,tile.WhiteNits);
                        int x=tile.Bounds.X-Bounds.X+tx;
                        row[x*4]=ScreenshotTile.Encode(b);row[x*4+1]=ScreenshotTile.Encode(g);row[x*4+2]=ScreenshotTile.Encode(r);
                    }
                }
            });
        }
        finally{Preview.UnlockBits(data);}
    }
    internal static ScreenshotFrame Capture(CancellationToken cancel=default)
    {
        using var factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var displays=DisplayInfo.All();var tiles=new List<ScreenshotTile>();
        foreach(var display in displays){cancel.ThrowIfCancellationRequested();tiles.Add(CaptureTile(factory,display,cancel));}
        return new(tiles);
    }
    static unsafe ScreenshotTile CaptureTile(IDXGIFactory1 factory,DisplayInfo display,CancellationToken cancel)
    {
        factory.EnumAdapters1(display.Adapter,out var adapter).CheckError();using var adapterLife=adapter;
        adapter.EnumOutputs(display.Output,out var raw).CheckError();using var rawLife=raw;
        using var output=raw.QueryInterface<IDXGIOutput6>();var d=output.Description1;
        var bounds=Rectangle.FromLTRB(d.DesktopCoordinates.Left,d.DesktopCoordinates.Top,d.DesktopCoordinates.Right,d.DesktopCoordinates.Bottom);CheckSize(bounds);
        bool hdr=d.ColorSpace==ColorSpaceType.RgbFullG2084NoneP2020;
        if(!d.AttachedToDesktop||hdr!=display.Hdr)throw new IOException(UiStrings.T("Display mode changed. Take the screenshot again."));
        D3D11.D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport,[FeatureLevel.Level_11_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
        using var deviceLife=device;using var contextLife=context;
        using var output5=output.QueryInterface<IDXGIOutput5>();
        using var duplication=output5.DuplicateOutput1(device,hdr?[Format.R16G16B16A16_Float]:[Format.B8G8R8A8_UNorm,Format.R16G16B16A16_Float]);
        for(int attempt=0;attempt<15;attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            var result=duplication.AcquireNextFrame(100,out _,out var resource);
            if(result.Code==unchecked((int)0x887A0027))continue;
            result.CheckError();
            try
            {
                using(resource)
                using(var texture=resource.QueryInterface<ID3D11Texture2D>())
                {
                    var td=texture.Description;bool linear=td.Format==Format.R16G16B16A16_Float;
                    if((!linear&&td.Format!=Format.B8G8R8A8_UNorm)||(hdr&&!linear))throw new IOException("Unsafe screenshot color format: "+td.Format);
                    bool rotated=d.Rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
                    if(td.Width!=(rotated?bounds.Height:bounds.Width)||td.Height!=(rotated?bounds.Width:bounds.Height))throw new IOException(UiStrings.T("Display mode changed. Take the screenshot again."));
                    td.BindFlags=BindFlags.None;td.Usage=ResourceUsage.Staging;td.CPUAccessFlags=CpuAccessFlags.Read;td.MiscFlags=ResourceOptionFlags.None;
                    using var staging=device.CreateTexture2D(td);context.CopyResource(staging,texture);
                    int stride=checked((int)td.Width*(linear?8:4));byte[] pixels=new byte[checked(stride*(int)td.Height)];
                    var map=context.Map(staging,0,MapMode.Read);
                    try{fixed(byte* dst=pixels)for(int y=0;y<td.Height;y++)System.Buffer.MemoryCopy((byte*)map.DataPointer+y*map.RowPitch,dst+y*stride,stride,stride);}
                    finally{context.Unmap(staging,0);}
                    return new(bounds,(int)td.Width,(int)td.Height,d.Rotation,linear,hdr,hdr?WhiteLevel.Read(display.Device):80,pixels);
                }
            }
            finally{duplication.ReleaseFrame();}
        }
        throw new IOException(UiStrings.T("No desktop frame arrived. Take the screenshot again."));
    }
    internal Bitmap Crop(Rectangle selection)
    {
        ValidateSelection(selection);
        return Preview.Clone(selection,PixelFormat.Format32bppArgb);
    }
    internal void ValidateSelection(Rectangle selection)
    {if(selection.Width<=0||selection.Height<=0||!new Rectangle(Point.Empty,Bounds.Size).Contains(selection))throw new ArgumentOutOfRangeException(nameof(selection));}
    public void Dispose()=>Preview.Dispose();
}

static class ScreenshotFiles
{
    internal static void SavePng(Stream stream,Bitmap bitmap)
    {
        // WIC writes an explicit sRGB chunk; ordinary image viewers must not
        // interpret this tone-mapped image as linear or as the HDR original.
        var data=bitmap.LockBits(new(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            var source=BitmapSource.Create(bitmap.Width,bitmap.Height,96,96,PixelFormats.Bgra32,null,data.Scan0,checked(data.Stride*data.Height),data.Stride);
            var metadata=new BitmapMetadata("png");metadata.SetQuery("/sRGB/RenderingIntent",(byte)0);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source,null,metadata,null));encoder.Save(stream);
        }
        finally{bitmap.UnlockBits(data);}
    }
    internal static string Save(string folder,string extension,Action<Stream> encode)
    {
        Directory.CreateDirectory(folder);
        string file=Path.Combine(folder,$"Screenshot-{DateTime.Now:yyyy-MM-dd-HH-mm-ss-fff}-{Guid.NewGuid().ToString("N")[..6]}{extension}");
        Write(file,encode);return file;
    }
    internal static void Write(string path,Action<Stream> encode)
    {
        // Stage beside the destination for an atomic rename, never in C:\.
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){encode(file);file.Flush(true);}File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
