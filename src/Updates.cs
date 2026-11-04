using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CallOfAI;

public record Package(string Version, string Platform, string Url, string Sha256, long Bytes, string Entry, string Notes, string Component);
public record SignedRelease(string Payload, string Signature);
public record Installation(string Version, string Folder, string Entry);

public sealed class Updates
{
    public const string Releases = "https://github.com/gregoryleonardclarke/call-of-ai-launcher/releases/download/stable/";
    public static string Platform => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CallOfAI");
    public readonly string Home;
    readonly string publicKey;
    readonly bool localTest;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
    public Updates(string home, string key, bool localTest = false) { Home = home; publicKey = key; this.localTest = localTest; Directory.CreateDirectory(home); }
    public static string EmbeddedKey()
    {
        var assembly = typeof(Updates).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith("release-public.pem"));
        if (name == null) throw new InvalidOperationException("Release verification key missing. This build cannot accept updates.");
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
    static void Component(string component) { if (component is not ("game" or "launcher")) throw new InvalidDataException("Unknown component"); }
    public Installation? Current(string component)
    {
        Component(component);
        var path = Path.Combine(Home, component, "current.json");
        if (!File.Exists(path)) return null;
        var current = JsonSerializer.Deserialize<Installation>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid installation record");
        if (Path.GetFileName(current.Folder) != current.Folder) throw new InvalidDataException("Invalid installation folder");
        SafePath(Path.Combine(Home, component, current.Folder), current.Entry);
        return current;
    }
    public string Executable(string component, Installation current) => SafePath(Path.Combine(Home, component, current.Folder), current.Entry);
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') || Path.IsPathRooted(relative)) throw new InvalidDataException("Unsafe package path");
        if (relative.Split('/').Any(p => p is ".." or ".")) throw new InvalidDataException("Unsafe package path");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Path escapes installation");
        return full;
    }
    async Task<Stream> Open(string url, CancellationToken cancel)
    {
        var uri = new Uri(url);
        if (localTest && uri.IsFile) return File.OpenRead(uri.LocalPath);
        if (uri.Scheme != "https") throw new InvalidDataException("Updates require HTTPS");
        return await http.GetStreamAsync(uri, cancel);
    }
    public Package Verify(byte[] document)
    {
        var envelope = JsonSerializer.Deserialize<SignedRelease>(document) ?? throw new InvalidDataException("Invalid release");
        var payload = Convert.FromBase64String(envelope.Payload);
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new InvalidDataException("Release signature is invalid");
        var p = JsonSerializer.Deserialize<Package>(payload) ?? throw new InvalidDataException("Empty release");
        if (p.Platform != Platform || !Version.TryParse(p.Version, out _) || !Regex.IsMatch(p.Sha256, "^[A-Fa-f0-9]{64}$") || p.Bytes <= 0 || p.Bytes > 2147483648L) throw new InvalidDataException("Invalid or incompatible release");
        SafePath(Home,p.Entry);
        return p;
    }
    public async Task<Package> Check(string component, CancellationToken cancel, string? source = null)
    {
        Component(component);
        using var stream = await Open(source ?? Releases + component + "-" + Platform + ".json", cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk,cancel))>0) { if(buffer.Length+count>262144) throw new InvalidDataException("Manifest too large"); await buffer.WriteAsync(chunk.AsMemory(0,count),cancel); }
        var package=Verify(buffer.ToArray());
        if(package.Component!=component) throw new InvalidDataException("Release component mismatch");
        return package;
    }
    public async Task Install(string component, Package package, IProgress<string> progress, CancellationToken cancel)
    {
        Component(component);
        if(package.Component!=component) throw new InvalidDataException("Release component mismatch");
        Directory.CreateDirectory(Path.Combine(Home,component));
        using var gate = new FileStream(Path.Combine(Home,"update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var previous = Current(component);
        if(previous != null && Version.Parse(package.Version)<Version.Parse(previous.Version)) throw new InvalidDataException("Refusing an older release");
        var stage = Path.Combine(Home, component, ".stage-"+Guid.NewGuid().ToString("N"));
        var zip = stage+".zip";
        Directory.CreateDirectory(stage);
        try
        {
            using(var source = await Open(package.Url,cancel))
            using(var target = File.Create(zip))
            {
                var bytes = new byte[131072]; long total=0; int n;
                while((n=await source.ReadAsync(bytes,cancel))>0)
                {
                    total+=n; if(total>package.Bytes) throw new InvalidDataException("Download larger than signed size");
                    await target.WriteAsync(bytes.AsMemory(0,n),cancel);
                    progress.Report($"Downloading {component}: {100*total/package.Bytes}%");
                }
                if(total!=package.Bytes) throw new InvalidDataException("Download incomplete");
            }
            using(var source=File.OpenRead(zip))
                if(!Convert.ToHexString(await SHA256.HashDataAsync(source,cancel)).Equals(package.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded package failed verification");
            progress.Report("Verified. Extracting…");
            using(var archive=ZipFile.OpenRead(zip))
            {
                long expanded=0;
                if(archive.Entries.Count>50000) throw new InvalidDataException("Too many files");
                foreach(var entry in archive.Entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    expanded+=entry.Length;
                    if(expanded>12L*1024*1024*1024) throw new InvalidDataException("Package expands beyond allowed size");
                    if(((entry.ExternalAttributes>>16)&0xF000)==0xA000) throw new InvalidDataException("Symlinks are not allowed");
                    var destination=SafePath(stage,entry.FullName.TrimEnd('/'));
                    if(entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input=entry.Open(); using var output=new FileStream(destination,FileMode.CreateNew);
                    await input.CopyToAsync(output,cancel);
                }
            }
            var executable=SafePath(stage,package.Entry);
            if(!File.Exists(executable)) throw new InvalidDataException("Package entry point missing");
            if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);
            cancel.ThrowIfCancellationRequested();
            var folder=package.Version+"-"+Guid.NewGuid().ToString("N")[..8];
            Directory.Move(stage,Path.Combine(Home,component,folder));
            if(previous!=null) Atomic(Path.Combine(Home,component,"previous.json"),JsonSerializer.Serialize(previous));
            Atomic(Path.Combine(Home,component,"current.json"),JsonSerializer.Serialize(new Installation(package.Version,folder,package.Entry)));
            progress.Report($"{component} {package.Version} installed.");
        }
        finally { if(File.Exists(zip)) File.Delete(zip); if(Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    static void Atomic(string path,string text) { var temp=path+".new"; File.WriteAllText(temp,text); File.Move(temp,path,true); }
    public void Rollback()
    {
        using var gate = new FileStream(Path.Combine(Home,"update.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var path=Path.Combine(Home,"game","previous.json");
        if(!File.Exists(path)) throw new InvalidOperationException("No previous game version available");
        var old=JsonSerializer.Deserialize<Installation>(File.ReadAllText(path))!;
        if(Path.GetFileName(old.Folder)!=old.Folder || !File.Exists(Executable("game",old))) throw new InvalidDataException("Previous installation missing");
        var current=Current("game")!;
        Atomic(Path.Combine(Home,"game","current.json"),JsonSerializer.Serialize(old));
        Atomic(path,JsonSerializer.Serialize(current));
    }
    public Process Play()
    {
        var current=Current("game") ?? throw new InvalidOperationException("Install the game first");
        var exe=Executable("game",current);
        return Process.Start(new ProcessStartInfo(exe){WorkingDirectory=Path.GetDirectoryName(exe)!,UseShellExecute=false}) ?? throw new IOException("Game did not start");
    }
}