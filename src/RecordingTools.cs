using System.IO.Compression;
using System.Security.Cryptography;

namespace SdrCapture;

// Download only when replay is requested. Keep dependencies outside the app's
// update payload, while continuing to support existing portable installations.
static class RecordingTools
{
    internal const string Version="9.0.2";
    internal const string ArchiveSha256="60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";
    internal const string DownloadUrl="https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip";
    static readonly string[] files=["ffmpeg.exe","ffprobe.exe","FFmpeg-LICENSE"];
    static readonly SemaphoreSlim gate=new(1,1);
    static readonly HttpClient http=new(){Timeout=TimeSpan.FromMinutes(10)};
    static DateTimeOffset retryAfter;
    static string? lastFailure;
    internal static string CacheRoot=>Environment.GetEnvironmentVariable("SDRCAPTURE_RECORDING_TOOLS_ROOT")??Path.Combine(Log.Folder,"RecordingTools");
    internal static string SharedDirectory=>Path.Combine(CacheRoot,"ffmpeg-"+Version);
    internal static bool Complete(string folder)=>new[]{"ffmpeg.exe","ffprobe.exe"}.All(name=>File.Exists(Path.Combine(folder,name))&&new FileInfo(Path.Combine(folder,name)).Length>0);
    internal static string ImportedDirectory(string appDirectory,string sharedDirectory)
    {
        string identity=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sharedDirectory))!,"Imported",identity[..32]);
    }
    internal static string? Resolve(string appDirectory,string sharedDirectory)
    {
        string imported=ImportedDirectory(appDirectory,sharedDirectory),legacy=Path.Combine(appDirectory,"tools");
        return Complete(sharedDirectory)?sharedDirectory:Complete(imported)?imported:Complete(legacy)?legacy:null;
    }
    public static string? DirectoryPath=>Resolve(AppContext.BaseDirectory,SharedDirectory);
    public static bool Ready=>DirectoryPath!=null;

    // Run in the background: old installations must not leave tools beside the
    // portable EXE. Preserve their exact binaries, and never touch unknown files.
    public static async Task MigrateLegacyAsync(CancellationToken token=default)
    {
        if(!Directory.Exists(Path.Combine(AppContext.BaseDirectory,"tools")))return;
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(CacheRoot);Updates.RejectReparse(CacheRoot);
            using var installLock=await LockAsync(Path.Combine(CacheRoot,"install.lock"),token);
            await Task.Run(()=>RelocateLegacy(AppContext.BaseDirectory,SharedDirectory,token),token);
        }
        finally{gate.Release();}
    }
    internal static void RelocateLegacy(string appDirectory,string sharedDirectory,CancellationToken token=default)
    {
        string source=Path.GetFullPath(Path.Combine(appDirectory,"tools")),destination=ImportedDirectory(appDirectory,sharedDirectory);
        if(!Directory.Exists(source))return;
        Updates.RejectReparse(source);Updates.RejectReparse(destination);
        if(!Complete(source)&&!Complete(destination))return;
        var present=files.Where(name=>File.Exists(Path.Combine(source,name))).ToArray();
        foreach(string name in present)Updates.RejectReparse(Path.Combine(source,name));
        if(!Complete(source))present=present.Where(name=>File.Exists(Path.Combine(destination,name))&&SameFile(Path.Combine(source,name),Path.Combine(destination,name))).ToArray();
        Directory.CreateDirectory(destination);
        foreach(string name in present)
        {
            token.ThrowIfCancellationRequested();
            string old=Path.Combine(source,name),saved=Path.Combine(destination,name),temporary=saved+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                if(File.Exists(saved)&&SameFile(old,saved))continue;
                File.Copy(old,temporary,false);
                if(!SameFile(old,temporary))throw new IOException("Recording tools changed during migration.");
                File.Move(temporary,saved,true);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        if(!Complete(destination))throw new IOException("Recording tools migration is incomplete.");
        token.ThrowIfCancellationRequested();
        // Verify every copy before removing any original. Busy encoders remain
        // in place until the next launch; the valid cached pair is used meanwhile.
        foreach(string name in present)if(!SameFile(Path.Combine(source,name),Path.Combine(destination,name)))throw new IOException("Recording tools copy verification failed.");
        foreach(string name in present)
            try{File.Delete(Path.Combine(source,name));}catch(IOException){}catch(UnauthorizedAccessException){}
        try{Directory.Delete(source,false);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    static bool SameFile(string first,string second)
    {using var a=File.OpenRead(first);using var b=File.OpenRead(second);return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));}

    public static async Task EnsureAsync(Action<string>? progress=null,CancellationToken token=default,bool retry=false)
    {
        try{await MigrateLegacyAsync(token);}
        catch(Exception e) when(Ready&&e is IOException or UnauthorizedAccessException){Log.Write("Recording tools migration deferred: "+e.Message);}
        if(Ready)return;
        await gate.WaitAsync(token);
        bool attempted=false;
        try
        {
            if(Ready)return;
            if(!retry&&DateTimeOffset.UtcNow<retryAfter)throw new IOException(lastFailure);
            attempted=true;
            Directory.CreateDirectory(CacheRoot);Updates.RejectReparse(CacheRoot);
            // Serialize setup across two app processes as well as UI/recorder.
            using var installLock=await LockAsync(Path.Combine(CacheRoot,"install.lock"),token);
            if(Ready)return;
            string zip=Path.Combine(CacheRoot,"download-"+Guid.NewGuid().ToString("N")+".zip");
            try
            {
                progress?.Invoke(UiStrings.T("Downloading recording tools…"));
                using(var response=await http.GetAsync(DownloadUrl,HttpCompletionOption.ResponseHeadersRead,token))
                {
                    response.EnsureSuccessStatusCode();long total=response.Content.Headers.ContentLength??0;
                    await using var input=await response.Content.ReadAsStreamAsync(token);
                    await using var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true);
                    byte[] chunk=new byte[131072];long received=0;int count,lastPercent=-1;
                    while((count=await input.ReadAsync(chunk,token))>0)
                    {
                        received+=count;if(received>268435456)throw new IOException(UiStrings.T("Recording tools download is too large."));
                        await output.WriteAsync(chunk.AsMemory(0,count),token);
                        int percent=total>0?(int)Math.Clamp(received*100/total,0,100):-1;
                        if(percent!=lastPercent){lastPercent=percent;progress?.Invoke(UiStrings.F("Downloading recording tools: {0}%",percent));}
                    }
                    if(total>0&&received!=total)throw new IOException(UiStrings.T("Recording tools download is incomplete."));
                }
                progress?.Invoke(UiStrings.T("Verifying recording tools…"));
                await Task.Run(()=>InstallArchive(zip,SharedDirectory,ArchiveSha256,token),token);
                retryAfter=default;lastFailure=null;progress?.Invoke(UiStrings.T("Recording tools are ready."));
            }
            finally{if(File.Exists(zip))File.Delete(zip);}
        }
        catch(Exception e) when(attempted&&e is not OperationCanceledException)
        {
            lastFailure=UiStrings.T("Recording setup failed. Check your connection and retry in Instant replay.")+" "+e.Message;
            retryAfter=DateTimeOffset.UtcNow.AddMinutes(1);throw new IOException(lastFailure,e);
        }
        finally{gate.Release();}
    }

    static async Task<FileStream> LockAsync(string path,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            try{return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){await Task.Delay(200,token);}
        }
    }

    internal static void InstallArchive(string zip,string destination,string expectedHash,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        using(var input=File.OpenRead(zip))if(!Convert.ToHexString(SHA256.HashData(input)).Equals(expectedHash,StringComparison.OrdinalIgnoreCase))throw new IOException(UiStrings.T("Recording tools checksum mismatch. Nothing installed."));
        using var archive=ZipFile.OpenRead(zip);
        string prefix="ffmpeg-"+Version+"-essentials_build/";
        var entries=files.Select(name=>archive.GetEntry(prefix+(name=="FFmpeg-LICENSE"?"LICENSE":"bin/"+name))).ToArray();
        if(entries.Any(e=>e==null||e.Length<1)||entries.Sum(e=>e!.Length)>536870912)throw new IOException(UiStrings.T("Recording tools archive is incomplete."));
        destination=Path.GetFullPath(destination);
        string parent=Path.GetFullPath(Path.GetDirectoryName(destination)!);Directory.CreateDirectory(parent);Updates.RejectReparse(destination);
        string stage=Path.Combine(parent,"setup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            for(int i=0;i<files.Length;i++){token.ThrowIfCancellationRequested();entries[i]!.ExtractToFile(Path.Combine(stage,files[i]));}
            token.ThrowIfCancellationRequested();
            if(!Directory.Exists(destination))
            {
                string prefixPath=parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(stage).StartsWith(prefixPath,StringComparison.OrdinalIgnoreCase)||!destination.StartsWith(prefixPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Setup paths leave the recording tools directory.");
                Directory.Move(stage,destination);
            }
            else
            {
                // Repair an incomplete pair without deleting any unrelated files.
                foreach(string name in files)File.Move(Path.Combine(stage,name),Path.Combine(destination,name),true);
            }
            if(!Complete(destination))throw new IOException(UiStrings.T("Recording tools archive is incomplete."));
        }
        finally
        {
            if(Directory.Exists(stage)){foreach(string name in files){string path=Path.Combine(stage,name);if(File.Exists(path))File.Delete(path);}Directory.Delete(stage,false);}
        }
    }
}
