using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SdrCapture;

// Use the timestamp of the first WASAPI sample, never the time the callback ran.
// Preserve continuous device samples and gently follow device/QPC clock drift.
sealed class AudioPacketClock
{
    long nextDevice=long.MinValue, nextOutput;
    double correction;
    public (long Start,int Frames) Place(long devicePosition,long timestamp,int frames,bool discontinuity=false)
    {
        if(nextDevice==long.MinValue || devicePosition!=nextDevice || discontinuity || Math.Abs(timestamp-nextOutput)>4800)
        {nextOutput=timestamp;correction=0;}
        long start=nextOutput;
        correction=Math.Clamp(correction+(timestamp-nextOutput)*.002,-2,2);
        int adjust=(int)correction;correction-=adjust;
        int length=Math.Max(1,frames+adjust);
        nextDevice=devicePosition+frames;nextOutput+=length;
        return(start,length);
    }
    public static void Resample(ReadOnlySpan<float> input,Span<float> output)
    {
        int count=input.Length/2, length=output.Length/2;
        if(count==length){input.CopyTo(output);return;}
        for(int i=0;i<length;i++)
        {
            double p=i*(double)count/length;int a=Math.Min((int)p,count-1),b=Math.Min(a+1,count-1);float t=(float)(p-a);
            for(int c=0;c<2;c++)output[i*2+c]=input[a*2+c]+(input[b*2+c]-input[a*2+c])*t;
        }
    }
}

static class TimedAudioCapture
{
    // This method owns its COM objects on a dedicated capture thread. A 100 ms
    // endpoint buffer protects against scheduling stalls, but is drained on every
    // audio event (normally 10 ms), with a short polling fallback during silence.
    public static void Run(string id,CancellationToken stop,Action<long,float[],int> packet,
        Action<string> status,Func<bool>? enabled=null)
    {
        using var devices=new MMDeviceEnumerator();
        using var device=id.StartsWith("default:")?devices.GetDefaultAudioEndpoint(id=="default:render"?DataFlow.Render:DataFlow.Capture,Role.Multimedia):devices.GetDevice(id);
        using var client=device.AudioClient;
        using var ready=new EventWaitHandle(false,EventResetMode.AutoReset);
        var flags=AudioClientStreamFlags.AutoConvertPcm|AudioClientStreamFlags.SrcDefaultQuality|AudioClientStreamFlags.EventCallback;
        if(device.DataFlow==DataFlow.Render)flags|=AudioClientStreamFlags.Loopback;
        client.Initialize(AudioClientShareMode.Shared,flags,1000000,0,WaveFormat.CreateIeeeFloatWaveFormat(48000,2),Guid.Empty);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var capture=client.AudioCaptureClient;
        float[] raw=new float[Math.Max(client.BufferSize,4800)*2],converted=new float[raw.Length+8];
        var clock=new AudioPacketClock();long nextCheck=0,nextDevice=long.MinValue,nextStamp=0;
        var waits=new[]{stop.WaitHandle,ready};
        uint task=0;IntPtr mmcss=AvSetMmThreadCharacteristics("Audio",ref task);
        try
        {
            client.Start();status(device.FriendlyName);
            while(!stop.IsCancellationRequested&&(enabled?.Invoke()??true))
            {
                if(WaitHandle.WaitAny(waits,10)==0)break;
                while(capture.GetNextPacketSize()>0)
                {
                    IntPtr data=capture.GetBuffer(out int frames,out var bufferFlags,out long position,out long qpc);
                    try
                    {
                        if(frames<=0)continue;
                        if(frames*2>raw.Length){raw=new float[frames*2];converted=new float[raw.Length+8];}
                        if((bufferFlags&AudioClientBufferFlags.Silent)!=0)Array.Clear(raw,0,frames*2);
                        else Marshal.Copy(data,raw,0,frames*2);
                        bool badStamp=(bufferFlags&AudioClientBufferFlags.TimestampError)!=0||qpc<=0;
                        long stamp=badStamp?(position==nextDevice?nextStamp:AudioTimeline.Position()-frames):(long)Math.Round(qpc*(48000.0/10000000));
                        var mapped=clock.Place(position,stamp,frames,(bufferFlags&AudioClientBufferFlags.DataDiscontinuity)!=0);
                        nextDevice=position+frames;nextStamp=stamp+frames;
                        AudioPacketClock.Resample(raw.AsSpan(0,frames*2),converted.AsSpan(0,mapped.Frames*2));
                        packet(mapped.Start,converted,mapped.Frames);
                    }
                    finally{capture.ReleaseBuffer(frames);}
                }
                if(id.StartsWith("default:")&&AudioTimeline.Position()>nextCheck)
                {
                    nextCheck=AudioTimeline.Position()+48000;
                    using var current=devices.GetDefaultAudioEndpoint(device.DataFlow,Role.Multimedia);
                    if(current.ID!=device.ID)return;
                }
            }
        }
        finally{try{client.Stop();}finally{if(mmcss!=IntPtr.Zero)AvRevertMmThreadCharacteristics(mmcss);}}
    }
    [DllImport("avrt.dll",CharSet=CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristics(string task,ref uint index);
    [DllImport("avrt.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
