using System.Buffers.Binary;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace SdrCapture;

// Eager native formats: pasting never depends on a closed editor, OLE image
// conversion or delayed rendering. Windows owns each HGLOBAL after publication.
static class ScreenshotClipboard
{
    internal sealed record Payload(byte[] Dib,byte[] Png);
    internal static unsafe Payload Prepare(Bitmap image)
    {
        int stride=checked(image.Width*4);byte[] dib=new byte[checked(40+stride*image.Height)];
        BinaryPrimitives.WriteInt32LittleEndian(dib,40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4),image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8),-image.Height); // top-down BI_RGB
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12),1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14),32);
        var bits=image.LockBits(new(Point.Empty,image.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{for(int y=0;y<image.Height;y++)new ReadOnlySpan<byte>((byte*)bits.Scan0+y*bits.Stride,stride).CopyTo(dib.AsSpan(40+y*stride,stride));}
        finally{image.UnlockBits(bits);}
        using var png=new MemoryStream();ScreenshotFastPng.Write(png,dib.AsSpan(40),image.Width,image.Height);
        return new(dib,png.ToArray());
    }
    internal static void Publish(nint owner,Payload payload)
    {
        uint png=RegisterClipboardFormat("PNG");if(png==0)throw new Win32Exception();
        using var bitmap=new ClipboardBitmap(payload.Dib);
        using var dibMemory=new ClipboardMemory(payload.Dib);using var pngMemory=new ClipboardMemory(payload.Png);
        bool opened=false;
        for(int attempt=0;attempt<6;attempt++){if(OpenClipboard(owner)){opened=true;break;}if(attempt<5)Thread.Sleep(10);}
        if(!opened)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if(!EmptyClipboard())throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap.Publish();dibMemory.Publish(8);pngMemory.Publish(png);
        }
        finally{CloseClipboard();}
    }
    sealed class ClipboardBitmap:IDisposable
    {
        nint handle;
        internal unsafe ClipboardBitmap(byte[] dib)
        {
            fixed(byte* header=dib)
            {
                handle=CreateDIBSection(0,(nint)header,0,out var pixels,0,0);
                if(handle==0)throw new Win32Exception(Marshal.GetLastWin32Error());
                dib.AsSpan(40).CopyTo(new Span<byte>((void*)pixels,dib.Length-40));
            }
        }
        internal void Publish()
        {if(SetClipboardData(2,handle)==0)throw new Win32Exception(Marshal.GetLastWin32Error());handle=0;}
        public void Dispose(){if(handle!=0){DeleteObject(handle);handle=0;}}
    }
    sealed class ClipboardMemory:IDisposable
    {
        nint handle;
        internal unsafe ClipboardMemory(byte[] bytes)
        {
            handle=GlobalAlloc(2,(nuint)bytes.Length);if(handle==0)throw new Win32Exception();
            nint address=GlobalLock(handle);
            if(address==0){Dispose();throw new Win32Exception();}
            try{bytes.CopyTo(new Span<byte>((void*)address,bytes.Length));}finally{GlobalUnlock(handle);}
        }
        internal void Publish(uint format)
        {if(SetClipboardData(format,handle)==0)throw new Win32Exception(Marshal.GetLastWin32Error());handle=0;}
        public void Dispose(){if(handle!=0){GlobalFree(handle);handle=0;}}
    }
    [DllImport("gdi32.dll",SetLastError=true)]static extern nint CreateDIBSection(nint dc,nint info,uint usage,out nint pixels,nint section,uint offset);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(nint bitmap);
    [DllImport("user32.dll",SetLastError=true)]static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll",SetLastError=true)]static extern bool EmptyClipboard();
    [DllImport("user32.dll")]static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)]static extern nint SetClipboardData(uint format,nint memory);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")]static extern nint GlobalFree(nint memory);
}

// Clipboard PNG prioritizes latency over compression ratio. Lossless RGBA,
// explicit sRGB, filter 0 and fast zlib; ordinary file saves retain WIC encoding.
static class ScreenshotFastPng
{
    static readonly uint[] crc=Enumerable.Range(0,256).Select(i=>{uint c=(uint)i;for(int b=0;b<8;b++)c=(c&1)!=0?0xedb88320^(c>>1):c>>1;return c;}).ToArray();
    internal static void Write(Stream output,ReadOnlySpan<byte> bgra,int width,int height)
    {
        if(width<=0||height<=0||bgra.Length!=checked(width*height*4))throw new ArgumentException("Invalid PNG pixels.");
        output.Write(new byte[]{137,80,78,71,13,10,26,10});
        Span<byte> header=stackalloc byte[13];header.Clear();BinaryPrimitives.WriteInt32BigEndian(header,width);BinaryPrimitives.WriteInt32BigEndian(header[4..],height);header[8]=8;header[9]=6;
        Chunk(output,"IHDR",header);Chunk(output,"sRGB",new byte[]{0});
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,CompressionLevel.Fastest,leaveOpen:true))
        {
            byte[] row=new byte[checked(1+width*4)];
            for(int y=0;y<height;y++)
            {
                var source=bgra.Slice(y*width*4,width*4);
                for(int x=0;x<width;x++){int p=x*4;row[p+1]=source[p+2];row[p+2]=source[p+1];row[p+3]=source[p];row[p+4]=source[p+3];}
                zlib.Write(row);
            }
        }
        Chunk(output,"IDAT",compressed.GetBuffer().AsSpan(0,checked((int)compressed.Length)));Chunk(output,"IEND",[]);
    }
    static void Chunk(Stream output,string type,ReadOnlySpan<byte> data)
    {
        Span<byte> number=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(number,data.Length);output.Write(number);
        byte[] name=Encoding.ASCII.GetBytes(type);output.Write(name);output.Write(data);
        uint checksum=0xffffffff;foreach(byte b in name)checksum=crc[(checksum^b)&255]^(checksum>>8);
        foreach(byte b in data)checksum=crc[(checksum^b)&255]^(checksum>>8);
        BinaryPrimitives.WriteUInt32BigEndian(number,checksum^0xffffffff);output.Write(number);
    }
}
