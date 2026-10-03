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
    readonly Task worker;
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
        public void Dispose(){File?.Dispose();Hash?.Dispose();}
    }
    public event Action<string>? Notification;
    public KvmClipboard(KvmService service,KvmOptions options,Control dispatcher,Action<string[]>? completed=null,string? storage=null)
    {
        this.service=service;this.options=options.Copy();this.dispatcher=dispatcher;
        testCompleted=completed;this.storage=storage??Path.Combine(Log.Folder,"Kvm","Clipboard");
        CreateHandle(new CreateParams{Caption="SDR Capture Clipboard",Parent=new IntPtr(-3)});
        AddClipboardFormatListener(Handle);service.Received+=OnMessage;
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
                if(Interlocked.CompareExchange(ref sending,1,0)!=0){Notification?.Invoke("Дождитесь завершения предыдущей передачи файлов.");return;}
                _=Task.Run(async()=>
                {
                    try{await SendFiles(files);}
                    catch(Exception e){Notification?.Invoke("Файлы не переданы: "+e.Message);}
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
        if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Ссылки и junction-папки нельзя передавать через буфер обмена.");
        if((attributes&FileAttributes.Directory)==0){yield return(path,new(relative,new FileInfo(path).Length,false));yield break;}
        yield return(path,new(relative,0,true));
        foreach(string child in Directory.EnumerateFileSystemEntries(path))
            foreach(var item in Enumerate(child,relative+"/"+Path.GetFileName(child)))yield return item;
    }
    async Task SendFiles(string[] paths)
    {
        var items=new List<(string Source,KvmFileEntry Entry)>();long total=0;
        foreach(string path in paths)
        foreach(var item in Enumerate(path,Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))))
        {
            total=checked(total+item.Entry.Length);items.Add(item);
            if(items.Count>10000||total>MaxBytes)throw new IOException("За один раз можно передать до 32 ГБ и 10 000 файлов/папок.");
        }
        ValidateManifest(items.Select(i=>i.Entry).ToArray());
        foreach(var peer in service.Peers)
        {
            string id=Guid.NewGuid().ToString("N");
            await service.SendBulk(peer.Id,new(){Type="file-begin",Id=id,Files=items.Select(i=>i.Entry).ToArray()},stop.Token);
            foreach(var item in items.Where(i=>!i.Entry.Directory))
            {
                await service.SendBulk(peer.Id,new(){Type="file-open",Id=id,Text=item.Entry.Path},stop.Token);
                using var file=new FileStream(item.Source,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer=new byte[65536];long count=0;int n;
                while((n=await file.ReadAsync(buffer,stop.Token))>0)
                {
                    count+=n;if(count>item.Entry.Length)throw new IOException("Файл изменился во время передачи.");
                    hash.AppendData(buffer,0,n);
                    await service.SendBulk(peer.Id,new(){Type="file-data",Id=id,Data=buffer.AsSpan(0,n).ToArray()},stop.Token);
                }
                if(count!=item.Entry.Length)throw new IOException("Файл изменился во время передачи.");
                await service.SendBulk(peer.Id,new(){Type="file-close",Id=id,Data=hash.GetHashAndReset()},stop.Token);
            }
            await service.SendBulk(peer.Id,new(){Type="file-done",Id=id},stop.Token);
        }
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
                    throw new IOException("Недопустимое имя файла в передаче.");
            }
            size=checked(size+entry.Length);if(size>MaxBytes)throw new IOException("Передача превышает 32 ГБ.");
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
                    if(transfers.Remove(peer,out var failed))failed.Dispose();
                    Notification?.Invoke("Передача файлов отклонена: "+e.Message);
                }
            }
        }
        catch(OperationCanceledException){}
        finally{foreach(var transfer in transfers.Values)transfer.Dispose();transfers.Clear();}
    }
    internal void HandleFile(string peer,KvmMessage message)
    {
        if(message.Type=="file-begin")
        {
            if(!Guid.TryParseExact(message.Id,"N",out _)||message.Files==null)throw new IOException("Invalid transfer.");
            ValidateManifest(message.Files);
            string folder=Path.Combine(storage,Guid.NewGuid().ToString("N"));
            if(new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace<message.Files.Sum(e=>e.Length)+512L*1024*1024)throw new IOException("Недостаточно места для файлов.");
            Directory.CreateDirectory(folder);
            if(transfers.Remove(peer,out var old))old.Dispose();
            foreach(var entry in message.Files.Where(e=>e.Directory).OrderBy(e=>e.Path.Length))Directory.CreateDirectory(SafePath(folder,entry.Path));
            transfers[peer]=new(){Id=message.Id,Folder=folder,Entries=message.Files.Where(e=>!e.Directory).ToArray()};
            if(testCompleted==null)Ui(()=>{Clipboard.Clear();ownSequence=GetClipboardSequenceNumber();});
            Notification?.Invoke("Получаю файлы по KVM. Вставка будет доступна после завершения.");return;
        }
        if(!transfers.TryGetValue(peer,out var state)||state.Id!=message.Id)throw new IOException("Unknown transfer.");
        switch(message.Type)
        {
            case "file-open":
                if(state.File!=null||state.Index>=state.Entries.Length||state.Entries[state.Index].Path!=message.Text)throw new IOException("Invalid file order.");
                string path=SafePath(state.Folder,message.Text);
                state.File=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536);state.Hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);state.Received=0;break;
            case "file-data":
                if(state.File==null||message.Data is not {Length:>0 and <=65536}||state.Received+message.Data.Length>state.Entries[state.Index].Length)throw new IOException("Invalid file data.");
                state.File.Write(message.Data);state.Hash!.AppendData(message.Data);state.Received+=message.Data.Length;break;
            case "file-close":
                if(state.File==null||state.Received!=state.Entries[state.Index].Length||message.Data?.Length!=32||!CryptographicOperations.FixedTimeEquals(state.Hash!.GetHashAndReset(),message.Data))throw new IOException("Контрольная сумма файла не совпала.");
                state.File.Dispose();state.File=null;state.Hash!.Dispose();state.Hash=null;state.Index++;break;
            case "file-done":
                if(state.File!=null||state.Index!=state.Entries.Length)throw new IOException("Incomplete transfer.");
                string[] roots=Directory.GetFileSystemEntries(state.Folder);transfers.Remove(peer);state.Dispose();
                if(testCompleted!=null){testCompleted(roots);break;}
                Ui(()=>{var list=new StringCollection();list.AddRange(roots);Clipboard.SetFileDropList(list);ownSequence=GetClipboardSequenceNumber();Notification?.Invoke("Файлы получены. Можно вставить их через Ctrl+V.");});break;
            default:throw new IOException("Unknown file operation.");
        }
    }
    static string SafePath(string folder,string relative)
    {
        string root=Path.GetFullPath(folder)+Path.DirectorySeparatorChar,path=Path.GetFullPath(Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Path outside clipboard folder.");
        return path;
    }
    void Ui(Action action){try{dispatcher.BeginInvoke(()=>{if(stop.IsCancellationRequested)return;try{action();}catch(ExternalException){Notification?.Invoke("Буфер обмена временно занят.");}});}catch(InvalidOperationException){}}
    public void Dispose(){service.Received-=OnMessage;RemoveClipboardFormatListener(Handle);DestroyHandle();stop.Cancel();incoming.Writer.TryComplete();}
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
}
