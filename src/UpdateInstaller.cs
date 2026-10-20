using System.Diagnostics;
using System.Text.Json;

namespace SdrCapture;

// Runs from the verified staging directory, after the application hands over.
static class UpdateInstaller
{
    public static int Run(string jobPath)
    {
        var plan=ReadPlan(jobPath);var job=plan.Job;string stage=plan.Stage;
        string resultPath=Path.Combine(Updates.Root,"last-result.json");
        bool restartPrevious=false,installed=false;
        Process? child=null;UpdateFileSession? files=null;
        try
        {
            var manifest=plan.Manifest;
            using(var parent=OpenParent(job))
            {
                // Obtain Windows approval before the parent stops any live services.
                files=UpdateFileSession.Start(plan,jobPath);
                using var ready=EventWaitHandle.OpenExisting("Local\\SdrCapture.UpdateReady."+job.Nonce);ready.Set();
                if(!parent.WaitForExit(45000))throw new IOException("Приложение не завершилось. Файлы не заменены.");
            }
            restartPrevious=true;
            files.Install();installed=true;
            using var health=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\SdrCapture.UpdateHealth."+job.Nonce);
            var start=new ProcessStartInfo(Path.Combine(job.Destination,job.Executable)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=job.Destination,WindowStyle=ProcessWindowStyle.Hidden};
            start.ArgumentList.Add("--updated");start.ArgumentList.Add(job.Nonce);
            child=Process.Start(start)??throw new IOException("Не удалось перезапустить приложение.");
            if(!health.WaitOne(20000)||child.HasExited)
            {
                if(!child.HasExited){child.Kill();child.WaitForExit(5000);}
                files.Rollback();
                installed=false;
                string previous=job.Executable;
                Process.Start(new ProcessStartInfo(Path.Combine(job.Destination,previous)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=job.Destination,WindowStyle=ProcessWindowStyle.Hidden});
                restartPrevious=false;
                throw new IOException("Новая версия не запустилась; предыдущая восстановлена.");
            }
            // A lost final acknowledgement must not kill an already healthy app.
            restartPrevious=false;files.Commit();
            File.WriteAllText(resultPath,JsonSerializer.Serialize(new{Success=true,Version=manifest.Version,At=DateTimeOffset.UtcNow,Backup=Path.Combine(stage,"backup")}));
            return 0;
        }
        catch(Exception e)
        {
            if(restartPrevious)
            {
                try
                {
                    if(child!=null&&!child.HasExited){child.Kill();child.WaitForExit(5000);}
                    if(installed)files!.Rollback();
                    files?.Dispose();files=null;
                    string previous=job.Executable;
                    Process.Start(new ProcessStartInfo(Path.Combine(job.Destination,previous)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=job.Destination,WindowStyle=ProcessWindowStyle.Hidden});
                }
                catch(Exception recovery){Log.Write("Update recovery: "+recovery.Message);}
            }
            File.WriteAllText(Path.Combine(stage,"install-error.txt"),e.ToString());
            File.WriteAllText(Path.Combine(stage,"install-message.txt"),UpdateFileSession.FriendlyError(e));
            File.WriteAllText(resultPath,JsonSerializer.Serialize(new{Success=false,Error=e.Message,At=DateTimeOffset.UtcNow}));
            return 1;
        }
        finally{files?.Dispose();child?.Dispose();}
    }
    internal sealed record Plan(string Stage,string Payload,UpdateJob Job,ReleaseManifest Manifest);
    internal static Plan ReadPlan(string jobPath)
    {
        string stage=Path.GetFullPath(Path.GetDirectoryName(jobPath)!);
        string expectedRoot=Path.GetFullPath(Path.Combine(Updates.Root,"stage"))+Path.DirectorySeparatorChar;
        if(!stage.StartsWith(expectedRoot,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(stage),"N",out _)||Path.GetFileName(jobPath)!="job.json")throw new IOException("Неверный каталог установщика.");
        string payload=Path.Combine(stage,"payload");
        if(!Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)).Equals(payload,StringComparison.OrdinalIgnoreCase))throw new IOException("Запустите установщик из проверенного пакета.");
        Updates.RejectReparse(jobPath);Updates.RejectReparse(payload);
        if(new FileInfo(jobPath).Length>16384)throw new IOException("Слишком большое задание обновления.");
        var job=JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(jobPath))??throw new IOException("Пустое задание обновления.");
        if(!Guid.TryParseExact(job.Nonce,"N",out _)||job.ParentPid<=0||!SupportedExecutable(job.Executable)||!Path.IsPathFullyQualified(job.Destination))throw new IOException("Неверное задание установки.");
        Updates.RejectReparse(job.Destination);
        var manifest=Updates.VerifyManifest(File.ReadAllBytes(Path.Combine(stage,"update.json")),File.ReadAllBytes(Path.Combine(stage,"update.sig")),Updates.PublicKey);
        if(Updates.ParseVersion(manifest.Version)!=Updates.Current)throw new IOException("Версия установщика не совпадает с пакетом.");
        if(job.Portable)Updates.VerifyArtifact(Path.Combine(payload,"ScreenCapture.exe"),manifest.Portable??throw new IOException("В релизе отсутствует переносимый EXE."));
        else Updates.VerifyPayload(payload,manifest);
        return new(stage,payload,job,manifest);
    }
    internal static Process OpenParent(UpdateJob job)
    {
        var parent=Process.GetProcessById(job.ParentPid);
        try
        {
            using var current=Process.GetCurrentProcess();
            if(parent.StartTime.ToUniversalTime().Ticks!=job.ParentStarted||parent.SessionId!=current.SessionId||
                !Path.GetFullPath(parent.MainModule!.FileName).Equals(Path.GetFullPath(Path.Combine(job.Destination,job.Executable)),StringComparison.OrdinalIgnoreCase))throw new IOException("Исходный процесс изменился.");
            return parent;
        }
        catch{parent.Dispose();throw;}
    }
    internal static bool SupportedExecutable(string name)=>name is "DeskGlide.exe" or "ScreenCapture.exe" or "SdrCapture.exe";
    internal static void WithInstallationLock(Action install,string name="Local\\SdrCapture.Tray")
    {
        // Dispose the named object before launching the app: its singleton checks creation.
        using var applicationMutex=new Mutex(false,name);
        bool acquired=false;
        try
        {
            try{acquired=applicationMutex.WaitOne(15000);}catch(AbandonedMutexException){acquired=true;}
            if(!acquired)throw new IOException("Другая копия DeskGlide ещё работает.");
            install();
        }
        finally{if(acquired)applicationMutex.ReleaseMutex();}
    }
    internal sealed record Change(string Path,bool Existed,string Sha256);
    internal static IReadOnlyList<Change> InstallPortable(string source,string destination,string backup,ReleaseManifest manifest,string executable,Action<int>? afterFile=null,bool removeComponents=true)
    {
        if(!SupportedExecutable(executable))throw new IOException("Неверное имя приложения.");
        Updates.VerifyArtifact(source,manifest.Portable??throw new IOException("Отсутствует переносимый пакет."));
        Updates.RejectReparse(destination);Updates.RejectReparse(backup);Directory.CreateDirectory(backup);var journal=new List<Change>();
        void Remember(string relative)
        {
            string target=Updates.Under(destination,relative),saved=Updates.Under(backup,relative);Updates.RejectReparse(target);Updates.RejectReparse(saved);
            bool existed=File.Exists(target);
            string hash=existed?BackupFile(target,saved):"";
            journal.Add(new(relative,existed,hash));WriteJournal(backup,journal);
        }
        try
        {
            Remember(executable);Directory.CreateDirectory(destination);
            string target=Updates.Under(destination,executable),temporary=target+".new-"+Guid.NewGuid().ToString("N");
            try{CopyVerified(source,temporary,manifest.Portable!.Sha256);File.Move(temporary,target,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            afterFile?.Invoke(journal.Count);
            // A portable EXE owns only itself, even beside files whose names
            // happen to match a legacy folder package (especially in C:\).
            if(!removeComponents)return journal;
            // The signed folder payload owns these paths. Unknown files, tools,
            // recordings, settings and keys are never removed during migration.
            foreach(string relative in manifest.Files.Keys.Where(p=>!p.Equals(executable,StringComparison.OrdinalIgnoreCase)))
            {
                string old=Updates.Under(destination,relative);if(!File.Exists(old))continue;
                Remember(relative);File.Delete(old);afterFile?.Invoke(journal.Count);
            }
            return journal;
        }
        catch{Restore(destination,backup,journal);throw;}
    }
    internal static void RemoveEmptyComponentFolders(string destination,ReleaseManifest manifest)
    {
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string relative in manifest.Files.Keys)
            for(string? directory=Path.GetDirectoryName(Updates.Under(destination,relative));directory!=null&&!directory.Equals(destination,StringComparison.OrdinalIgnoreCase);directory=Path.GetDirectoryName(directory))directories.Add(directory);
        foreach(string directory in directories.OrderByDescending(p=>p.Length))
            try{Updates.RejectReparse(directory);if(Directory.Exists(directory))Directory.Delete(directory,false);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    internal static IReadOnlyList<Change> InstallFiles(string payload,string destination,string backup,ReleaseManifest manifest,Action<int>? afterFile=null)
    {
        Updates.VerifyPayload(payload,manifest);Updates.RejectReparse(destination);
        Updates.RejectReparse(backup);Directory.CreateDirectory(backup);var journal=new List<Change>();
        try
        {
            foreach(var file in manifest.Files)
            {
                string target=Updates.Under(destination,file.Key),saved=Updates.Under(backup,file.Key);Updates.RejectReparse(target);Updates.RejectReparse(saved);
                bool existed=File.Exists(target);
                string hash=existed?BackupFile(target,saved):"";
                journal.Add(new(file.Key,existed,hash));WriteJournal(backup,journal);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary=target+".new-"+Guid.NewGuid().ToString("N");
                try{CopyVerified(Updates.Under(payload,file.Key),temporary,file.Value);File.Move(temporary,target,true);}
                finally{if(File.Exists(temporary))File.Delete(temporary);}
                afterFile?.Invoke(journal.Count);
            }
            // Settings, pairing keys, video buffers and tools live outside this set.
            return journal;
        }
        catch{Restore(destination,backup,journal);throw;}
    }
    static string BackupFile(string source,string saved)
    {
        Updates.RejectReparse(source);Updates.RejectReparse(saved);Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));input.Position=0;
        using var output=new FileStream(saved,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);return hash;
    }
    static void WriteJournal(string backup,List<Change> journal)
    {
        string path=Path.Combine(backup,"journal.json");Updates.RejectReparse(path);File.WriteAllText(path,JsonSerializer.Serialize(journal));
    }
    static void CopyVerified(string source,string target,string hash,bool overwrite=false)
    {
        Updates.RejectReparse(source);Updates.RejectReparse(target);
        // Keep the verified file open against writes/deletion until the copy is complete.
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).Equals(hash,StringComparison.OrdinalIgnoreCase))throw new IOException("Файл обновления или резервной копии изменён.");
        input.Position=0;using var output=new FileStream(target,overwrite?FileMode.Create:FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);
    }
    internal static void Restore(string destination,string backup,IReadOnlyList<Change> journal)
    {
        // Never trust an editable on-disk journal when restoring protected files.
        foreach(var change in journal.AsEnumerable().Reverse())
        {
            string target=Updates.Under(destination,change.Path);Updates.RejectReparse(target);
            if(change.Existed){Directory.CreateDirectory(Path.GetDirectoryName(target)!);CopyVerified(Updates.Under(backup,change.Path),target,change.Sha256,true);}
            else if(File.Exists(target))File.Delete(target);
        }
    }
    public static void Acknowledge(string nonce)
    {
        if(!Guid.TryParseExact(nonce,"N",out _))return;
        try{using var ready=EventWaitHandle.OpenExisting("Local\\SdrCapture.UpdateHealth."+nonce);ready.Set();}catch(WaitHandleCannotBeOpenedException){}
    }
}
