using NAudio.CoreAudioApi;

namespace SdrCapture;
static class DiscordDeviceTests
{
    static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
    public static void Rules()
    {
        DiscordDevices.Endpoint[] endpoints=[new("speaker",DataFlow.Render,"Speakers","Realtek"),new("mic",DataFlow.Capture,"Microphone","USB Audio"),new("other",DataFlow.Render,"Headphones","USB Audio"),new("send",DataFlow.Render,"renamed output","VB-Audio Virtual Cable"),new("capture",DataFlow.Capture,"renamed input","VB-Audio Virtual Cable")];
        var original=(from flow in new[]{DataFlow.Render,DataFlow.Capture} from role in new[]{Role.Console,Role.Multimedia,Role.Communications} select new DiscordDevices.DefaultEndpoint(flow,role,flow==DataFlow.Render?"speaker":"mic")).ToArray();
        var installed=original.Select(d=>d with{Id=d.Flow==DataFlow.Render?"send":"capture"}).ToArray();
        Require(DiscordDevices.RestorationPlan(original,installed,endpoints).SequenceEqual(original),"Did not restore all six audio roles");
        Require(!DiscordDevices.RestorationPlan(installed,installed,endpoints).Any(),"Changed pre-existing cable default");
        var manual=installed.Select(d=>d.Flow==DataFlow.Render?d with{Id="other"}:d).ToArray();
        Require(DiscordDevices.RestorationPlan(original,manual,endpoints).All(d=>d.Flow==DataFlow.Capture),"Overrode a manual device choice");
        Require(!DiscordDevices.RestorationPlan(original,installed,endpoints.Where(d=>d.Id is not("speaker" or "mic"))).Any(),"Restored a disconnected endpoint");
        var pair=DiscordDevices.Pair("send",endpoints);Require(pair.Capture.Id=="capture"&&pair.Render.Id=="send","Pairing depends on display name");
        bool ambiguous=false;try{DiscordDevices.Pair("send",endpoints.Append(new("second",DataFlow.Capture,"Second","VB-Audio Virtual Cable")));}catch(IOException){ambiguous=true;}Require(ambiguous,"Ambiguous cable was renamed");
        const string camera="Gaming PC Camera";Require(DiscordDevices.CaptureName(camera).Contains(camera,StringComparison.Ordinal),"Chromium group label mismatch");
        Require(DiscordDevices.PairedName(camera,new("rode",DataFlow.Capture,"RØDECaster Duo Main","RØDE"))==camera+" Audio","Mixer endpoint naming mismatch");
        var options=new DiscordOptions{AudioMode="Local",CaptureDevice="rode",AudioDevice="send"};options.Validate();Require(options.AudioMode=="Local","Direct mixer selection lost");
        Require(!DiscordDevices.IsCable(new("fake",DataFlow.Render,"CABLE custom mic","USB Audio")),"Unrelated device matched by friendly name");
        bool host=false;try{DiscordDevices.PairAudio("Host","send",camera);}catch(InvalidOperationException){host=true;}Require(host,"Host device mutation allowed");
    }
    public static void Run()
    {
        Rules();var devices=DiscordDevices.Endpoints();
        // Read-only ABI verification on the actual OS. Never install a cable or change gaming-PC audio.
        using(var policy=new AudioPolicy())foreach(var device in devices)Require(policy.ReadName(device.Id)==device.Name,"PolicyConfig property ABI mismatch");
        Program.Write("discord-device-test.json",new{Pass=true,DefaultRoles=6,RenamedEndpointDetection=true,AmbiguousPairRejected=true,DefaultDevicesChanged=false,RealPolicyReads=devices.Count});
    }
}
