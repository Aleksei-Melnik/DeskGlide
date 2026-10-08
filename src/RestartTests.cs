using System.Diagnostics;
using System.Globalization;

namespace SdrCapture;

static class RestartTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static string Singleton(string nonce)=>"Local\\DeskGlide.RestartTest.Singleton."+nonce;
    static string Release(string nonce)=>"Local\\DeskGlide.RestartTest.Release."+nonce;

    public static void Parent(string root,string nonce)
    {
        using var singleton=new Mutex(true,Singleton(nonce),out bool first);
        Require(first,"Test parent could not take its isolated singleton.");
        using var release=EventWaitHandle.OpenExisting(Release(nonce));
        try
        {
            ApplicationRestart.Start("--restart-test-child",root,nonce);
            File.WriteAllText(Path.Combine(root,"ready"),"ready");
            Require(release.WaitOne(15_000),"Test parent was not released.");
        }
        finally{singleton.ReleaseMutex();}
    }

    public static void Child(string root,string nonce)
    {
        using var singleton=new Mutex(true,Singleton(nonce),out bool first);
        Require(first,"Replacement started while the previous singleton still existed.");
        try{File.WriteAllText(Path.Combine(root,"restarted"),"restarted");}
        finally{singleton.ReleaseMutex();}
    }

    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeskGlide-restart-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);string nonce=Guid.NewGuid().ToString("N");
        using var release=new EventWaitHandle(false,EventResetMode.ManualReset,Release(nonce));
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(string argument in new[]{"--restart-test-parent",root,nonce})start.ArgumentList.Add(argument);
        using var parent=Process.Start(start)??throw new Exception("Test fixture did not start.");
        try
        {
            var clock=Stopwatch.StartNew();
            while(!File.Exists(Path.Combine(root,"ready"))&&!parent.HasExited&&clock.Elapsed.TotalSeconds<12)Thread.Sleep(20);
            Require(File.Exists(Path.Combine(root,"ready")),"Replacement did not acknowledge startup.");
            Thread.Sleep(200);
            Require(!File.Exists(Path.Combine(root,"restarted")),"Replacement did not wait for its parent to exit.");
            try
            {
                ApplicationRestart.WaitForPreviousInstance(["--restart-after",parent.Id.ToString(CultureInfo.InvariantCulture),"1",Guid.NewGuid().ToString("N")]);
                throw new Exception("Mismatched parent start time was accepted.");
            }
            catch(IOException){}
            release.Set();Require(parent.WaitForExit(10_000)&&parent.ExitCode==0,"Test parent did not exit cleanly.");
            clock.Restart();while(!File.Exists(Path.Combine(root,"restarted"))&&clock.Elapsed.TotalSeconds<10)Thread.Sleep(20);
            Require(File.Exists(Path.Combine(root,"restarted")),"Replacement did not continue after parent exit.");
            try
            {
                ApplicationRestart.WaitForPreviousInstance(["--restart-after",parent.Id.ToString(CultureInfo.InvariantCulture),"1",Guid.NewGuid().ToString("N")]);
                throw new Exception("Expired parent identity was accepted.");
            }
            catch(ArgumentException){}
            catch(IOException){}
            Program.Write("restart-tests.json",new{Pass=true,WaitedForOldProcess=true,SingletonReleasedBeforeReplacement=true,ExpiredParentRejected=true,NoCaptureOrInputStarted=true});
        }
        finally{release.Set();}
    }
}
