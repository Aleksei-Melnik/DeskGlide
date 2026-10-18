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
    public static void Observe(int fps,int seconds)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"capture-game-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var settings=Settings.Load();var displays=DisplayInfo.All();var display=displays.FirstOrDefault(d=>d.Device==settings.Device)??displays.First();
        var options=new ReplayOptions{Enabled=true,Fps=fps,Codec="HEVC",AudioMode="Silent",Width=display.Width,Height=display.Height,Folder=Path.Combine(root,"clips")};
        using var recorder=new ReplayRecorder(options,true,Math.Max(4,seconds-4));
        using var engine=new StreamEngine{NdiEnabled=false,ReplayEnabled=true,ReplayFrameRate=fps};
        var samples=new List<(long Time,double Arrival)>();
        engine.FrameAvailable=frame=>{lock(samples)samples.Add((frame.CapturedAt,ReplayRecorder.Now));recorder.Offer(frame);};
        engine.Start(settings.Options with{Device=display.Device});Thread.Sleep(seconds*1000);
        var clip=recorder.SaveAsync("Capture diagnostic").GetAwaiter().GetResult();engine.ReplayEnabled=false;
        (long Time,double Arrival)[] frames;lock(samples)frames=samples.ToArray();
        double[] intervals=frames.Zip(frames.Skip(1),(a,b)=>(b.Time-a.Time)*1000.0/Stopwatch.Frequency).Order().ToArray();
        double[] age=frames.Select(s=>(s.Arrival-s.Time/(double)Stopwatch.Frequency)*1000).Order().ToArray();
        Program.Write("capture-game-test.json",new{fps,seconds,display.Hdr,Width=display.Width,Height=display.Height,Captured=frames.Length,
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
        public Exception? Failure;
        protected override bool ShowWithoutActivation=>false;
        public Surface(Rectangle screen)
        {Text="DeskGlide capture timing test · closes automatically";ClientSize=new(960,540);StartPosition=FormStartPosition.Manual;Location=new(screen.X+(screen.Width-960)/2,screen.Y+(screen.Height-540)/2);TopMost=true;}
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
                    using var swap=factory.CreateSwapChainForHwnd(device,window,new SwapChainDescription1{Width=960,Height=540,Format=Format.R8G8B8A8_UNorm,BufferCount=2,BufferUsage=Usage.RenderTargetOutput,SampleDescription=new(1,0),SwapEffect=SwapEffect.FlipDiscard});
                    using var image=device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,960,540,1,1,BindFlags.UnorderedAccess));
                    using var view=device.CreateUnorderedAccessView(image);
                    using var back=swap.GetBuffer<ID3D11Texture2D>(0);
                    using var shader=device.CreateComputeShader(Vortice.D3DCompiler.Compiler.Compile(Scene,"main","CaptureCadence.hlsl","cs_5_0").Span);
                    using var constants=device.CreateBuffer(new BufferDescription(16,BindFlags.ConstantBuffer,ResourceUsage.Default));
                    using var pacing=new Pacer();long origin=Stopwatch.GetTimestamp();
                    while(!stop.IsCancellationRequested)
                    {
                        uint index=(uint)Interlocked.Read(ref Presented)+1;
                        pacing.WaitUntil(origin+(index-1)*Stopwatch.Frequency/240,stop.Token);
                        context.UpdateSubresource(new[]{index,0u,0u,0u},constants);
                        context.CSSetShader(shader);context.CSSetConstantBuffer(0,constants);context.CSSetUnorderedAccessView(0,view);
                        context.Dispatch(120,68,1);context.CSSetUnorderedAccessView(0,null);context.CopyResource(back,image);
                        swap.Present(0,PresentFlags.None).CheckError();Interlocked.Increment(ref Presented);
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
                if(p.x>=960||p.y>=540)return;
                uint bit=(index>>((p.x/16)%16))&1;if(p.x>=256)bit^=1;
                float v=p.y<24&&p.x<512?bit:frac((p.x+index*3)/960.0);
                image[p.xy]=float4(v,v,v,1);
            }
            """;
    }
    public static void Run(int fps,int seconds,string codec="HEVC",int width=0,int height=0,bool ndi=false)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"capture-cadence-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        var display=DisplayInfo.All().First();var observations=new List<(long Time,int Id,double Arrival,bool Valid)>();
        var options=new ReplayOptions{Enabled=true,Fps=fps,Codec=codec,Width=width==0?display.Width:width,Height=height==0?display.Height:height,Folder=Path.Combine(root,"clips")};
        using var recorder=new ReplayRecorder(options,syntheticAudio:true,testWindowSeconds:Math.Max(5,seconds-5));
        var screen=Screen.AllScreens.Single(s=>s.DeviceName==display.Device).Bounds;
        using var surface=new Surface(screen);
        using var engine=new StreamEngine{NdiEnabled=ndi,ReplayEnabled=true,ReplayFrameRate=fps};
        engine.FrameAvailable=frame=>
        {
            int x=surface.Origin.X-screen.X,y=surface.Origin.Y-screen.Y+8;
            if(x<0||y<0||x+512>frame.Width||y>=frame.Height)return;
            int id=0,inverse=0;for(int bit=0;bit<16;bit++){if(frame.Data[(y*frame.Width+x+bit*16+8)*2+1]>128)id|=1<<bit;if(frame.Data[(y*frame.Width+x+256+bit*16+8)*2+1]>128)inverse|=1<<bit;}
            lock(observations)observations.Add((frame.CapturedAt,id,ReplayRecorder.Now,(id^inverse)==65535));
            recorder.Offer(frame);
        };
        ReplayResult? saved=null;
        surface.Shown+=async(_,_)=>
        {
            try{surface.Begin();WindowActivation.Show(surface);await Task.Delay(10000);engine.Start(new(display.Device));await Task.Delay(seconds*1000);saved=await recorder.SaveAsync("Capture cadence");}
            catch(Exception e){surface.Failure=e;}
            finally{surface.Close();}
        };
        Application.Run(surface);engine.ReplayEnabled=false;
        if(surface.Failure!=null)throw surface.Failure;
        var clip=saved??throw new Exception("Diagnostic closed before saving.");recorder.Update(options with{Enabled=false});
        (long Time,int Id,double Arrival,bool Valid)[] samples;lock(observations)samples=observations.ToArray();var deltas=samples.Zip(samples.Skip(1),(a,b)=>(b.Time-a.Time)*1000.0/Stopwatch.Frequency).ToArray();
        string raw=Path.Combine(root,"frame-ids.raw");int ox=(surface.Origin.X-screen.X)*options.Width/display.Width,oy=(surface.Origin.Y-screen.Y+8)*options.Height/display.Height;
        using var deadline=new CancellationTokenSource(120000);
        ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",clip.LocalPath,"-an","-vf",$"crop={512*options.Width/display.Width}:2:{ox}:{oy},scale=32:1:flags=neighbor,format=gray","-fps_mode","passthrough","-f","rawvideo",raw],deadline.Token).GetAwaiter().GetResult();
        byte[] bytes=File.ReadAllBytes(raw);var ids=new List<int>();int invalid=0;
        for(int at=0;at+32<=bytes.Length;at+=32){int id=0,inverse=0;for(int bit=0;bit<16;bit++){if(bytes[at+bit]>128)id|=1<<bit;if(bytes[at+16+bit]>128)inverse|=1<<bit;}if((id^inverse)!=65535)invalid++;ids.Add(id);}
        var steps=ids.Skip(1).Zip(ids,(a,b)=>(a-b+65536)%65536).ToArray();
        // A partially covered marker can otherwise make a long freeze look like
        // an encoder defect. Require coverage throughout the exported interval.
        bool valid=ids.Count>fps*3&&invalid==0;
        Program.Write("capture-cadence-test.json",new{Pass=valid,MarkerValid=valid,InvalidMarkers=invalid,codec,options.Width,options.Height,ndi,QpcFrequency=Stopwatch.Frequency,fps,seconds,surface.Presented,Captured=samples.Length,SourceRepeated=samples.Zip(samples.Skip(1),(a,b)=>a.Id==b.Id).Count(v=>v),
            SourceIntervalMedian=deltas.Order().ElementAt(deltas.Length/2),SourceIntervalP95=deltas.Order().ElementAt((int)(deltas.Length*.95)),SourceIntervalMaximum=deltas.Max(),
            engine.MaxCaptureMs,engine.MaxObserverMs,recorder.MaxConvertMs,recorder.MaxPipeWriteMs,recorder.RepeatedInputFrames,recorder.HistoryMisses,
            OutputFrames=ids.Count,OutputRepeated=steps.Count(s=>s==0),OutputMaxStep=steps.Max(),recorder.Error,clip.LocalPath,Root=root,Samples=samples.Select(s=>new{s.Time,s.Id,s.Arrival,s.Valid}).ToArray(),OutputIds=ids});
        if(!valid)throw new Exception("The presentation test surface was obscured; its frame-ID comparison is invalid.");
    }
}
