using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SdrCapture;
sealed record KvmMessage
{
    public string Version {get;set;}="";
    public int UpdateProtocol {get;set;}
    public bool RemoteViewOnly {get;set;}
    public string Type {get;set;}="";
    public string Text {get;set;}="";
    public string Id {get;set;}="";
    public string Device {get;set;}="";
    public int X {get;set;}
    public int Y {get;set;}
    public int Code {get;set;}
    public int Flags {get;set;}
    public long Size {get;set;}
    public byte[]? Data {get;set;}
    public KvmScreen[]? Screens {get;set;}
    public KvmFileEntry[]? Files {get;set;}
}
sealed record KvmFileEntry(string Path,long Length,bool Directory);

sealed class KvmIdentity:IDisposable
{
    public X509Certificate2 Certificate {get;}
    public byte[] Key {get;}
    public string Code=>KvmPairing.Encode(SHA256.HashData(Certificate.RawData),Key);
    public KvmIdentity(string folder)
    {
        Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"host-identity.json");
        if(File.Exists(path))
        {
            var data=JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))??throw new IOException("Повреждён ключ KVM.");
            Key=Convert.FromBase64String(data[1]);
            Certificate=X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(data[0]),"",X509KeyStorageFlags.UserKeySet);
        }
        else
        {
            using var rsa=RSA.Create(2048);
            var request=new CertificateRequest("CN=SdrCapture KVM",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
            using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(5));
            byte[] pfx=certificate.Export(X509ContentType.Pfx,"");
            Key=RandomNumberGenerator.GetBytes(32);
            Certificate=X509CertificateLoader.LoadPkcs12(pfx,"",X509KeyStorageFlags.UserKeySet);
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new[]{Convert.ToBase64String(pfx),Convert.ToBase64String(Key)}));
            File.Move(path+".tmp",path,true);
        }
        if(Key.Length!=32)throw new IOException("Повреждён ключ KVM.");
    }
    public void Dispose()=>Certificate.Dispose();
}

