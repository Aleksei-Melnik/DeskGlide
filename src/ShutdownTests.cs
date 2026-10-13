using System.Diagnostics;

namespace SdrCapture;
static class ShutdownTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var released=new ManualResetEventSlim();
        var clock=Stopwatch.StartNew();
        bool drained=MediaShutdown.Drain([blocked.Task],()=>released.Set(),50);
        Require(!drained&&clock.ElapsedMilliseconds<1000,"Blocked native worker held shutdown indefinitely.");
        Require(!released.IsSet,"Native worker resources were released while still in use.");
        blocked.SetResult();Require(released.Wait(1000),"Deferred teardown was never released.");
        int clean=0;
        Require(MediaShutdown.Drain([Task.CompletedTask],()=>clean++),"Completed worker not drained.");
        Require(clean==1,"Completed worker cleaned more than once.");
        Require(MediaShutdown.Drain([Task.FromException(new IOException("fixture"))],()=>clean++),"Failed worker prevented shutdown.");

        string name="Local\\DeskGlide.InstanceTest."+Guid.NewGuid().ToString("N");
        // The named object may outlive the previous owner (e.g. an updater's
        // handle). Creation alone does not establish that an instance is alive.
        using var lingering=new Mutex(false,name);
        using(var instance=ApplicationInstance.Acquire(name))
        {
            Require(instance!=null,"Unowned existing singleton prevented recovery.");
            bool duplicateAccepted=Task.Run(()=>{using var other=ApplicationInstance.Acquire(name);return other!=null;}).GetAwaiter().GetResult();
            Require(!duplicateAccepted,"A live duplicate instance was accepted.");
        }
        using var recovered=ApplicationInstance.Acquire(name);Require(recovered!=null,"Singleton was not released.");
        Program.Write("shutdown-tests.json",new{Pass=true,BlockedWorkerBounded=true,NativeResourcesRetained=true,ExistingUnownedSingletonRecovered=true,DuplicateRejected=true,DeadlineMilliseconds=50});
    }
}
