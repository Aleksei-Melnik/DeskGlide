using System.Collections.Concurrent;
using System.Text.Json;

namespace SdrCapture;

// Uses the existing paired, TLS-authenticated control channel. A client can
// control replay on its host, but cannot open host windows or route to another client.
sealed class DeckRemote:IDisposable
{
    readonly KvmService service;
    readonly Func<DeckRequest,Task<DeckReply>> execute;
    readonly ConcurrentDictionary<(string Peer,string Id),TaskCompletionSource<DeckReply>> pending=new();
    readonly ConcurrentDictionary<(string Peer,string Id),DateTime> received=new();
    readonly CancellationTokenSource stop=new();
    int active;
    public DeckRemote(KvmService service,Func<DeckRequest,Task<DeckReply>> execute)
    {
        this.service=service;this.execute=execute;service.Received+=Receive;service.Disconnected+=Disconnected;
    }
    public async Task<DeckReply> Execute(DeckRequest request)
    {
        if(service.Options.Role!="Client"||!DeckProtocol.RemoteAllowed(request))return new(request.Id,false,"unsupported","This action is only available on the local controller PC.");
        var host=service.Peers.SingleOrDefault();
        if(host==null)return new(request.Id,false,"offline","The controller PC is disconnected.");
        if(host.DeckProtocol<1)return new(request.Id,false,"upgrade","Update DeskGlide on the controller PC to 0.7.19 or later.");
        var key=(host.Id,request.Id);var done=new TaskCompletionSource<DeckReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!pending.TryAdd(key,done))return new(request.Id,false,"duplicate","This command was already sent.");
        try
        {
            await service.SendControl(host.Id,new(){Type="deck-request",Id=request.Id,Text=JsonSerializer.Serialize(request,DeckProtocol.Json)});
            return await done.Task.WaitAsync(DeckProtocol.Timeout(request.Command),stop.Token);
        }
        catch(TimeoutException){return new(request.Id,false,"unknown","No confirmation received. The command is not retried automatically.");}
        catch(Exception e){return new(request.Id,false,"offline",e.Message);}
        finally{pending.TryRemove(key,out _);}
    }
    void Disconnected(string peer)
    {
        foreach(var entry in pending.Where(p=>p.Key.Peer==peer))entry.Value.TrySetException(new IOException("Controller PC disconnected. The command is not retried."));
        foreach(var key in received.Keys.Where(k=>k.Peer==peer))received.TryRemove(key,out _);
    }
    void Receive(string peer,KvmMessage message)
    {
        if(stop.IsCancellationRequested||message.Text.Length>(message.Type=="deck-reply"?65536:DeckProtocol.MaxLine)||!Guid.TryParseExact(message.Id,"N",out _))return;
        try
        {
            if(message.Type=="deck-reply"&&service.Options.Role=="Client")
            {
                var reply=JsonSerializer.Deserialize<DeckReply>(message.Text,DeckProtocol.Json);
                if(reply?.Id==message.Id&&pending.TryGetValue((peer,message.Id),out var done))done.TrySetResult(reply);
            }
            else if(message.Type=="deck-request"&&service.Options.Role=="Host")
            {
                var request=JsonSerializer.Deserialize<DeckRequest>(message.Text,DeckProtocol.Json);
                if(request==null||request.Id!=message.Id||!DeckProtocol.RemoteAllowed(request))return;
                foreach(var item in received.Where(p=>p.Value<DateTime.UtcNow.AddMinutes(-15)))received.TryRemove(item.Key,out _);
                if(received.Count>2048||!received.TryAdd((peer,request.Id),DateTime.UtcNow)){Reply(peer,new(request.Id,false,"duplicate","This command was already received."));return;}
                if(Interlocked.Increment(ref active)>4){Interlocked.Decrement(ref active);Reply(peer,new(request.Id,false,"busy","Too many pending commands."));return;}
                _=Run(peer,request);
            }
        }
        catch(JsonException){/* Ignore malformed requests on the authenticated channel. */}
    }
    async Task Run(string peer,DeckRequest request)
    {
        try{Reply(peer,await execute(request with{Target="local"}).WaitAsync(stop.Token));}
        catch(Exception e){Reply(peer,new(request.Id,false,"failed",e.Message));}
        finally{Interlocked.Decrement(ref active);}
    }
    void Reply(string peer,DeckReply reply){if(!stop.IsCancellationRequested)service.Send(peer,new(){Type="deck-reply",Id=reply.Id,Text=JsonSerializer.Serialize(reply,DeckProtocol.Json)});}
    public void Dispose(){stop.Cancel();service.Received-=Receive;service.Disconnected-=Disconnected;foreach(var done in pending.Values)done.TrySetCanceled();}
}