sealed class KvmWire:IDisposable
{
    const int MaximumPacket=4*1024*1024;
    readonly TcpClient tcp;
    readonly SslStream stream;
    readonly SemaphoreSlim writeGate=new(1,1);
    readonly CancellationTokenSource stop=new();
    readonly Channel<KvmMessage> queue=System.Threading.Channels.Channel.CreateBounded<KvmMessage>(new BoundedChannelOptions(512){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    Task? writer;
    public KvmPeerInfo Peer {get;private set;}=new("","",[]);
    public string Channel {get;private set;}="control";
    public CancellationToken Token=>stop.Token;
    public KvmWire(TcpClient tcp,SslStream stream){this.tcp=tcp;this.stream=stream;tcp.NoDelay=true;}
    public static async Task<KvmWire> Accept(TcpClient tcp,KvmIdentity identity,KvmOptions options,CancellationToken token)
    {
        var stream=new SslStream(tcp.GetStream(),false);
        var wire=new KvmWire(tcp,stream);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(10000);
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=identity.Certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,ClientCertificateRequired=false},timeout.Token);
            byte[] nonce=RandomNumberGenerator.GetBytes(32);
            await wire.SendAsync(new(){Type="challenge",Data=nonce},timeout.Token);
            var hello=await wire.ReadAsync(timeout.Token);
            if(hello.Type!="hello"||!Guid.TryParseExact(hello.Id,"N",out _)||hello.Id==options.Id||hello.Text.Length>100||hello.Device is not ("control" or "bulk" or "audio"))throw new AuthenticationException("Invalid KVM hello.");
            byte[] expected=HMACSHA256.HashData(identity.Key,nonce.Concat(Encoding.UTF8.GetBytes(hello.Id+"|"+hello.Device)).ToArray());
            if(hello.Data==null||!CryptographicOperations.FixedTimeEquals(expected,hello.Data))throw new AuthenticationException("Неверный код KVM.");
            ValidateScreens(hello.Screens);
            wire.Peer=new(hello.Id,hello.Text,hello.Screens!,hello.Version,Math.Clamp(hello.UpdateProtocol,0,1),hello.RemoteViewOnly);wire.Channel=hello.Device;
            await wire.SendAsync(new(){Type="welcome",Version=Updates.VersionText,UpdateProtocol=1,Id=options.Id,Text=Environment.MachineName,Screens=KvmScreen.Local()},timeout.Token);
            wire.StartWriter();return wire;
        }
        catch{wire.Dispose();throw;}
    }
    public static async Task<KvmWire> Connect(KvmOptions options,string channel,CancellationToken token)
    {
        var (fingerprint,key)=KvmPairing.Decode(options.PairingCode);
        var tcp=new TcpClient();KvmWire? wire=null;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(10000);
            await tcp.ConnectAsync(options.Host,options.Port,timeout.Token);
            var stream=new SslStream(tcp.GetStream(),false,(_,certificate,_,_)=>certificate!=null&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()),fingerprint));
            wire=new(tcp,stream){Channel=channel};
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="SdrCapture KVM",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},timeout.Token);
            var challenge=await wire.ReadAsync(timeout.Token);
            if(challenge.Type!="challenge"||challenge.Data?.Length!=32)throw new AuthenticationException("Invalid host challenge.");
            byte[] proof=HMACSHA256.HashData(key,challenge.Data.Concat(Encoding.UTF8.GetBytes(options.Id+"|"+channel)).ToArray());
            await wire.SendAsync(new(){Type="hello",Version=Updates.VersionText,UpdateProtocol=1,RemoteViewOnly=options.RemoteViewOnly,Id=options.Id,Device=channel,Text=Environment.MachineName,Data=proof,Screens=KvmScreen.Local()},timeout.Token);
            var welcome=await wire.ReadAsync(timeout.Token);
            if(welcome.Type!="welcome"||!Guid.TryParseExact(welcome.Id,"N",out _)||welcome.Text.Length>100)throw new AuthenticationException("Invalid host reply.");
            ValidateScreens(welcome.Screens);
            wire.Peer=new(welcome.Id,welcome.Text,welcome.Screens!,welcome.Version,Math.Clamp(welcome.UpdateProtocol,0,1));wire.StartWriter();return wire;
        }
        catch{wire?.Dispose();tcp.Dispose();throw;}
    }
    public static void ValidateScreens(KvmScreen[]? screens)
    {
        if(screens==null||screens.Length>16||screens.Any(s=>s==null)||screens.Select(s=>s.Device).Distinct().Count()!=screens.Length)throw new IOException("Invalid monitor list.");
        foreach(var s in screens)if(string.IsNullOrEmpty(s.Device)||s.Device.Length>128||s.Width<1||s.Width>16384||s.Height<1||s.Height>16384||Math.Abs((long)s.X)>100000||Math.Abs((long)s.Y)>100000)throw new IOException("Invalid monitor bounds.");
    }
    public void SetScreens(KvmScreen[] screens,bool remoteOnly)=>Peer=Peer with{Screens=screens,RemoteViewOnly=remoteOnly};
    void StartWriter()=>writer=Task.Run(async()=>
    {
        try{await foreach(var queued in queue.Reader.ReadAllAsync(stop.Token))
        {
            var message=queued;
            // High-polling-rate mice should send the newest absolute position, not a backlog.
            // Never cross a key/button/wheel boundary: input ordering remains intact.
            if(message.Type=="mouse"&&message.Flags==0)
                while(queue.Reader.TryPeek(out var next)&&next.Type=="mouse"&&next.Flags==0&&queue.Reader.TryRead(out var latest))message=latest;
            await SendAsync(message,stop.Token);
        }}
        catch(OperationCanceledException){}catch(Exception){Dispose();}
    });
    public bool Post(KvmMessage message)
    {
        if(stop.IsCancellationRequested)return false;
        if(queue.Writer.TryWrite(message))return true;
        // Never drop a key-up: disconnect releases every injected key/button.
        Dispose();return false;
    }
    public async Task SendAsync(KvmMessage message,CancellationToken token)
    {
        byte[] data=JsonSerializer.SerializeToUtf8Bytes(message);
        if(data.Length>MaximumPacket)throw new IOException("KVM packet too large.");
        byte[] packet=new byte[4+data.Length];BinaryPrimitives.WriteInt32LittleEndian(packet,data.Length);data.CopyTo(packet,4);
        await writeGate.WaitAsync(token);
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);deadline.CancelAfter(10000);
            await stream.WriteAsync(packet,deadline.Token);
        }
        finally{writeGate.Release();}
    }
    public async Task<KvmMessage> ReadAsync(CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);deadline.CancelAfter(15000);
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,deadline.Token);
        int size=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(size<2||size>MaximumPacket)throw new IOException("Invalid KVM packet length.");
        byte[] data=new byte[size];await stream.ReadExactlyAsync(data,deadline.Token);
        return JsonSerializer.Deserialize<KvmMessage>(data)??throw new IOException("Empty KVM packet.");
    }
    public void Dispose(){if(stop.IsCancellationRequested)return;stop.Cancel();queue.Writer.TryComplete();tcp.Dispose();}
}
