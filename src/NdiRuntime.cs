using System.Diagnostics;
using System.Security.Cryptography;

namespace SdrCapture;

// Installs only when an NDI feature is used. KVM and local replay need no NDI.
static class NdiRuntime
{
    internal const string Version="6.3.2.0";
    internal const string DownloadUrl="https://downloads.ndi.tv/SDK/NDI_SDK/NDI%206%20Runtime.exe";
    internal const string Sha256="7EE73EEDB56402BCA5100868353DBAB4E944B6C37D2D9881580698A3B61346CD";
    static readonly DependencySetup setup=new();
    static readonly HttpClient http=new(){Timeout=TimeSpan.FromMinutes(3)};
    public static bool Ready=>NdiNative.TryLoadRuntime()!=IntPtr.Zero;
    public static Task EnsureAsync(Action<string>? progress=null,CancellationToken token=default,bool retry=false)
        =>Task.Run(()=>setup.EnsureAsync(()=>Ready,t=>InstallAsync(progress,t),token,retry),token);
    internal static void VerifyPackage(byte[] bytes)
    {
        if(bytes.Length>32*1024*1024||!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256,StringComparison.Ordinal))
            throw new IOException(UiStrings.T("NDI Runtime checksum mismatch. Update DeskGlide or install the official Runtime manually."));
    }
    internal static bool CachedPackageValid(string file)
    {
        try
        {
            using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
            return input.Length is >0 and <=32*1024*1024 &&
                Convert.ToHexString(SHA256.HashData(input)).Equals(Sha256,StringComparison.Ordinal);
        }
        catch(IOException){return false;}
    }
    static async Task InstallAsync(Action<string>? progress,CancellationToken token)
    {
        string root=Path.Combine(Log.Folder,"Dependencies","NDI",Version);Updates.RejectReparse(root);Directory.CreateDirectory(root);
        string installer=Path.Combine(root,"NDI-Runtime.exe");Updates.RejectReparse(installer);
        // A damaged cache must be replaceable on retry, rather than permanently
        // blocking first-time setup. Replace it only after verifying the download.
        if(!CachedPackageValid(installer))
        {
            progress?.Invoke(UiStrings.T("Downloading NDI Runtime…"));
            using var response=await http.GetAsync(DownloadUrl,HttpCompletionOption.ResponseHeadersRead,token);
            response.EnsureSuccessStatusCode();
            await using var input=await response.Content.ReadAsStreamAsync(token);
            using var buffer=new MemoryStream();byte[] block=new byte[65536];int count;
            while((count=await input.ReadAsync(block,token))>0)
            {
                if(buffer.Length+count>32*1024*1024)throw new IOException(UiStrings.T("NDI Runtime download is too large."));
                buffer.Write(block,0,count);
            }
            byte[] bytes=buffer.ToArray();VerifyPackage(bytes);
            string temporary=installer+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{await File.WriteAllBytesAsync(temporary,bytes,token);File.Move(temporary,installer,true);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        token.ThrowIfCancellationRequested();
        VerifyPackage(await File.ReadAllBytesAsync(installer,token));
        DiscordSetup.VerifySignature(installer,"NDI Runtime");
        progress?.Invoke(UiStrings.T("Complete the NDI Runtime installer. Windows may request administrator permission."));
        // Keep the unmodified vendor wizard and its license acceptance visible.
        // Never silently accept NDI's license or request a Windows restart.
        var start=new ProcessStartInfo(installer){UseShellExecute=true,WorkingDirectory=root};start.ArgumentList.Add("/NORESTART");
        try
        {
            using var process=Process.Start(start)??throw new IOException(UiStrings.T("NDI Runtime installer did not start."));
            await process.WaitForExitAsync(token);
            if(process.ExitCode is not (0 or 3010))throw new IOException(UiStrings.F("NDI Runtime installation did not finish (code {0}). Retry in Screen streaming.",process.ExitCode));
            if(!Ready)throw new IOException(UiStrings.T("NDI Runtime is still unavailable. Retry setup in Screen streaming."));
            progress?.Invoke(UiStrings.T("NDI Runtime is ready."));
        }
        catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode==1223)
        {throw new IOException(UiStrings.T("NDI Runtime setup was cancelled. Retry in Screen streaming."),e);}
    }
}

// A cancelled or failed installer must not reopen a UAC/license wizard every
// second from the sender, receiver or device-discovery retry loops.
sealed class DependencySetup
{
    readonly SemaphoreSlim gate=new(1,1);
    bool attempted;string? failure;
    internal async Task EnsureAsync(Func<bool> ready,Func<CancellationToken,Task> install,CancellationToken token,bool retry=false)
    {
        if(ready())return;
        await gate.WaitAsync(token);
        try
        {
            if(ready())return;
            if(attempted&&!retry)throw new IOException(failure??UiStrings.T("NDI Runtime setup was cancelled. Retry in Screen streaming."));
            attempted=true;
            try{await install(token);if(!ready())throw new IOException(UiStrings.T("NDI Runtime is still unavailable. Retry setup in Screen streaming."));failure=null;}
            catch(Exception e){failure=e.Message;throw;}
        }
        finally{gate.Release();}
    }
}
