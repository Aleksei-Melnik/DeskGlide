using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace SdrCapture;

// Per-thread handles: SIMD JPEG, full chroma for readable desktop text.
sealed class KvmJpeg:IDisposable
{
    readonly IntPtr encoder=tjInitCompress(),decoder=tjInitDecompress();
    const string Library="libturbojpeg.dll";
    public byte[] Encode(Bitmap bitmap,int quality)
    {
        var bits=bitmap.LockBits(new(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);
        IntPtr output=IntPtr.Zero;uint size=0;
        try
        {
            if(bits.Stride<bitmap.Width*4)throw new IOException("Invalid bitmap pitch.");
            Check(tjCompress2(encoder,bits.Scan0,bitmap.Width,bits.Stride,bitmap.Height,3,ref output,ref size,0,quality,0),encoder);
            if(size>int.MaxValue)throw new IOException("JPEG too large.");
            var data=new byte[(int)size];Marshal.Copy(output,data,0,data.Length);return data;
        }
        finally{if(output!=IntPtr.Zero)tjFree(output);bitmap.UnlockBits(bits);}
    }
    public Bitmap Decode(byte[] data)
    {
        if(data.Length is <1 or >2800000)throw new IOException("Invalid JPEG length.");
        Check(tjDecompressHeader3(decoder,data,(uint)data.Length,out int width,out int height,out _,out _),decoder);
        if(width<1||height<1||width>16384||height>16384||(long)width*height>40000000)throw new IOException("Invalid JPEG dimensions.");
        var bitmap=new Bitmap(width,height,PixelFormat.Format32bppRgb);
        try
        {
            var bits=bitmap.LockBits(new(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
            try{Check(tjDecompress2(decoder,data,(uint)data.Length,bits.Scan0,width,bits.Stride,height,3,0),decoder);}
            finally{bitmap.UnlockBits(bits);}
            return bitmap;
        }
        catch{bitmap.Dispose();throw;}
    }
    static void Check(int result,IntPtr handle){if(result!=0)throw new IOException(Marshal.PtrToStringUTF8(tjGetErrorStr2(handle))??"JPEG codec error.");}
    public void Dispose(){if(encoder!=IntPtr.Zero)tjDestroy(encoder);if(decoder!=IntPtr.Zero)tjDestroy(decoder);}
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr tjInitCompress();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr tjInitDecompress();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int tjDestroy(IntPtr handle);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern void tjFree(IntPtr buffer);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern IntPtr tjGetErrorStr2(IntPtr handle);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int tjCompress2(IntPtr handle,IntPtr source,int width,int pitch,int height,int format,ref IntPtr destination,ref uint size,int sampling,int quality,int flags);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int tjDecompressHeader3(IntPtr handle,byte[] data,uint size,out int width,out int height,out int sampling,out int colorspace);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]static extern int tjDecompress2(IntPtr handle,byte[] data,uint size,IntPtr output,int width,int pitch,int height,int format,int flags);
}

// One worker per authenticated viewing session. Two unacknowledged frames maximum.
sealed class KvmVideoSession:IDisposable
{
    readonly KvmWire wire;readonly bool allowed;
    CancellationTokenSource? stop;Task? worker;SemaphoreSlim? credits;string id="";
    public KvmVideoSession(KvmWire wire,bool allowed){this.wire=wire;this.allowed=allowed;}
    public async Task Handle(KvmMessage request,CancellationToken token)
    {
        if(request.Type=="view-ack")
        {if(request.Id==id&&credits is {} c&&c.CurrentCount<2)try{c.Release();}catch(SemaphoreFullException){}return;}
        if(request.Type=="view-stop"){if(request.Id==id){await Stop();await wire.SendAsync(new(){Type="view-stopped",Id=request.Id},token);}return;}
        if(request.Type!="view-start")return;
        await Stop();
        if(!Guid.TryParseExact(request.Id,"N",out _)||request.Code is not(30 or 60)||request.Flags is <0 or >2||request.Device.Length>128)throw new IOException("Invalid KVM video settings.");
        if(!allowed){await wire.SendAsync(new(){Type="view-error",Id=request.Id,Text="Desktop viewing is disabled on the remote PC."},token);return;}
        id=request.Id;stop=CancellationTokenSource.CreateLinkedTokenSource(token,wire.Token);credits=new(2,2);
        var cancellation=stop.Token;var window=credits;
        worker=Task.Factory.StartNew(()=>Stream(request,window,cancellation),cancellation,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    void Stream(KvmMessage request,SemaphoreSlim window,CancellationToken token)
    {
        long count=0;double captureMs=0,encodeMs=0,sendMs=0;var elapsed=Stopwatch.StartNew();
        try
        {
            using var codec=new KvmJpeg();using var pacer=new Pacer();pacer.SetRate(request.Code);
            var screen=Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==request.Device);
            if(screen==null)throw new IOException("The monitor is unavailable. Select an active display on the remote PC.");
            var bounds=screen.Bounds;
            if(bounds.Width<1||bounds.Height<1||(long)bounds.Width*bounds.Height>40000000)throw new IOException("Invalid display size.");
            double scale=request.Flags==2?Math.Min(1,Math.Min(1920d/bounds.Width,1080d/bounds.Height)):1;
            using var image=new Bitmap(Math.Max(1,(int)(bounds.Width*scale)),Math.Max(1,(int)(bounds.Height*scale)),PixelFormat.Format32bppRgb);
            KvmGpuCapture? gpu=null;try{var display=DisplayInfo.All().First(d=>d.Device==request.Device);gpu=new(display,image.Width,image.Height);}catch(Exception e){Log.Write("KVM GPU fallback: "+e.Message);}
            using var gpuCapture=gpu;
            using var original=gpu==null?new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppRgb):null;
            using var graphics=original==null?null:Graphics.FromImage(original);
            using var resized=original==null?null:Graphics.FromImage(image);
            if(resized!=null)resized.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            byte[]? bytes=null;int level=request.Flags==0?95:request.Flags==1?88:80;
            long frame=0;
            while(!token.IsCancellationRequested)
            {
                pacer.Wait(token);token.ThrowIfCancellationRequested();
                if(!window.Wait(3000,token))throw new IOException("Video frames are not being acknowledged. Check the connection.");
                long stamp=Stopwatch.GetTimestamp();bool fresh;
                if(gpu!=null)fresh=gpu.Read(image);
                else{graphics!.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);resized!.DrawImage(original!,new Rectangle(0,0,image.Width,image.Height));fresh=true;}
                captureMs+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;stamp=Stopwatch.GetTimestamp();
                if(fresh){level=request.Flags==0?95:request.Flags==1?88:80;bytes=codec.Encode(image,level);while(bytes.Length>2800000&&level>45){level-=10;bytes=codec.Encode(image,level);}}
                if(bytes==null){window.Release();continue;}
                if(bytes.Length>2800000)throw new IOException("The frame is too large. Choose 1080p mode.");
                encodeMs+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;stamp=Stopwatch.GetTimestamp();
                wire.SendAsync(new(){Type="view-frame",Id=request.Id,Size=++frame,Device=request.Device,X=bounds.Width,Y=bounds.Height,Data=bytes,Text=$"{image.Width} × {image.Height} · JPEG {level} · 4:4:4 · {(gpu==null?"GDI":"GPU")}"},token).GetAwaiter().GetResult();
                sendMs+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;count++;
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e)
        {Log.Write("KVM video: "+e.Message);try{wire.SendAsync(new(){Type="view-error",Id=request.Id,Text=e.Message},token).GetAwaiter().GetResult();}catch{}}
        finally{if(count>0)Log.Write($"KVM video stats: frames={count}, seconds={elapsed.Elapsed.TotalSeconds:F2}, capture={captureMs/count:F2}ms, encode={encodeMs/count:F2}ms, send={sendMs/count:F2}ms");}
    }
    async Task Stop()
    {
        stop?.Cancel();if(worker!=null)try{await worker;}catch(OperationCanceledException){}
        stop?.Dispose();credits?.Dispose();stop=null;worker=null;credits=null;id="";
    }
    public void Dispose(){stop?.Cancel();if(worker!=null)_=worker.ContinueWith(_=>{stop?.Dispose();credits?.Dispose();},TaskScheduler.Default);}
}
