namespace SdrCapture;

static class RecordingPerformanceTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        Deltas();GpuCsv();LabelsAndCommand();SystemCpu();
        Program.Write("recording-performance-tests.json",new{Pass=true,MachineNormalizedCpu=true,PerInterfaceNetworkDeltas=true,CounterResetAndNewAdapters=true,PausedBaseline=true,ZeroElapsedBounded=true,MalformedGpuCsvRejected=true,MultiGpuBounds=true,UnknownGpuIsNull=true,ReadOnlySystem32Command=true,ProcessAndSystemLabels=true});
    }
    static RecordingPerformanceCounters Sample(long timestamp,double cpu,params RecordingNetworkCounters[] network)=>new(timestamp,TimeSpan.FromSeconds(cpu),104857600,network);
    static void SystemCpu()
    {
        var baseline=new RecordingPerformanceBaseline(4,1000);
        baseline.Observe(true,Sample(1000,1) with{SystemIdle=100,SystemTotal=1000});
        var active=baseline.Observe(true,Sample(3000,2) with{SystemIdle=200,SystemTotal=1400});
        Require(active.SystemCpuPercent==75,"System CPU omitted encoder processes or used a per-core percentage.");
        var reset=baseline.Observe(true,Sample(5000,3) with{SystemIdle=10,SystemTotal=20});
        Require(reset.SystemCpuPercent==null,"Reset system CPU counters fabricated utilization.");
    }
    static void Deltas()
    {
        var baseline=new RecordingPerformanceBaseline(4,1000);
        var first=baseline.Observe(true,Sample(1000,1,new RecordingNetworkCounters("A",1000,2000)));
        Require(!first.Ready&&first.CpuPercent==0&&first.SentPerSecond==0,"The first sample reported rates without a baseline.");
        var next=baseline.Observe(true,Sample(3000,3,new RecordingNetworkCounters("A",5000,8000)));
        Require(next.Ready&&next.CpuPercent==25&&next.SentPerSecond==2000&&next.ReceivedPerSecond==3000,"CPU was not normalized by all processors or NIC deltas were not divided by elapsed seconds.");
        var added=baseline.Observe(true,Sample(5000,4,new RecordingNetworkCounters("A",7000,10000),new RecordingNetworkCounters("B",9_000_000_000,18_000_000_000)));
        Require(added.CpuPercent==12.5&&added.SentPerSecond==1000&&added.ReceivedPerSecond==1000,"Adding a network adapter injected its lifetime counters as current traffic.");
        var both=baseline.Observe(true,Sample(7000,5,new RecordingNetworkCounters("A",9000,12000),new RecordingNetworkCounters("B",9_000_002_000,18_000_004_000)));
        Require(both.SentPerSecond==2000&&both.ReceivedPerSecond==3000,"Independent system NIC deltas did not aggregate.");
        var removed=baseline.Observe(true,Sample(9000,6,new RecordingNetworkCounters("B",9_000_004_000,18_000_008_000)));
        Require(removed.SentPerSecond==1000&&removed.ReceivedPerSecond==2000,"Removing one interface changed the other interface's measured rate.");
        var reset=baseline.Observe(true,Sample(11000,1,new RecordingNetworkCounters("B",10,20)));
        Require(reset.CpuPercent==0&&reset.SentPerSecond==0&&reset.ReceivedPerSecond==0,"Reset counters generated negative traffic or CPU.");
        var bounded=baseline.Observe(true,Sample(13000,100,new RecordingNetworkCounters("B",110,220)));
        Require(bounded.CpuPercent==100&&bounded.SentPerSecond==50&&bounded.ReceivedPerSecond==100,"CPU exceeded machine capacity or post-reset NIC rates were lost.");
        baseline.Observe(false,null);
        var resumed=baseline.Observe(true,Sample(30000,200,new RecordingNetworkCounters("B",8_000_000_000,9_000_000_000)));
        Require(!resumed.Ready&&resumed.CpuPercent==0&&resumed.SentPerSecond==0,"Resume included CPU or traffic accumulated while sampling was paused.");
        var after=baseline.Observe(true,Sample(32000,201,new RecordingNetworkCounters("B",8_000_001_000,9_000_002_000)));
        Require(after.Ready&&after.CpuPercent==12.5&&after.SentPerSecond==500&&after.ReceivedPerSecond==1000,"Resume failed to establish a fresh sampling interval.");
        var zero=baseline.Observe(true,Sample(32000,202,new RecordingNetworkCounters("B",long.MaxValue,long.MaxValue)));
        Require(!zero.Ready&&zero.CpuPercent==0&&zero.SentPerSecond==0&&zero.ReceivedPerSecond==0,"Zero elapsed time generated infinity or a false rate.");
        var backwards=baseline.Observe(true,Sample(31000,203));Require(!backwards.Ready&&backwards.CpuPercent==0,"A backwards timestamp generated a rate.");
    }
    static void GpuCsv()
    {
        var one=RecordingPerformanceMonitor.ParseGpuCsv("NVIDIA GeForce RTX 5070, 72, 2, 9020\r\n");
        Require(one is{Label:"NVIDIA GeForce RTX 5070",Utilization:72,VideoEncode:2,MemoryUsed:9458155520,Error:null},"The documented read-only NVIDIA CSV did not parse into the reported units.");
        var multi=RecordingPerformanceMonitor.ParseGpuCsv("\"NVIDIA, One\",10,4,100\n\"NVIDIA \"\"Two\"\"\",90,8,200\n");
        Require(multi.Utilization==90&&multi.VideoEncode==8&&multi.MemoryUsed==314572800&&multi.Label!.Contains("max reported across 2 GPUs"),"Multi-GPU rates were summed as a fake utilization percentage or quoted CSV was lost.");
        var partial=RecordingPerformanceMonitor.ParseGpuCsv("NVIDIA A,N/A,[Not Supported],N/A\nNVIDIA B,30,15,512");
        Require(partial.Utilization==30&&partial.VideoEncode==15&&partial.MemoryUsed==null&&partial.Error==null,"Partial unsupported GPU data became fabricated zero-valued metrics.");
        var unavailable=RecordingPerformanceMonitor.ParseGpuCsv("NVIDIA A,N/A,[N/A],[Not Supported]");
        Require(unavailable.Utilization==null&&unavailable.VideoEncode==null&&unavailable.MemoryUsed==null&&unavailable.Error==null,"Unsupported GPU metrics were reported as idle.");
        var idle=RecordingPerformanceMonitor.ParseGpuCsv("NVIDIA A,0,0,0");Require(idle.Utilization==0&&idle.VideoEncode==0&&idle.MemoryUsed==0,"A real zero measurement became unavailable.");
        string[] invalid=["","NVIDIA A,101,0,1","NVIDIA A,-1,0,1","NVIDIA A,NaN,0,1","NVIDIA A,Infinity,0,1","NVIDIA A,1,2,-1","NVIDIA A,1,2,99999999999999999999999","NVIDIA A,1,2","NVIDIA A,1,2,3,4","\"NVIDIA A,1,2,3",new string('A',129)+",1,2,3",new string('A',16385),string.Join('\n',Enumerable.Repeat("NVIDIA A,1,2,3",17))];
        foreach(string text in invalid)
        {var rejected=RecordingPerformanceMonitor.ParseGpuCsv(text);Require(rejected.Utilization==null&&rejected.VideoEncode==null&&rejected.MemoryUsed==null&&rejected.Error!=null,"Malformed/unbounded GPU CSV was accepted.");}
        var limit=RecordingPerformanceMonitor.ParseGpuCsv(string.Join('\n',Enumerable.Repeat("NVIDIA A,1,2,1",16)));
        Require(limit.Error==null&&limit.MemoryUsed==16777216,"The bounded multi-GPU row limit rejected a valid boundary sample.");
    }
    static void LabelsAndCommand()
    {
        var snapshot=new RecordingPerformanceSnapshot(DateTimeOffset.UnixEpoch,true,true,12.5,104857600,125000,250000,70,10,"NVIDIA A",SystemGpuMemoryUsedBytes:1048576);
        string summary=RecordingPerformanceMonitor.FormatSummary(snapshot);
        Require(summary.Contains("DeskGlide process CPU 12.5% of machine")&&summary.Contains("RAM 100.0 MiB")&&summary.Contains("System NIC sum ↑1.0 / ↓2.0 Mbps")&&summary.Contains("System GPU (NVIDIA A)")&&summary.Contains("video encode: 10.0%"),"The summary attributed system GPU/network utilization to DeskGlide or changed metric units.");
        string unknown=RecordingPerformanceMonitor.FormatSummary(snapshot with{SystemGpuPercent=null,SystemVideoEncodePercent=null,GpuLabel=null,SystemGpuMemoryUsedBytes=null});
        Require(unknown.Contains("System GPU / video encode: unavailable"),"A non-NVIDIA/unavailable GPU appeared idle.");
        string warming=RecordingPerformanceMonitor.FormatSummary(snapshot with{HasRateSample=false});Require(warming.Contains("CPU warming up")&&warming.Contains("System NIC sum warming up"),"A new baseline displayed false idle rates.");
        string paused=RecordingPerformanceMonitor.FormatSummary(snapshot with{Active=false,ProcessCpuPercent=97});Require(paused.Contains("paused")&&!paused.Contains("97"),"Inactive monitoring displayed stale active metrics.");
        if(OperatingSystem.IsWindows())
        {
            var info=RecordingPerformanceMonitor.GpuStartInfo();string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
            Require(info.FileName==Path.Combine(system,"nvidia-smi.exe")&&info.WorkingDirectory==system&&!info.UseShellExecute&&info.CreateNoWindow,"GPU monitoring searched an untrusted executable path or opened a visible process.");
            Require(info.ArgumentList.SequenceEqual(new[]{"--query-gpu=name,utilization.gpu,utilization.encoder,memory.used","--format=csv,noheader,nounits"}),"GPU monitoring escaped the fixed read-only argument allowlist.");
        }
    }
}
