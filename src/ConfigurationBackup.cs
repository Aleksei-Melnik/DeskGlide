using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SdrCapture;
sealed record ConfigurationProfile(int Format,string Computer,DateTimeOffset Created,Settings Settings,bool Autorun,string? HostIdentity);
sealed record ConfigurationEnvelope(int Format,string Salt,string Nonce,string Tag,string Data);
static class ConfigurationBackup
{
    const int MaximumBytes=2*1024*1024;
    const int Iterations=210000;
    public static ConfigurationProfile Capture(Settings settings,bool autorun,string root)
    {
        string? identity=null;
        string path=Path.Combine(root,"Kvm","host-identity.json");
        if(File.Exists(path))identity=File.ReadAllText(path);
        if(settings.Kvm.Role=="Host"&&identity==null){using var created=new KvmIdentity(Path.Combine(root,"Kvm"));identity=File.ReadAllText(path);}
        var profile=new ConfigurationProfile(1,Environment.MachineName,DateTimeOffset.UtcNow,settings.Copy(),autorun,identity);Validate(profile);return profile;
    }
    public static byte[] Encode(ConfigurationProfile profile,string password)
    {
        Validate(profile);if(password.Length<8)throw new ArgumentException("Use at least 8 characters for the backup password.");
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(profile),salt=RandomNumberGenerator.GetBytes(16),nonce=RandomNumberGenerator.GetBytes(12),tag=new byte[16],data=new byte[plain.Length];
        byte[] key=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        try{using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,data,tag,"ScreenCapture profile v1"u8);}
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
        return JsonSerializer.SerializeToUtf8Bytes(new ConfigurationEnvelope(1,Convert.ToBase64String(salt),Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(data)));
    }
    public static ConfigurationProfile Decode(byte[] bytes,string password)
    {
        if(bytes.Length>MaximumBytes)throw new IOException("The profile file is too large.");
        var envelope=JsonSerializer.Deserialize<ConfigurationEnvelope>(bytes)??throw new IOException("The backup is empty.");
        if(envelope.Format!=1)throw new IOException("This backup version is not supported yet.");
        byte[] salt=Convert.FromBase64String(envelope.Salt),nonce=Convert.FromBase64String(envelope.Nonce),tag=Convert.FromBase64String(envelope.Tag),data=Convert.FromBase64String(envelope.Data);
        if(salt.Length!=16||nonce.Length!=12||tag.Length!=16)throw new IOException("The profile file is damaged.");
        byte[] key=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32),plain=new byte[data.Length];
        try
        {
            using var aes=new AesGcm(key,16);
            try{aes.Decrypt(nonce,data,tag,plain,"ScreenCapture profile v1"u8);}catch(CryptographicException){throw new IOException("Incorrect password or damaged backup.");}
            var profile=JsonSerializer.Deserialize<ConfigurationProfile>(plain)??throw new IOException("The profile is empty.");Validate(profile);return profile;
        }
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    public static void Validate(ConfigurationProfile profile)
    {
        if(profile.Format!=1||profile.Settings==null||profile.Settings.Kvm==null||profile.Settings.Replay==null||profile.Settings.Updates==null||profile.Settings.Discord==null)throw new IOException("The profile is incomplete.");
        var s=profile.Settings;s.Replay.Validate();s.Kvm.Validate();s.Discord.Validate();
        if(s.NdiAudioVolume is <0 or >100||s.Replay.HotkeyModifiers is 0 or >7||s.Replay.HotkeyKey<(uint)Keys.F1||s.Replay.HotkeyKey>(uint)Keys.F12)throw new IOException("The profile settings are invalid.");
        if(profile.HostIdentity!=null)
        {
            if(profile.HostIdentity.Length>65536)throw new IOException("The KVM key is invalid.");
            var identity=JsonSerializer.Deserialize<string[]>(profile.HostIdentity);
            if(identity?.Length!=2||Convert.FromBase64String(identity[1]).Length!=32)throw new IOException("The KVM key is incomplete.");
            using var cert=X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(identity[0]),"",X509KeyStorageFlags.EphemeralKeySet);
            if(!cert.HasPrivateKey||cert.NotAfter.ToUniversalTime()<DateTime.UtcNow)throw new IOException("The KVM key is missing its private key or has expired.");
        }
        else if(s.Kvm.Role=="Host")throw new IOException("The backup does not contain the host key.");
    }
    public static void Restore(ConfigurationProfile profile,string root,Action<int>? checkpoint=null)
    {
        Validate(profile);
        string settings=Path.Combine(root,"settings.json"),identity=Path.Combine(root,"Kvm","host-identity.json");
        byte[]? oldSettings=File.Exists(settings)?File.ReadAllBytes(settings):null,oldIdentity=File.Exists(identity)?File.ReadAllBytes(identity):null;
        try
        {
            if(profile.HostIdentity!=null)AtomicWrite(identity,System.Text.Encoding.UTF8.GetBytes(profile.HostIdentity));
            checkpoint?.Invoke(1);AtomicWrite(settings,JsonSerializer.SerializeToUtf8Bytes(profile.Settings));checkpoint?.Invoke(2);
        }
        catch
        {
            if(oldIdentity!=null)AtomicWrite(identity,oldIdentity);else if(File.Exists(identity))File.Delete(identity);
            if(oldSettings!=null)AtomicWrite(settings,oldSettings);else if(File.Exists(settings))File.Delete(settings);
            throw;
        }
    }
    static void AtomicWrite(string path,byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(tmp,content);File.Move(tmp,path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
}
