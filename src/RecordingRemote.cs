using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SdrCapture;

// This DTO has no output path, device selector, executable arguments, or hotkeys.
sealed record RecordingProfile(string Codec="HEVC",string Quality="High",int Fps=60,int Width=0,int Height=0,
    string RateControl="Quality",int BitrateMbps=0,int KeyframeSeconds=2,string EncoderPreset="Balanced",int DurationSeconds=300,int SourceWidth=0,int SourceHeight=0,string Encoder="GPU")
{
    public static RecordingProfile FromReplay(ReplayOptions value)=>new(value.Codec,value.Quality,value.Fps,value.Width,value.Height,
        value.RateControl,value.BitrateMbps,value.KeyframeSeconds,value.EncoderPreset,value.BufferSeconds,Encoder:value.Encoder);
    public ReplayOptions ToReplay(ReplayOptions local)
    {
        ArgumentNullException.ThrowIfNull(local);
        if(DurationSeconds is <60 or >7200||Width==0&&Height!=0||Width!=0&&Height==0)
            throw new ArgumentException("Invalid recording profile duration or dimensions.");
        if((SourceWidth==0)!=(SourceHeight==0)||SourceWidth!=0&&(SourceWidth is <2 or >16384||SourceHeight is <2 or >16384||
            (SourceWidth&1)!=0||(SourceHeight&1)!=0||(long)SourceWidth*SourceHeight>40_000_000))
            throw new ArgumentException("Invalid recording source dimensions.");
        var result=local with{Codec=Codec,Encoder=Encoder,Quality=Quality,Fps=Fps,Width=Width,Height=Height,RateControl=RateControl,
            BitrateMbps=BitrateMbps,KeyframeSeconds=KeyframeSeconds,EncoderPreset=EncoderPreset,DurationSeconds=DurationSeconds};
        result.Validate();return result;
    }
}

sealed record RecordingCommand(string Id,string Command,string? Session=null,string? Source=null,RecordingProfile? Profile=null,string? App=null);
sealed record RecordingNodeState(bool ReplayEnabled=false,double BufferedSeconds=0,bool Recording=false,string? RecordingSession=null,
    string? Error=null,string? LastSaved=null,double InputFps=0,double OutputFps=0,long DroppedFrames=0,long RepeatedFrames=0,int RequiredFps=60);
sealed record RecordingResponse(string Id,bool Ok,string Code="",string Message="",RecordingNodeState? State=null,RecordingCapability[]? Capabilities=null);

