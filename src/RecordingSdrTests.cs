using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SdrCapture;

static class RecordingSdrTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run(bool hardware)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"recording-sdr-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SDRCAPTURE_REPLAY_ROOT",root);
        using var device=D3D11.D3D11CreateDevice(DriverType.Hardware,DeviceCreationFlags.BgraSupport,FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;using var color=new ColorPipeline(device,context);
        using var texture=device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,640,360,1,1,BindFlags.ShaderResource));
        float[][] patches=[[0,0,0],[.01f,.01f,.01f],[.18f,.18f,.18f],[1,1,1],[1,0,0],[0,1,0],[0,0,1],[4,2,1]];
        Half[] pixels=new Half[640*360*4];
        for(int y=0;y<360;y++)for(int x=0;x<640;x++){int at=(y*640+x)*4;var patch=patches[x/80];for(int c=0;c<3;c++)pixels[at+c]=(Half)(patch[c]*3);pixels[at+3]=(Half)1;}
        context.UpdateSubresource(pixels,texture,0,640*8);
        using var ndi=color.ConvertNdi(texture,true,1/3f);byte[] expected=new byte[640*360*3/2];RecordingPixels.Nv12(ndi.Data,expected,640,360);
        var results=new List<object>();
        foreach(string codec in hardware?new[]{"AV1","HEVC","H264"}:new[]{"H264"})foreach(string audio in new[]{"Mixed","Silent"})
        {
            int fps=codec=="H264"&&(hardware||audio=="Mixed")?60:120;
            var options=new ReplayOptions{Enabled=true,Codec=codec,Fps=fps,AudioMode=audio,Width=640,Height=360,Folder=Path.Combine(root,"clips")};
            var encoder=hardware?RecordingEncoders.Candidates(options,640,360)[0]:new RecordingEncoder("libx264","Software regression",["-c:v","libx264","-preset","ultrafast","-crf","12","-g",(fps*2).ToString(),"-bf","0","-pix_fmt","yuv420p"]);
            using var recorder=new ReplayRecorder(options,true,4,encoder);using var stop=new CancellationTokenSource();
            long producerSkipped=0;
            var producer=Task.Factory.StartNew(()=>
            {
                using var pacing=new Pacer();long origin=Stopwatch.GetTimestamp(),index=0;
                while(!stop.IsCancellationRequested)
                {
                    pacing.WaitUntil(origin+index*Stopwatch.Frequency/fps,stop.Token);if(stop.IsCancellationRequested)break;
                    long next=Math.Max(index,(Stopwatch.GetTimestamp()-origin)*fps/Stopwatch.Frequency);producerSkipped+=next-index;index=next;
                    for(int y=0;y<16;y++)for(int bit=0;bit<16;bit++)for(int x=bit*16;x<(bit+1)*16;x++)ndi.Data[(y*640+x)*2+1]=(byte)(((index>>bit)&1)==0?16:235);
                    ndi.CapturedAt=origin+index*Stopwatch.Frequency/fps+(index%2==0?-1:1)*Stopwatch.Frequency/1000;
                    recorder.Offer(ndi);index++;
                }
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            ReplayResult saved;
            try{Thread.Sleep(6500);Require(recorder.Error==null,"SDR recording failed: "+recorder.Error);saved=recorder.SaveAsync("SDR tests").GetAwaiter().GetResult();}
            finally{stop.Cancel();producer.GetAwaiter().GetResult();recorder.Update(options with{Enabled=false});}
            using var deadline=new CancellationTokenSource(30000);
            string elementary=Path.Combine(root,codec+"-"+audio+(codec=="AV1"?".obu":codec=="HEVC"?".hevc":".h264"));
            var extract=new List<string>{"-v","error","-y","-i",saved.LocalPath,"-map","0:v:0","-c:v","copy","-frames:v","1"};
            if(codec!="AV1")extract.AddRange(["-bsf:v",codec=="HEVC"?"hevc_mp4toannexb":"h264_mp4toannexb"]);
            extract.AddRange(["-f",codec=="AV1"?"obu":codec=="HEVC"?"hevc":"h264",elementary]);
            ReplayTools.RunAsync(ReplayTools.Ffmpeg,extract,deadline.Token).GetAwaiter().GetResult();
            string json=ReplayTools.RunAsync(ReplayTools.Ffprobe,["-v","error","-show_streams","-of","json",elementary],deadline.Token).GetAwaiter().GetResult();
            using var streamInfo=JsonDocument.Parse(json);var video=streamInfo.RootElement.GetProperty("streams")[0];
            foreach(string property in new[]{"color_space","color_transfer","color_primaries"})Require(video.TryGetProperty(property,out var value)&&value.GetString()=="bt709",codec+" "+audio+" elementary stream lost SDR "+property);
            Require(video.GetProperty("color_range").GetString()=="tv","Elementary video lost limited range.");
            string raw=Path.Combine(root,codec+"-"+audio+".nv12");
            ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",saved.LocalPath,"-map","0:v:0","-frames:v","1","-pix_fmt","nv12","-f","rawvideo",raw],deadline.Token).GetAwaiter().GetResult();
            byte[] decoded=File.ReadAllBytes(raw);int maximum=0;
            for(int patch=0;patch<8;patch++)
            {
                int x=patch*80+40;
                foreach(int at in new[]{180*640+x,640*360+90*640+x,640*360+90*640+x+1})maximum=Math.Max(maximum,Math.Abs(decoded[at]-expected[at]));
            }
            Require(maximum<=8,"Encoded SDR patches differ from the NDI source: "+maximum);
            using var container=ReplayTests.Probe(saved.LocalPath).GetAwaiter().GetResult();int tracks=container.RootElement.GetProperty("streams").EnumerateArray().Count(s=>s.GetProperty("codec_type").GetString()=="audio");
            Require(tracks==(audio=="Silent"?0:1),"Audio mode changed while testing SDR.");
            string idsFile=Path.Combine(root,codec+"-"+audio+"-ids.raw");
            ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",saved.LocalPath,"-an","-vf","crop=256:16:0:0,scale=16:1:flags=neighbor,format=gray","-fps_mode","passthrough","-f","rawvideo",idsFile],deadline.Token).GetAwaiter().GetResult();
            byte[] strip=File.ReadAllBytes(idsFile);var ids=new List<int>();
            for(int at=0;at+16<=strip.Length;at+=16){int id=0;for(int bit=0;bit<16;bit++)if(strip[at+bit]>128)id|=1<<bit;ids.Add(id);}
            var steps=ids.Skip(1).Zip(ids,(a,b)=>(a-b+65536)%65536).Skip(4).ToArray();int repeated=steps.Count(s=>s==0);
            Require(ids.Count>fps*2&&steps.All(s=>s<fps),"Recorded frame IDs were missing or reordered.");
            Require(repeated<=producerSkipped+Math.Max(4,steps.Length*.02),"Recording added repeat/drop pairs to available jittered source frames.");
            double? minimumAudioRms=null;
            if(audio!="Silent")
            {
                string pcm=Path.Combine(root,codec+"-"+audio+".f32");
                ReplayTools.RunAsync(ReplayTools.Ffmpeg,["-v","error","-y","-i",saved.LocalPath,"-map","0:a:0","-ac","1","-ar","48000","-f","f32le",pcm],deadline.Token).GetAwaiter().GetResult();
                minimumAudioRms=MinimumRms(File.ReadAllBytes(pcm));
                Require(minimumAudioRms>.04,"AAC recording contains an artificial audio hole.");
            }
            results.Add(new{codec,audio,fps,Encoder=encoder.Name,ElementaryBt709=true,MaximumCodeDifferenceFromNdi=maximum,Frames=ids.Count,Repeated=repeated,producerSkipped,MinimumAudioRms=minimumAudioRms,File=saved.LocalPath});
        }
        Program.Write("recording-sdr-tests.json",new{Pass=true,hardware,RealHdrGpuSource=true,SdrWhiteNits=240,SameColorPatchesAsNdi=true,ElementaryHeadersChecked=true,DecodedFrameOrderChecked=true,SyntheticAudioContinuityChecked=true,Results=results});
    }
    static double MinimumRms(byte[] pcm)
    {
        var samples=MemoryMarshal.Cast<byte,float>(pcm);double minimum=double.MaxValue;
        Require(samples.Length>48000,"Audio was truncated.");
        for(int at=4800;at+4800<samples.Length;at+=960)
        {
            double energy=0;for(int i=0;i<960;i++)energy+=samples[at+i]*samples[at+i];
            minimum=Math.Min(minimum,Math.Sqrt(energy/960));
        }
        return minimum;
    }
}
