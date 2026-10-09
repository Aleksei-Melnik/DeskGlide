using System.IO.Compression;
using System.Security.Cryptography;

namespace SdrCapture;

// A portable executable carries the camera and dependency notices. The camera
// is still registered from the existing persistent per-user camera directory.
static class PortableResources
{
    const string Resource="DeskGlide.PortableResources";
    public static bool Bundled=>typeof(PortableResources).Assembly.GetManifestResourceInfo(Resource)!=null;
    public static string FilePath(string relative)
    {
        if(!Bundled)return Path.Combine(AppContext.BaseDirectory,relative.Replace('/',Path.DirectorySeparatorChar));
        using var source=typeof(PortableResources).Assembly.GetManifestResourceStream(Resource)!;
        string hash=Convert.ToHexString(SHA256.HashData(source));source.Position=0;
        string root=Path.Combine(Log.Folder,"Resources",hash[..32]);Updates.RejectReparse(root);
        using var archive=new ZipArchive(source,ZipArchiveMode.Read);
        var entry=archive.GetEntry(relative)??throw new IOException("Missing bundled component: "+relative);
        if(entry.Length>8*1024*1024)throw new IOException("Bundled component is too large.");
        string target=Updates.Under(root,relative);Updates.RejectReparse(target);
        using var input=entry.Open();using var buffer=new MemoryStream();input.CopyTo(buffer);
        byte[] data=buffer.ToArray();
        if(File.Exists(target))
        {
            using var cached=File.OpenRead(target);
            if(SHA256.HashData(cached).AsSpan().SequenceEqual(SHA256.HashData(data)))return target;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,data);File.Move(temporary,target,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        return target;
    }
}
