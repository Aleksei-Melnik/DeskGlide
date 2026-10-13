using System.Text.RegularExpressions;

namespace SdrCapture;

enum PeerUpdateStage { Preparing, Progress, Prepared, Current, Restarting, Updated, AwaitingReconnect, Failed }
sealed record PeerUpdateState(string Request,string Target,PeerUpdateStage Stage,string Detail="");

// Outcomes belong to one release and one request, never to the lifetime of the app.
sealed class UpdatePeerStates
{
    readonly Dictionary<string,PeerUpdateState> states=[];
    public string Target {get;private set;}="";
    public void SetTarget(string version)
    {
        if(Target==version)return;
        Target=version;states.Clear();
    }
    public void Begin(string peer,string request)=>states[peer]=new(request,Target,PeerUpdateStage.Preparing);
    public void Set(string peer,string request,PeerUpdateStage stage,string detail="")
    {
        if(states.TryGetValue(peer,out var state)&&state.Request==request&&state.Target==Target)
            states[peer]=state with{Stage=stage,Detail=detail};
    }
    public bool Progress(string peer,KvmMessage message)
    {
        if(!states.TryGetValue(peer,out var state)||state.Request!=message.Id||state.Target!=Target||
            (message.UpdateTarget.Length>0&&message.UpdateTarget!=Target)||
            state.Stage is not (PeerUpdateStage.Preparing or PeerUpdateStage.Progress))return false;
        states[peer]=state with{Stage=PeerUpdateStage.Progress,Detail=message.Text};return true;
    }
    public KvmMessage Prepared(string peer,string request,KvmMessage result)
    {
        if(result.Code==2&&!AtLeast(result.Version,Target))
            result=result with{Code=0,Text="This PC reported an older installed version. Check for updates again."};
        Set(peer,request,result.Code==2?PeerUpdateStage.Current:result.Code==1?PeerUpdateStage.Prepared:PeerUpdateStage.Failed,result.Text);
        return result;
    }
    internal static bool AtLeast(string installed,string target)=>Version.TryParse(installed,out var version)&&Version.TryParse(target,out var latest)&&version>=latest;
    public string Describe(KvmPeerInfo peer)
    {
        string version=peer.Version.Length==0?"0.5.x":peer.Version;
        string status=peer.UpdateProtocol<1?UiStrings.T("Needs a manual update to 0.6 or later"):
            Target.Length==0?UiStrings.T("Ready for updates"):
            AtLeast(version,Target)?UiStrings.F("Current version: {0}",version):UiStrings.F("Update available: {0}",Target);
        if(peer.UpdateProtocol>=1&&!AtLeast(version,Target)&&states.TryGetValue(peer.Id,out var state)&&state.Target==Target)
        {
            status=state.Stage switch
            {
                PeerUpdateStage.Preparing=>UiStrings.T("Preparing update…"),
                PeerUpdateStage.Progress=>UpdateStatusText.Translate(state.Detail),
                PeerUpdateStage.Prepared=>UiStrings.T("Package downloaded and verified"),
                PeerUpdateStage.Restarting=>UiStrings.T("Restarting…"),
                PeerUpdateStage.AwaitingReconnect=>UiStrings.T("Waiting for this PC to reconnect…"),
                PeerUpdateStage.Failed=>UiStrings.F("Update failed: {0}",UpdateStatusText.Translate(state.Detail)),
                // A terminal result must still agree with the connected peer's version.
                _=>status
            };
        }
        return $"{peer.Name}  ·  {version}  ·  {status}";
    }
}

static class UpdateStatusText
{
    public static string Translate(string text)
    {
        var download=Regex.Match(text,@"^(?:Скачивание|Downloading) ([0-9.]+): ([0-9]+)%$");
        if(download.Success)return UiStrings.F("Downloading {0}: {1}%",download.Groups[1].Value,download.Groups[2].Value);
        var installed=Regex.Match(text,@"^(?:Уже установлена|Already installed) ([0-9.]+)$");
        if(installed.Success)return UiStrings.F("Current version: {0}",installed.Groups[1].Value);
        foreach(string key in new[]{"Version {0} is available","Current version: {0}","Update available: {0}"})
        {
            // Status may have been formatted before the UI language changed.
            foreach(string template in new[]{key,UiStrings.InRussian(key)})
            {
                var match=Regex.Match(text,"^"+Regex.Escape(template).Replace(Regex.Escape("{0}"),"([0-9.]+)")+"$");
                if(match.Success)return UiStrings.F(key,match.Groups[1].Value);
            }
        }
        return UiStrings.T(text);
    }
}
