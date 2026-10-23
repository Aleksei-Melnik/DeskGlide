using System.Text.Json;
namespace SdrCapture;
static class RecordingRoutingTests
{
    internal static void Run()
    {
        static void Check(bool value,string message){if(!value)throw new Exception(message);}
        var settings=new Settings();
        Check(RecordingRouting.DistinctHotkeys(settings),"New recording shortcut conflicts with replay.");
        var copy=settings.Copy();copy.Recording.Profile.Fps=120;
        Check(settings.Recording.Profile.Fps==60,"Recording profile copy shares mutable settings.");
        Check(settings.Replay.Fps==60,"Recording profile modified Replay.");
        copy.Recording.Profile.HotkeyKey=copy.Replay.HotkeyKey;
        Check(!RecordingRouting.DistinctHotkeys(copy),"Identical shortcut is accepted.");
        var legacy=JsonSerializer.Deserialize<Settings>("{\"Replay\":{\"Enabled\":true,\"Fps\":120}}")!;
        legacy.Recording.Validate();Check(legacy.Replay.Fps==120&&legacy.Recording.Computer==""&&legacy.Replay.Encoder=="GPU","Old settings lost their defaults.");
        var idle=RecordingRouting.CaptureDemand(false,60,false,false,120,false,120);
        Check(!idle.Capture&&!idle.Ndi,"Idle recording needlessly runs desktop capture or NDI.");
        var local=RecordingRouting.CaptureDemand(true,120,false,false,60,false,60);
        Check(local.Capture&&local.CaptureFps==120&&!local.Ndi,"Local recording enables unnecessary network sending.");
        var remote=RecordingRouting.CaptureDemand(false,60,false,true,120,false,60);
        Check(remote.Capture&&remote.CaptureFps==120&&remote.Ndi&&remote.NdiFps==120,"Remote Replay is limited to streaming60FPS.");
        var both=RecordingRouting.CaptureDemand(true,60,false,true,60,true,120);
        Check(both.CaptureFps==120&&both.NdiFps==120,"Concurrent profiles reduce the faster session rate.");
        var streaming=RecordingRouting.CaptureDemand(false,60,true,false,120,false,120);
        Check(!streaming.Capture&&streaming.Ndi&&streaming.NdiFps==60,"Streaming starts the local replay encoder.");
        var profile=ConfigurationBackup.Capture(settings,false,Path.Combine(Path.GetTempPath(),"DeskGlide-Routing-"+Guid.NewGuid().ToString("N")));
        var restored=ConfigurationBackup.Decode(ConfigurationBackup.Encode(profile,"recording-test-password"),"recording-test-password");
        Check(restored.Settings.Recording.Profile==settings.Recording.Profile,"Backup lost the independent recording profile.");
        Check(!RecordingRouting.CurrentPoll(1,2,"pending","active")&&!RecordingRouting.CurrentPoll(1,2,"active","active")&&RecordingRouting.CurrentPoll(2,2,"active","active"),"A delayed pre-start status can overwrite a confirmed recording session.");
        Check(!RecordingRouting.PreserveUnconfirmedStart("busy",null,"session")&&!RecordingRouting.PreserveUnconfirmedStart("denied",null,"session")&&RecordingRouting.PreserveUnconfirmedStart("unknown",null,"session")&&RecordingRouting.PreserveUnconfirmedStart("failed",new(Recording:true,RecordingSession:"session"),"session"),"A definitive remote rejection leaves a phantom recording or an uncertain start is discarded.");
        Check(RecordingProtocol.Timeout("stopRecording").TotalSeconds==600&&RecordingProtocol.Timeout("release").TotalSeconds==600&&RecordingProtocol.Timeout("status").TotalSeconds==8,"Long recording finalization inherits the short status deadline.");
        using(var service=new KvmService(new(){Role="Off"}))
        {
            string one=Guid.NewGuid().ToString("N"),two=Guid.NewGuid().ToString("N");service.SelectAudioPeers([one,two,one]);
            var selected=(string[])typeof(KvmService).GetField("activeAudioPeers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
            Check(selected.Length==2&&selected.Contains(one)&&selected.Contains(two),"Concurrent Replay and recording discarded one remote audio subscription.");
            service.RefreshScreens();
            selected=(string[])typeof(KvmService).GetField("activeAudioPeers",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
            Check(selected.Length==2,"Monitor refresh replaced multi-peer audio with a single subscription.");
        }
        Program.Write("recording-routing-tests.json",new{Pass=true,Checks=11,LegacyDefaults=true,IndependentSettings=true,CaptureDemand=true,EncryptedBackup=true});
    }
}
