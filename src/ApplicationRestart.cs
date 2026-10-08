using System.Diagnostics;
using System.Globalization;

namespace SdrCapture;

static class ApplicationRestart
{
    const string ReadyPrefix="Local\\DeskGlide.RestartReady.";

    // The replacement keeps the same executable name, including legacy launchers.
    // It acknowledges startup, then waits outside the application singleton.
    public static void Start(params string[] nextArguments)
    {
        using var current=Process.GetCurrentProcess();
        string nonce=Guid.NewGuid().ToString("N");
        using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,ReadyPrefix+nonce);
        var start=new ProcessStartInfo(Environment.ProcessPath??throw new IOException("Application path is unavailable."))
        {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppContext.BaseDirectory};
        foreach(string argument in new[]{"--restart-after",current.Id.ToString(CultureInfo.InvariantCulture),current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),nonce}.Concat(nextArguments))start.ArgumentList.Add(argument);
        using var replacement=Process.Start(start)??throw new IOException("Could not start DeskGlide.");
        if(!ready.WaitOne(TimeSpan.FromSeconds(8)))
            throw new IOException("DeskGlide could not prepare its restart. The current instance is still running.");
    }

    public static string[] WaitForPreviousInstance(string[] arguments)
    {
        if(arguments.Length<4||arguments[0]!="--restart-after"||
            !int.TryParse(arguments[1],NumberStyles.None,CultureInfo.InvariantCulture,out int pid)||pid<=0||pid==Environment.ProcessId||
            !long.TryParse(arguments[2],NumberStyles.None,CultureInfo.InvariantCulture,out long started)||
            !Guid.TryParseExact(arguments[3],"N",out _))throw new IOException("Invalid restart request.");
        using var parent=Process.GetProcessById(pid);
        using var current=Process.GetCurrentProcess();
        if(parent.StartTime.ToUniversalTime().Ticks!=started||parent.SessionId!=current.SessionId||
            !string.Equals(parent.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))
            throw new IOException("The original DeskGlide instance does not match this restart request.");
        using(var ready=EventWaitHandle.OpenExisting(ReadyPrefix+arguments[3]))ready.Set();
        if(!parent.WaitForExit(60_000))throw new IOException("The original DeskGlide instance has not finished closing. Restart cancelled.");
        return arguments[4..];
    }
}
