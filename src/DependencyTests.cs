namespace SdrCapture;

static class DependencyTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task RunAsync()
    {
        var setup=new DependencySetup();bool ready=true;int installs=0;
        async Task Install(CancellationToken token){++installs;await Task.Delay(25,token);ready=true;}
        await setup.EnsureAsync(()=>ready,Install,default);Require(installs==0,"An existing NDI installation was replaced");
        ready=false;await Task.WhenAll(Enumerable.Range(0,4).Select(_=>setup.EnsureAsync(()=>ready,Install,default)));
        Require(installs==1&&ready,"Concurrent NDI features launched more than one installer");
        var declined=new DependencySetup();int attempts=0;
        Task Fail(CancellationToken token){++attempts;throw new IOException("Installer cancelled");}
        for(int i=0;i<3;i++)try{await declined.EnsureAsync(()=>false,Fail,default);}catch(IOException){}
        Require(attempts==1,"A declined NDI installation reopened its wizard automatically");
        try{await declined.EnsureAsync(()=>false,Fail,default,retry:true);}catch(IOException){}
        Require(attempts==2,"A manual NDI retry did not restart setup");
        using(var cancellation=new CancellationTokenSource())
        {
            cancellation.Cancel();try{await new DependencySetup().EnsureAsync(()=>false,Install,cancellation.Token);throw new Exception("Cancelled setup was started");}catch(OperationCanceledException){}
        }
        Require(installs==1,"Cancelled setup installed a dependency");
        bool rejected=false;try{NdiRuntime.VerifyPackage([1,2,3]);}catch(IOException){rejected=true;}Require(rejected,"An untrusted NDI installer was accepted");
        var candidates=NdiNative.RuntimeCandidates(@"C:\Program Files",@"C:\App",(key,target)=>key=="NDI_RUNTIME_DIR_V6"&&target==EnvironmentVariableTarget.Machine?@"C:\InstalledAfterLaunch":null).ToArray();
        Require(candidates.Contains(@"C:\InstalledAfterLaunch\Processing.NDI.Lib.x64.dll")&&candidates.Contains(@"C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll"),"Newly installed runtime could not be found without restarting the application");
        foreach(string codec in new[]{"H264","HEVC","AV1"})
        foreach(int fps in new[]{60,120})
        foreach(string quality in new[]{"Ultra","High","Medium","Low"})
        {
            var profiles=RecordingEncoders.Candidates(new(){Codec=codec,Fps=fps,Quality=quality},2560,1440);
            var tried=new List<string>();var selected=await RecordingEncoders.FindAsync(profiles,(encoder,_)=>{tried.Add(encoder.Label);return Task.FromResult(encoder.Label=="AMD AMF");},default);
            Require(selected?.Label=="AMD AMF"&&tried.SequenceEqual(new[]{"NVIDIA NVENC","AMD AMF"}),"NVIDIA failure did not select the available AMD encoder");
            selected=await RecordingEncoders.FindAsync(profiles,(encoder,_)=>Task.FromResult(encoder.Label=="Intel Quick Sync"),default);Require(selected?.Label=="Intel Quick Sync","An Intel-only PC cannot record");
            Require(await RecordingEncoders.FindAsync(profiles,(_,_)=>Task.FromResult(false),default)==null,"Unavailable hardware silently switched to CPU recording");
            foreach(var encoder in profiles)
            {
                var args=RecordingEncoders.ProbeArguments(encoder,2560,1440,fps);
                Require(args.Contains(encoder.Name)&&args.Contains("nv12")&&args.Contains((fps*2).ToString())&&args.Contains($"color=c=black:s=2560x1440:r={fps}")&&!args.Contains("libx264"),"GPU preflight does not use the chosen codec, size or frame rate");
                if(encoder.Label!="NVIDIA NVENC")Require(!args.Contains("p4")&&!args.Contains("-rc-lookahead")&&!args.Contains("-tier"),"NVIDIA-only options leaked into another GPU encoder");
            }
        }
        using(var cancellation=new CancellationTokenSource())
        {
            cancellation.Cancel();bool probed=false;
            try{await RecordingEncoders.FindAsync(RecordingEncoders.Candidates(new(),1920,1080),(_,_)=>{probed=true;return Task.FromResult(true);},cancellation.Token);throw new Exception("Cancelled encoder probe was run");}catch(OperationCanceledException){}
            Require(!probed,"A cancelled replay startup used the GPU");
        }
        Program.Write("dependency-tests.json",new{Pass=true,ExistingRuntimeReused=true,OneInstallerPerProcess=true,NoRepeatedUacOnCancellation=true,FreshMachineEnvironmentRead=true,RuntimeIntegrity=true,NvidiaAmdIntelSelection=true,NoImplicitSoftwareEncoder=true});
    }
    public static async Task VerifyRuntimeDownloadAsync()
    {
        using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(2)};
        byte[] bytes=await http.GetByteArrayAsync(NdiRuntime.DownloadUrl);NdiRuntime.VerifyPackage(bytes);
        string folder=Path.Combine(Log.Folder,"DependencyTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string file=Path.Combine(folder,"NDI-Runtime.exe");
        try{await File.WriteAllBytesAsync(file,bytes);DiscordSetup.VerifySignature(file,"NDI Runtime");Program.Write("ndi-runtime-package-test.json",new{Pass=true,Version=NdiRuntime.Version,Sha256=NdiRuntime.Sha256,Bytes=bytes.Length,WindowsSignatureVerified=true,InstallerLaunched=false});}
        finally{File.Delete(file);Directory.Delete(folder,false);}
    }
}
