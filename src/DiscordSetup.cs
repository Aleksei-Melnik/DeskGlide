using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;

namespace SdrCapture;
static class DiscordSetup
{
    const string PackageUrl="https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    const string PackageHash="B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    public static List<AudioChoice> Outputs()
    {
        var result=new List<AudioChoice>{new("","Silent — без звука")};
        foreach(var device in DiscordDevices.Endpoints().Where(d=>d.Flow==DataFlow.Render).OrderByDescending(DiscordDevices.IsCable))
            result.Add(new(device.Id,device.Name));
        return result;
    }
    public static async Task InstallCable(string role)
    {
        CameraInstallation.RequireReceiver(role);
        if(DiscordDevices.Endpoints().Any(DiscordDevices.IsCable))throw new IOException("VB-CABLE уже найден. Выберите его в списке; повторная установка не нужна.");
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(60)};
        byte[] zip=await http.GetByteArrayAsync(PackageUrl);
        VerifyPackage(zip);
        string folder=Path.Combine(Log.Folder,"Discord","Setup",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        using(var archive=new ZipArchive(new MemoryStream(zip)))archive.ExtractToDirectory(folder);
        string exe=Path.Combine(folder,"VBCABLE_Setup_x64.exe");VerifySignature(exe);
        // Keep the vendor's signed, unmodified installer and its license prompts visible.
        DiscordDevices.BeforeInstall(role);
        bool launched=false;
        try
        {
            using var process=Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,Verb="runas",WorkingDirectory=folder})??throw new IOException("Установщик не запущен.");
            launched=true;
            await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new IOException("Установщик VB-CABLE завершился с кодом "+process.ExitCode+". Если установка отменена, устройства не будут включены.");
        }
        finally{if(launched)await DiscordDevices.AfterInstall(role);else DiscordDevices.CancelBeforeLaunch(role);}
    }
    internal static void VerifyPackage(byte[] zip){if(zip.Length>4*1024*1024||!Convert.ToHexString(SHA256.HashData(zip)).Equals(PackageHash,StringComparison.Ordinal))throw new IOException("Контрольная сумма VB-CABLE не совпала. Установка отменена.");}
    internal static void VerifySignature(string file)
    {
        var info=new WintrustFile{Size=(uint)Marshal.SizeOf<WintrustFile>(),Path=file};
        IntPtr ptr=Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFile>());
        try
        {
            Marshal.StructureToPtr(info,ptr,false);
            var data=new WintrustData{Size=(uint)Marshal.SizeOf<WintrustData>(),Ui=2,Union=1,File=ptr,Flags=0x1000};
            var policy=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if(WinVerifyTrust(new IntPtr(-1),ref policy,ref data)!=0)throw new IOException("Подпись установщика VB-CABLE не прошла проверку Windows.");
        }
        finally{Marshal.DestroyStructure<WintrustFile>(ptr);Marshal.FreeHGlobal(ptr);}
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WintrustFile{public uint Size;[MarshalAs(UnmanagedType.LPWStr)]public string Path;public IntPtr Handle,Subject;}
    [StructLayout(LayoutKind.Sequential)] struct WintrustData{public uint Size;public IntPtr Callback,Client;public uint Ui,Revocation,Union;public IntPtr File;public uint State;public IntPtr StateData,Url;public uint Flags,Context;public IntPtr Signature;}
    [DllImport("wintrust.dll",ExactSpelling=true,PreserveSig=true)] static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref WintrustData data);
}
