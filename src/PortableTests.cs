using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SdrCapture;

static class PortableTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        Require(PortableResources.Bundled,"This is not the portable executable.");
        string root=AppContext.BaseDirectory;
        Require(Path.GetFullPath(Path.GetDirectoryName(Environment.ProcessPath)!+Path.DirectorySeparatorChar)==Path.GetFullPath(root),"Bundle changes the updater destination.");
        Require(Directory.GetFiles(root,"*.dll").Length==0,"Test is not isolated from loose dependencies.");
        string camera=PortableResources.FilePath("camera/ScreenCapture.Camera.dll");
        string expected=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(camera)));
        File.WriteAllBytes(camera,[1,2,3]);
        Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PortableResources.FilePath("camera/ScreenCapture.Camera.dll"))))==expected,"Damaged cached camera was not repaired.");
        IntPtr native=NativeLibrary.Load(camera);
        try{Require(NativeLibrary.GetExport(native,"DllGetClassObject")!=IntPtr.Zero,"Bundled camera is not a COM server.");}finally{NativeLibrary.Free(native);}
        using var jpeg=new KvmJpeg();using var image=new System.Drawing.Bitmap(320,180);
        using var decoded=jpeg.Decode(jpeg.Encode(image,95));Require(decoded.Width==320&&decoded.Height==180,"Bundled KVM JPEG library failed.");
        foreach(string notice in new[]{"LICENSE","THIRD-PARTY-NOTICES.md","dependency-licenses/Microsoft-DirectShow-baseclasses-LICENSE"})
            Require(File.ReadAllText(PortableResources.FilePath(notice)).Length>20,"Dependency notice missing: "+notice);
        Program.Write("portable-tests.json",new{Pass=true,BundledRuntime=true,CameraExtractedAndRepairable=true,NativeCameraLoaded=true,NativeKvmCodecLoaded=true,LicensesIncluded=true,UpdateDirectoryPreserved=true});
        Require(Directory.GetFiles(root).All(p=>p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)),"Portable app wrote a sidecar file beside the executable.");
    }
}
