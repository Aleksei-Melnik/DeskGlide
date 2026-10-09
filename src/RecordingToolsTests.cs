using System.IO.Compression;
using System.Security.Cryptography;

namespace SdrCapture;

static class RecordingToolsTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){try{action();}catch(IOException){return;}throw new Exception(message);}
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"ScreenCapture-recording-tools-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string app=Path.Combine(root,"app"),portable=Path.Combine(app,"tools"),shared=Path.Combine(root,"shared"),zip=Path.Combine(root,"tools.zip");
        Directory.CreateDirectory(portable);File.WriteAllText(Path.Combine(portable,"ffmpeg.exe"),"existing encoder");
        Require(RecordingTools.Resolve(app,shared)==null,"A missing ffprobe was accepted as a complete installation.");
        File.WriteAllText(Path.Combine(portable,"ffprobe.exe"),"existing probe");Require(RecordingTools.Resolve(app,shared)==portable,"Portable tools are not supported.");
        using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create))
        {
            foreach(string name in new[]{"bin/ffmpeg.exe","bin/ffprobe.exe","LICENSE","../../escape.txt"})
            {using var writer=new StreamWriter(archive.CreateEntry("ffmpeg-"+RecordingTools.Version+"-essentials_build/"+name).Open());writer.Write("fixture-"+name);}
        }
        string hash;using(var input=File.OpenRead(zip))hash=Convert.ToHexString(SHA256.HashData(input));
        Reject(()=>RecordingTools.InstallArchive(zip,shared,new string('0',64)),"An untrusted archive was installed.");Require(!Directory.Exists(shared),"Checksum failure changed the destination.");
        using(var cancelled=new CancellationTokenSource())
        {cancelled.Cancel();bool rejected=false;try{RecordingTools.InstallArchive(zip,shared,hash,cancelled.Token);}catch(OperationCanceledException){rejected=true;}Require(rejected&&!Directory.Exists(shared),"Cancelled installation published files.");}
        RecordingTools.InstallArchive(zip,shared,hash);Require(RecordingTools.Complete(shared),"Verified tools did not install.");Require(!File.Exists(Path.Combine(root,"escape.txt")),"Unexpected archive entries were extracted.");
        File.Delete(Path.Combine(portable,"ffprobe.exe"));Require(RecordingTools.Resolve(app,shared)==shared,"The shared cache cannot recover an incomplete portable installation.");
        Require(RecordingTools.Resolve(Path.Combine(root,"fresh-app-folder"),shared)==shared,"Moving/extracting the app lost recording tools.");
        File.WriteAllText(Path.Combine(shared,"keep.txt"),"user sentinel");File.Delete(Path.Combine(shared,"ffprobe.exe"));RecordingTools.InstallArchive(zip,shared,hash);
        Require(RecordingTools.Complete(shared)&&File.ReadAllText(Path.Combine(shared,"keep.txt"))=="user sentinel","Repair changed unrelated files.");
        Require(!Directory.GetDirectories(root,"setup-*").Any(),"Setup staging files were left behind.");
        string incomplete=Path.Combine(root,"incomplete.zip");using(var archive=ZipFile.Open(incomplete,ZipArchiveMode.Create)){using var writer=new StreamWriter(archive.CreateEntry("ffmpeg-"+RecordingTools.Version+"-essentials_build/bin/ffmpeg.exe").Open());writer.Write("encoder only");}
        string incompleteHash;using(var input=File.OpenRead(incomplete))incompleteHash=Convert.ToHexString(SHA256.HashData(input));
        string previous=File.ReadAllText(Path.Combine(shared,"ffmpeg.exe"));Reject(()=>RecordingTools.InstallArchive(incomplete,shared,incompleteHash),"An incomplete archive was installed.");Require(File.ReadAllText(Path.Combine(shared,"ffmpeg.exe"))==previous,"An incomplete archive replaced working tools.");
        string oldApp=Path.Combine(root,"old-install"),oldTools=Path.Combine(oldApp,"tools"),current=Path.Combine(root,"cache","ffmpeg-current");Directory.CreateDirectory(oldTools);
        foreach(string name in new[]{"ffmpeg.exe","ffprobe.exe","FFmpeg-LICENSE"})File.WriteAllText(Path.Combine(oldTools,name),"original-"+name);
        using(var cancelled=new CancellationTokenSource())
        {cancelled.Cancel();try{RecordingTools.RelocateLegacy(oldApp,current,cancelled.Token);}catch(OperationCanceledException){}Require(RecordingTools.Complete(oldTools),"Cancelled migration removed original tools.");}
        RecordingTools.RelocateLegacy(oldApp,current);string imported=RecordingTools.ImportedDirectory(current);
        Require(!Directory.Exists(oldTools)&&RecordingTools.Complete(imported)&&RecordingTools.Resolve(oldApp,current)==imported,"Old tools were not relocated outside the app directory.");
        Require(RecordingTools.Resolve(Path.Combine(root,"moved-exe"),current)==imported,"Moving the EXE lost imported recording tools.");
        foreach(string name in new[]{"ffmpeg.exe","ffprobe.exe","FFmpeg-LICENSE"})Require(File.ReadAllText(Path.Combine(imported,name))=="original-"+name,"Imported tool bytes changed.");
        Directory.CreateDirectory(current);File.WriteAllText(Path.Combine(current,"ffmpeg.exe"),"verified current encoder");File.WriteAllText(Path.Combine(current,"ffprobe.exe"),"verified current probe");
        Require(RecordingTools.Resolve(oldApp,current)==current,"Verified current tools must take priority over imported tools.");
        Directory.CreateDirectory(oldTools);foreach(string name in new[]{"ffmpeg.exe","ffprobe.exe","FFmpeg-LICENSE"})File.Copy(Path.Combine(imported,name),Path.Combine(oldTools,name));File.WriteAllText(Path.Combine(oldTools,"user-note.txt"),"keep me");
        RecordingTools.RelocateLegacy(oldApp,current);Require(Directory.GetFiles(oldTools).Length==1&&File.ReadAllText(Path.Combine(oldTools,"user-note.txt"))=="keep me","Migration removed unknown user files.");
        File.WriteAllText(Path.Combine(oldTools,"ffmpeg.exe"),"unmatched incomplete tool");RecordingTools.RelocateLegacy(oldApp,current);
        Require(File.ReadAllText(Path.Combine(oldTools,"ffmpeg.exe"))=="unmatched incomplete tool"&&File.ReadAllText(Path.Combine(imported,"ffmpeg.exe"))=="original-ffmpeg.exe","An incomplete legacy pair changed a working cache.");
        Program.Write("recording-tools-tests.json",new{Pass=true,CompletePairRequired=true,PortableCompatible=true,SharedCacheSurvivesAppMove=true,ChecksumRejected=true,CancelledInstallRejected=true,IncompleteArchiveRejected=true,UnrelatedFilesPreserved=true,UnexpectedEntriesIgnored=true,StagingCleaned=true});
    }
}
