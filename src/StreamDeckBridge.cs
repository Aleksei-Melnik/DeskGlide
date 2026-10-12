using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SdrCapture;

sealed record DeckPeer(string Id,string Name,string Version,bool Compatible);
sealed record DeckMonitor(string Id,string Name,int Width,int Height,bool Active);
sealed record DeckState(string Version,string Name,string Role,bool ReplayEnabled,double BufferedSeconds,int DurationSeconds,
    bool Streaming,bool Seamless,string? ReplayError,bool Saving,string SaveState,long SaveRevision,DeckPeer[] Peers,
    DeckMonitor[]? Monitors=null,bool BufferAdvancing=false);
sealed record DeckSnapshot(DeckState Local,DeckState? Host=null);
sealed record DeckRequest(string Id,string Command,string Target="local",string Peer="",string Monitor="");
sealed record DeckReply(string Id,bool Ok,string Code="",string Message="",DeckSnapshot? State=null);

static class DeckProtocol
{
    public const int MaxLine=8192;
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static bool Valid(DeckRequest r)=>Guid.TryParseExact(r.Id,"N",out _)&&r.Target is "local" or "host"&&
        r.Command is "status" or "saveReplay" or "openKvm" or "switchMonitor" or "toggleEdges" or "toggleReplay" or "toggleStreaming"&&
        (r.Peer==""||Guid.TryParseExact(r.Peer,"N",out _))&&(r.Command=="openKvm"||r.Peer=="")&&
        r.Monitor!=null&&(r.Command=="switchMonitor"?r.Monitor.Length is >0 and <=256&&!r.Monitor.Any(char.IsControl):r.Monitor=="");
    public static bool RemoteAllowed(DeckRequest r)=>Valid(r)&&r.Command is not "openKvm";
    public static async Task<string?> ReadLine(Stream stream,CancellationToken token)
    {
        using var buffer=new MemoryStream();byte[] one=new byte[1];
        while(await stream.ReadAsync(one,token)>0)
        {
            if(one[0]==10)return Encoding.UTF8.GetString(buffer.ToArray());
            if(buffer.Length>=MaxLine)throw new IOException("Stream Deck request is too large.");
            buffer.WriteByte(one[0]);
        }
        if(buffer.Length>0)throw new IOException("Incomplete Stream Deck request.");
        return null;
    }
}

// Same Windows user only. No TCP listener, password, shell command or keyboard injection.
sealed class StreamDeckBridge:IDisposable
{
    readonly CancellationTokenSource stop=new();
    readonly Func<DeckRequest,Task<DeckReply>> execute;
    readonly string descriptor,pipeName;
    readonly Task worker;
    DeckSnapshot? snapshot;
    int clients;
    public int Clients=>Volatile.Read(ref clients);
    public void Publish(DeckSnapshot value)=>Volatile.Write(ref snapshot,value);
    public static string Descriptor=>Path.Combine(Log.Folder,"streamdeck-bridge.json");
    public StreamDeckBridge(Func<DeckRequest,Task<DeckReply>> execute,string? descriptor=null,string? pipeName=null)
    {
        this.execute=execute;this.descriptor=descriptor??Descriptor;
        this.pipeName=pipeName??$"DeskGlide.StreamDeck.{WindowsIdentity.GetCurrent().User!.Value}.{Environment.ProcessId}";
        worker=Task.Run(Listen);
    }
    async Task Listen()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                try
                {
                    // Write discovery only after a private server has been created successfully.
                    Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
                    string temp=descriptor+".tmp";
                    File.WriteAllText(temp,JsonSerializer.Serialize(new{protocol=1,pipe=pipeName},DeckProtocol.Json));File.Move(temp,descriptor,true);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    Interlocked.Increment(ref clients);_=Serve(pipe);
                    while(Clients>=3)await Task.Delay(250,stop.Token);
                }
                catch{pipe.Dispose();throw;}
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){Log.Write("Stream Deck bridge: "+e.Message);}
    }
    async Task Serve(NamedPipeServerStream pipe)
    {
        using var ownedPipe=pipe;
        using var ended=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        using var write=new SemaphoreSlim(1);
        async Task Send(object message)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message,DeckProtocol.Json)+"\n");
            await write.WaitAsync(ended.Token);
            try{await pipe.WriteAsync(bytes,ended.Token);await pipe.FlushAsync(ended.Token);}finally{write.Release();}
        }
        var publish=Task.Run(async()=>
        {
            try
            {
                while(!ended.IsCancellationRequested)
                {
                    if(Volatile.Read(ref snapshot) is {} state)await Send(new{type="status",state});
                    await Task.Delay(1000,ended.Token);
                }
            }
            catch(Exception) when(ended.IsCancellationRequested){}
            catch(IOException){ended.Cancel();}
        });
        var ids=new HashSet<string>();var order=new Queue<string>();var running=new List<Task>();int active=0;
        async Task Execute(DeckRequest r)
        {
            try
            {
                DeckReply result;
                try{result=await execute(r).WaitAsync(ended.Token);}
                catch(OperationCanceledException){return;}
                catch(Exception e){result=new(r.Id,false,"failed",e.Message);}
                await Send(new{type="reply",reply=result});
            }
            catch(OperationCanceledException){}
            catch(IOException){ended.Cancel();}
            finally{Interlocked.Decrement(ref active);}
        }
        try
        {
            while(await DeckProtocol.ReadLine(pipe,ended.Token) is {} line)
            {
                var r=JsonSerializer.Deserialize<DeckRequest>(line,DeckProtocol.Json);
                if(r==null||!DeckProtocol.Valid(r))throw new IOException("Invalid Stream Deck command.");
                if(!ids.Add(r.Id))await Send(new{type="reply",reply=new DeckReply(r.Id,false,"duplicate","This command was already received.")});
                else
                {
                    order.Enqueue(r.Id);if(order.Count>128)ids.Remove(order.Dequeue());
                    if(Interlocked.Increment(ref active)>4){Interlocked.Decrement(ref active);await Send(new{type="reply",reply=new DeckReply(r.Id,false,"busy","Too many pending commands.")});}
                    else{running.RemoveAll(t=>t.IsCompleted);running.Add(Execute(r));}
                }
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){if(!stop.IsCancellationRequested)Log.Write("Stream Deck connection: "+e.Message);}
        finally{ended.Cancel();try{await Task.WhenAll(running.Append(publish));}catch(OperationCanceledException){}Interlocked.Decrement(ref clients);}
    }
    public void Dispose()
    {
        stop.Cancel();
        try
        {
            if(File.Exists(descriptor)&&JsonDocument.Parse(File.ReadAllText(descriptor)).RootElement.GetProperty("pipe").GetString()==pipeName)File.Delete(descriptor);
        }
        catch(Exception e){Log.Write("Stream Deck discovery cleanup: "+e.Message);}
    }
}

static class StreamDeckInstallation
{
    public const string Package="streamdeck/com.yami.deskglide.streamDeckPlugin";
    public static void Install()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PortableResources.FilePath(Package)){UseShellExecute=true});
}
