using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace SdrCapture;
sealed class KvmClipboard:NativeWindow,IDisposable
{
    const long MaxBytes=32L*1024*1024*1024;
    readonly KvmService service;
    readonly KvmOptions options;
    readonly Control dispatcher;
    readonly CancellationTokenSource stop=new();
    readonly Channel<(string,KvmMessage)> incoming=Channel.CreateBounded<(string,KvmMessage)>(128);
    readonly Dictionary<string,Transfer> transfers=[];
    readonly Dictionary<string,long> cancelled=[];
    readonly Task worker;
    readonly SemaphoreSlim sendGate=new(1,1);
    readonly Action<string[]>? testCompleted;
    readonly string storage;
    uint ownSequence;
    int sending;
    sealed class Transfer:IDisposable
    {
        public required string Id,Folder;
        public required KvmFileEntry[] Entries;
        public int Index;
        public FileStream? File;
        public IncrementalHash? Hash;
        public long Received;
        public long LeaseAt=Environment.TickCount64;
        public bool Drag;
        public void Dispose(){File?.Dispose();Hash?.Dispose();}
    }
    public event Action<string>? Notification;
    public event Action<string,string,string[]>? DragFilesReady;
    public KvmClipboard(KvmService service,KvmOptions options,Control dispatcher,Action<string[]>? completed=null,string? storage=null)
    {
        this.service=service;this.options=options.Copy();this.dispatcher=dispatcher;
        testCompleted=completed;this.storage=storage??Path.Combine(Log.Folder,"Kvm","Clipboard");
        CreateHandle(new CreateParams{Caption="SDR Capture Clipboard",Parent=new IntPtr(-3)});
        AddClipboardFormatListener(Handle);service.Received+=OnMessage;service.Disconnected+=OnDisconnected;
        worker=Task.Run(ReadLoop);
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x31D&&GetClipboardSequenceNumber()!=ownSequence)Publish();
        base.WndProc(ref m);
    }
    void Publish()
    {
        if(service.Peers.Length==0)return;
        try
        {
            if(options.ClipboardFiles&&Clipboard.ContainsFileDropList())
            {
                string[] files=Clipboard.GetFileDropList().Cast<string>().ToArray();
                if(Interlocked.CompareExchange(ref sending,1,0)!=0){Log.Write("Clipboard transfer already in progress.");return;}
                _=Task.Run(async()=>
                {
                    try{await SendFiles(files);}
                    catch(Exception e){Notification?.Invoke("Files could not be sent: "+e.Message);}
                    finally{Interlocked.Exchange(ref sending,0);}
                });
            }
            else if(options.ClipboardText&&Clipboard.ContainsText())
            {
                string text=Clipboard.GetText();
                if(text.Length<=262144)foreach(var peer in service.Peers)service.Send(peer.Id,new(){Type="clipboard-text",Text=text});
            }
        }
        catch(ExternalException){}
    }
    static IEnumerable<(string Source,KvmFileEntry Entry)> Enumerate(string path,string relative)
    {
        var attributes=File.GetAttributes(path);
        if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Links and junction folders cannot be transferred through the clipboard.");
        if((attributes&FileAttributes.Directory)==0){yield return(path,new(relative,new FileInfo(path).Length,false));yield break;}
        yield return(path,new(relative,0,true));
        foreach(string child in Directory.EnumerateFileSystemEntries(path))
            foreach(var item in Enumerate(child,relative+"/"+Path.GetFileName(child)))yield return item;
    }
    public async Task SendDrag(string peer,string id,string[] paths,CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,token);
        try{await SendFiles(paths,peer,id,linked.Token);}
        catch{service.Send(peer,new(){Type="file-abort",Id=id});throw;}
    }
    async Task SendFiles(string[] paths,string? onlyPeer=null,string? dragId=null,CancellationToken token=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,token);token=linked.Token;
        await sendGate.WaitAsync(token);
        try
        {
        var items=new List<(string Source,KvmFileEntry Entry)>();long total=0;
        foreach(string path in paths)
        foreach(var item in Enumerate(path,Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))))
        {
            total=checked(total+item.Entry.Length);items.Add(item);
            if(items.Count>10000||total>MaxBytes)throw new IOException("A transfer can contain up to 32 GB and 10,000 files or folders.");
        }
        ValidateManifest(items.Select(i=>i.Entry).ToArray());
        foreach(var peer in service.Peers.Where(p=>onlyPeer==null||p.Id==onlyPeer))
        {
            string id=dragId??Guid.NewGuid().ToString("N");
            try
            {
            await service.SendBulk(peer.Id,new(){Type="file-begin",Id=id,Flags=dragId==null?0:1,Files=items.Select(i=>i.Entry).ToArray()},token);
            foreach(var item in items.Where(i=>!i.Entry.Directory))
            {
                await service.SendBulk(peer.Id,new(){Type="file-open",Id=id,Text=item.Entry.Path},token);
                using var file=new FileStream(item.Source,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer=new byte[65536];long count=0;int n;
                while((n=await file.ReadAsync(buffer,token))>0)
                {
                    count+=n;if(count>item.Entry.Length)throw new IOException("The file changed during transfer.");
                    hash.AppendData(buffer,0,n);
                    await service.SendBulk(peer.Id,new(){Type="file-data",Id=id,Data=buffer.AsSpan(0,n).ToArray()},token);
                }
                if(count!=item.Entry.Length)throw new IOException("The file changed during transfer.");
                await service.SendBulk(peer.Id,new(){Type="file-close",Id=id,Data=hash.GetHashAndReset()},token);
            }
            await service.SendBulk(peer.Id,new(){Type="file-done",Id=id},token);
            }
            catch{service.Send(peer.Id,new(){Type="file-abort",Id=id});throw;}
        }
        }
        finally{sendGate.Release();}
    }
    void OnMessage(string peer,KvmMessage message)
    {
        if(message.Type=="clipboard-text")
        {
            if(!options.ClipboardText)return;
            Ui(()=>{Clipboard.SetText(message.Text.Length>0?message.Text:" ");ownSequence=GetClipboardSequenceNumber();});
        }
        else if(message.Type.StartsWith("file-")&&options.ClipboardFiles)
        {
            // Bounded backpressure on the dedicated bulk channel, never on mouse/keyboard.
            try{incoming.Writer.WriteAsync((peer,message),stop.Token).AsTask().GetAwaiter().GetResult();}catch(OperationCanceledException){}
        }
    }
    async void OnDisconnected(string peer){try{await incoming.Writer.WriteAsync((peer,new(){Type="file-peer-disconnect"}),stop.Token);}catch(OperationCanceledException){}catch(ChannelClosedException){}}
    internal static void ValidateManifest(KvmFileEntry[] entries)
    {
        if(entries.Length is <1 or >10000)throw new IOException("Invalid clipboard manifest.");
        long size=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in entries)
        {
            if(entry.Path.Length>220||entry.Length<0||entry.Directory&&entry.Length!=0||!names.Add(entry.Path))throw new IOException("Invalid clipboard entry.");
            var parts=entry.Path.Split('/');
            foreach(string part in parts)
            {
                string name=part.Split('.')[0].ToUpperInvariant();
                if(part.Length==0||part is "." or ".."||part.EndsWith(' ')||part.EndsWith('.')||part.IndexOfAny(Path.GetInvalidFileNameChars())>=0||part.Contains('\\')||
                   name is "CON" or "PRN" or "AUX" or "NUL"||System.Text.RegularExpressions.Regex.IsMatch(name,@"^(COM|LPT)[0-9]$"))
                    throw new IOException("Invalid file name in transfer.");
            }
            size=checked(size+entry.Length);if(size>MaxBytes)throw new IOException("The transfer exceeds 32 GB.");
        }
        foreach(var entry in entries)
        {
            string parent=entry.Path;
            while(parent.Contains('/'))
            {
                parent=parent[..parent.LastIndexOf('/')];
                if(!entries.Any(e=>e.Path.Equals(parent,StringComparison.OrdinalIgnoreCase)&&e.Directory))throw new IOException("Missing parent directory.");
            }
        }
    }
    async Task ReadLoop()
    {
        try
        {
            await foreach(var (peer,message) in incoming.Reader.ReadAllAsync(stop.Token))
            {
                try{HandleFile(peer,message);}
                catch(Exception e)
                {
                    if(transfers.Remove(peer+"|"+message.Id,out var failed))failed.Dispose();
                    if(Guid.TryParseExact(message.Id,"N",out _))service.Send(peer,new(){Type="drag-error",Id=message.Id,Text=e.Message});
                    Notification?.Invoke("File transfer rejected: "+e.Message);
                }
            }
        }
        catch(OperationCanceledException){}
        finally{foreach(var transfer in transfers.Values)transfer.Dispose();transfers.Clear();}
    }
    internal void HandleFile(string peer,KvmMessage message)
    {
        string transferKey=peer+"|"+message.Id;
        foreach(var key in cancelled.Where(p=>Environment.TickCount64-p.Value>600000).Select(p=>p.Key).ToArray())cancelled.Remove(key);
        if(message.Type=="file-peer-disconnect"){foreach(var key in transfers.Keys.Where(k=>k.StartsWith(peer+"|",StringComparison.Ordinal)).ToArray()){transfers[key].Dispose();transfers.Remove(key);}return;}
        if(message.Type=="file-abort"){if(transfers.Remove(transferKey,out var aborted))aborted.Dispose();if(cancelled.Count<256)cancelled[transferKey]=Environment.TickCount64;return;}
        if(cancelled.ContainsKey(transferKey))return;
        if(message.Type=="file-begin")
        {
            if(!Guid.TryParseExact(message.Id,"N",out _)||message.Files==null)throw new IOException("Invalid transfer.");
            ValidateManifest(message.Files);
            string folder=Path.Combine(storage,Guid.NewGuid().ToString("N"));
            if(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace<message.Files.Sum(e=>e.Length)+512L*1024*1024)throw new IOException("Not enough space for the files.");
            Directory.CreateDirectory(folder);
            KvmFileCache.Mark(storage,folder);
            if(transfers.Count>=16)throw new IOException("Too many simultaneous transfers.");
            if(transfers.Remove(transferKey,out var old))old.Dispose();
            foreach(var entry in message.Files.Where(e=>e.Directory).OrderBy(e=>e.Path.Length))Directory.CreateDirectory(SafePath(folder,entry.Path));
            transfers[transferKey]=new(){Id=message.Id,Folder=folder,Entries=message.Files.Where(e=>!e.Directory).ToArray(),Drag=message.Flags==1};
            if(message.Flags!=1&&testCompleted==null)Ui(()=>{Clipboard.Clear();ownSequence=GetClipboardSequenceNumber();});
            return;
        }
        if(!transfers.TryGetValue(transferKey,out var state)||state.Id!=message.Id)throw new IOException("Unknown transfer.");
        switch(message.Type)
        {
            case "file-open":
                if(state.File!=null||state.Index>=state.Entries.Length||state.Entries[state.Index].Path!=message.Text)throw new IOException("Invalid file order.");
                string path=SafePath(state.Folder,message.Text);
                state.File=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536);state.Hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);state.Received=0;break;
            case "file-data":
                if(state.File==null||message.Data is not {Length:>0 and <=65536}||state.Received+message.Data.Length>state.Entries[state.Index].Length)throw new IOException("Invalid file data.");
                state.File.Write(message.Data);state.Hash!.AppendData(message.Data);state.Received+=message.Data.Length;if(Environment.TickCount64-state.LeaseAt>60000){KvmFileCache.Mark(storage,state.Folder);state.LeaseAt=Environment.TickCount64;}break;
            case "file-close":
                if(state.File==null||state.Received!=state.Entries[state.Index].Length||message.Data?.Length!=32||!CryptographicOperations.FixedTimeEquals(state.Hash!.GetHashAndReset(),message.Data))throw new IOException("The file checksum did not match.");
                state.File.Dispose();state.File=null;state.Hash!.Dispose();state.Hash=null;state.Index++;break;
            case "file-done":
                if(state.File!=null||state.Index!=state.Entries.Length)throw new IOException("Incomplete transfer.");
                string[] roots=Directory.GetFileSystemEntries(state.Folder);transfers.Remove(transferKey);state.Dispose();
                if(state.Drag){KvmFileCache.Mark(storage,state.Folder);Ui(()=>DragFilesReady?.Invoke(peer,message.Id,roots));break;}
                if(testCompleted!=null){testCompleted(roots);break;}
                Ui(()=>{var list=new StringCollection();list.AddRange(roots);Clipboard.SetFileDropList(list);ownSequence=GetClipboardSequenceNumber();});break;
            default:throw new IOException("Unknown file operation.");
        }
    }
    static string SafePath(string folder,string relative)
    {
        string root=Path.GetFullPath(folder)+Path.DirectorySeparatorChar,path=Path.GetFullPath(Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Path outside clipboard folder.");
        return path;
    }
    void Ui(Action action){try{dispatcher.BeginInvoke(()=>{if(stop.IsCancellationRequested)return;try{action();}catch(ExternalException){Log.Write("Clipboard temporarily busy.");}});}catch(InvalidOperationException){}}
    public void Dispose(){service.Received-=OnMessage;service.Disconnected-=OnDisconnected;RemoveClipboardFormatListener(Handle);DestroyHandle();stop.Cancel();incoming.Writer.TryComplete();}
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
}
