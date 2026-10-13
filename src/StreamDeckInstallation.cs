using System.Diagnostics;
using Microsoft.Win32;

namespace SdrCapture;

static class StreamDeckInstallation
{
    public const string Package="streamdeck/com.yami.deskglide.streamDeckPlugin";

    public static void Install()
    {
        string package=PortableResources.FilePath(Package);
        string application=Applications().FirstOrDefault(File.Exists)
            ??throw new IOException(UiStrings.T("Install Stream Deck 7.1 or later first."));
        // File association activation can redirect to streamdeck://app/openfile
        // without starting installation. Stream Deck accepts the package directly.
        using var installer=Process.Start(InstallCommand(application,package))
            ??throw new IOException(UiStrings.T("Could not start the Stream Deck installer."));
    }

    internal static ProcessStartInfo InstallCommand(string application,string package)
    {
        var command=new ProcessStartInfo(application){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        command.ArgumentList.Add(package);
        return command;
    }

    static IEnumerable<string> Applications()
    {
        foreach(var process in Process.GetProcessesByName("StreamDeck"))
        {
            string? path=null;
            using(process){try{path=process.MainModule?.FileName;}catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}}
            if(path!=null)yield return path;
        }
        foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})
        {
            using var key=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\StreamDeck.exe");
            if(key?.GetValue("") is string path)yield return path.Trim('"');
        }
        foreach(var folder in new[]{Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86})
            yield return Path.Combine(Environment.GetFolderPath(folder),"Elgato","StreamDeck","StreamDeck.exe");
    }
}
