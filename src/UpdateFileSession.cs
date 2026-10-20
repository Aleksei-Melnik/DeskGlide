using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace SdrCapture;

// The ordinary installer owns application restart/health. The optional elevated
// worker accepts only a verified, process-bound file transaction over private IPC.
sealed class UpdateFileSession:IDisposable
{
    internal const string WorkerArgument="--update-file-worker";
    readonly UpdateFileChannel? channel;
    readonly Process? worker;
    readonly UpdateFileTransaction? local;
    bool finished;
    UpdateFileSession(UpdateInstaller.Plan plan,UpdateFileChannel? channel=null,Process? worker=null)
    {this.channel=channel;this.worker=worker;if(channel==null)local=new(plan);}
    public static UpdateFileSession Start(UpdateInstaller.Plan plan,string jobPath)
    {
        if(!plan.Job.NeedsElevation||KvmInputAccess.Elevated)return new(plan);
        string name="DeskGlide.Update."+Environment.ProcessId+"."+Guid.NewGuid().ToString("N");
        var pipe=KvmInputHelper.CreatePipe(name);Process? child=null;
        try
        {
            using var current=Process.GetCurrentProcess();
            var start=Updates.InstallerStart(Environment.ProcessPath!,true);
            foreach(string argument in new[]{WorkerArgument,jobPath,name,current.Id.ToString(),current.StartTime.ToUniversalTime().Ticks.ToString()})start.ArgumentList.Add(argument);
            child=Process.Start(start)??throw new IOException("Не удалось запустить установщик с правами администратора.");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var connected=pipe.WaitForConnectionAsync(timeout.Token);
            var exited=child.WaitForExitAsync(timeout.Token);
            if(Task.WhenAny(connected,exited).GetAwaiter().GetResult()!=connected)throw new IOException("Установщик с правами администратора завершился до подключения.");
            connected.GetAwaiter().GetResult();KvmInputHelper.VerifyClient(pipe,child.Id);
            var channel=new UpdateFileChannel(pipe);channel.Expect("ready");
            return new(plan,channel,child);
        }
        catch{pipe.Dispose();child?.Dispose();throw;}
    }
    void Command(string command,string expected)
    {
        channel!.Send(command);channel.Expect(expected);
    }
    public void Install(){if(local!=null)local.Install();else Command("install","installed");}
    public void Rollback(){if(local!=null)local.Rollback();else Command("rollback","rolled-back");finished=true;}
    public void Commit(){if(local!=null)local.Commit();else Command("commit","committed");finished=true;}
    public void Dispose()
    {
        // Closing an unfinished private connection makes the worker abandon or roll back.
        channel?.Dispose();
        if(worker!=null){try{worker.WaitForExit(10000);}catch(InvalidOperationException){}worker.Dispose();}
        if(local!=null&&!finished)local.Dispose();
    }
    internal static string FriendlyError(Exception error)=>error is Win32Exception{NativeErrorCode:1223}
        ?UiStrings.T("Administrator approval was canceled. DeskGlide is still running; no files were changed."):error.Message;

