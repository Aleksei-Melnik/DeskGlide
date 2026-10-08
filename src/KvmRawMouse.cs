using System.Runtime.InteropServices;

namespace SdrCapture;
// Physical deltas remain usable when a foreground game confines or recentres its cursor.
sealed class KvmRawMouse:Control
{
    [StructLayout(LayoutKind.Sequential)]struct Device{public ushort Page,Usage;public uint Flags;public IntPtr Target;}
    readonly Action<int,int> move;
    public KvmRawMouse(Action<int,int> move)
    {
        this.move=move;_=Handle;
        if(!RegisterRawInputDevices([new(){Page=1,Usage=2,Flags=0x100,Target=Handle}],1,(uint)Marshal.SizeOf<Device>()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override unsafe void WndProc(ref Message message)
    {
        if(message.Msg==0xFF)
        {
            uint size=64,header=(uint)(8+IntPtr.Size*2);byte* data=stackalloc byte[64];
            uint read=GetRawInputData(message.LParam,0x10000003,(IntPtr)data,ref size,header);
            if(read<=64&&RelativeDelta(new ReadOnlySpan<byte>(data,(int)read),IntPtr.Size) is {} delta)move(delta.X,delta.Y);
        }
        base.WndProc(ref message);
    }
    internal static Point? RelativeDelta(ReadOnlySpan<byte> data,int pointerSize)
    {
        int header=8+pointerSize*2;if(data.Length<header+24||System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data)!=0)return null;
        bool physical=data.Slice(8,pointerSize).ContainsAnyExcept((byte)0);if(!physical)return null;
        var mouse=data[header..];if((System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(mouse)&1)!=0)return null;
        return new(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(mouse[12..]),System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(mouse[16..]));
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing&&IsHandleCreated)RegisterRawInputDevices([new(){Page=1,Usage=2,Flags=1,Target=IntPtr.Zero}],1,(uint)Marshal.SizeOf<Device>());
        base.Dispose(disposing);
    }
    [DllImport("user32.dll",SetLastError=true)]static extern bool RegisterRawInputDevices(Device[] devices,uint count,uint size);
    [DllImport("user32.dll")]static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint header);
}
