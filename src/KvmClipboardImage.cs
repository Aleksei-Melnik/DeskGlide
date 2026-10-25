using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace SdrCapture;

// Screenshots are clipboard bitmaps/PNG data, not Explorer file-drop lists.
static class KvmClipboardImage
{
    internal const long MaxPixels=64L*1024*1024,MaxBytes=256L*1024*1024;
    static readonly byte[] pngHeader=[137,80,78,71,13,10,26,10];
    static readonly string[] formats=["PNG","image/png",DataFormats.GetFormat(17).Name,DataFormats.Dib,DataFormats.Bitmap];
    static void ValidateSize(long width,long height)
    {
        if(width<1||height<1||width>MaxPixels||height>MaxPixels||width*height>MaxPixels)throw new IOException("Clipboard image is too large.");
    }
    static void Validate(Image image)
    {
        ValidateSize(image.Width,image.Height);
    }
    public static Bitmap? Capture(IDataObject data)
    {
        Exception? failure=null;
        foreach(string format in formats)
        {
            try
            {
                object? value=data.GetData(format,format==DataFormats.Bitmap);
                if(value is Image bitmap){Validate(bitmap);return new Bitmap(bitmap);}
                using var bytes=value is byte[] array?new MemoryStream(array,false):null;
                var stream=bytes??value as Stream;if(stream==null)continue;
                if(!stream.CanRead||!stream.CanSeek||stream.Length>MaxBytes)throw new IOException("Invalid clipboard image stream.");
                long position=stream.Position;
                try
                {
                    stream.Position=0;
                    return format==DataFormats.Dib||format==formats[2]?DecodeDib(stream):DecodePng(stream);
                }
                finally{stream.Position=position;}
            }
            // Preserve the caller's clipboard-busy retry. A bad image format must
            // not prevent trying another representation of the same picture.
            catch(ExternalException e)when(e.ErrorCode==unchecked((int)0x800401D0)){throw;}
            catch(Exception e)when(e is ArgumentException or IOException or ExternalException or NotSupportedException or ObjectDisposedException or FormatException)
            {failure??=new IOException("Clipboard image format: "+format,e);}
        }
        if(failure!=null)throw new IOException("Не удалось прочитать изображение из буфера обмена.",failure);
        return null;
    }
    static Bitmap DecodePng(Stream stream)
    {
        Span<byte> header=stackalloc byte[24];stream.ReadExactly(header);
        if(!header[..8].SequenceEqual(pngHeader)||BinaryPrimitives.ReadUInt32BigEndian(header[8..])!=13||!header.Slice(12,4).SequenceEqual("IHDR"u8))throw new IOException("Invalid clipboard PNG.");
        ValidateSize(BinaryPrimitives.ReadUInt32BigEndian(header[16..]),BinaryPrimitives.ReadUInt32BigEndian(header[20..]));
        stream.Position=0;using var image=Image.FromStream(stream,false,true);Validate(image);return new Bitmap(image);
    }
    // Qt applications may offer opaque photos only as CF_DIB / CF_DIBV5.
    // Supply the BMP file header explicitly instead of relying on OLE/GDI to
    // synthesize CF_BITMAP. Validate dimensions and storage before decoding.
    static Bitmap DecodeDib(Stream stream)
    {
        Span<byte> info=stackalloc byte[40];stream.ReadExactly(info);
        uint header=BinaryPrimitives.ReadUInt32LittleEndian(info);
        int width=BinaryPrimitives.ReadInt32LittleEndian(info[4..]),signedHeight=BinaryPrimitives.ReadInt32LittleEndian(info[8..]);
        long height=Math.Abs((long)signedHeight);ValidateSize(width,height);
        ushort planes=BinaryPrimitives.ReadUInt16LittleEndian(info[12..]),bpp=BinaryPrimitives.ReadUInt16LittleEndian(info[14..]);
        uint compression=BinaryPrimitives.ReadUInt32LittleEndian(info[16..]),colors=BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
        if(header is not (40 or 52 or 56 or 108 or 124)||planes!=1||bpp is not (1 or 4 or 8 or 16 or 24 or 32)||
            compression is not (0 or 3 or 6)||compression!=0&&bpp is not (16 or 32)||compression==6&&header is >40 and <56)
            throw new IOException("Unsupported clipboard DIB.");
        if(bpp<=8){if(colors==0)colors=1u<<bpp;if(colors>(1u<<bpp))throw new IOException("Invalid clipboard DIB palette.");}
        long masks=header==40?(compression==3?12:compression==6?16:0):0;
        long offset=header+masks+colors*4L,stride=((width*(long)bpp+31)/32)*4;
        if(offset+stride*height>stream.Length)throw new IOException("Truncated clipboard DIB.");
        using var bmp=new MemoryStream(checked(14+(int)stream.Length));
        Span<byte> file=stackalloc byte[14];file.Clear();file[0]=(byte)'B';file[1]=(byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file[2..],checked(14+(int)stream.Length));
        BinaryPrimitives.WriteInt32LittleEndian(file[10..],checked(14+(int)offset));bmp.Write(file);
        stream.Position=0;stream.CopyTo(bmp);bmp.Position=0;
        var frame=BitmapDecoder.Create(bmp,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
        ValidateSize(frame.PixelWidth,frame.PixelHeight);
        var pixels=new FormatConvertedBitmap(frame,System.Windows.Media.PixelFormats.Bgra32,null,0);
        var result=new Bitmap(frame.PixelWidth,frame.PixelHeight,PixelFormat.Format32bppArgb);
        try
        {
            var bits=result.LockBits(new(Point.Empty,result.Size),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            try{pixels.CopyPixels(System.Windows.Int32Rect.Empty,bits.Scan0,checked(bits.Stride*result.Height),bits.Stride);}
            finally{result.UnlockBits(bits);}
            return result;
        }
        catch{result.Dispose();throw;}
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
