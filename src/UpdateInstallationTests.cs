using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SdrCapture;

static class UpdateInstallationTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){try{action();}catch(IOException){return;}throw new Exception(message);}
    public static void Run(string root,string payload,ReleaseManifest manifest)
    {
        AccessAndTransport(root);
        foreach(string outcome in new[]{"commit","rollback","disconnect"})
        {
            string stage=Path.Combine(root,"transaction-"+outcome),destination=Path.Combine(stage,"destination");Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination,"ScreenCapture.exe"),"old exe");
            var job=new UpdateJob(destination,1,1,Guid.NewGuid().ToString("N"),"ScreenCapture.exe");
            using(var transaction=new UpdateFileTransaction(new(stage,payload,job,manifest),"Local\\DeskGlide.Update.Test."+Guid.NewGuid().ToString("N")))
            {
                transaction.Install();Require(File.ReadAllText(Path.Combine(destination,"ScreenCapture.exe"))=="new exe","File worker did not install");
                // The disk journal is diagnostic only; it cannot redirect privileged rollback.
                File.WriteAllText(Path.Combine(stage,"backup","journal.json"),"untrusted replacement");
                if(outcome=="commit")transaction.Commit();else if(outcome=="rollback")transaction.Rollback();
            }
            Require(File.ReadAllText(Path.Combine(destination,"ScreenCapture.exe"))==(outcome=="commit"?"new exe":"old exe"),"Worker commit, health rollback or disconnect recovery failed: "+outcome);
        }
        string tamperStage=Path.Combine(root,"backup-tamper"),target=Path.Combine(tamperStage,"destination");Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target,"ScreenCapture.exe"),"old exe");
        using var altered=new UpdateFileTransaction(new(tamperStage,payload,new(target,1,1,Guid.NewGuid().ToString("N"),"ScreenCapture.exe"),manifest),"Local\\DeskGlide.Update.Test."+Guid.NewGuid().ToString("N"));
        altered.Install();File.WriteAllText(Path.Combine(tamperStage,"backup","ScreenCapture.exe"),"injected backup");
        Reject(altered.Rollback,"An altered backup was restored into the application folder");
        Require(File.ReadAllText(Path.Combine(target,"ScreenCapture.exe"))=="new exe","A tampered backup replaced the application");
        altered.Commit();
        Program.Write("update-installation-tests.json",new{Pass=true,ProtectedDirectoryDetected=true,NoProbeFiles=true,WritableDirectoryUnelevated=true,CanceledApprovalHandled=true,PrivatePipeIdentityVerified=true,BoundedMessages=true,TransactionCommitRollbackAndDisconnect=true,DiskJournalIgnored=true,TamperedBackupRejected=true,ActualUacPromptTested=false});
    }
    static void AccessAndTransport(string root)
    {
        string directory=Path.Combine(root,"access-check");Directory.CreateDirectory(directory);
        Require(!Updates.NeedsElevation(directory),"A writable user folder requested elevation");
        Require(!Directory.EnumerateFileSystemEntries(directory).Any(),"Checking directory access left a temporary file");
        var info=new DirectoryInfo(directory);var original=info.GetAccessControl();
        using var identity=WindowsIdentity.GetCurrent();var denied=info.GetAccessControl();
        denied.AddAccessRule(new(identity.User!,FileSystemRights.CreateFiles,AccessControlType.Deny));
        try
        {
            info.SetAccessControl(denied);
            Require(Updates.NeedsElevation(directory),"A protected folder did not select the administrator file worker");
            Require(!Directory.EnumerateFileSystemEntries(directory).Any(),"Checking a protected directory created a probe");
        }
        finally
        {
            var restored=new DirectorySecurity();restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(),AccessControlSections.Access);info.SetAccessControl(restored);
        }
        Require(!Updates.NeedsElevation(directory),"Access decision stayed cached after permissions changed");
        var normal=Updates.InstallerStart("C:\\test\\ScreenCapture.exe",false);
        var admin=Updates.InstallerStart("C:\\test\\ScreenCapture.exe",true);
        Require(!normal.UseShellExecute&&normal.Verb==""&&admin.UseShellExecute&&admin.Verb=="runas","Elevation escaped the file worker");
        Require(UpdateFileSession.FriendlyError(new Win32Exception(1223))==UiStrings.T("Administrator approval was canceled. DeskGlide is still running; no files were changed."),"Canceled approval did not produce a useful error");
        var legacy=JsonSerializer.Deserialize<UpdateJob>(JsonSerializer.Serialize(new{Destination=directory,ParentPid=1,ParentStarted=1,Nonce="old",Executable="DeskGlide.exe"}))!;
        Require(!legacy.NeedsElevation,"Legacy update handover changed");

        // Real user-restricted IPC and identity checks, without UAC or input hooks.
        string name="DeskGlide.Update.Test."+Guid.NewGuid().ToString("N");
        using var server=KvmInputHelper.CreatePipe(name);using var client=KvmInputHelper.ClientPipe(name);
        var connected=server.WaitForConnectionAsync();client.Connect(5000);connected.GetAwaiter().GetResult();
        KvmInputHelper.VerifyClient(server,Environment.ProcessId);KvmInputHelper.VerifyServer(client,Environment.ProcessId);
        Reject(()=>KvmInputHelper.VerifyClient(server,Environment.ProcessId+1),"An unrelated updater pipe client was accepted");
        using(var sender=new UpdateFileChannel(client))using(var receiver=new UpdateFileChannel(server))
        {
            void Exchange(string state,string expected,string? rejection=null,string error="")
            {
                var write=Task.Run(()=>sender.Send(state,error));
                if(rejection==null)receiver.Expect(expected);else Reject(()=>receiver.Expect(expected),rejection);
                write.GetAwaiter().GetResult();
            }
            Exchange("ready","ready");
            foreach(string state in new[]{"install","installed","rollback","rolled-back","commit","committed"})Exchange(state,state);
            Exchange("error","installed","Worker errors were ignored","fixture failure");
            Exchange("commit","install","Out-of-order worker response accepted");
        }
        foreach(int size in new[]{0,-1,UpdateFileChannel.MaximumMessage+1})
        {
            using var bytes=new MemoryStream(BitConverter.GetBytes(size));using var channel=new UpdateFileChannel(bytes);
            Reject(()=>channel.Receive(),"Unbounded updater IPC frame accepted");
        }
    }
}