static class RecordingProtocol
{
    public const int MaxWireBytes=16384;
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=12};
    public static bool GuidId(string? value)=>value!=null&&Guid.TryParseExact(value,"N",out _);
    public static bool BoundedText(string? value,int length)=>value!=null&&value.Length<=length&&!value.Any(char.IsControl);
    public static bool Envelope(string channel,KvmMessage message)=>channel=="control"&&message.Type is "recording-request" or "recording-reply"&&
        GuidId(message.Id)&&message.Text!=null&&message.Text.Length<=MaxWireBytes&&Encoding.UTF8.GetByteCount(message.Text)<=MaxWireBytes;
    public static TimeSpan Timeout(string command)=>TimeSpan.FromSeconds(command=="saveReplay"?600:command=="capabilities"?120:command=="status"?8:30);
    public static RecordingCommand Normalize(RecordingCommand request)=>request with{Id=Guid.ParseExact(request.Id,"N").ToString("N"),Session=request.Session==null?null:Guid.ParseExact(request.Session,"N").ToString("N")};
    public static bool Valid(RecordingCommand value)
    {
        if(value==null||!GuidId(value.Id)||(value.Session!=null&&!GuidId(value.Session)))return false;
        if(value.Profile is {} profile){try{profile.ToReplay(new());}catch(ArgumentException){return false;}}
        if(value.App!=null&&(!BoundedText(value.App,128)||string.IsNullOrWhiteSpace(value.App)||value.App is "." or ".."||value.App.IndexOfAny(['/', '\\', ':'])>=0))return false;
        return value.Command switch
        {
            "status"=>value.Source==null&&value.Profile==null&&value.App==null,
            "capabilities"=>value.Source==null&&value.App==null,
            "configureReplay"=>value.Session!=null&&Source(value.Source)&&value.Profile!=null&&value.App==null,
            "startRecording"=>value.Session!=null&&Source(value.Source)&&value.Profile!=null,
            "saveReplay"=>value.Session!=null&&value.Source==null&&value.Profile==null,
            "stopRecording" or "disableReplay" or "release"=>value.Session!=null&&value.Source==null&&value.Profile==null&&value.App==null,
            _=>false
        };
    }
    static bool Source(string? source)=>BoundedText(source,512)&&!string.IsNullOrWhiteSpace(source);
    public static bool Valid(RecordingResponse value)
    {
        if(value==null||!GuidId(value.Id)||!BoundedText(value.Code,64)||!BoundedText(value.Message,1024))return false;
        if(value.State is {} state&&(!double.IsFinite(state.BufferedSeconds)||state.BufferedSeconds is <0 or >86400||
            !double.IsFinite(state.InputFps)||state.InputFps is <0 or >1000||!double.IsFinite(state.OutputFps)||state.OutputFps is <0 or >1000||
            state.DroppedFrames<0||state.RepeatedFrames<0||state.RequiredFps is not (60 or 120)||(state.RecordingSession!=null&&!GuidId(state.RecordingSession))||
            (state.Error!=null&&!BoundedText(state.Error,1024))||(state.LastSaved!=null&&!BoundedText(state.LastSaved,1024))))return false;
        if(value.Capabilities is {} capabilities&&(capabilities.Length>32||capabilities.Any(c=>c==null||!BoundedText(c.Codec,16)||!BoundedText(c.Encoder,64)||!BoundedText(c.Label,128)||
            c.SourceWidth is <0 or >16384||c.SourceHeight is <0 or >16384||c.Width is <1 or >7680||c.Height is <1 or >4320||c.Fps is not (60 or 120)||
            c.Backend is not ("GPU" or "CPU")||c.Backend=="CPU"&&(c.Encoder!="libx264"||c.Codec!="H264"))))return false;
        return true;
    }
    public static string Serialize(RecordingResponse response)
    {
        if(!Valid(response))response=new(response.Id,false,"failed","The recording node returned an invalid response.");
        string json=JsonSerializer.Serialize(response,Json);
        return Encoding.UTF8.GetByteCount(json)<=MaxWireBytes?json:JsonSerializer.Serialize(new RecordingResponse(response.Id,false,"tooLarge","The recording node returned too much data."),Json);
    }
}

