using System.Text.Json;

namespace SdrCapture;

sealed record DeliveryJob(string Source,string Destination,double Seconds=0);

// A separate worker owns slow SMB I/O. Neither capture nor the encoder waits on it.
sealed class ReplayDelivery:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly Task worker;
    readonly HashSet<string> warned=[];
    readonly SemaphoreSlim wake=new(0,1);
    public event Action<string>? Notification;
    public event Action<DeliveryJob>? Saved;
    public string Status {get;private set;}="";
    public ReplayDelivery(){worker=Task.Run(Run);}
    public void Wake(){if(wake.CurrentCount==0)try{wake.Release();}catch(SemaphoreFullException){}}
    async Task Run()
    {
        while(!stop.IsCancellationRequested)
        {
            string folder=Path.Combine(ReplayTools.Root,"saved");
            if(Directory.Exists(folder))foreach(string manifest in Directory.GetFiles(folder,"*.delivery.json"))
            {
                if(stop.IsCancellationRequested)break;
                try
                {
                    var job=JsonSerializer.Deserialize<DeliveryJob>(await File.ReadAllTextAsync(manifest,stop.Token))??throw new IOException("Повреждена задача сохранения");
                    string source=Path.GetFullPath(job.Source);
                    if(!source.StartsWith(Path.GetFullPath(folder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!source.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase))throw new IOException("Неверный локальный путь клипа");
                    Status="Копирование: "+job.Destination;
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(CopyTimeout(new FileInfo(source).Length));
                    await CopyAsync(job,timeout.Token);
                    File.Delete(manifest);File.Delete(source);warned.Remove(manifest);
                    Status=UiStrings.F("Saved {0}.\n{1}",ReplayTime.Format(job.Seconds),job.Destination);
                    // A notification failure must never retry an already delivered clip.
                    try{Saved?.Invoke(job);}catch(Exception e){Log.Write("Replay save notification: "+e.Message);}
                    try{Notification?.Invoke(Status);}catch(Exception e){Log.Write("Replay notification: "+e.Message);}
                }
                catch(OperationCanceledException) when(stop.IsCancellationRequested){break;}
                catch(Exception e)
                {
                    Status="Клип сохранён локально; повтор копирования через 30 с. "+e.Message;
                    if(warned.Add(manifest))Notification?.Invoke(Status+"\n"+folder);
                }
            }
            try{await wake.WaitAsync(TimeSpan.FromSeconds(30),stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    internal static TimeSpan CopyTimeout(long bytes)=>TimeSpan.FromSeconds(Math.Max(60,bytes/(4.0*1024*1024)+30));
    internal static async Task CopyAsync(DeliveryJob job,CancellationToken token)
    {
        string destination=Path.GetFullPath(job.Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if(File.Exists(destination))
        {
            // A crash after atomic publication may have left the delivery manifest behind.
            if(new FileInfo(destination).Length==new FileInfo(job.Source).Length)
            {
                await using var a=File.OpenRead(destination);await using var b=File.OpenRead(job.Source);
                var hashA=await System.Security.Cryptography.SHA256.HashDataAsync(a,token);var hashB=await System.Security.Cryptography.SHA256.HashDataAsync(b,token);
                if(hashA.AsSpan().SequenceEqual(hashB))return;
            }
            throw new IOException("Файл с таким именем уже существует: "+destination);
        }
        string partial=destination+".uploading";
        try
        {
            await using(var input=new FileStream(job.Source,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan))
            await using(var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan))
            {await input.CopyToAsync(output,1024*1024,token);await output.FlushAsync(token);}
            if(new FileInfo(partial).Length!=new FileInfo(job.Source).Length)throw new IOException("Не совпал размер скопированного клипа");
            File.Move(partial,destination);
        }
        catch{try{File.Delete(partial);}catch{}throw;}
    }
    public void Dispose(){stop.Cancel();/* SMB shutdown never delays NDI shutdown or the UI. */}
}
