using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;

// Visible, automatically closing presentation/capture diagnostic. No display
// mode changes, input injection, live profile edits, network stream or audio output.
static class CaptureCadenceTests
{
    internal readonly record struct Presentation(uint Id,long DrawBegin,long PresentBegin,long PresentEnd,uint DisplayedCount,uint Refresh,long SyncAt,int StatisticsResult);
    readonly record struct PixelSample(long Time,int Id,double Arrival,bool Valid,int Inverse,int BottomId,int BottomInverse,string Probe);
    public static void Present(int seconds)
    {
        var display=DisplayInfo.All().First();var screen=Screen.AllScreens.Single(s=>s.DeviceName==display.Device).Bounds;
        using var surface=new Surface(screen);
        surface.Shown+=async(_,_)=>{surface.Begin();WindowActivation.Show(surface);await Task.Delay(seconds*1000);surface.Close();};
        Application.Run(surface);
        if(surface.Failure!=null)throw surface.Failure;
        Program.Write("capture-presentation.json",new{surface.Presented,seconds,SourceFps=240,ScreenWidth=screen.Width,ScreenHeight=screen.Height,OriginX=surface.Origin.X-screen.X,OriginY=surface.Origin.Y-screen.Y,MarkerWidth=512,MarkerHeight=24});
    }
    // Explicit opt-in capture of the current desktop. This path creates no
    // window and does not change focus. Timestamp statistics are diagnostic;
    // without the known marker they do not prove playback smoothness.
    public static void Observe(int fps,int seconds,string codec="HEVC",int width=0,int height=0)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"capture-game-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var settings=Settings.Load();var displays=DisplayInfo.All();var display=displays.FirstOrDefault(d=>d.Device==settings.Device)??displays.First();
        var options=new ReplayOptions{Enabled=true,Fps=fps,Codec=codec,AudioMode="Silent",Width=width==0?display.Width:width,Height=height==0?display.Height:height,Folder=Path.Combine(root,"clips")};
        using var recorder=new ReplayRecorder(options,true,Math.Max(4,seconds-4));
        using var engine=new StreamEngine{NdiEnabled=false,ReplayEnabled=true,ReplayFrameRate=fps};
        var samples=new List<(long Time,double Arrival)>();
        engine.FrameAvailable=frame=>{lock(samples)samples.Add((frame.CapturedAt,ReplayRecorder.Now));recorder.Offer(frame);};
        engine.Start(settings.Options with{Device=display.Device});Thread.Sleep(seconds*1000);
        var clip=recorder.SaveAsync("Capture diagnostic").GetAwaiter().GetResult();engine.ReplayEnabled=false;
        (long Time,double Arrival)[] frames;lock(samples)frames=samples.ToArray();
        double[] intervals=frames.Zip(frames.Skip(1),(a,b)=>(b.Time-a.Time)*1000.0/Stopwatch.Frequency).Order().ToArray();
        double[] age=frames.Select(s=>(s.Arrival-s.Time/(double)Stopwatch.Frequency)*1000).Order().ToArray();
        Program.Write("capture-game-test.json",new{fps,seconds,codec,display.Hdr,options.Width,options.Height,Captured=frames.Length,
            CaptureFps=frames.Length/Math.Max(1,frames[^1].Arrival-frames[0].Arrival),IntervalMedian=intervals[intervals.Length/2],IntervalP95=intervals[(int)(intervals.Length*.95)],
            AgeMedian=age[age.Length/2],AgeP95=age[(int)(age.Length*.95)],engine.MaxCaptureMs,MeanCaptureMs=engine.TotalCaptureMs/Math.Max(1,engine.CapturedFrames),
            recorder.EncodedFrames,recorder.RepeatedInputFrames,recorder.HistoryMisses,recorder.MaxPipeWriteMs,recorder.MaxWriterLateMs,recorder.EncoderStarts,recorder.Error,
            NoFocusChanges=true,PlaybackSmoothnessVerified=false,clip.LocalPath,Root=root,Samples=frames.Select(s=>new{s.Time,s.Arrival}).ToArray()});
        if(recorder.Error!=null||engine.CapturedFrames<fps*seconds*.3)throw new Exception("Current-desktop diagnostic failed; see report.");
    }
    sealed class Surface:Form
    {
        readonly CancellationTokenSource stop=new();
        Task? worker;
        public Point Origin {get;private set;}
        public long Presented;
        public readonly System.Collections.Concurrent.ConcurrentQueue<Presentation> Presentations=new();
        public Exception? Failure;
        public readonly int RenderWidth,RenderHeight;
        protected override bool ShowWithoutActivation=>false;
        public Surface(Rectangle screen,bool fullScreen=false)
        {
            RenderWidth=fullScreen?screen.Width:960;RenderHeight=fullScreen?screen.Height:540;
            Text="DeskGlide capture timing test · closes automatically";ClientSize=new(RenderWidth,RenderHeight);StartPosition=FormStartPosition.Manual;
            if(fullScreen){FormBorderStyle=FormBorderStyle.None;Location=screen.Location;}else Location=new(screen.X+(screen.Width-RenderWidth)/2,screen.Y+(screen.Height-RenderHeight)/2);
            TopMost=true;
        }
        public void Begin()
        {
            Origin=PointToScreen(Point.Empty);IntPtr window=Handle;
            worker=Task.Factory.StartNew(()=>
            {
                try
                {
                    using var factory=DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
                    using var device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.BgraSupport,FeatureLevel.Level_11_0);
                    using var context=device.ImmediateContext;
                    using var swap=factory.CreateSwapChainForHwnd(device,window,new SwapChainDescription1{Width=(uint)RenderWidth,Height=(uint)RenderHeight,Format=Format.R8G8B8A8_UNorm,BufferCount=2,BufferUsage=Usage.RenderTargetOutput,SampleDescription=new(1,0),SwapEffect=SwapEffect.FlipDiscard});
                    using var image=device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,(uint)RenderWidth,(uint)RenderHeight,1,1,BindFlags.UnorderedAccess));
                    using var view=device.CreateUnorderedAccessView(image);
                    using var back=swap.GetBuffer<ID3D11Texture2D>(0);
                    using var shader=device.CreateComputeShader(Vortice.D3DCompiler.Compiler.Compile(Scene,"main","CaptureCadence.hlsl","cs_5_0").Span);
                    using var constants=device.CreateBuffer(new BufferDescription(16,BindFlags.ConstantBuffer,ResourceUsage.Default));
                    using var pacing=new Pacer();long origin=Stopwatch.GetTimestamp();
                    while(!stop.IsCancellationRequested)
                    {
                        uint index=(uint)Interlocked.Read(ref Presented)+1;
                        pacing.WaitUntil(origin+(index-1)*Stopwatch.Frequency/240,stop.Token);
                        long drawBegin=Stopwatch.GetTimestamp();
                        context.UpdateSubresource(new[]{index,0u,0u,0u},constants);
                        context.CSSetShader(shader);context.CSSetConstantBuffer(0,constants);context.CSSetUnorderedAccessView(0,view);
                        context.Dispatch((uint)((RenderWidth+7)/8),(uint)((RenderHeight+7)/8),1);context.CSSetUnorderedAccessView(0,null);context.CopyResource(back,image);
                        long presentBegin=Stopwatch.GetTimestamp();swap.Present(0,PresentFlags.None).CheckError();
                        long presentEnd=Stopwatch.GetTimestamp();var statisticsResult=swap.GetFrameStatistics(out var statistics);
                        Presentations.Enqueue(new(index,drawBegin,presentBegin,presentEnd,statistics.PresentCount,statistics.PresentRefreshCount,statistics.SyncQPCTime,statisticsResult.Code));Interlocked.Increment(ref Presented);
                    }
                }
                catch(Exception e){Failure=e;}
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        protected override void OnFormClosing(FormClosingEventArgs e){stop.Cancel();worker?.Wait(3000);base.OnFormClosing(e);}
        const string Scene="""
            RWTexture2D<float4> image:register(u0);
            cbuffer Parameters:register(b0){uint index;uint3 pad;}
            [numthreads(8,8,1)]void main(uint3 p:SV_DispatchThreadID){
                uint w,h;image.GetDimensions(w,h);if(p.x>=w||p.y>=h)return;
                uint bit=(index>>((p.x/16)%16))&1;if(p.x>=256)bit^=1;
                float v=(p.y<24||p.y>=h-24)&&p.x<512?bit:frac((p.x+index*3)/(float)w);
                image[p.xy]=float4(v,v,v,1);
            }
            """;
    }
    public static void Run(int fps,int seconds,string codec="HEVC",int width=0,int height=0,bool ndi=false,bool blocking=false,bool fullScreen=false,bool directConversion=false)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"capture-cadence-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var display=DisplayInfo.All().First();var observations=new List<PixelSample>();
        var options=new ReplayOptions{Enabled=true,Fps=fps,Codec=codec,Width=width==0?display.Width:width,Height=height==0?display.Height:height,Folder=Path.Combine(root,"clips")};
        using var recorder=new ReplayRecorder(options,syntheticAudio:true,testWindowSeconds:Math.Max(5,seconds-5));
        recorder.SynchronousFrameConversionForTest=directConversion;
        var screen=Screen.AllScreens.Single(s=>s.DeviceName==display.Device).Bounds;
        using var surface=new Surface(screen,fullScreen);
        using var engine=new StreamEngine{NdiEnabled=ndi,ReplayEnabled=true,ReplayFrameRate=fps};
        var acquisition=new System.Collections.Concurrent.ConcurrentQueue<CaptureObservation>();engine.CaptureObserved=acquisition.Enqueue;engine.BlockingReadbackForTest=blocking;
        engine.FrameAvailable=frame=>
        {
            int x=surface.Origin.X-screen.X,y=surface.Origin.Y-screen.Y+8;
            int bottom=surface.Origin.Y-screen.Y+surface.RenderHeight-8;
            if(x<0||y<0||x+512>frame.Width||bottom>=frame.Height)return;
            int id=0,inverse=0,bottomId=0,bottomInverse=0;
            for(int bit=0;bit<16;bit++)
            {
                if(frame.Data[(y*frame.Width+x+bit*16+8)*2+1]>128)id|=1<<bit;
                if(frame.Data[(y*frame.Width+x+256+bit*16+8)*2+1]>128)inverse|=1<<bit;
                if(frame.Data[(bottom*frame.Width+x+bit*16+8)*2+1]>128)bottomId|=1<<bit;
                if(frame.Data[(bottom*frame.Width+x+256+bit*16+8)*2+1]>128)bottomInverse|=1<<bit;
            }
            int middle=surface.Origin.Y-screen.Y+surface.RenderHeight/2;
            Span<byte> probe=stackalloc byte[16];for(int n=0;n<probe.Length;n++)probe[n]=frame.Data[(middle*frame.Width+x+30+n*55)*2+1];
            lock(observations)observations.Add(new(frame.CapturedAt,id,ReplayRecorder.Now,(id^inverse)==65535&&(bottomId^bottomInverse)==65535&&id==bottomId,inverse,bottomId,bottomInverse,Convert.ToHexString(probe)));
            recorder.Offer(frame);
        };
        ReplayResult? saved=null;int captureRate=0;
        using var receiveStop=new CancellationTokenSource();
        Task receive=Task.CompletedTask;Exception? receiveError=null;
        var ndiAges=new List<double>();long ndiFrames=0,ndiRepeated=0;int ndiPrevious=-1;
        surface.Shown+=async(_,_)=>
        {
            try
            {
                surface.Begin();WindowActivation.Show(surface);await Task.Delay(10000);engine.Start(new(display.Device));
                if(ndi)receive=Task.Run(()=>
                {
                    try
                    {
                        var startup=Stopwatch.StartNew();
                        while(engine.SourceName=="SdrCapture SDR"&&startup.Elapsed.TotalSeconds<10&&!receiveStop.IsCancellationRequested)Thread.Sleep(20);
                        using var receiver=new TestReceiver(engine.SourceName);
                        while(!receiveStop.IsCancellationRequested)receiver.Read(video=>
                        {
                            int x=surface.Origin.X-screen.X,y=surface.Origin.Y-screen.Y+8,id=0,inverse=0;
                            if(video.FourCC!=0x59565955||x<0||y<0||x+512>video.Width||y>=video.Height)return;
                            for(int bit=0;bit<16;bit++)
                            {
                                if(Marshal.ReadByte(video.Data,y*video.Stride+(x+bit*16+8)*2+1)>128)id|=1<<bit;
                                if(Marshal.ReadByte(video.Data,y*video.Stride+(x+256+bit*16+8)*2+1)>128)inverse|=1<<bit;
                            }
                            if((id^inverse)!=65535)return;
                            lock(observations)
                            {
                                var source=observations.LastOrDefault(s=>s.Id==id&&s.Valid);
                                if(source.Time>0)ndiAges.Add((ReplayRecorder.Now-source.Time/(double)Stopwatch.Frequency)*1000);
                            }
                            if(id==ndiPrevious)ndiRepeated++;ndiPrevious=id;ndiFrames++;
                        },100);
                    }
                    catch(Exception e){receiveError=e;}
                });
                await Task.Delay(seconds*1000);captureRate=engine.CaptureRate;saved=await recorder.SaveAsync("Capture cadence");
            }
            catch(Exception e){surface.Failure=e;}
            finally
            {
                receiveStop.Cancel();await receive;
                engine.NdiEnabled=false;engine.ReplayEnabled=false;engine.FrameAvailable=null;
                surface.Close();
            }
        };
        Application.Run(surface);engine.ReplayEnabled=false;
        if(surface.Failure!=null)throw surface.Failure;
        var clip=saved??throw new Exception("Diagnostic closed before saving.");recorder.Update(options with{Enabled=false});
        PixelSample[] samples;lock(observations)samples=observations.ToArray();var deltas=samples.Zip(samples.Skip(1),(a,b)=>(b.Time-a.Time)*1000.0/Stopwatch.Frequency).ToArray();
        string raw=Path.Combine(root,"frame-ids.raw");int ox=(surface.Origin.X-screen.X)*options.Width/display.Width,oy=(surface.Origin.Y-screen.Y+8)*options.Height/display.Height;
        using var deadline=new CancellationTokenSource(120000);
        int bottomY=(surface.Origin.Y-screen.Y+surface.RenderHeight-8)*options.Height/display.Height;
        string markerFilter=$"split[a][b];[a]crop={512*options.Width/display.Width}:2:{ox}:{oy},scale=32:1:flags=neighbor[t];[b]crop={512*options.Width/display.Width}:2:{ox}:{bottomY},scale=32:1:flags=neighbor[d];[t][d]vstack,format=gray";
        ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",clip.LocalPath,"-an","-vf",markerFilter,"-fps_mode","passthrough","-f","rawvideo",raw],deadline.Token).GetAwaiter().GetResult();
        byte[] bytes=File.ReadAllBytes(raw);var ids=new List<int>();int invalid=0;
        for(int at=0;at+64<=bytes.Length;at+=64)
        {
            int id=0,inverse=0,bottomId=0,bottomInverse=0;
            for(int bit=0;bit<16;bit++){if(bytes[at+bit]>128)id|=1<<bit;if(bytes[at+16+bit]>128)inverse|=1<<bit;if(bytes[at+32+bit]>128)bottomId|=1<<bit;if(bytes[at+48+bit]>128)bottomInverse|=1<<bit;}
            if((id^inverse)!=65535||(bottomId^bottomInverse)!=65535||id!=bottomId)invalid++;ids.Add(id);
        }
        var steps=ids.Skip(1).Zip(ids,(a,b)=>(a-b+65536)%65536).ToArray();
        // A partially covered marker can otherwise make a long freeze look like
        // an encoder defect. Require coverage throughout the exported interval.
        bool valid=ids.Count>fps*3&&invalid==0&&(!ndi||(ndiFrames>=seconds*30&&ndiAges.Count>0&&receiveError==null));
        var orderedAges=ndiAges.Order().ToArray();
        Program.Write("capture-cadence-test.json",new{Pass=valid,MarkerValid=valid,InvalidMarkers=invalid,codec,options.Width,options.Height,ndi,CaptureRate=captureRate,QpcFrequency=Stopwatch.Frequency,fps,seconds,surface.Presented,Captured=samples.Length,SourceRepeated=samples.Zip(samples.Skip(1),(a,b)=>a.Id==b.Id).Count(v=>v),
            SourceIntervalMedian=deltas.Order().ElementAt(deltas.Length/2),SourceIntervalP95=deltas.Order().ElementAt((int)(deltas.Length*.95)),SourceIntervalMaximum=deltas.Max(),
            engine.MaxCaptureMs,engine.MaxObserverMs,recorder.MaxConvertMs,recorder.MaxFramePreparationAgeMs,recorder.ConversionQueueDrops,recorder.ConversionQueueBytes,recorder.MaxPipeWriteMs,recorder.RepeatedInputFrames,recorder.HistoryMisses,
            NdiFrames=ndiFrames,NdiRepeated=ndiRepeated,NdiMatchedFrames=ndiAges.Count,NdiAgeMedianMs=orderedAges.Length==0?0:orderedAges[orderedAges.Length/2],NdiAgeP95Ms=orderedAges.Length==0?0:orderedAges[(int)(orderedAges.Length*.95)],NdiReceiveError=receiveError?.Message,
            OutputFrames=ids.Count,OutputRepeated=steps.Count(s=>s==0),OutputMaxStep=steps.Max(),recorder.Error,clip.LocalPath,Root=root,Samples=samples,OutputIds=ids,
            BlockingReadback=blocking,FullScreenSource=fullScreen,DirectConversion=directConversion,Presentations=surface.Presentations.ToArray(),Acquisitions=acquisition.ToArray()});
        if(!valid)throw new Exception("Presentation marker or local NDI receive validation failed; see report.");
    }
}