// Retain results across disconnect. Timeouts keep their concurrency slot until
// the actual operation settles: an already started side effect cannot be undone.
sealed class RecordingRequestCache:IDisposable
{
    sealed class Entry(string fingerprint,DateTimeOffset now)
    {
        public readonly string Fingerprint=fingerprint;
        public readonly TaskCompletionSource<RecordingResponse> Done=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset FinishedAt=now;
        public bool Finished;
    }
    readonly object gate=new();
    readonly Dictionary<(string Peer,string Id),Entry> entries=new();
    readonly Func<string,RecordingCommand,Task<RecordingResponse>> execute;
    readonly Func<DateTimeOffset> now;
    readonly TimeSpan? timeout;
    readonly int capacity,maxActive;
    readonly CancellationTokenSource stop=new();
    int active;bool disposed;
    internal int Count{get{lock(gate)return entries.Count;}}
    public RecordingRequestCache(Func<string,RecordingCommand,Task<RecordingResponse>> execute,Func<DateTimeOffset>? now=null,TimeSpan? timeout=null,int capacity=2048,int maxActive=4)
    {this.execute=execute;this.now=now??(()=>DateTimeOffset.UtcNow);this.timeout=timeout;this.capacity=capacity;this.maxActive=maxActive;}
    public Task<RecordingResponse> ExecuteAsync(string peer,RecordingCommand request)
    {
        if(!RecordingProtocol.GuidId(peer)||!RecordingProtocol.Valid(request))return Task.FromResult(new RecordingResponse(request.Id,false,"invalid","Invalid recording command."));
        request=RecordingProtocol.Normalize(request);peer=Guid.ParseExact(peer,"N").ToString("N");
        string fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request,RecordingProtocol.Json)));
        var key=(peer,request.Id);Entry entry;
        lock(gate)
        {
            if(disposed)return Task.FromResult(new RecordingResponse(request.Id,false,"unknown","Recording control has stopped. Check status before retrying."));
            var cutoff=now().AddMinutes(-15);
            foreach(var old in entries.Where(p=>p.Value.Finished&&p.Value.FinishedAt<cutoff).Select(p=>p.Key).ToArray())entries.Remove(old);
            if(entries.TryGetValue(key,out var existing))return existing.Fingerprint==fingerprint?existing.Done.Task:Task.FromResult(new RecordingResponse(request.Id,false,"conflict","This request ID was already used for a different command."));
            if(entries.Count>=capacity)return Task.FromResult(new RecordingResponse(request.Id,false,"busy","Recording request history is full. Check status later."));
            entry=new(fingerprint,now());entries.Add(key,entry);
            if(active>=maxActive){entry.Finished=true;entry.Done.SetResult(new(request.Id,false,"busy","Too many recording commands are pending."));return entry.Done.Task;}
            active++;
        }
        _=Task.Run(()=>Run(peer,request,entry));return entry.Done.Task;
    }
    async Task Run(string peer,RecordingCommand request,Entry entry)
    {
        Task<RecordingResponse>? operation=null;
        try
        {
            if(stop.IsCancellationRequested){entry.Done.TrySetResult(new(request.Id,false,"unknown","Recording control has stopped. Check status before retrying."));return;}
            operation=Task.Run(()=>execute(peer,request));
            var response=await operation.WaitAsync(timeout??RecordingProtocol.Timeout(request.Command),stop.Token);
            entry.Done.TrySetResult(response.Id==request.Id&&RecordingProtocol.Valid(response)?response:new(request.Id,false,"failed","The recording node returned an invalid response."));
        }
        catch(TimeoutException){entry.Done.TrySetResult(new(request.Id,false,"unknown","The command timed out. Check status before retrying; it is not repeated automatically."));}
        catch(OperationCanceledException){entry.Done.TrySetResult(new(request.Id,false,"unknown","Recording control stopped. Check status before retrying."));}
        catch(Exception){entry.Done.TrySetResult(new(request.Id,false,"failed","The recording command failed on the receiving PC."));}
        finally{if(operation is {IsCompleted:false})_=FinishLater(operation,entry);else Finish(entry);}
    }
    async Task FinishLater(Task<RecordingResponse> operation,Entry entry)
    {try{await operation;}catch(Exception){}finally{Finish(entry);}}
    void Finish(Entry entry){lock(gate){active--;entry.Finished=true;entry.FinishedAt=now();}}
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;stop.Cancel();
            foreach(var item in entries)item.Value.Done.TrySetResult(new(item.Key.Id,false,"unknown","Recording control stopped. Check status before retrying."));
        }
    }
}

