using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
namespace SdrCapture;

// A visible, automatically closing DirectX test surface. Does not set a display mode,
// HDR switch, refresh rate, registry setting or persistent system preference.
static class FullscreenTest
{
    public static void Run()
    {
        var display=DisplayInfo.All().First();
        using var engine=new StreamEngine();engine.Start(new(display.Device),"SdrCapture SDR Fullscreen Test");
        using var window=new TestWindow(display,engine);
        Application.Run(window);
        if(window.Failure!=null) throw window.Failure;
        if(window.Transitions<4 || window.FreshAfterTransitions<4) throw new Exception("Fullscreen recovery not verified");
    }
    sealed class TestWindow:Form
    {
        readonly DisplayInfo display;readonly StreamEngine engine;
        readonly System.Windows.Forms.Timer tick=new(){Interval=16};
        readonly Stopwatch clock=new();
        IDXGIFactory2? factory;ID3D11Device? device;ID3D11DeviceContext? context;
        IDXGISwapChain1? swap;ID3D11RenderTargetView? target;
        readonly List<object> results=new();
        int phase;long freshBefore;double changedAt;bool awaitingFresh;
        public Exception? Failure {get;private set;}
        public int Transitions {get;private set;}
        public int FreshAfterTransitions {get;private set;}
        public TestWindow(DisplayInfo d,StreamEngine e)
        {
            display=d;engine=e;Text="SDR Capture — fullscreen recovery test (auto close)";
            StartPosition=FormStartPosition.CenterScreen;ClientSize=new System.Drawing.Size(960,540);
            Shown+=(_,_)=>Initialize();FormClosing+=(_,_)=>Finish();
            tick.Tick+=(_,_)=>Step();
        }
        void Initialize()
        {
            try
            {
                factory=DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
                D3D11.D3D11CreateDevice(null,DriverType.Hardware,DeviceCreationFlags.BgraSupport,[FeatureLevel.Level_11_0],out device,out context).CheckError();
                var desc=new SwapChainDescription1{Width=960,Height=540,Format=Format.B8G8R8A8_UNorm,
                    BufferCount=2,BufferUsage=Usage.RenderTargetOutput,SampleDescription=new SampleDescription(1,0),SwapEffect=SwapEffect.FlipDiscard};
                swap=factory.CreateSwapChainForHwnd(device,Handle,desc);
                CreateTarget();Activate();BringToFront();clock.Start();tick.Start();
            }
            catch(Exception ex){Failure=ex;Close();}
        }
        void CreateTarget(){using var texture=swap!.GetBuffer<ID3D11Texture2D>(0);target=device!.CreateRenderTargetView(texture);}
        void Step()
        {
            try
            {
                double now=clock.Elapsed.TotalSeconds;
                if(awaitingFresh && engine.CapturedFrames>freshBefore+2)
                {
                    results.Add(new{Transition=Transitions,RecoveryMs=(now-changedAt)*1000,engine.CapturedFrames,engine.Recoveries});
                    FreshAfterTransitions++;awaitingFresh=false;
                }
                int desired=now<3?0:now<6?1:now<9?2:now<12?3:4;
                if(desired!=phase)
                {
                    if(awaitingFresh) throw new Exception("No fresh frames after previous fullscreen transition");
                    phase=desired;bool full=(phase&1)==1;
                    target?.Dispose();target=null;context!.ClearState();context.Flush();
                    Activate();BringToFront();
                    swap!.SetFullscreenState(full,null).CheckError();
                    swap.ResizeBuffers(2,full?(uint)display.Width:960,full?(uint)display.Height:540,Format.Unknown,SwapChainFlags.None).CheckError();
                    CreateTarget();Transitions++;freshBefore=engine.CapturedFrames;changedAt=now;awaitingFresh=true;
                }
                float v=(float)(.12+.05*Math.Sin(now*4));
                context!.ClearRenderTargetView(target!,new Color4(v,.15f,.2f,1));
                swap!.Present(0,PresentFlags.None).CheckError();
                if(now>15) Close();
            }
            catch(Exception ex){Failure=ex;Close();}
        }
        void Finish()
        {
            tick.Stop();
            try{swap?.SetFullscreenState(false,null);}catch{}
            target?.Dispose();swap?.Dispose();context?.Dispose();device?.Dispose();factory?.Dispose();tick.Dispose();
            Program.Write("fullscreen-test.json",new{Transitions,FreshAfterTransitions,Results=results,engine.Frames,engine.CapturedFrames,engine.Recoveries,engine.Error,Failure=Failure?.ToString()});
        }
    }
}
