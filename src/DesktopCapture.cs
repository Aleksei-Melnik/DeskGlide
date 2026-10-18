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
    long whiteReadAt;
    bool acquired;
    internal long ReadbackDrops=>color.ReadbackDrops;
    public bool CompensateSdr {get;set;}
    public bool CaptureCursor {get;set;}=true;
    public double WhiteNits {get;private set;}
    internal uint AccumulatedFrames {get;private set;}
    internal bool DesktopUpdated {get;private set;}
    public int Width=>color.Width;
    public int Height=>color.Height;
    public DesktopCapture(DisplayInfo display,bool compensate,int canvasWidth=0,int canvasHeight=0)
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
            using(var dxgiDevice=device.QueryInterface<IDXGIDevice>())dxgiDevice.SetGPUThreadPriority(3);
            color=new(device,context,canvasWidth,canvasHeight);
        }
        catch{Dispose();throw;}
    }
    public VideoBuffer? Next()
    {
        VideoBuffer? frame=color.TryReadNdi();
        try{return NextCore(frame);}
        catch{frame?.Dispose();throw;}
    }
    VideoBuffer? NextCore(VideoBuffer? frame)
    {
        DesktopUpdated=false;AccumulatedFrames=0;
        var current=output.Description1;
        if(!current.AttachedToDesktop || (current.ColorSpace==ColorSpaceType.RgbFullG2084NoneP2020)!=display.Hdr ||
            current.DesktopCoordinates.Right-current.DesktopCoordinates.Left!=display.Width ||
            current.DesktopCoordinates.Bottom-current.DesktopCoordinates.Top!=display.Height)
            throw new CaptureResetException("Display mode changed; rebuilding capture only");
        // Display configuration queries are expensive on gaming drivers. Sample the
        // slider at 4 Hz instead of twice per frame; do not stall every capture slot.
        long now=System.Diagnostics.Stopwatch.GetTimestamp();
        if(!display.Hdr)WhiteNits=80;
        else if(now>=whiteReadAt){WhiteNits=WhiteLevel.Read(display.Device);whiteReadAt=now+System.Diagnostics.Stopwatch.Frequency/4;}
        ReleaseAcquired();
        var result=duplication.AcquireNextFrame(0,out var info,out var resource);
        if(result.Code==unchecked((int)0x887A0027))return frame??color.TryReadNdi();
        result.CheckError();
        acquired=true;
        AccumulatedFrames=info.AccumulatedFrames;DesktopUpdated=info.LastPresentTime>0;
        try
        {
            using(resource)
            using(var texture=resource.QueryInterface<ID3D11Texture2D>())
            {
                color.Cursor.Enabled=CaptureCursor;color.Cursor.Update(duplication,info);
                long capturedAt=Math.Max(info.LastPresentTime,info.LastMouseUpdateTime);
                if(capturedAt<=0)capturedAt=System.Diagnostics.Stopwatch.GetTimestamp();
                color.QueueNdi(texture,display.Hdr,display.Hdr&&CompensateSdr?(float)(80/WhiteNits):1,capturedAt);
            }
        }
        // This path polls with a zero timeout. Holding ownership across the
        // next pacing wait can prevent a fresh desktop copy and halve capture.
        finally{ReleaseAcquired();}
        return frame??color.TryReadNdi();
    }
    void ReleaseAcquired(){if(!acquired)return;acquired=false;duplication.ReleaseFrame().CheckError();}
    public void Dispose(){if(acquired)try{ReleaseAcquired();}catch{}color?.Dispose();duplication?.Dispose();context?.Dispose();device?.Dispose();output?.Dispose();adapter?.Dispose();factory?.Dispose();}
}
