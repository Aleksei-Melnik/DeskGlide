using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SdrCapture;

// Soft ascending fifth: two gentle bell notes, generated in memory. No sidecar assets.
sealed class ReplaySaveSound:IDisposable
{
    internal const int SampleRate=48000;
    readonly Lazy<byte[]> wave=new(CreateWave);
    int playing;
    volatile bool disposed;
    public void Play(int volume)
    {
        if(disposed||volume<=0||Interlocked.CompareExchange(ref playing,1,0)!=0)return;
        _=Task.Run(()=>
        {
            try
            {
                if(disposed)return;
                using var memory=new MemoryStream(wave.Value,false);
                using var reader=new WaveFileReader(memory);
                using var output=new WasapiOut(AudioClientShareMode.Shared,80);
                var samples=new VolumeSampleProvider(reader.ToSampleProvider()){Volume=Math.Clamp(volume,0,100)/100f};
                int stopped=0;output.PlaybackStopped+=(_,_)=>Volatile.Write(ref stopped,1);
                output.Init(samples.ToWaveProvider());
                if(disposed)return;
                output.Play();var clock=System.Diagnostics.Stopwatch.StartNew();
                while(!disposed&&Volatile.Read(ref stopped)==0&&clock.Elapsed.TotalSeconds<2)Thread.Sleep(10);
                output.Stop();
            }
            catch(Exception e){Log.Write("Replay notification sound: "+e.Message);}
            finally{Interlocked.Exchange(ref playing,0);}
        });
    }
    internal static double Sample(double time)
    {
        static double Note(double time,double pitch,double level)
        {
            const double length=.46;
            if(time<=0||time>=length)return 0;
            double attack=Math.Min(1,time/.018);attack=attack*attack*(3-2*attack);
            double release=Math.Min(1,(length-time)/.10);release=release*release*(3-2*release);
            double phase=2*Math.PI*pitch*time;
            return level*attack*release*Math.Exp(-6*time)*(Math.Sin(phase)+.12*Math.Sin(2*phase)+.035*Math.Sin(3*phase));
        }
        return Note(time,587.3295358,.31)+Note(time-.22,880,.27);
    }
    internal static byte[] CreateWave()
    {
        const int count=SampleRate*72/100;using var memory=new MemoryStream();using var writer=new BinaryWriter(memory);
        writer.Write("RIFF"u8);writer.Write(36+count*2);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);
        writer.Write(SampleRate);writer.Write(SampleRate*2);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(count*2);
        for(int n=0;n<count;n++)writer.Write((short)Math.Round(Sample(n/(double)SampleRate)*short.MaxValue));
        writer.Flush();return memory.ToArray();
    }
    public void Dispose()=>disposed=true;
}
