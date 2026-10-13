using System.Drawing.Imaging;

namespace SdrCapture;

// Screenshots are clipboard bitmaps/PNG data, not Explorer file-drop lists.
static class KvmClipboardImage
{
    internal const long MaxPixels=64L*1024*1024,MaxBytes=256L*1024*1024;
    static readonly byte[] pngHeader=[137,80,78,71,13,10,26,10];
    static void Validate(Image image)
    {
        if(image.Width<1||image.Height<1||(long)image.Width*image.Height>MaxPixels)throw new IOException("Clipboard image is too large.");
    }
    public static Bitmap? Capture(IDataObject data)
    {
        object? png=data.GetData("PNG",false);
        using var bytes=png is byte[] array?new MemoryStream(array,false):null;
        var stream=bytes??png as Stream;
        if(stream==null)
        {
            if(data.GetData(DataFormats.Bitmap,true) is Image bitmap){Validate(bitmap);return new Bitmap(bitmap);}
            return null;
        }
        if(!stream.CanSeek||stream.Length>MaxBytes)throw new IOException("Clipboard image is too large.");
        long position=stream.Position;
        try{stream.Position=0;using var image=Image.FromStream(stream,false,true);Validate(image);return new Bitmap(image);}
        finally{stream.Position=position;}
    }
    public static void Save(Image image,string path)
    {
        Validate(image);image.Save(path,ImageFormat.Png);
        if(new FileInfo(path).Length>MaxBytes)throw new IOException("Clipboard image is too large.");
    }
    public static void ValidateManifest(KvmFileEntry[] entries)
    {
        if(entries.Length!=1||entries[0].Directory||entries[0].Length<8||entries[0].Length>MaxBytes||!entries[0].Path.EndsWith(".png",StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid clipboard image transfer.");
    }
    public static Payload Open(string path)
    {
        if(new FileInfo(path).Length>MaxBytes)throw new IOException("Clipboard image is too large.");
        byte[] bytes=File.ReadAllBytes(path);
        if(bytes.Length<24||!bytes.AsSpan(0,8).SequenceEqual(pngHeader))throw new IOException("Invalid clipboard PNG.");
        uint width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)),height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
        if(width==0||height==0||width>MaxPixels||height>MaxPixels||(long)width*height>MaxPixels)throw new IOException("Clipboard image is too large.");
        var png=new MemoryStream(bytes,false);
        Bitmap? bitmap=null;
        try
        {
            using(var image=Image.FromStream(png,false,true)){Validate(image);bitmap=new Bitmap(image);}
            png.Position=0;
            var data=new DataObject();data.SetImage(bitmap);data.SetData("PNG",false,png);
            var files=new System.Collections.Specialized.StringCollection{path};data.SetFileDropList(files);
            return new(data,bitmap,png);
        }
        catch{bitmap?.Dispose();png.Dispose();throw;}
    }
    internal sealed class Payload(DataObject data,Bitmap bitmap,MemoryStream png):IDisposable
    {
        public DataObject Data {get;}=data;
        public void Dispose(){bitmap.Dispose();png.Dispose();}
    }
}