sealed class RecordingRemote:IDisposable
{
    sealed record Pending(string Fingerprint,TaskCompletionSource<RecordingResponse> Done);
    readonly KvmService? service;
    readonly Func<string,bool> connected;
    readonly Func<string,KvmMessage,bool> send;
    readonly RecordingRequestCache received;
    readonly ConcurrentDictionary<(string Peer,string Id),Pending> pending=new();
    readonly ConcurrentDictionary<(string Peer,string Id),byte> replying=new();
    readonly CancellationTokenSource stop=new();
    readonly object gate=new();
    bool disposed;
    public RecordingRemote(KvmService service,Func<string,RecordingCommand,Task<RecordingResponse>> execute)
        :this(peer=>service.Peers.Any(p=>string.Equals(p.Id,peer,StringComparison.OrdinalIgnoreCase)),
            (peer,message)=>service.Peers.FirstOrDefault(p=>string.Equals(p.Id,peer,StringComparison.OrdinalIgnoreCase)) is {} matched&&service.Send(matched.Id,message),execute)
    {this.service=service;service.Received+=Receive;service.Disconnected+=Disconnected;}
    internal RecordingRemote(Func<string,bool> connected,Func<string,KvmMessage,bool> send,Func<string,RecordingCommand,Task<RecordingResponse>> execute)
    {this.connected=connected;this.send=send;received=new(execute);}
    public async Task<RecordingResponse> ExecuteAsync(string peer,RecordingCommand request,CancellationToken token=default)
    {
        if(token.IsCancellationRequested)return new(request.Id,false,"cancelled","The recording command was cancelled before sending.");
        if(!RecordingProtocol.GuidId(peer)||!RecordingProtocol.Valid(request))return new(request.Id,false,"invalid","Invalid recording command.");
        request=RecordingProtocol.Normalize(request);peer=Guid.ParseExact(peer,"N").ToString("N");
        string json=JsonSerializer.Serialize(request,RecordingProtocol.Json);
        if(Encoding.UTF8.GetByteCount(json)>RecordingProtocol.MaxWireBytes)return new(request.Id,false,"invalid","Recording command is too large.");
        var key=(peer,request.Id);Pending item;bool shouldSend=false;
        lock(gate)
        {
            if(disposed)return new(request.Id,false,"unknown","Recording control has stopped.");
            if(pending.TryGetValue(key,out var existing))
            {if(existing.Fingerprint!=json)return new(request.Id,false,"conflict","This request ID was already used for a different command.");item=existing;}
            else
            {
                if(!connected(peer))return new(request.Id,false,"offline","The selected recording PC is disconnected.");
                if(pending.Count>=64)return new(request.Id,false,"busy","Too many recording requests are pending.");
                item=new(json,new(TaskCreationOptions.RunContinuationsAsynchronously));pending[key]=item;shouldSend=true;
            }
        }
        if(shouldSend)
        {
            if(!send(peer,new(){Type="recording-request",Id=request.Id,Text=json}))item.Done.TrySetResult(new(request.Id,false,"unknown","The connection closed before confirmation. Check status before retrying."));
            _=Expire(key,item,request.Command);
        }
        try{return await item.Done.Task.WaitAsync(token);}
        catch(OperationCanceledException){return new(request.Id,false,"unknown","Waiting was cancelled. Check status before retrying; the command may have executed.");}
    }
    async Task Expire((string Peer,string Id) key,Pending item,string command)
    {
        try{await item.Done.Task.WaitAsync(RecordingProtocol.Timeout(command),stop.Token);}
        catch(TimeoutException){item.Done.TrySetResult(new(key.Id,false,"unknown","No confirmation received. Check status before retrying; the command is not repeated automatically."));}
        catch(OperationCanceledException){item.Done.TrySetResult(new(key.Id,false,"unknown","Recording control stopped. Check status before retrying."));}
        finally{pending.TryRemove(new KeyValuePair<(string Peer,string Id),Pending>(key,item));}
    }
    internal void Disconnected(string peer)
    {
        if(!RecordingProtocol.GuidId(peer))return;
        peer=Guid.ParseExact(peer,"N").ToString("N");
        foreach(var item in pending.Where(p=>p.Key.Peer==peer))item.Value.Done.TrySetResult(new(item.Key.Id,false,"unknown","The recording PC disconnected. Check status before retrying; the command is not repeated automatically."));
        // received intentionally remains intact for reconnect retransmissions.
    }
    internal void Receive(string peer,KvmMessage message)
    {
        if(stop.IsCancellationRequested||!RecordingProtocol.GuidId(peer)||!RecordingProtocol.Envelope("control",message)||!connected(peer))return;
        peer=Guid.ParseExact(peer,"N").ToString("N");
        try
        {
            if(message.Type=="recording-reply")
            {
                var response=JsonSerializer.Deserialize<RecordingResponse>(message.Text,RecordingProtocol.Json);
                if(response!=null&&response.Id==message.Id&&RecordingProtocol.Valid(response)&&pending.TryGetValue((peer,message.Id),out var item))item.Done.TrySetResult(response);
            }
            else
            {
                var request=JsonSerializer.Deserialize<RecordingCommand>(message.Text,RecordingProtocol.Json);
                if(request==null||request.Id!=message.Id||!RecordingProtocol.Valid(request))return;
                request=RecordingProtocol.Normalize(request);
                var response=received.ExecuteAsync(peer,request);
                if(replying.TryAdd((peer,request.Id),0))_=Reply(peer,request.Id,response,true);
                else if(response.IsCompleted)_=Reply(peer,request.Id,response,false);
            }
        }
        catch(JsonException){/* Ignore malformed recording JSON. */}
    }
    async Task Reply(string peer,string id,Task<RecordingResponse> operation,bool registered)
    {
        try
        {
            var response=await operation;
            if(!stop.IsCancellationRequested&&connected(peer))send(peer,new(){Type="recording-reply",Id=response.Id,Text=RecordingProtocol.Serialize(response)});
        }
        catch(Exception){/* Observe background send/serialization failures. */}
        finally{if(registered)replying.TryRemove((peer,id),out _);}
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            if(service!=null){service.Received-=Receive;service.Disconnected-=Disconnected;}
            stop.Cancel();received.Dispose();
            foreach(var item in pending)item.Value.Done.TrySetResult(new(item.Key.Id,false,"unknown","Recording control stopped. Check status before retrying."));
            pending.Clear();
        }
    }
}
