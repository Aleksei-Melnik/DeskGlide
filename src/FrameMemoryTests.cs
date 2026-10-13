namespace SdrCapture;

static class FrameMemoryTests
{
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    public static void Run()
    {
        using var pool=new RecordingFramePool();
        using var source=new VideoBuffer(2560,1440){CapturedAt=73};
        Require(source.Data.Length==source.Length,"NDI frame was rounded up to a larger allocation bucket.");
        for(int i=0;i<source.Length;i+=4){source.Data[i]=100;source.Data[i+1]=40;source.Data[i+2]=150;source.Data[i+3]=70;}
        var packed=new RecordingFrame(source,pool);
        Require(packed.Data.Length==2560*1440*3/2,"Replay frame did not use the compact NV12 size.");
        var held=packed.Retain();packed.Dispose();packed.Dispose();
        using(var other=new RecordingFrame(source,pool))
        {
            Require(held.Address!=other.Address,"Producer reused a frame still held by the encoder.");
            Require(held.CapturedAt==73&&held.Data[0]==40&&held.Data[1]==70&&held.Data[2560*1440]==100&&held.Data[2560*1440+1]==150,"NV12 frame pixels/timestamp changed while retained.");
        }
        held.Dispose();
        // Simulate a startup backlog being released from another worker.
        var backlog=Enumerable.Range(0,12).Select(_=>new RecordingFrame(source,pool)).ToArray();
        Task.Run(()=>{foreach(var frame in backlog)frame.Dispose();}).GetAwaiter().GetResult();
        Require(pool.CachedBytes<=32L*1024*1024,"Released warm-up frames retained unbounded memory.");
        long cached=pool.CachedBytes;
        for(int i=0;i<100;i++){using var frame=new RecordingFrame(source,pool);}
        Require(pool.CachedBytes==cached,"Steady frame reuse kept growing the free pool.");
        // A outstanding writer may outlive shutdown; its bytes remain valid,
        // and its later return cannot recreate the disposed pool.
        var pending=new RecordingFrame(source,pool);pool.Dispose();
        Require(pending.Data[0]==40,"Pool disposal invalidated the outstanding frame.");pending.Dispose();
        Require(pool.CachedBytes==0,"Late encoder completion resurrected disposed buffers.");
        byte[] resized=new byte[8*8*3/2];
        byte[] small=[40,70,90,110,100,150];
        RecordingPixels.ResizeNv12(small,2,2,resized,8,8);
        Require(resized[0]==40&&resized[7]==70&&resized[56]==90&&resized[63]==110&&resized[64]==100&&resized[65]==150,"NV12 native-size change lost pixels.");
        byte[] wide=Enumerable.Repeat((byte)70,8*2*3/2).ToArray();
        RecordingPixels.ResizeNv12(wide,8,2,resized,8,8);
        Require(resized[0]==16&&resized[24]==70&&resized[56]==16&&resized[64]==128,"NV12 letterbox range changed.");
        ReadbackFormats();
        Program.Write("frame-memory-tests.json",new{Pass=true,NativeFrameBytes=source.Length,ReplayFrameBytes=2560*1440*3/2,PoolBoundBytes=32L*1024*1024,HeldWriterProtected=true,WarmupReleased=true,SteadyReuse=true,LateReturnProtected=true,ResolutionChange=true,UnusedReadbacksReleased=true});
    }
    static void ReadbackFormats()
    {
        using var device=Vortice.Direct3D11.D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware,Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,Vortice.Direct3D.FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;
        using var pipeline=new ColorPipeline(device,context);
        foreach(int size in new[]{8,16})
        {
            using var texture=device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.R8G8B8A8_UNorm,(uint)size,(uint)size,1,1,Vortice.Direct3D11.BindFlags.ShaderResource));
            context.UpdateSubresource(Enumerable.Repeat((byte)255,size*size*4).ToArray(),texture,0,(uint)(size*4));
            using var ndi=pipeline.ConvertNdi(texture,false,1);
            Require(pipeline.ReadbackBytes==size*size*2,"NDI retained readbacks for unused formats after creation/resize.");
            Require(ndi.Data[0]==128&&ndi.Data[1]==235,"Lazy NDI readback changed limited-range white.");
            var rgb=pipeline.Convert(texture,false,1);
            Require(pipeline.ReadbackBytes==size*size*10&&rgb.Length==size*size*8,"RGB readback did not initialize independently.");
            var p010=pipeline.ConvertP010(texture,false,1);
            Require(pipeline.ReadbackBytes==size*size*13&&p010.Length==size*size*3,"P010 readback did not initialize independently.");
            Require(BitConverter.ToUInt16(p010,0)==940<<6,"Lazy P010 readback changed limited-range white.");
        }
    }
}
