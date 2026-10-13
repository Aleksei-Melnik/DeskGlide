namespace SdrCapture;

// Native capture/runtime teardown can stop returning after a receiver disconnects.
// Keep its resources alive until the owning worker finishes, but never hold the
// application dispatcher (and the updater's exit handshake) indefinitely.
static class MediaShutdown
{
    internal static bool Drain(IEnumerable<Task> workers,Action release,int milliseconds=1500)
    {
        var ended=Task.WhenAll(workers);
        try{ended.Wait(milliseconds);}catch(AggregateException){}
        if(ended.IsCompleted){release();return true;}
        _=ended.ContinueWith(task=>{_ = task.Exception;release();},CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        return false;
    }
}

sealed class ApplicationInstance:IDisposable
{
    readonly Mutex mutex;
    ApplicationInstance(Mutex mutex)=>this.mutex=mutex;
    internal static ApplicationInstance? Acquire(string name="Local\\SdrCapture.Tray")
    {
        var mutex=new Mutex(false,name);
        bool owned;
        try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        if(owned)return new(mutex);
        mutex.Dispose();return null;
    }
    public void Dispose(){mutex.ReleaseMutex();mutex.Dispose();}
}
