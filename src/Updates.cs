using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SdrCapture;

public sealed record UpdateOptions
{
    public bool CheckOnStartup {get;set;}=true;
    public bool AllowFromHost {get;set;}=true;
}
sealed record ReleaseManifest(string Version,string File,long Size,string Sha256,Dictionary<string,string> Files);
sealed record AvailableRelease(ReleaseManifest Manifest,byte[] Json,byte[] Signature,string Notes);
sealed record PreparedUpdate(string Folder,ReleaseManifest Manifest);
sealed record UpdateJob(string Destination,int ParentPid,long ParentStarted,string Nonce,string Executable);

static class Updates
{
    public const string Repository="Aleksei-Melnik/ScreenCapture";
    public const string RepositoryUrl="https://github.com/"+Repository;
    public static Version Current=>typeof(Updates).Assembly.GetName().Version??new(0,0,0);
    public static string VersionText=>$"{Current.Major}.{Current.Minor}.{Current.Build}";
    public static string Root=>Path.Combine(Log.Folder,"Updates");
    public static string PublicKey {get{using var stream=typeof(Updates).Assembly.GetManifestResourceStream("SdrCapture.ReleasePublicKey")!;using var reader=new StreamReader(stream);return reader.ReadToEnd();}}
    static readonly HttpClient http=CreateClient();
    static HttpClient CreateClient()
    {
        var client=new HttpClient{Timeout=TimeSpan.FromMinutes(10)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenCapture/"+VersionText);
        return client;
    }
    public static string AssetUrl(string version,string name)=>$"{RepositoryUrl}/releases/download/v{version}/{Uri.EscapeDataString(name)}";
    public static Version ParseVersion(string value)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(value,@"^\d{1,4}\.\d{1,4}\.\d{1,4}$")||!Version.TryParse(value+".0",out var version))throw new IOException("Invalid update version.");
        return version;
    }
    public static ReleaseManifest VerifyManifest(byte[] json,byte[] signature,string publicKey)
    {
        if(json.Length>512000||signature.Length>1024)throw new IOException("The update manifest is too large.");
        using var rsa=RSA.Create();rsa.ImportFromPem(publicKey);
        if(!rsa.VerifyData(json,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))throw new IOException("The update signature could not be verified.");
        var m=JsonSerializer.Deserialize<ReleaseManifest>(json)??throw new IOException("The manifest is empty.");
        _=ParseVersion(m.Version);
        if(m.File!=$"ScreenCapture-{m.Version}-win-x64.zip"||m.Size<1||m.Size>536870912||!HashValid(m.Sha256)||m.Files is not {Count:>0 and <=2000})throw new IOException("Invalid update package.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in m.Files){ValidateRelative(file.Key);if(!HashValid(file.Value)||!names.Add(file.Key))throw new IOException("Invalid file list.");}
        foreach(string name in new[]{"ScreenCapture.exe","ScreenCapture.dll","ScreenCapture.runtimeconfig.json"})if(!m.Files.ContainsKey(name))throw new IOException("The ScreenCapture package is incomplete.");
        return m;
    }
    static bool HashValid(string hash)=>hash!=null&&System.Text.RegularExpressions.Regex.IsMatch(hash,"^[A-Fa-f0-9]{64}$");
    public static void ValidateRelative(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>240||name.Contains('\\')||name.Contains(':')||name.StartsWith('/')||name.Split('/').Any(s=>s is "" or "." or ".."||s.EndsWith(' ')||s.EndsWith('.')))
            throw new IOException("Invalid path in update package.");
        foreach(string part in name.Split('/'))
            if(part.IndexOfAny(Path.GetInvalidFileNameChars())>=0||System.Text.RegularExpressions.Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("Invalid file name.");
        string first=name.Split('/')[0];
        if(new[]{"tools","Updates","Replay","Kvm","settings.json","host-identity.json","installed-files.json","journal.json"}.Contains(first,StringComparer.OrdinalIgnoreCase))throw new IOException("The package would modify user data.");
    }
    public static string Under(string root,string relative)
    {
        ValidateRelative(relative);string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string path=Path.GetFullPath(Path.Combine(prefix,relative));
        if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("The path leaves the update folder.");
        return path;
    }
    public static async Task<AvailableRelease?> Check(CancellationToken token=default)
    {
        using var response=await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        using var info=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        string tag=info.RootElement.GetProperty("tag_name").GetString()??"";
        if(!tag.StartsWith('v'))throw new IOException("Invalid GitHub release tag.");
        string version=tag[1..];_=ParseVersion(version);
        byte[] json=await SmallDownload(AssetUrl(version,"update.json"),512000,token);
        byte[] signature=await SmallDownload(AssetUrl(version,"update.sig"),1024,token);
        var manifest=VerifyManifest(json,signature,PublicKey);
        if(manifest.Version!=version)throw new IOException("The signed version does not match the release.");
        return new(manifest,json,signature,info.RootElement.TryGetProperty("body",out var body)?body.GetString()??"":"");
    }
    static async Task<byte[]> SmallDownload(string url,int limit,CancellationToken token)
    {
        using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
        using var buffer=new MemoryStream();await using var input=await response.Content.ReadAsStreamAsync(token);
        byte[] chunk=new byte[8192];int count;
        while((count=await input.ReadAsync(chunk,token))>0){if(buffer.Length+count>limit)throw new IOException("The server response is too large.");buffer.Write(chunk,0,count);}
        return buffer.ToArray();
    }
    public static async Task<PreparedUpdate> Prepare(AvailableRelease release,Action<string>? progress=null,CancellationToken token=default)
    {
        if(ParseVersion(release.Manifest.Version)<Current)throw new IOException("Downgrading to an older release is not allowed.");
        string folder=Path.Combine(Root,"stage",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder,"update.json"),release.Json,token);
        await File.WriteAllBytesAsync(Path.Combine(folder,"update.sig"),release.Signature,token);
        string zip=Path.Combine(folder,"package.zip");
        using(var response=await http.GetAsync(AssetUrl(release.Manifest.Version,release.Manifest.File),HttpCompletionOption.ResponseHeadersRead,token))
        {
            response.EnsureSuccessStatusCode();await using var input=await response.Content.ReadAsStreamAsync(token);await using var output=File.Create(zip);
            byte[] chunk=new byte[131072];long count=0;int got,last=-1;
            while((got=await input.ReadAsync(chunk,token))>0)
            {
                count+=got;if(count>release.Manifest.Size)throw new IOException("The package is larger than its signed size.");
                await output.WriteAsync(chunk.AsMemory(0,got),token);int percent=(int)(100*count/release.Manifest.Size);
                if(percent!=last){last=percent;progress?.Invoke($"Downloading {release.Manifest.Version}: {percent}%");}
            }
            if(count!=release.Manifest.Size)throw new IOException("The update download is incomplete.");
        }
        progress?.Invoke("Verifying signature and files…");
        await Task.Run(()=>ExtractVerified(zip,Path.Combine(folder,"payload"),release.Manifest),token);
        return new(folder,release.Manifest);
    }
    public static void ExtractVerified(string zip,string destination,ReleaseManifest manifest)
    {
        using(var input=File.OpenRead(zip))if(!Convert.ToHexString(SHA256.HashData(input)).Equals(manifest.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("The update SHA-256 hash did not match.");
        using var archive=ZipFile.OpenRead(zip);long size=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            if(entry.FullName.EndsWith('/'))continue;
            string path=Under(destination,entry.FullName);
            if(!seen.Add(entry.FullName)||!manifest.Files.ContainsKey(entry.FullName)||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new IOException("An unexpected or duplicate file was found in the package.");
            size+=entry.Length;if(size>1073741824)throw new IOException("The extracted package is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);entry.ExtractToFile(path,false);
        }
        if(seen.Count!=manifest.Files.Count)throw new IOException("Update files are missing.");
        VerifyPayload(destination,manifest);
    }
    public static void VerifyPayload(string folder,ReleaseManifest manifest)
    {
        foreach(var file in manifest.Files)
        {
            string path=Under(folder,file.Key);RejectReparse(path);
            using var input=File.OpenRead(path);
            if(!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("An update file was modified: "+file.Key);
        }
    }
    public static void RejectReparse(string path)
    {
        for(string? p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("The update folder contains a link: "+p);
    }
    public static async Task LaunchInstaller(PreparedUpdate prepared)
    {
        string target=Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);RejectReparse(target);
        string probe=Path.Combine(target,".update-write-test-"+Guid.NewGuid().ToString("N"));File.WriteAllText(probe,"");File.Delete(probe);
        using var process=Process.GetCurrentProcess();string nonce=Guid.NewGuid().ToString("N");
        var job=new UpdateJob(target,process.Id,process.StartTime.ToUniversalTime().Ticks,nonce,Path.GetFileName(Environment.ProcessPath!));
        string jobPath=Path.Combine(prepared.Folder,"job.json");File.WriteAllText(jobPath,JsonSerializer.Serialize(job));
        using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\SdrCapture.UpdateReady."+nonce);
        var start=new ProcessStartInfo(Path.Combine(prepared.Folder,"payload","ScreenCapture.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(jobPath);
        using var helper=Process.Start(start)??throw new IOException("Could not start the installer.");
        bool ok=await Task.Run(()=>ready.WaitOne(20000));
        if(!ok)throw new IOException("The installer did not confirm readiness. ScreenCapture is still running. Log: "+prepared.Folder);
    }
}
