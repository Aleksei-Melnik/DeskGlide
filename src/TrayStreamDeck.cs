namespace SdrCapture;

sealed partial class TrayApp
{
    StreamDeckBridge? deckBridge;
    DeckRemote? deckRemote;
    DeckState? deckHost;
    bool deckPolling,savingReplay;
    string deckSaveState="idle";
    long deckSaveRevision;
    readonly HashSet<string> pendingDeckSaves=[];
    readonly HashSet<string> deliveredDeckSaves=[];
    void StartDeckBridge()
    {
        deckBridge=new(ExecuteDeck);PublishDeckState();
    }
    DeckState DeckLocalState()=>new(Updates.VersionText,Environment.MachineName,settings.Kvm.Role,settings.Replay.Enabled,
        Math.Round(replay.DisplayBufferedSeconds,2),settings.Replay.BufferSeconds,engine.NdiEnabled,controller?.Seamless??false,
        replay.Error,savingReplay,deckSaveState,deckSaveRevision,(kvm?.Peers??[]).Select(p=>new DeckPeer(p.Id,p.Name,p.Version,p.DeckProtocol>=1)).ToArray(),
        settings.Kvm.Role=="Host"?(controller?.Monitors??[]).Select(m=>new DeckMonitor(m.Key,m.Name,m.Width,m.Height,m.Key==controller?.ActiveMonitorKey)).ToArray():[],replay.BufferAdvancing);
    void PublishDeckState()
    {
        if(closing)return;
        if(settings.Kvm.Role!="Client"||kvm?.Peers.Length!=1)deckHost=null;
        deckBridge?.Publish(new(DeckLocalState(),deckHost));
        if(deckBridge?.Clients>0&&settings.Kvm.Role=="Client"&&!deckPolling&&deckRemote!=null)_=PollDeckHost();
    }
    async Task PollDeckHost()
    {
        deckPolling=true;
        try{var response=await deckRemote!.Execute(new(Guid.NewGuid().ToString("N"),"status","host"));deckHost=response.Ok?response.State?.Local:null;}
        catch{deckHost=null;}
        finally{deckPolling=false;}
    }
    async Task<DeckReply> ExecuteDeck(DeckRequest r)
    {
        if(r.Target!="host"||settings.Kvm.Role=="Host")return await ExecuteDeckLocal(r);
        if(settings.Kvm.Role!="Client"||deckRemote==null)return new(r.Id,false,"offline","Pair this PC with a controller in DeskGlide first.");
        var reply=await deckRemote.Execute(r);
        var done=new TaskCompletionSource<DeckReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        try{dispatcher.BeginInvoke(()=>{deckHost=reply.State?.Local;PublishDeckState();done.TrySetResult(reply with{State=new(DeckLocalState(),deckHost)});});}
        catch(Exception e){done.TrySetResult(new(r.Id,false,"closing",e.Message));}
        return await done.Task;
    }
    Task<DeckReply> ExecuteDeckLocal(DeckRequest r)
    {
        var done=new TaskCompletionSource<DeckReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(closing){done.SetResult(new(r.Id,false,"closing","DeskGlide is closing."));return done.Task;}
        try
        {
            dispatcher.BeginInvoke(async()=>
            {
                try
                {
                    if(closing)throw new InvalidOperationException("DeskGlide is closing.");
                    if(updater.Busy&&r.Command!="status")throw new InvalidOperationException("An update is in progress.");
                    switch(r.Command)
                    {
                        case "status":break;
                        case "saveReplay":await SaveDeckReplay();break;
                        case "toggleReplay":ApplyReplay(settings.Replay with{Enabled=!settings.Replay.Enabled});break;
                        case "toggleStreaming":settings.SendOnLaunch=!settings.SendOnLaunch;settings.Save();engine.NdiEnabled=settings.SendOnLaunch;break;
                        case "toggleEdges":
                            if(settings.Kvm.Role!="Host"||controller==null)throw new InvalidOperationException("Set up this PC as a KVM controller first.");
                            controller.Seamless=!controller.Seamless;settings.Kvm.Seamless=controller.Seamless;
                            if(!controller.Seamless)controller.ReturnLocal();settings.Save();break;
                        case "switchMonitor":
                            if(settings.Kvm.Role!="Host"||controller==null)throw new InvalidOperationException("Set up the KVM controller first.");
                            var monitor=controller.Monitors.FirstOrDefault(m=>m.Key==r.Monitor)??throw new IOException("The selected monitor is disconnected or excluded from the layout.");
                            controller.Activate(monitor);break;
                        case "openKvm":
                            if(settings.Kvm.Role!="Host")throw new InvalidOperationException("Open KVM on the controller PC.");
                            if(r.Peer=="")OpenKvm();
                            else
                            {
                                var peer=kvm?.Peers.FirstOrDefault(p=>p.Id==r.Peer)??throw new IOException("The selected PC is disconnected.");
                                OpenViewer(peer);
                            }
                            break;
                        default:throw new InvalidOperationException("Unsupported command.");
                    }
                    PublishDeckState();done.TrySetResult(new(r.Id,true,State:new(DeckLocalState(),deckHost)));
                }
                catch(Exception e){done.TrySetResult(new(r.Id,false,"failed",e.Message,new(DeckLocalState(),deckHost)));}
            });
        }
        catch(Exception e){done.TrySetResult(new(r.Id,false,"closing",e.Message));}
        return done.Task;
    }
    async Task SaveDeckReplay()
    {
        if(!settings.Replay.Enabled)throw new InvalidOperationException(UiStrings.T("Instant replay is off."));
        if(savingReplay)throw new InvalidOperationException(UiStrings.T("A replay is already being saved."));
        savingReplay=true;deckSaveState="saving";PublishDeckState();
        try
        {
            ReplayAppContext.Capture();notifications.Enqueue(UiStrings.F("Saving the last {0}…",ReplayTime.Format(settings.Replay.BufferSeconds)));
            var result=await replay.SaveAsync();
            // Delivery may finish before the export continuation returns to the UI.
            if(deliveredDeckSaves.Remove(result.LocalPath)){deckSaveState="saved";deckSaveRevision++;}
            else{pendingDeckSaves.Add(result.LocalPath);deckSaveState="queued";}
            delivery.Wake();
        }
        catch{deckSaveState="error";throw;}
        finally{savingReplay=false;PublishDeckState();}
    }
    void ReplayDelivered(DeliveryJob job)
    {
        if(pendingDeckSaves.Remove(job.Source)){deckSaveState=pendingDeckSaves.Count>0?"queued":"saved";deckSaveRevision++;PublishDeckState();}
        else if(savingReplay)deliveredDeckSaves.Add(job.Source);
    }
}
