using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;
sealed class CaptureResetException(string message):Exception(message);
public sealed class DesktopCapture:IDisposable
{
    readonly IDXGIFactory1 factory;
    readonly IDXGIAdapter1 adapter;
    readonly IDXGIOutput6 output;
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly IDXGIOutputDuplication duplication;
    readonly ColorPipeline color;
    readonly DisplayInfo display;
    public bool CompensateSdr {get;set;}
    public bool CaptureCursor {get;set;}=true;
    public double WhiteNits {get;private set;}
    public int Width=>color.Width;
    public int Height=>color.Height;
    public DesktopCapture(DisplayInfo display,bool compensate)
    {
        this.display=display;CompensateSdr=compensate;
        try
        {
            factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1(display.Adapter,out adapter).CheckError();
            adapter.EnumOutputs(display.Output,out var raw).CheckError();
            using(raw) output=raw.QueryInterface<IDXGIOutput6>();
            if(output.Description1.Rotation!=ModeRotation.Identity && output.Description1.Rotation!=ModeRotation.Unspecified)
                throw new NotSupportedException("Поворот монитора пока не поддерживается");
            D3D11.D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0],out device,out context).CheckError();
            using var output5=output.QueryInterface<IDXGIOutput5>();
            // Always preserve the compositor's FP16 range on an HDR display.
            duplication=output5.DuplicateOutput1(device,display.Hdr?[Format.R16G16B16A16_Float]:[Format.B8G8R8A8_UNorm,Format.R16G16B16A16_Float]);
            color=new(device,context);
        }
        catch{Dispose();throw;}
    }
    public VideoBuffer? Next()
    {
        var current=output.Description1;
        if(!current.AttachedToDesktop || (current.ColorSpace==ColorSpaceType.RgbFullG2084NoneP2020)!=display.Hdr ||
            current.DesktopCoordinates.Right-current.DesktopCoordinates.Left!=display.Width ||
            current.DesktopCoordinates.Bottom-current.DesktopCoordinates.Top!=display.Height)
            throw new CaptureResetException("Display mode changed; rebuilding capture only");
        WhiteNits=display.Hdr?WhiteLevel.Read(display.Device):80;
        var result=duplication.AcquireNextFrame(0,out var info,out var resource);
        if(result.Code==unchecked((int)0x887A0027)) return null;
        result.CheckError();
        VideoBuffer? frame=null;
        try
        {
            using(resource)
            using(var texture=resource.QueryInterface<ID3D11Texture2D>())
            {
                color.Cursor.Enabled=CaptureCursor;color.Cursor.Update(duplication,info);
                frame=color.ConvertNdi(texture,display.Hdr,display.Hdr&&CompensateSdr?(float)(80/WhiteNits):1);
                frame.CapturedAt=Math.Max(info.LastPresentTime,info.LastMouseUpdateTime);
                if(frame.CapturedAt<=0)frame.CapturedAt=System.Diagnostics.Stopwatch.GetTimestamp();
                if(display.Hdr && Math.Abs(WhiteLevel.Read(display.Device)-WhiteNits)>.01){frame.Dispose();frame=null;return null;}
                return frame;
            }
        }
        catch{frame?.Dispose();throw;}
        finally
        {
            var released=duplication.ReleaseFrame();
            if(released.Failure){frame?.Dispose();released.CheckError();}
        }
    }
    public void Dispose(){color?.Dispose();duplication?.Dispose();context?.Dispose();device?.Dispose();output?.Dispose();adapter?.Dispose();factory?.Dispose();}
}
