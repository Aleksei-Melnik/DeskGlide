using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SdrCapture;
static class UpdateTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){try{action();}catch(IOException){return;}throw new Exception(message);}
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"ScreenCapture-update-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string zip=Path.Combine(root,"package.zip"),payload=Path.Combine(root,"payload"),target=Path.Combine(root,"target"),backup=Path.Combine(root,"backup");
        var content=new Dictionary<string,string>{{"ScreenCapture.exe","new exe"},{"ScreenCapture.dll","new dll"},{"ScreenCapture.runtimeconfig.json","{}"}};
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create))foreach(var file in content){using var stream=archive.CreateEntry(file.Key).Open();stream.Write(Encoding.UTF8.GetBytes(file.Value));}
        byte[] package=File.ReadAllBytes(zip);
        var manifest=new ReleaseManifest("0.6.0","ScreenCapture-0.6.0-win-x64.zip",package.Length,Hash(package),content.ToDictionary(f=>f.Key,f=>Hash(Encoding.UTF8.GetBytes(f.Value))));
        using var rsa=RSA.Create(2048);byte[] json=JsonSerializer.SerializeToUtf8Bytes(manifest),signature=rsa.SignData(json,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        string key=rsa.ExportSubjectPublicKeyInfoPem();var verified=Updates.VerifyManifest(json,signature,key);
        byte[] damaged=json.ToArray();damaged[10]^=1;Reject(()=>Updates.VerifyManifest(damaged,signature,key),"Tampered manifest accepted");
        using var wrong=RSA.Create(2048);Reject(()=>Updates.VerifyManifest(json,signature,wrong.ExportSubjectPublicKeyInfoPem()),"Wrong signing key accepted");
        foreach(string path in new[]{"../escape","/rooted","a/../../escape","a\\escape","a:stream","CON.dll","a/./b","a//b","a./b","tools/ffmpeg.exe","settings.json","Kvm/host-identity.json"})Reject(()=>Updates.ValidateRelative(path),"Unsafe path accepted: "+path);
        Updates.ExtractVerified(zip,payload,verified);
        Directory.CreateDirectory(target);Directory.CreateDirectory(Path.Combine(target,"tools"));
        File.WriteAllText(Path.Combine(target,"ScreenCapture.exe"),"old exe");File.WriteAllText(Path.Combine(target,"settings.json"),"private profile");File.WriteAllText(Path.Combine(target,"tools","ffmpeg.exe"),"existing encoder");
        bool failed=false;
        try{UpdateInstaller.InstallFiles(payload,target,backup,verified,count=>{if(count==2)throw new IOException("Simulated file lock");});}
        catch(IOException){failed=true;}
        Require(failed&&File.ReadAllText(Path.Combine(target,"ScreenCapture.exe"))=="old exe"&&!File.Exists(Path.Combine(target,"ScreenCapture.dll")),"Failed installation did not roll back");
        Require(File.ReadAllText(Path.Combine(target,"settings.json"))=="private profile"&&File.ReadAllText(Path.Combine(target,"tools","ffmpeg.exe"))=="existing encoder","User files changed");
        UpdateInstaller.InstallFiles(payload,target,Path.Combine(root,"backup-success"),verified);
        Require(File.ReadAllText(Path.Combine(target,"ScreenCapture.exe"))=="new exe","New executable not installed");
        File.AppendAllText(Path.Combine(payload,"ScreenCapture.dll"),"tampered");Reject(()=>Updates.VerifyPayload(payload,verified),"Tampered payload accepted");
        Require(Updates.ParseVersion("0.10.0")>Updates.ParseVersion("0.9.9"),"Version comparison is lexical");
        Reject(()=>Updates.ParseVersion("0.6.0/evil"),"Untrusted release path accepted");
        var settings=new Settings{NdiAudioVolume=17,Updates=new(){AllowFromHost=false,CheckOnStartup=false}};
        var roundtrip=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings.Copy()))!;
        Require(roundtrip.NdiAudioVolume==17&&!roundtrip.Updates.AllowFromHost&&!roundtrip.Updates.CheckOnStartup,"Update preferences lost");
        Program.Write("update-tests.json",new{Pass=true,SignedManifest=true,TamperingRejected=true,WrongKeyRejected=true,UnsafePathsRejected=true,HashValidation=true,InterruptedCopyRolledBack=true,ProfileAndEncoderPreserved=true,SuccessfulInstall=true,NumericVersions=true,PreferencesPreserved=true,Root=root});
    }
}
