using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace SdrCapture;

// One dedicated media thread owns each writer. Signal that thread directly;
// normal-priority thread-pool I/O callbacks can stall behind a CPU-heavy game.
sealed class RecordingPipeWriter(NamedPipeServerStream pipe):IDisposable
{
    readonly EventWaitHandle ready=new(false,EventResetMode.ManualReset);
    public unsafe void Write(byte[] data,int count,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var handle=pipe.SafePipeHandle;bool held=false;
        try
        {
            handle.DangerousAddRef(ref held);
            IntPtr native=handle.DangerousGetHandle();
            fixed(byte* bytes=data)
            {
                int offset=0;
                while(offset<count)
                {
                    ready.Reset();
                    // The event's low bit suppresses IOCP notifications for this
                    // request, even though connection setup uses .NET async I/O.
                    NativeOverlapped operation=new(){EventHandle=ready.SafeWaitHandle.DangerousGetHandle()|1};
                    bool completed=WriteFile(native,bytes+offset,(uint)(count-offset),IntPtr.Zero,&operation);
                    int error=completed?0:Marshal.GetLastWin32Error();
                    if(!completed&&error!=997)throw new IOException("Recording pipe write failed.",new Win32Exception(error));
                    if(!completed)
                    {
                        if(WaitHandle.WaitAny([token.WaitHandle,ready])==0)
                        {
                            CancelIoEx(native,&operation);
                            // Cancellation is asynchronous. Keep both the pinned
                            // buffer and OVERLAPPED alive until the kernel finishes.
                            ready.WaitOne();
                        }
                    }
                    completed=GetOverlappedResult(native,&operation,out uint written,false);
                    error=completed?0:Marshal.GetLastWin32Error();
                    token.ThrowIfCancellationRequested();
                    if(!completed)throw new IOException("Recording pipe write failed.",new Win32Exception(error));
                    if(written==0)throw new IOException("Recording pipe closed before completing the frame.");
                    offset+=checked((int)written);
                }
            }
        }
        finally{if(held)handle.DangerousRelease();}
    }
    public void Dispose()=>ready.Dispose();
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern unsafe bool WriteFile(IntPtr handle,byte* buffer,uint count,IntPtr written,NativeOverlapped* operation);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern unsafe bool GetOverlappedResult(IntPtr handle,NativeOverlapped* operation,out uint written,[MarshalAs(UnmanagedType.Bool)]bool wait);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern unsafe bool CancelIoEx(IntPtr handle,NativeOverlapped* operation);
}
