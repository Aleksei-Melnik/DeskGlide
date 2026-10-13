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
    static void PeerStatuses()
    {
        string language=UiStrings.Shared.Language;
        try
        {
            UiStrings.Shared.Language="en";
            var states=new UpdatePeerStates();var peer=new KvmPeerInfo("server","NMSERVER",[],"0.7.21",1);
            states.SetTarget("0.7.21");states.Begin(peer.Id,"old-request");
            Require(states.Prepared(peer.Id,"old-request",new(){Code=2,Version="0.7.21",Text="Уже установлена 0.7.21"}).Code==2,"A current peer was rejected");
            Require(states.Describe(peer).Contains("Current version: 0.7.21"),"Legacy Russian peer result was not translated");
            // Exact reported case: the window stays open while a newer release appears.
            states.SetTarget("0.7.23");
            Require(states.Describe(peer).Contains("Update available: 0.7.23")&&!states.Describe(peer).Contains("Current version"),"An old already-installed result hid the new release");
            Require(!states.Progress(peer.Id,new(){Id="old-request",Text="Скачивание 0.7.21: 50%"}),"Unsolicited old progress was accepted");
            states.Begin(peer.Id,"new-request");
            Require(!states.Progress(peer.Id,new(){Id="old-request",Text="Перезапуск…"}),"Late previous-request status replaced a new request");
            Require(!states.Progress(peer.Id,new(){Id="new-request",UpdateTarget="0.7.21",Text="Verifying signature and files…"}),"Progress for another release was accepted");
            Require(states.Progress(peer.Id,new(){Id="new-request",Text="Скачивание 0.7.23: 42%"}),"Legacy peer progress is no longer compatible");
            Require(states.Describe(peer).Contains("Downloading 0.7.23: 42%"),"Legacy dynamic download status was not translated");
            UiStrings.Shared.Language="ru";
            Require(states.Describe(peer).Contains("Скачивание 0.7.23: 42%"),"Peer status did not follow a language change");
            UiStrings.Shared.Language="en";
            states.Prepared(peer.Id,"new-request",new(){Code=1,Version="0.7.21",Text="Пакет скачан и проверен"});
            Require(states.Describe(peer).Contains("Package downloaded and verified"),"Prepared status used the peer's language");
            Require(!states.Progress(peer.Id,new(){Id="new-request",Text="Скачивание 0.7.23: 99%"}),"Late download progress overwrote a terminal result");
            states.Set(peer.Id,"new-request",PeerUpdateStage.Restarting);
            Require(!states.Progress(peer.Id,new(){Id="new-request",Text="Проверка подписи и файлов…"}),"Preparation progress overwrote restarting");
            states.Set(peer.Id,"new-request",PeerUpdateStage.Updated);
            Require(states.Describe(peer with{Version="0.7.23"}).Contains("Current version: 0.7.23"),"Reconnected version was not displayed");
            Require(states.Describe(peer).Contains("Update available: 0.7.23"),"An outdated connected peer was labelled current");
            states.Set(peer.Id,"new-request",PeerUpdateStage.AwaitingReconnect);
            Require(states.Describe(peer with{Version="0.7.23"}).Contains("Current version: 0.7.23"),"Reconnection did not clear the waiting status");
            states.Begin(peer.Id,"retry");
            Require(states.Prepared(peer.Id,"retry",new(){Code=2,Version="0.7.21",Text="Уже установлена 0.7.21"}).Code==0,"Older installed version was allowed to skip an available update");
            Require(states.Describe(peer).Contains("Update failed")&&!states.Describe(peer).Contains("Уже установлена"),"Invalid current-version response was presented as success");
            states.SetTarget("0.10.0");
            Require(states.Describe(peer with{Version="0.9.9"}).Contains("Update available: 0.10.0"),"Peer versions were compared lexically");
            Require(states.Describe(peer with{Version="0.10.1"}).Contains("Current version: 0.10.1"),"A newer peer was prompted to downgrade");
            Require(states.Describe(peer with{Version="",UpdateProtocol=0}).Contains("Needs a manual update"),"Legacy capability status was lost");
            Require(UpdateStatusText.Translate("Проверка подписи и файлов…")=="Verifying signature and files…","Legacy verification status was not translated");
            Require(UpdateStatusText.Translate("Обновления с управляющего ПК выключены в настройках.")=="Updates from the controller PC are disabled in settings.","Known peer error was not translated");
            UiStrings.Shared.Language="ru";
            Require(UpdateStatusText.Translate("Version 0.7.23 is available")=="Доступна версия 0.7.23","Formatted main status did not follow the current language");
            UiStrings.Shared.Language="en";
            Require(UpdateStatusText.Translate("Доступна версия 0.7.23")=="Version 0.7.23 is available","Formatted main status did not switch back to English");
            var roundtrip=JsonSerializer.Deserialize<KvmMessage>(JsonSerializer.Serialize(new KvmMessage{UpdateTarget="0.7.23"}))!;
            Require(roundtrip.UpdateTarget=="0.7.23"&&JsonSerializer.Deserialize<KvmMessage>("{\"Type\":\"update-status\"}")!.UpdateTarget=="","Update target broke older protocol messages");
        }
        finally{UiStrings.Shared.Language=language;}
    }
    public static void Run()
    {
        PeerStatuses();
        string root=Path.Combine(Path.GetTempPath(),"ScreenCapture-update-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string zip=Path.Combine(root,"package.zip"),payload=Path.Combine(root,"payload"),target=Path.Combine(root,"target"),backup=Path.Combine(root,"backup");
        var content=new Dictionary<string,string>{{"ScreenCapture.exe","new exe"},{"ScreenCapture.dll","new dll"},{"ScreenCapture.runtimeconfig.json","{}"},{"DeskGlide.exe","new exe"},{"SdrCapture.exe","new exe"}};
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
        string mutexName="Local\\ScreenCapture.UpdateTest."+Guid.NewGuid().ToString("N");
        foreach(bool fail in new[]{false,true})
        {
            try{UpdateInstaller.WithInstallationLock(()=>{if(fail)throw new IOException("Test failure");},mutexName);}catch(IOException)when(fail){}
            using var singleton=new Mutex(true,mutexName,out bool first);
            Require(first,"Installer still holds the singleton object: restarted app would exit");
            singleton.ReleaseMutex();
        }
        Require(File.ReadAllText(Path.Combine(target,"ScreenCapture.exe"))=="new exe","New executable not installed");
        Require(File.ReadAllText(Path.Combine(target,"DeskGlide.exe"))=="new exe"&&File.ReadAllText(Path.Combine(target,"SdrCapture.exe"))=="new exe","Branded or legacy launcher missing");
        foreach(string launcher in new[]{"DeskGlide.exe","ScreenCapture.exe","SdrCapture.exe"})Require(UpdateInstaller.SupportedExecutable(launcher),"Updater cannot restart launcher: "+launcher);
        Require(!UpdateInstaller.SupportedExecutable("other.exe")&&!UpdateInstaller.SupportedExecutable("../DeskGlide.exe"),"Updater accepted an unrelated launcher");
        var portableManifest=manifest with{Portable=new("DeskGlide.exe",Encoding.UTF8.GetByteCount("new exe"),Hash(Encoding.UTF8.GetBytes("new exe")))};
        byte[] portableJson=JsonSerializer.SerializeToUtf8Bytes(portableManifest);
        Require(Updates.VerifyManifest(portableJson,rsa.SignData(portableJson,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1),key).Portable?.File=="DeskGlide.exe","Signed portable artifact lost");
        foreach(var invalid in new[]{portableManifest.Portable with{File="../DeskGlide.exe"},portableManifest.Portable with{Size=0},portableManifest.Portable with{Sha256=new string('0',64)}})
        {
            byte[] invalidJson=JsonSerializer.SerializeToUtf8Bytes(portableManifest with{Portable=invalid});
            Reject(()=>Updates.VerifyManifest(invalidJson,rsa.SignData(invalidJson,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1),key),"Invalid portable artifact accepted");
        }
        foreach(bool interrupted in new[]{true,false})
        {
            string single=Path.Combine(root,interrupted?"portable-interrupted":"portable-success");Directory.CreateDirectory(single);
            foreach(string name in content.Keys)File.WriteAllText(Path.Combine(single,name),"old "+name);
            File.WriteAllText(Path.Combine(single,"settings.json"),"private profile");File.WriteAllText(Path.Combine(single,"notes.txt"),"user file");Directory.CreateDirectory(Path.Combine(single,"tools"));File.WriteAllText(Path.Combine(single,"tools","ffmpeg.exe"),"encoder");
            bool rolledBack=false;
            try{UpdateInstaller.InstallPortable(Path.Combine(payload,"ScreenCapture.exe"),single,Path.Combine(root,interrupted?"portable-rollback":"portable-backup"),portableManifest,"DeskGlide.exe",n=>{if(interrupted&&n==2)throw new IOException("Simulated migration interruption");});}
            catch(IOException)when(interrupted){rolledBack=true;}
            if(interrupted)Require(rolledBack&&content.Keys.All(n=>File.ReadAllText(Path.Combine(single,n))=="old "+n),"Portable migration did not roll back every old component");
            else Require(File.ReadAllText(Path.Combine(single,"DeskGlide.exe"))=="new exe"&&content.Keys.Where(n=>n!="DeskGlide.exe").All(n=>!File.Exists(Path.Combine(single,n))),"Migration left loose application dependencies");
            Require(File.ReadAllText(Path.Combine(single,"settings.json"))=="private profile"&&File.ReadAllText(Path.Combine(single,"notes.txt"))=="user file"&&File.ReadAllText(Path.Combine(single,"tools","ffmpeg.exe"))=="encoder","Migration changed a profile, user file or recording tools");
        }
        File.AppendAllText(Path.Combine(payload,"ScreenCapture.exe"),"corrupt");
        Reject(()=>Updates.VerifyArtifact(Path.Combine(payload,"ScreenCapture.exe"),portableManifest.Portable),"Damaged portable EXE accepted");
        File.WriteAllText(Path.Combine(payload,"ScreenCapture.exe"),"new exe");
        File.AppendAllText(Path.Combine(payload,"ScreenCapture.dll"),"tampered");Reject(()=>Updates.VerifyPayload(payload,verified),"Tampered payload accepted");
        Require(Updates.ParseVersion("0.10.0")>Updates.ParseVersion("0.9.9"),"Version comparison is lexical");
        Reject(()=>Updates.ParseVersion("0.6.0/evil"),"Untrusted release path accepted");
        var settings=new Settings{NdiAudioVolume=17,Updates=new(){AllowFromHost=false,CheckOnStartup=false}};
        var roundtrip=JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings.Copy()))!;
        Require(roundtrip.NdiAudioVolume==17&&!roundtrip.Updates.AllowFromHost&&!roundtrip.Updates.CheckOnStartup,"Update preferences lost");
        Program.Write("update-tests.json",new{Pass=true,PeerStatusesScopedToRelease=true,StalePeerMessagesRejected=true,OlderInstalledReplyRejected=true,PeerStatusesLocalized=true,LegacyUpdateProtocolCompatible=true,SignedManifest=true,TamperingRejected=true,WrongKeyRejected=true,UnsafePathsRejected=true,HashValidation=true,InterruptedCopyRolledBack=true,ProfileAndEncoderPreserved=true,SuccessfulInstall=true,NumericVersions=true,PreferencesPreserved=true,PortableSigned=true,PortableMigrationAndRollback=true,UnknownFilesPreserved=true,Root=root});
    }
}
