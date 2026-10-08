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
    public const string Repository="Aleksei-Melnik/DeskGlide";
    public const string RepositoryUrl="https://github.com/"+Repository;
    public static Version Current=>typeof(Updates).Assembly.GetName().Version??new(0,0,0);
    public static string VersionText=>$"{Current.Major}.{Current.Minor}.{Current.Build}";
    public static string Root=>Path.Combine(Log.Folder,"Updates");
    public static string PublicKey {get{using var stream=typeof(Updates).Assembly.GetManifestResourceStream("SdrCapture.ReleasePublicKey")!;using var reader=new StreamReader(stream);return reader.ReadToEnd();}}
    static readonly HttpClient http=CreateClient();
    static HttpClient CreateClient()
    {
        var client=new HttpClient{Timeout=TimeSpan.FromMinutes(10)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskGlide/"+VersionText);
        return client;
    }
    public static string AssetUrl(string version,string name)=>$"{RepositoryUrl}/releases/download/v{version}/{Uri.EscapeDataString(name)}";
    public static Version ParseVersion(string value)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(value,@"^\d{1,4}\.\d{1,4}\.\d{1,4}$")||!Version.TryParse(value+".0",out var version))throw new IOException("Некорректная версия обновления.");
        return version;
    }
    public static ReleaseManifest VerifyManifest(byte[] json,byte[] signature,string publicKey)
    {
        if(json.Length>512000||signature.Length>1024)throw new IOException("Слишком большой манифест обновления.");
        using var rsa=RSA.Create();rsa.ImportFromPem(publicKey);
        if(!rsa.VerifyData(json,signature,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))throw new IOException("Подпись обновления не прошла проверку.");
        var m=JsonSerializer.Deserialize<ReleaseManifest>(json)??throw new IOException("Пустой манифест.");
        _=ParseVersion(m.Version);
        // Legacy archive/assembly names allow ScreenCapture clients to migrate in place.
        if(m.File!=$"ScreenCapture-{m.Version}-win-x64.zip"||m.Size<1||m.Size>536870912||!HashValid(m.Sha256)||m.Files is not {Count:>0 and <=2000})throw new IOException("Некорректный пакет обновления.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in m.Files){ValidateRelative(file.Key);if(!HashValid(file.Value)||!names.Add(file.Key))throw new IOException("Некорректный список файлов.");}
        foreach(string name in new[]{"ScreenCapture.exe","ScreenCapture.dll","ScreenCapture.runtimeconfig.json"})if(!m.Files.ContainsKey(name))throw new IOException("Неполный пакет DeskGlide.");
        return m;
    }
    static bool HashValid(string hash)=>hash!=null&&System.Text.RegularExpressions.Regex.IsMatch(hash,"^[A-Fa-f0-9]{64}$");
    public static void ValidateRelative(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>240||name.Contains('\\')||name.Contains(':')||name.StartsWith('/')||name.Split('/').Any(s=>s is "" or "." or ".."||s.EndsWith(' ')||s.EndsWith('.')))
            throw new IOException("Недопустимый путь в обновлении.");
        foreach(string part in name.Split('/'))
            if(part.IndexOfAny(Path.GetInvalidFileNameChars())>=0||System.Text.RegularExpressions.Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("Недопустимое имя файла.");
        string first=name.Split('/')[0];
        if(new[]{"tools","Updates","Replay","Kvm","settings.json","host-identity.json","installed-files.json","journal.json"}.Contains(first,StringComparer.OrdinalIgnoreCase))throw new IOException("Пакет затрагивает пользовательские данные.");
    }
    public static string Under(string root,string relative)
    {
        ValidateRelative(relative);string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string path=Path.GetFullPath(Path.Combine(prefix,relative));
        if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("Путь выходит за каталог обновления.");
        return path;
    }
    public static async Task<AvailableRelease?> Check(CancellationToken token=default)
    {
        using var response=await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest",token);
        if(response.StatusCode==HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        using var info=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        string tag=info.RootElement.GetProperty("tag_name").GetString()??"";
        if(!tag.StartsWith('v'))throw new IOException("Некорректный тег GitHub Release.");
        string version=tag[1..];_=ParseVersion(version);
        byte[] json=await SmallDownload(AssetUrl(version,"update.json"),512000,token);
        byte[] signature=await SmallDownload(AssetUrl(version,"update.sig"),1024,token);
        var manifest=VerifyManifest(json,signature,PublicKey);
        if(manifest.Version!=version)throw new IOException("Версия подписи не совпадает с релизом.");
        return new(manifest,json,signature,info.RootElement.TryGetProperty("body",out var body)?body.GetString()??"":"");
    }
    static async Task<byte[]> SmallDownload(string url,int limit,CancellationToken token)
    {
        using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
        using var buffer=new MemoryStream();await using var input=await response.Content.ReadAsStreamAsync(token);
        byte[] chunk=new byte[8192];int count;
        while((count=await input.ReadAsync(chunk,token))>0){if(buffer.Length+count>limit)throw new IOException("Ответ сервера слишком велик.");buffer.Write(chunk,0,count);}
        return buffer.ToArray();
    }
    public static async Task<PreparedUpdate> Prepare(AvailableRelease release,Action<string>? progress=null,CancellationToken token=default)
    {
        if(ParseVersion(release.Manifest.Version)<Current)throw new IOException("Откат на старый релиз не разрешён.");
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
                count+=got;if(count>release.Manifest.Size)throw new IOException("Пакет больше подписанного размера.");
                await output.WriteAsync(chunk.AsMemory(0,got),token);int percent=(int)(100*count/release.Manifest.Size);
                if(percent!=last){last=percent;progress?.Invoke($"Скачивание {release.Manifest.Version}: {percent}%");}
            }
            if(count!=release.Manifest.Size)throw new IOException("Обновление скачалось не полностью.");
        }
        progress?.Invoke("Проверка подписи и файлов…");
        await Task.Run(()=>ExtractVerified(zip,Path.Combine(folder,"payload"),release.Manifest),token);
        return new(folder,release.Manifest);
    }
    public static void ExtractVerified(string zip,string destination,ReleaseManifest manifest)
    {
        using(var input=File.OpenRead(zip))if(!Convert.ToHexString(SHA256.HashData(input)).Equals(manifest.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Не совпал SHA-256 обновления.");
        using var archive=ZipFile.OpenRead(zip);long size=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in archive.Entries)
        {
            if(entry.FullName.EndsWith('/'))continue;
            string path=Under(destination,entry.FullName);
            if(!seen.Add(entry.FullName)||!manifest.Files.ContainsKey(entry.FullName)||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new IOException("Лишний или повторяющийся файл в пакете.");
            size+=entry.Length;if(size>1073741824)throw new IOException("Распакованный пакет слишком велик.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);entry.ExtractToFile(path,false);
        }
        if(seen.Count!=manifest.Files.Count)throw new IOException("Не хватает файлов обновления.");
        VerifyPayload(destination,manifest);
    }
    public static void VerifyPayload(string folder,ReleaseManifest manifest)
    {
        foreach(var file in manifest.Files)
        {
            string path=Under(folder,file.Key);RejectReparse(path);
            using var input=File.OpenRead(path);
            if(!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("Файл обновления изменён: "+file.Key);
        }
    }
    public static void RejectReparse(string path)
    {
        for(string? p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Каталог обновления содержит ссылку: "+p);
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
        using var helper=Process.Start(start)??throw new IOException("Не удалось запустить установщик.");
        bool ok=await Task.Run(()=>ready.WaitOne(20000));
        if(!ok)throw new IOException("Установщик не подтвердил готовность. Программа продолжает работать; журнал: "+prepared.Folder);
    }
}