    public static int RunWorker(string[] args)
    {
        if(args.Length!=5||args[0]!=WorkerArgument||!args[2].StartsWith("DeskGlide.Update.",StringComparison.Ordinal)||args[2].Length>150||
            !int.TryParse(args[3],out int pid)||pid<=0||!long.TryParse(args[4],out long started)||!KvmInputAccess.Elevated)throw new IOException("Неверный запрос установщика с правами администратора.");
        using var coordinator=Process.GetProcessById(pid);using var current=Process.GetCurrentProcess();
        if(coordinator.StartTime.ToUniversalTime().Ticks!=started||coordinator.SessionId!=current.SessionId||
            !string.Equals(coordinator.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Исходный установщик изменился.");
        using var pipe=KvmInputHelper.ClientPipe(args[2]);pipe.Connect(10000);KvmInputHelper.VerifyServer(pipe,pid);
        using var channel=new UpdateFileChannel(pipe);
        try
        {
            // Recheck the signature, every payload hash and the real destination
            // independently after elevation, while the original process still exists.
            var plan=UpdateInstaller.ReadPlan(args[1]);using var parent=UpdateInstaller.OpenParent(plan.Job);
            using var transaction=new UpdateFileTransaction(plan);
            channel.Send("ready");
            channel.Expect("install");
            if(!parent.WaitForExit(45000))throw new IOException("Приложение не завершилось. Файлы не заменены.");
            transaction.Install();channel.Send("installed");
            var response=channel.Receive();
            if(response.State=="commit"){transaction.Commit();channel.Send("committed");}
            else if(response.State=="rollback"){transaction.Rollback();channel.Send("rolled-back");}
            else throw new IOException("Неверная команда завершения обновления.");
            return 0;
        }
        catch(Exception e)
        {
            try{channel.Send("error",FriendlyError(e));}catch(IOException){}catch(OperationCanceledException){}
            Log.Write("Update file worker: "+e.Message);return 1;
        }
    }
}

// The same transaction is used by both integrity levels and the regression tests.
sealed class UpdateFileTransaction(UpdateInstaller.Plan plan,string mutexName="Local\\SdrCapture.Tray"):IDisposable
{
    bool installed,finished;
    IReadOnlyList<UpdateInstaller.Change>? journal;
    string Backup=>Path.Combine(plan.Stage,"backup");
    public void Install()
    {
        if(installed||finished)throw new IOException("Повторная установка не разрешена.");
        UpdateInstaller.WithInstallationLock(()=>
        {
            if(plan.Manifest.Portable!=null)journal=UpdateInstaller.InstallPortable(Path.Combine(plan.Payload,"ScreenCapture.exe"),plan.Job.Destination,Backup,plan.Manifest,plan.Job.Executable);
            else journal=UpdateInstaller.InstallFiles(plan.Payload,plan.Job.Destination,Backup,plan.Manifest);
        },mutexName);
        installed=true;
    }
    public void Rollback()
    {
        if(finished)throw new IOException("Обновление уже завершено.");
        if(installed)UpdateInstaller.WithInstallationLock(()=>UpdateInstaller.Restore(plan.Job.Destination,Backup,journal!),mutexName);
        installed=false;finished=true;
    }
    public void Commit()
    {
        if(!installed||finished)throw new IOException("Обновление ещё не установлено.");
        if(plan.Manifest.Portable!=null)UpdateInstaller.RemoveEmptyComponentFolders(plan.Job.Destination,plan.Manifest);
        finished=true;
    }
    public void Dispose()
    {
        if(installed&&!finished)
            try{Rollback();}catch(Exception e){Log.Write("Update file rollback: "+e.Message);}
    }
}

sealed record UpdateFileReply(string State,string Error="");
sealed class UpdateFileChannel(Stream stream):IDisposable
{
    internal const int MaximumMessage=8192;
    public void Send(string state,string error="")
    {
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(new UpdateFileReply(state,error));
        if(body.Length>MaximumMessage)throw new IOException("Слишком большое сообщение установщика.");
        byte[] length=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(length,body.Length);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        stream.WriteAsync(length,timeout.Token).AsTask().GetAwaiter().GetResult();
        stream.WriteAsync(body,timeout.Token).AsTask().GetAwaiter().GetResult();
    }
    public UpdateFileReply Receive()
    {
        byte[] length=new byte[4];using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        stream.ReadExactlyAsync(length,timeout.Token).AsTask().GetAwaiter().GetResult();
        int count=BinaryPrimitives.ReadInt32LittleEndian(length);
        if(count<2||count>MaximumMessage)throw new IOException("Неверный размер сообщения установщика.");
        byte[] body=new byte[count];stream.ReadExactlyAsync(body,timeout.Token).AsTask().GetAwaiter().GetResult();
        try{return JsonSerializer.Deserialize<UpdateFileReply>(body)??throw new IOException("Пустое сообщение установщика.");}
        catch(JsonException e){throw new IOException("Повреждённое сообщение установщика.",e);}
    }
    public void Expect(string state)
    {
        var reply=Receive();if(reply.State=="error")throw new IOException(reply.Error);
        if(reply.State!=state)throw new IOException("Нарушен порядок установки обновления.");
    }
    public void Dispose()=>stream.Dispose();
}
