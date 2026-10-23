using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;

namespace SdrCapture;

sealed record RecordingPerformanceSnapshot(DateTimeOffset SampledAt,bool Active,bool HasRateSample,double ProcessCpuPercent,long WorkingSetBytes,double SystemSendBytesPerSecond,double SystemReceiveBytesPerSecond,double? SystemGpuPercent=null,double? SystemVideoEncodePercent=null,string? GpuLabel=null,string? Error=null,long? SystemGpuMemoryUsedBytes=null,double? SystemCpuPercent=null);
sealed record RecordingNetworkCounters(string Id,long Sent,long Received);
sealed record RecordingPerformanceCounters(long Timestamp,TimeSpan CpuTime,long WorkingSetBytes,RecordingNetworkCounters[] Network,string? Error=null,ulong SystemIdle=0,ulong SystemTotal=0);
sealed record RecordingPerformanceRates(bool Ready,double CpuPercent,double SentPerSecond,double ReceivedPerSecond,double? SystemCpuPercent=null);
sealed record RecordingGpuMetrics(string? Label,double? Utilization,double? VideoEncode,long? MemoryUsed,string? Error=null);

sealed class RecordingPerformanceBaseline(int processors,long frequency)
{
    readonly int processors=processors>0?processors:throw new ArgumentOutOfRangeException(nameof(processors));
    readonly long frequency=frequency>0?frequency:throw new ArgumentOutOfRangeException(nameof(frequency));
    RecordingPerformanceCounters? previous;
    public RecordingPerformanceRates Observe(bool active,RecordingPerformanceCounters? current)
    {
        if(!active||current==null){previous=null;return new(false,0,0,0);}
        var before=previous;previous=current;
        if(before==null||current.Timestamp<=before.Timestamp)return new(false,0,0,0);
        double seconds=((double)current.Timestamp-before.Timestamp)/frequency;
        double cpu=Math.Clamp(((double)current.CpuTime.Ticks-before.CpuTime.Ticks)/TimeSpan.TicksPerSecond/seconds/processors*100,0,100);
        var old=before.Network.GroupBy(n=>n.Id,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        double sent=0,received=0;
        foreach(var nic in current.Network.GroupBy(n=>n.Id,StringComparer.Ordinal).Select(g=>g.First()))
        {
            if(!old.TryGetValue(nic.Id,out var last))continue;
            // New/reset/removed adapters establish a baseline rather than a spike.
            if(nic.Sent>=last.Sent&&last.Sent>=0)sent+=(double)nic.Sent-last.Sent;
            if(nic.Received>=last.Received&&last.Received>=0)received+=(double)nic.Received-last.Received;
        }
        double? systemCpu=current.SystemTotal>before.SystemTotal&&current.SystemIdle>=before.SystemIdle?
            Math.Clamp(100*(1-(current.SystemIdle-before.SystemIdle)/(double)(current.SystemTotal-before.SystemTotal)),0,100):null;
        return new(true,cpu,sent/seconds,received/seconds,systemCpu);
    }
}

sealed class RecordingPerformanceMonitor:IDisposable
{
    readonly Func<bool> active;
    readonly CancellationTokenSource stop=new();
    readonly Task worker;
    readonly object snapshotGate=new();
    RecordingPerformanceSnapshot snapshot=Empty();
    int disposed;
    public RecordingPerformanceSnapshot Snapshot=>Volatile.Read(ref snapshot);
    public string Summary=>FormatSummary(Snapshot);
    public RecordingPerformanceMonitor(Func<bool> active)
    {
        ArgumentNullException.ThrowIfNull(active);this.active=active;worker=Task.Run(RunAsync);
    }
    static RecordingPerformanceSnapshot Empty(string? error=null)=>new(DateTimeOffset.UtcNow,false,false,0,0,0,0,Error:error);
    void Publish(RecordingPerformanceSnapshot value){lock(snapshotGate){if(disposed==0)Volatile.Write(ref snapshot,value);}}
    async Task RunAsync()
    {
        var baseline=new RecordingPerformanceBaseline(Environment.ProcessorCount,Stopwatch.Frequency);
        try
        {
            using var process=Process.GetCurrentProcess();using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                if(stop.IsCancellationRequested)break;
                bool enabled;
                try{enabled=active();}
                catch{baseline.Observe(false,null);Publish(Empty("Recording activity state is unavailable."));continue;}
                if(!enabled)
                {
                    baseline.Observe(false,null);
                    if(Snapshot.Active||Snapshot.Error!=null)Publish(Empty());
                    continue;
                }
                try
                {
                    var counters=ReadCounters(process);var rates=baseline.Observe(true,counters);
                    var gpu=await QueryGpuAsync(stop.Token);
                    if(!active()){baseline.Observe(false,null);Publish(Empty());continue;}
                    string? error=counters.Error==null?gpu.Error:gpu.Error==null?counters.Error:counters.Error+" "+gpu.Error;
                    Publish(new(DateTimeOffset.UtcNow,true,rates.Ready,rates.CpuPercent,counters.WorkingSetBytes,rates.SentPerSecond,rates.ReceivedPerSecond,gpu.Utilization,gpu.VideoEncode,gpu.Label,error,gpu.MemoryUsed,rates.SystemCpuPercent));
                }
                catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
                catch(Exception failure){baseline.Observe(false,null);Publish(Empty("Resource metrics are unavailable: "+failure.GetType().Name+"."));}
            }
            while(await timer.WaitForNextTickAsync(stop.Token));
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception failure){Publish(Empty("Resource monitor stopped: "+failure.GetType().Name+"."));}
    }
    static RecordingPerformanceCounters ReadCounters(Process process)
    {
        process.Refresh();TimeSpan cpu=process.TotalProcessorTime;long memory=process.WorkingSet64;
        var counters=new List<RecordingNetworkCounters>();string? error=null;
        try
        {
            foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if(nic.NetworkInterfaceType==NetworkInterfaceType.Loopback||nic.OperationalStatus!=OperationalStatus.Up)continue;
                    var stats=nic.GetIPStatistics();counters.Add(new(nic.Id,stats.BytesSent,stats.BytesReceived));
                }
                catch(NetworkInformationException){error="Some system network counters are unavailable.";}
            }
        }
        catch(NetworkInformationException){error="System network counters are unavailable.";}
        ulong idle=0,total=0;
        if(OperatingSystem.IsWindows()&&GetSystemTimes(out idle,out ulong kernel,out ulong user))total=kernel+user;
        return new(Stopwatch.GetTimestamp(),cpu,memory,counters.ToArray(),error,idle,total);
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return:System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    internal static async Task<RecordingGpuMetrics> QueryGpuAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Never search PATH or install a monitoring tool. This is the driver's
        // fixed read-only query, with no writable options or user-supplied args.
        if(!OperatingSystem.IsWindows())return new(null,null,null,null);
        string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        if(!Path.IsPathFullyQualified(system))return new(null,null,null,null);
        string executable=Path.Combine(system,"nvidia-smi.exe");
        if(!File.Exists(executable))return new(null,null,null,null);
        var info=GpuStartInfo();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        using var process=new Process{StartInfo=info};
        try
        {
            if(!process.Start())return new(null,null,null,null,"GPU metrics are unavailable.");
            using var killed=timeout.Token.Register(()=>Kill(process));
            Task<string> output=BoundedOutput(process.StandardOutput,16384,timeout.Token),error=BoundedOutput(process.StandardError,2048,timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token),output,error);
            if(process.ExitCode!=0)return new(null,null,null,null,"GPU metrics are unavailable (nvidia-smi exit "+process.ExitCode+").");
            return ParseGpuCsv(await output);
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){return new(null,null,null,null,"GPU metrics query exceeded 900 ms.");}
        catch(Exception failure)when(failure is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {return new(null,null,null,null,"GPU metrics are unavailable: "+failure.GetType().Name+".");}
        finally{Kill(process);}
    }
    internal static ProcessStartInfo GpuStartInfo()
    {
        string system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        if(!Path.IsPathFullyQualified(system))throw new PlatformNotSupportedException("Windows System32 is unavailable.");
        var info=new ProcessStartInfo(Path.Combine(system,"nvidia-smi.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=system};
        info.ArgumentList.Add("--query-gpu=name,utilization.gpu,utilization.encoder,memory.used");info.ArgumentList.Add("--format=csv,noheader,nounits");
        return info;
    }
    static void Kill(Process process){try{if(!process.HasExited)process.Kill(true);}catch{}}
    static async Task<string> BoundedOutput(StreamReader reader,int maximum,CancellationToken token)
    {
        var text=new StringBuilder();char[] buffer=new char[512];int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),token))>0)
        {if(text.Length+count>maximum)throw new IOException("GPU metrics output exceeded its limit.");text.Append(buffer,0,count);}
        return text.ToString();
    }
    internal static RecordingGpuMetrics ParseGpuCsv(string? text)
    {
        static RecordingGpuMetrics Invalid()=>new(null,null,null,null,"GPU metrics output is invalid or exceeds its limit.");
        if(string.IsNullOrWhiteSpace(text)||text.Length>16384)return Invalid();
        string[] rows=text.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(rows.Length is 0 or >16)return Invalid();
        var parsed=new List<RecordingGpuMetrics>();
        foreach(string row in rows)
        {
            var fields=CsvRow(row);
            if(fields is not{Length:4}||fields[0].Length is 0 or >128||fields[0].Any(char.IsControl)||!Percent(fields[1],out var gpu)||!Percent(fields[2],out var encoder)||!Memory(fields[3],out var memory))return Invalid();
            parsed.Add(new(fields[0],gpu,encoder,memory));
        }
        static double? Maximum(IEnumerable<double?> values){var present=values.Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();return present.Length==0?null:present.Max();}
        string label=parsed.Count==1?parsed[0].Label!:string.Join(" + ",parsed.Select(g=>g.Label).Distinct().Take(3))+" · max reported across "+parsed.Count+" GPUs";
        long? used=null;
        if(parsed.All(g=>g.MemoryUsed.HasValue))try{used=parsed.Sum(g=>g.MemoryUsed!.Value);}catch(OverflowException){return Invalid();}
        return new(label,Maximum(parsed.Select(g=>g.Utilization)),Maximum(parsed.Select(g=>g.VideoEncode)),used);
    }
    static bool Unavailable(string value)=>value is "N/A" or "[N/A]" or "Not Supported" or "[Not Supported]";
    static bool Percent(string value,out double? result)
    {
        result=null;if(Unavailable(value))return true;
        if(!double.TryParse(value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double number)||!double.IsFinite(number)||number is <0 or >100)return false;
        result=number;return true;
    }
    static bool Memory(string value,out long? result)
    {
        result=null;if(Unavailable(value))return true;
        if(!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out long number)||number<0||number>long.MaxValue/(1024*1024))return false;
        result=number*1024*1024;return true;
    }
    static string[]? CsvRow(string row)
    {
        var fields=new List<string>();var field=new StringBuilder();bool quoted=false,closed=false;
        for(int i=0;i<row.Length;i++)
        {
            char c=row[i];
            if(quoted)
            {
                if(c=='"'){if(i+1<row.Length&&row[i+1]=='"'){field.Append('"');i++;}else{quoted=false;closed=true;}}
                else field.Append(c);
            }
            else if(c==','){fields.Add(field.ToString().Trim());field.Clear();closed=false;if(fields.Count>3)return null;}
            else if(c=='"'){if(closed||field.ToString().Any(ch=>!char.IsWhiteSpace(ch)))return null;field.Clear();quoted=true;}
            else if(closed){if(!char.IsWhiteSpace(c))return null;}
            else field.Append(c);
        }
        if(quoted)return null;fields.Add(field.ToString().Trim());return fields.ToArray();
    }
    internal static string FormatSummary(RecordingPerformanceSnapshot value)
    {
        if(!value.Active)return value.Error??"Resource monitor paused; no CPU, network or GPU sampling.";
        static string Number(double number)=>number.ToString("0.0",CultureInfo.InvariantCulture);
        static string Percent(double? number)=>number.HasValue?Number(number.Value)+"%":"unavailable";
        string cpu=value.HasRateSample?Number(value.ProcessCpuPercent)+"% of machine":"warming up";
        string network=value.HasRateSample?$"↑{Number(value.SystemSendBytesPerSecond*8/1_000_000)} / ↓{Number(value.SystemReceiveBytesPerSecond*8/1_000_000)} Mbps":"warming up";
        string gpu=value.GpuLabel==null?"System GPU / video encode: unavailable":$"System GPU ({value.GpuLabel}): {Percent(value.SystemGpuPercent)} · video encode: {Percent(value.SystemVideoEncodePercent)}";
        string memory=value.SystemGpuMemoryUsedBytes.HasValue?" · GPU memory "+Number(value.SystemGpuMemoryUsedBytes.Value/1048576.0)+" MiB":"";
        return $"DeskGlide process CPU {cpu} · RAM {Number(value.WorkingSetBytes/1048576.0)} MiB | System CPU (all apps, including encoders): {Percent(value.SystemCpuPercent)} | System NIC sum {network} | {gpu}{memory}"+(value.Error==null?"":" | "+value.Error);
    }
    public void Dispose()
    {
        lock(snapshotGate){if(disposed!=0)return;disposed=1;Volatile.Write(ref snapshot,Empty());}
        stop.Cancel();
        _=worker.ContinueWith(_=>stop.Dispose(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
}
