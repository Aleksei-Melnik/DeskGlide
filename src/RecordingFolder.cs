using System.Diagnostics;

namespace SdrCapture;
static class RecordingFolder
{
    internal static ProcessStartInfo Command(string folder)
    {
        if(string.IsNullOrWhiteSpace(folder))throw new IOException("Выберите папку записи в настройках.");
        // Start Explorer explicitly: ShellExecute on a UNC folder can block on a shell extension.
        var command=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")){UseShellExecute=false};
        command.ArgumentList.Add(Path.GetFullPath(folder));
        return command;
    }
    public static Task Open(string folder)=>Task.Run(()=>{using var process=Process.Start(Command(folder));});
}
