using System.Text.Json;
using NAudio.Wave;

namespace SdrCapture;

static class ReplayPreferencesTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(Action action,string message){try{action();}catch(ArgumentException){return;}throw new Exception(message);}
    public static void Run()
    {
        Require(new ReplayOptions().BufferSeconds==300,"Default replay duration changed");
        foreach(int minutes in new[]{5,13,20})Require(JsonSerializer.Deserialize<ReplayOptions>($"{{\"Minutes\":{minutes}}}")!.BufferSeconds==minutes*60,"Legacy duration was not migrated");
        foreach(int seconds in new[]{30,31,3599,3600,7199,7200})
        {
            var options=new ReplayOptions{DurationSeconds=seconds,SaveSoundEnabled=false,SaveSoundVolume=37};options.Validate();
            var copy=JsonSerializer.Deserialize<ReplayOptions>(JsonSerializer.Serialize(options))!;
            Require(copy.BufferSeconds==seconds&&!copy.SaveSoundEnabled&&copy.SaveSoundVolume==37,"Replay duration or sound preferences lost during serialization");
            Require(ReplayTime.Parse((seconds/60).ToString(),(seconds%60).ToString())==seconds,"Duration fields changed seconds");
        }
        foreach(int seconds in new[]{-1,1,29,7201,int.MaxValue})Reject(()=>(new ReplayOptions{DurationSeconds=seconds}).Validate(),"Out-of-range replay duration accepted");
        Reject(()=>(new ReplayOptions{Minutes=int.MaxValue}).Validate(),"Legacy duration overflow accepted");
        foreach(var (minutes,seconds) in new[]{("0","29"),("120","1"),("1","60"),("-1","30"),("text","0"),("","30"),("9999999999","0")})Reject(()=>ReplayTime.Parse(minutes,seconds),"Invalid duration input accepted");
        Require(ReplayTime.Format(30)=="0:30"&&ReplayTime.Format(3600)=="1:00:00"&&ReplayTime.Format(7200)=="2:00:00","Durations wrapped at an hour");
        Reject(()=>(new ReplayOptions{SaveSoundVolume=-1}).Validate(),"Negative chime volume accepted");Reject(()=>(new ReplayOptions{SaveSoundVolume=101}).Validate(),"Invalid chime volume accepted");
        using(var memory=new MemoryStream(ReplaySaveSound.CreateWave()))using(var reader=new WaveFileReader(memory))
        {
            Require(reader.WaveFormat.SampleRate==48000&&reader.WaveFormat.BitsPerSample==16&&reader.WaveFormat.Channels==1&&Math.Abs(reader.TotalTime.TotalSeconds-.72)<.001,"Chime format or length is invalid");
            byte[] data=new byte[reader.Length];reader.ReadExactly(data);short[] pcm=new short[data.Length/2];Buffer.BlockCopy(data,0,pcm,0,data.Length);
            Require(pcm[0]==0&&pcm[^1]==0&&pcm.Select(n=>Math.Abs((int)n)).Max()<short.MaxValue/2,"Chime clicks at its ends or clips");
            Require(pcm.Zip(pcm.Skip(1),(a,b)=>Math.Abs(b-a)).Max()<2000,"Chime has an abrupt sample discontinuity");
            double Energy(double pitch,double from,double to)
            {
                double real=0,imaginary=0;
                for(int n=(int)(from*48000);n<(int)(to*48000);n++){double phase=2*Math.PI*pitch*n/48000;real+=pcm[n]*Math.Cos(phase);imaginary+=pcm[n]*Math.Sin(phase);}
                return real*real+imaginary*imaginary;
            }
            Require(Energy(587.3295358,.02,.20)>Energy(880,.02,.20)*20&&Energy(880,.48,.62)>Energy(587.3295358,.48,.62)*20,"Chime no longer contains two distinct ascending notes");
        }
        Require(ReplayDelivery.CopyTimeout(1).TotalSeconds==60&&ReplayDelivery.CopyTimeout(40L*1024*1024*1024).TotalMinutes>150,"Large replay clips still have a 60-second delivery timeout");
        Delivery().GetAwaiter().GetResult();
    }
    static async Task Delivery()
    {
        string? previous=Environment.GetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT");
        string root=Path.Combine(AppContext.BaseDirectory,"replay-preferences-"+Guid.NewGuid().ToString("N"));string saved=Path.Combine(root,"saved");Directory.CreateDirectory(saved);
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        try
        {
            using var delivery=new ReplayDelivery();
            var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int count=0;bool published=false;
            string source=Path.Combine(saved,"clip.mp4"),manifest=source+".delivery.json",blocked=Path.Combine(root,"blocked"),destination=Path.Combine(blocked,"clip.mp4");
            delivery.Notification+=_=>{if(Volatile.Read(ref count)==0)failed.TrySetResult();};
            delivery.Saved+=job=>{published=File.Exists(destination)&&!File.Exists(source)&&!File.Exists(manifest);Interlocked.Increment(ref count);completed.TrySetResult();throw new IOException("Fixture notification callback failure");};
            File.WriteAllBytes(source,[1,4,9,16]);File.WriteAllText(blocked,"Not a directory");
            string staged=manifest+".tmp";File.WriteAllText(staged,JsonSerializer.Serialize(new DeliveryJob(source,destination,7200)));File.Move(staged,manifest);delivery.Wake();
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));Require(count==0&&File.Exists(source)&&File.Exists(manifest),"Failed delivery announced success or lost the clip");
            File.Delete(blocked);Directory.CreateDirectory(blocked);delivery.Wake();await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(published&&count==1&&File.ReadAllBytes(destination).SequenceEqual(new byte[]{1,4,9,16}),"Save chime was signaled before successful publication");
            delivery.Wake();await Task.Delay(100);Require(count==1&&!File.Exists(manifest),"Notification failure retried or replayed a saved clip");
        }
        finally{Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",previous);}
    }
}
