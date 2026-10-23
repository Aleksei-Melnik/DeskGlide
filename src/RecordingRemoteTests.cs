using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace SdrCapture;

static class RecordingRemoteTests
{
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static string Id()=>Guid.NewGuid().ToString("N");
    public static void Run()=>RunAsync().GetAwaiter().GetResult();
    static async Task RunAsync()
    {
        Protocol();await Cache();await Limits();await Correlation();await Transport();
        using var service=new KvmService(new(){Role="Off"});
        using var remote=new RecordingRemote(service,(_,_)=>throw new InvalidOperationException("Unpaired handler ran."));
        Require((await remote.ExecuteAsync(Id(),new(Id(),"status"))).Code=="offline","Unknown peer was accepted.");
        Require((await remote.ExecuteAsync("bad",new(Id(),"status"))).Code=="invalid","Malformed peer ID was accepted.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Require((await remote.ExecuteAsync(Id(),new(Id(),"status"),cancelled.Token)).Code=="cancelled","Already cancelled command was sent.");
        Program.Write("recording-remote-tests.json",new{Pass=true,DuplicatePendingAndCompleted=true,PayloadConflictRejected=true,
            HistoryBounded=true,RetainedAcrossTlsReconnect=true,TimeoutReconciliation=true,ActiveLimitAfterTimeout=true,
            PairedTlsRoundtrip=true,UnpairedDenied=true,ControlChannelOnly=true,ReceiverOwnsPaths=true,
            ReplyPeerAndIdMatched=true,DisconnectUnknownWithoutRetry=true,NoRecorderOrInputStarted=true});
    }
    static void Protocol()
    {
        string session=Id();var profile=new RecordingProfile();
        foreach(string command in new[]{"status","capabilities"})Require(RecordingProtocol.Valid(new RecordingCommand(Id(),command)),"Read-only recording command rejected.");
        foreach(string command in new[]{"configureReplay","startRecording"})Require(RecordingProtocol.Valid(new RecordingCommand(Id(),command,session,"Gaming PC (Desktop)",profile)),"Typed encoding command rejected.");
        foreach(string command in new[]{"stopRecording","saveReplay","disableReplay","release"})Require(RecordingProtocol.Valid(new RecordingCommand(Id(),command,session)),"Session command rejected.");
        Require(RecordingProtocol.Valid(new RecordingCommand(Id(),"capabilities",Profile:profile)),"Capability profile rejected.");
        var start=new RecordingCommand(Id(),"startRecording",session,"Gaming PC (Desktop)",profile,"Counter-Strike 2");
        Require(RecordingProtocol.Valid(start),"Safe source application rejected.");
        foreach(var invalid in new[]{start with{Id="bad"},start with{Session=null},start with{Session="bad"},start with{Command="exec"},
            start with{Source="bad\nsource"},start with{Source=new string('x',513)},start with{Profile=null},start with{App="../clips"},
            start with{App="C:\\clips"},start with{App=".."},start with{App="bad\napp"},start with{Profile=profile with{DurationSeconds=30}},
            start with{Profile=profile with{Fps=240}},start with{Profile=profile with{Height=1080}},start with{Profile=profile with{EncoderPreset="p1 -i evil"}}})
            Require(!RecordingProtocol.Valid(invalid),"Invalid command/profile accepted: "+invalid.Command);
        foreach(var dimensions in new[]{profile with{SourceWidth=2560},profile with{SourceWidth=2561,SourceHeight=1440},profile with{SourceWidth=16384,SourceHeight=16384}})
            Require(!RecordingProtocol.Valid(start with{Profile=dimensions}),"Unbounded or incomplete source dimensions were accepted.");
        var hinted=profile with{SourceWidth=2560,SourceHeight=1440};
        Require(RecordingProtocol.Valid(start with{Profile=hinted})&&hinted.ToReplay(new()).Width==0,"Native source hints replaced the selected output dimensions.");
        var cpu=RecordingProfile.FromReplay(new(){Encoder="CPU",Codec="H264"});
        Require(cpu.Encoder=="CPU"&&cpu.ToReplay(new()).Encoder=="CPU"&&RecordingProtocol.Valid(start with{Profile=cpu}),"Remote profile lost the explicit CPU backend.");
        Require(!RecordingProtocol.Valid(start with{Profile=cpu with{Encoder="exec"}})&&!RecordingProtocol.Valid(start with{Profile=cpu with{Codec="AV1"}}),"Remote profile accepted an arbitrary backend or an unsupported CPU codec.");
        foreach(var malformed in new[]{profile with{Codec=null!},profile with{Quality=null!},profile with{RateControl=null!},profile with{EncoderPreset=null!},profile with{Encoder=null!}})
            Require(!RecordingProtocol.Valid(start with{Profile=malformed}),"A null profile field was accepted or threw out of validation.");
        Require(!RecordingProtocol.Valid((RecordingCommand)null!)&&!RecordingProtocol.Valid((RecordingResponse)null!),"A null protocol object passed validation.");
        var fast=new RecordingResponse(Id(),true,State:new(RequiredFps:120));
        Require(JsonSerializer.Deserialize<RecordingResponse>(RecordingProtocol.Serialize(fast),RecordingProtocol.Json)?.State?.RequiredFps==120,"Recording status lost its required source frame rate.");
        Require(!RecordingProtocol.Valid(fast with{State=fast.State! with{RequiredFps=240}}),"Recording status accepted an unbounded source frame rate.");
        bool nullLocalRejected=false;try{profile.ToReplay(new(){Microphone=null!});}catch(ArgumentException){nullLocalRejected=true;}
        Require(nullLocalRejected,"A malformed local audio identifier threw an unchecked null reference.");
        var envelope=new KvmMessage{Type="recording-request",Id=start.Id,Text=JsonSerializer.Serialize(start,RecordingProtocol.Json)};
        Require(RecordingProtocol.Envelope("control",envelope),"Control recording envelope rejected.");
        foreach(string channel in new[]{"bulk","video","audio"})Require(!RecordingProtocol.Envelope(channel,envelope),"Recording envelope accepted on "+channel);
        Require(!RecordingProtocol.Envelope("control",envelope with{Text=new string('я',9000)}),"UTF-8 byte bound was bypassed.");
        Require(!RecordingProtocol.Envelope("control",envelope with{Type="recording-exec"}),"Unlisted recording envelope accepted.");
        bool unknownRejected=false;
        try{JsonSerializer.Deserialize<RecordingCommand>("{\"id\":\""+Id()+"\",\"command\":\"status\",\"folder\":\"C:\\\\clips\"}",RecordingProtocol.Json);}catch(JsonException){unknownRejected=true;}
        Require(unknownRejected,"Arbitrary wire properties were ignored.");
        var local=new ReplayOptions{Folder=Path.Combine(Path.GetTempPath(),"Receiver clips"),Microphone="receiver-mic",GameAudio="receiver-game",ExtraAudio="receiver-extra",HotkeyKey=123};
        var converted=profile.ToReplay(local);
        Require(converted.Folder==local.Folder&&converted.Microphone==local.Microphone&&converted.GameAudio==local.GameAudio&&converted.ExtraAudio==local.ExtraAudio&&converted.HotkeyKey==local.HotkeyKey,"Wire profile replaced receiver paths, devices or hotkeys.");
        Require(RecordingProtocol.Timeout("status")==TimeSpan.FromSeconds(8)&&RecordingProtocol.Timeout("capabilities")==TimeSpan.FromSeconds(120)&&RecordingProtocol.Timeout("startRecording")==TimeSpan.FromSeconds(30)&&RecordingProtocol.Timeout("saveReplay")==TimeSpan.FromSeconds(600),"Command deadlines changed.");
    }
    static async Task Cache()
    {
        int calls=0;string peer=Id();var command=new RecordingCommand(Id(),"status");
        var completion=new TaskCompletionSource<RecordingResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cache=new RecordingRequestCache((_,r)=>{Interlocked.Increment(ref calls);return completion.Task;});
        var first=cache.ExecuteAsync(peer,command);var repeated=cache.ExecuteAsync(peer,command);
        Require(ReferenceEquals(first,repeated),"Retransmitted requests must share the pending result.");
        Require((await cache.ExecuteAsync(peer,command with{Command="capabilities"})).Code=="conflict","Changed payload reused a pending request ID.");
        completion.SetResult(new(command.Id,true));await first;
        Require(calls==1,"Duplicate requests executed more than once.");
        Require(ReferenceEquals(first,cache.ExecuteAsync(peer,command))&&(await cache.ExecuteAsync(peer,command)).Ok,"Completed reply was not retained.");
        Require((await cache.ExecuteAsync(peer,command with{Command="capabilities"})).Code=="conflict"&&calls==1,"Completed request ID was reused with another payload.");
        Require(ReferenceEquals(first,cache.ExecuteAsync(peer.ToUpperInvariant(),command with{Id=command.Id.ToUpperInvariant()})),"Case variants bypassed GUID deduplication.");
        using var failing=new RecordingRequestCache((_,_)=>throw new IOException("Secret local path C:\\private"));
        var failure=await failing.ExecuteAsync(peer,new(Id(),"status"));
        Require(failure.Code=="failed"&&!failure.Message.Contains("private"),"Handler exception leaked local details.");
        using var stopped=new RecordingRequestCache((_,_)=>new TaskCompletionSource<RecordingResponse>().Task);
        var pending=stopped.ExecuteAsync(peer,new(Id(),"status"));stopped.Dispose();
        Require((await pending.WaitAsync(TimeSpan.FromSeconds(1))).Code=="unknown","Dispose left a pending response hanging.");
    }
    static async Task Limits()
    {
        string peer=Id();var now=DateTimeOffset.UtcNow;int executions=0;
        using var cache=new RecordingRequestCache((_,r)=>{Interlocked.Increment(ref executions);return Task.FromResult(new RecordingResponse(r.Id,true));},()=>now,capacity:3);
        for(int i=0;i<3;i++)Require((await cache.ExecuteAsync(peer,new(Id(),"status"))).Ok,"History fixture command failed.");
        Require((await cache.ExecuteAsync(peer,new(Id(),"status"))).Code=="busy"&&cache.Count==3&&executions==3,"Request history grew past its cap.");
        await Task.Delay(20);now=now.AddMinutes(16);
        Require((await cache.ExecuteAsync(peer,new(Id(),"status"))).Ok&&cache.Count==1,"Expired completed history was not evicted.");
        int active=0;var operations=new List<TaskCompletionSource<RecordingResponse>>();var ids=new List<string>();
        using var bounded=new RecordingRequestCache((_,r)=>{Interlocked.Increment(ref active);lock(operations){var t=new TaskCompletionSource<RecordingResponse>(TaskCreationOptions.RunContinuationsAsynchronously);operations.Add(t);ids.Add(r.Id);return t.Task;}},timeout:TimeSpan.FromMilliseconds(100));
        var requests=Enumerable.Range(0,4).Select(_=>new RecordingCommand(Id(),"status")).ToArray();
        var pending=requests.Select(r=>bounded.ExecuteAsync(peer,r)).ToArray();
        var results=await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3));
        Require(results.All(r=>r.Code=="unknown")&&active==4,"Unsettled command timeout was not reported as unknown.");
        Require((await bounded.ExecuteAsync(peer,new(Id(),"status"))).Code=="busy"&&active==4,"Timed-out handlers released their concurrency slots too early.");
        Require(ReferenceEquals(pending[0],bounded.ExecuteAsync(peer,requests[0]))&&bounded.Count==5,"Timed-out request was repeated.");
        lock(operations)for(int i=0;i<operations.Count;i++)operations[i].SetResult(new(ids[i],true));
        await Task.Delay(20);
    }
    static async Task Transport()
    {
        string folder=Path.Combine(Path.GetTempPath(),"DeskGlide-Recording-Rpc-"+Id());Directory.CreateDirectory(folder);
        try
        {
            using var identity=new KvmIdentity(folder);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));var token=deadline.Token;
            var hostOptions=new KvmOptions{Role="Host",Id=Id()};var clientOptions=new KvmOptions{Role="Client",Id=Id(),Host="127.0.0.1",PairingCode=identity.Code};
            int calls=0;string peer="";
            using var cache=new RecordingRequestCache((p,r)=>{peer=p;Interlocked.Increment(ref calls);return Task.FromResult(new RecordingResponse(r.Id,true,State:new(ReplayEnabled:true,BufferedSeconds:42,RequiredFps:120)));});
            var request=new RecordingCommand(Id(),"status");
            for(int connection=0;connection<2;connection++)
            {
                using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();clientOptions.Port=((IPEndPoint)listener.LocalEndpoint).Port;
                var connecting=KvmWire.Connect(clientOptions,"control",token);
                var accepting=KvmWire.Accept(await listener.AcceptTcpClientAsync(token),identity,hostOptions,token);
                using var client=await connecting;using var server=await accepting;
                await client.SendAsync(new(){Type="recording-request",Id=request.Id,Text=JsonSerializer.Serialize(request,RecordingProtocol.Json)},token);
                var incoming=await server.ReadAsync(token);Require(RecordingProtocol.Envelope(server.Channel,incoming),"TLS request did not pass production control validation.");
                var decoded=JsonSerializer.Deserialize<RecordingCommand>(incoming.Text,RecordingProtocol.Json)!;
                var response=await cache.ExecuteAsync(server.Peer.Id,decoded);
                await server.SendAsync(new(){Type="recording-reply",Id=response.Id,Text=RecordingProtocol.Serialize(response)},token);
                var received=await client.ReadAsync(token);var reply=JsonSerializer.Deserialize<RecordingResponse>(received.Text,RecordingProtocol.Json)!;
                Require(reply.Ok&&reply.Id==request.Id&&reply.State?.BufferedSeconds==42&&reply.State.RequiredFps==120,"Paired TLS recording response or required source rate failed.");
            }
            Require(calls==1&&peer==clientOptions.Id,"Reconnect repeated a recording command or lost authenticated peer identity.");
            using var rejectedListener=new TcpListener(IPAddress.Loopback,0);rejectedListener.Start();clientOptions.Port=((IPEndPoint)rejectedListener.LocalEndpoint).Port;
            clientOptions.PairingCode=KvmPairing.Encode(SHA256.HashData(identity.Certificate.RawData),RandomNumberGenerator.GetBytes(32));
            var rejectedConnect=KvmWire.Connect(clientOptions,"control",token);
            var rejectedAccept=KvmWire.Accept(await rejectedListener.AcceptTcpClientAsync(token),identity,hostOptions,token);
            bool rejected=false;try{await Task.WhenAll(rejectedConnect,rejectedAccept);}catch(Exception){rejected=true;}
            Require(rejected&&!rejectedAccept.IsCompletedSuccessfully&&calls==1,"Unpaired TLS identity reached recording dispatch.");
        }
        finally{Directory.Delete(folder,true);}
    }
    static async Task Correlation()
    {
        string firstPeer=Id(),otherPeer=Id();var peers=new HashSet<string>{firstPeer,otherPeer};
        var sent=new List<(string Peer,KvmMessage Message)>();int calls=0;
        var execution=new TaskCompletionSource<RecordingResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var remote=new RecordingRemote(peers.Contains,(peer,message)=>{lock(sent)sent.Add((peer,message));return true;},(_,_)=>{Interlocked.Increment(ref calls);return execution.Task;});
        var request=new RecordingCommand(Id(),"status");var pending=remote.ExecuteAsync(firstPeer,request);
        KvmMessage Reply(string id)=>new(){Type="recording-reply",Id=id,Text=RecordingProtocol.Serialize(new(id,true))};
        remote.Receive(otherPeer,Reply(request.Id));remote.Receive(firstPeer,Reply(Id()));
        await Task.Delay(20);Require(!pending.IsCompleted,"Reply from another peer or another request completed a pending request.");
        remote.Receive(firstPeer,Reply(request.Id));Require((await pending.WaitAsync(TimeSpan.FromSeconds(1))).Ok,"Matched peer reply was lost.");
        int sends;lock(sent)sends=sent.Count;
        var next=new RecordingCommand(Id(),"status");var disconnected=remote.ExecuteAsync(firstPeer,next);
        peers.Remove(firstPeer);remote.Disconnected(firstPeer);
        Require((await disconnected.WaitAsync(TimeSpan.FromSeconds(1))).Code=="unknown","Disconnect lost an uncertain command outcome.");
        peers.Add(firstPeer);await Task.Delay(20);
        lock(sent)Require(sent.Count==sends+1,"Reconnect automatically repeated an uncertain command.");
        var incoming=new RecordingCommand(Id(),"startRecording",Id(),"Gaming PC (Desktop)",new());
        var message=new KvmMessage{Type="recording-request",Id=incoming.Id,Text=JsonSerializer.Serialize(incoming,RecordingProtocol.Json)};
        remote.Receive(Id(),message);await Task.Delay(20);Require(calls==0,"Unpaired peer reached the recording handler.");
        foreach(string malformed in new[]{"null",JsonSerializer.Serialize(incoming with{Profile=null},RecordingProtocol.Json),
            JsonSerializer.Serialize(incoming with{Profile=new(Codec:null!)},RecordingProtocol.Json),JsonSerializer.Serialize(incoming with{Profile=new(Encoder:null!)},RecordingProtocol.Json),
            "{\"id\":\""+incoming.Id+"\",\"command\":\"startRecording\",\"profile\":{\"microphone\":null}}"})
            remote.Receive(firstPeer,message with{Text=malformed});
        await Task.Delay(20);Require(calls==0,"Malformed paired JSON invoked a recording handler or escaped receive validation.");
        remote.Receive(firstPeer,message);remote.Receive(firstPeer,message);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while(calls==0)await Task.Delay(5,deadline.Token);
        Require(calls==1,"A pending recording request was executed twice.");
        remote.Receive(firstPeer,message with{Text=JsonSerializer.Serialize(incoming with{Command="configureReplay",App=null},RecordingProtocol.Json)});
        await Task.Delay(20);
        lock(sent)Require(sent.Any(s=>s.Message.Type=="recording-reply"&&JsonSerializer.Deserialize<RecordingResponse>(s.Message.Text,RecordingProtocol.Json)?.Code=="conflict"),"A conflicting payload was hidden behind the original pending response.");
        peers.Remove(firstPeer);remote.Disconnected(firstPeer);execution.SetResult(new(incoming.Id,true));
        await Task.Delay(20);peers.Add(firstPeer);remote.Receive(firstPeer,message);await Task.Delay(20);
        Require(calls==1,"A command retransmitted after disconnect was executed again.");
        lock(sent)Require(sent.Any(s=>s.Peer==firstPeer&&s.Message.Type=="recording-reply"&&s.Message.Id==incoming.Id),"Cached completion was not returned after reconnect.");
        var disposed=remote.ExecuteAsync(firstPeer,new(Id(),"status"));remote.Dispose();
        Require((await disposed.WaitAsync(TimeSpan.FromSeconds(1))).Code=="unknown","Disposing client left a pending request hanging.");
    }
}
