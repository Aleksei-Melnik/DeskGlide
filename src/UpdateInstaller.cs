using System.Diagnostics;
using System.Text.Json;

namespace SdrCapture;

// Runs from the verified staging directory, after the application hands over.
static class UpdateInstaller
{
    public static int Run(string jobPath)
    {
        string stage=Path.GetFullPath(Path.GetDirectoryName(jobPath)!);
        string expectedRoot=Path.GetFullPath(Path.Combine(Updates.Root,"stage"))+Path.DirectorySeparatorChar;
        if(!stage.StartsWith(expectedRoot,StringComparison.OrdinalIgnoreCase)||!Guid.TryParseExact(Path.GetFileName(stage),"N",out _))throw new IOException("Неверный каталог установщика.");
        string payload=Path.Combine(stage,"payload");
        if(!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(payload,StringComparison.OrdinalIgnoreCase))throw new IOException("Запустите установщик из проверенного пакета.");
        var job=JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(jobPath))??throw new IOException("Пустое задание обновления.");
        if(!Guid.TryParseExact(job.Nonce,"N",out _))throw new IOException("Неверный идентификатор установки.");
        if(job.Executable is not ("ScreenCapture.exe" or "SdrCapture.exe"))throw new IOException("Неверное имя приложения.");
        string resultPath=Path.Combine(Updates.Root,"last-result.json");
        bool restartPrevious=false,installed=false;
        Process? child=null;
        try
        {
            Updates.RejectReparse(stage);Updates.RejectReparse(job.Destination);
            var manifest=Updates.VerifyManifest(File.ReadAllBytes(Path.Combine(stage,"update.json")),File.ReadAllBytes(Path.Combine(stage,"update.sig")),Updates.PublicKey);
            if(Updates.ParseVersion(manifest.Version)!=Updates.Current)throw new IOException("Версия установщика не совпадает с пакетом.");
            Updates.VerifyPayload(payload,manifest);
            using(var parent=Process.GetProcessById(job.ParentPid))
            {
                if(parent.StartTime.ToUniversalTime().Ticks!=job.ParentStarted||!Path.GetDirectoryName(parent.MainModule!.FileName)!.Equals(job.Destination,StringComparison.OrdinalIgnoreCase))throw new IOException("Исходный процесс изменился.");
                string parentName=Path.GetFileName(parent.MainModule.FileName);
                if(parentName is not ("SdrCapture.exe" or "ScreenCapture.exe"))throw new IOException("Неверное исходное приложение.");
                if(parentName!=job.Executable)throw new IOException("Изменилось имя исходного приложения.");
                using var ready=EventWaitHandle.OpenExisting("Local\\SdrCapture.UpdateReady."+job.Nonce);ready.Set();
                if(!parent.WaitForExit(45000))throw new IOException("Приложение не завершилось. Файлы не заменены.");
            }
            restartPrevious=true;
            // A second copy must not start while files are being replaced.
            using var applicationMutex=new Mutex(false,"Local\\SdrCapture.Tray");
            bool acquired=false;
            try{try{acquired=applicationMutex.WaitOne(15000);}catch(AbandonedMutexException){acquired=true;}
                if(!acquired)throw new IOException("Другая копия ScreenCapture ещё работает.");
                InstallFiles(payload,job.Destination,Path.Combine(stage,"backup"),manifest);
                installed=true;
            }
            finally{if(acquired)applicationMutex.ReleaseMutex();}
            using var health=new EventWaitHandle(false,EventResetMode.ManualReset,"Local\\SdrCapture.UpdateHealth."+job.Nonce);
            var start=new ProcessStartInfo(Path.Combine(job.Destination,job.Executable)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=job.Destination,WindowStyle=ProcessWindowStyle.Hidden};
            start.ArgumentList.Add("--updated");start.ArgumentList.Add(job.Nonce);
            child=Process.Start(start)??throw new IOException("Не удалось перезапустить приложение.");
            if(!health.WaitOne(20000)||child.HasExited)
            {
                if(!child.HasExited){child.Kill();child.WaitForExit(5000);}
                Restore(job.Destination,Path.Combine(stage,"backup"));
                installed=false;
                string previous=job.Executable;
                Process.Start(new ProcessStartInfo(Path.Combine(job.Destination,previous)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=job.Destination,WindowStyle=ProcessWindowStyle.Hidden});
                restartPrevious=false;
                throw new IOException("Новая версия не запустилась; предыдущая восстановлена.");
            }
            restartPrevious=false;
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
                    if(installed)Restore(job.Destination,Path.Combine(stage,"backup"));
                    string previous=job.Executable;
                    Process.Start(new ProcessStartInfo(Path.Combine(job.Destination,previous)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=job.Destination,WindowStyle=ProcessWindowStyle.Hidden});
                }
                catch(Exception recovery){Log.Write("Update recovery: "+recovery.Message);}
            }
            File.WriteAllText(Path.Combine(stage,"install-error.txt"),e.ToString());
            File.WriteAllText(resultPath,JsonSerializer.Serialize(new{Success=false,Error=e.Message,At=DateTimeOffset.UtcNow}));
            return 1;
        }
        finally{child?.Dispose();}
    }
    sealed record Change(string Path,bool Existed);
    public static void InstallFiles(string payload,string destination,string backup,ReleaseManifest manifest,Action<int>? afterFile=null)
    {
        Updates.VerifyPayload(payload,manifest);Updates.RejectReparse(destination);
        Directory.CreateDirectory(backup);var journal=new List<Change>();
        try
        {
            foreach(var file in manifest.Files)
            {
                string target=Updates.Under(destination,file.Key),saved=Updates.Under(backup,file.Key);Updates.RejectReparse(target);
                bool existed=File.Exists(target);
                if(existed){Directory.CreateDirectory(Path.GetDirectoryName(saved)!);File.Copy(target,saved,false);}
                journal.Add(new(file.Key,existed));File.WriteAllText(Path.Combine(backup,"journal.json"),JsonSerializer.Serialize(journal));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary=target+".new-"+Guid.NewGuid().ToString("N");
                try{File.Copy(Updates.Under(payload,file.Key),temporary,false);File.Move(temporary,target,true);}
                finally{if(File.Exists(temporary))File.Delete(temporary);}
                afterFile?.Invoke(journal.Count);
            }
            // Settings, pairing keys, video buffers and tools live outside this set.
        }
        catch{Restore(destination,backup);throw;}
    }
    public static void Restore(string destination,string backup)
    {
        var journal=JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(Path.Combine(backup,"journal.json")))??throw new IOException("Повреждён журнал отката.");
        foreach(var change in journal.AsEnumerable().Reverse())
        {
            string target=Updates.Under(destination,change.Path);Updates.RejectReparse(target);
            if(change.Existed)File.Copy(Updates.Under(backup,change.Path),target,true);
            else if(File.Exists(target))File.Delete(target);
        }
    }
    public static void Acknowledge(string nonce)
    {
        if(!Guid.TryParseExact(nonce,"N",out _))return;
        try{using var ready=EventWaitHandle.OpenExisting("Local\\SdrCapture.UpdateHealth."+nonce);ready.Set();}catch(WaitHandleCannotBeOpenedException){}
    }
}
