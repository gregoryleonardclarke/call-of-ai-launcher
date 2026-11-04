using CallOfAI;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length==3 && args[0]=="keygen")
{
    if(File.Exists(args[1])||File.Exists(args[2]))throw new IOException("Refusing to overwrite signing keys");
    using var rsa=RSA.Create(3072);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    File.WriteAllText(args[1],rsa.ExportRSAPrivateKeyPem());
    File.WriteAllText(args[2],rsa.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine("Signing key created outside the source repository. Back it up securely.");return;
}
if(args.Length>=10 && args[0]=="pack")
{
    // pack <folder> <entry> <version> <rid> <download-url> <private-key> <zip> <manifest> [notes]
    var folder=Path.GetFullPath(args[1]);
    if(!File.Exists(Updates.SafePath(folder,args[2])))throw new IOException("Entry point missing");
    if(File.Exists(args[7]))throw new IOException("ZIP already exists; choose a new output");
    ZipFile.CreateFromDirectory(folder,args[7],CompressionLevel.Optimal,false);
    using var file=File.OpenRead(args[7]);
    if(args[9] is not ("game" or "launcher")) throw new ArgumentException("Component must be game or launcher");
    var p=new Package(args[3],args[4],args[5],Convert.ToHexString(SHA256.HashData(file)),file.Length,args[2],args.Length>10?args[10]:"New release",args[9]);
    var payload=JsonSerializer.SerializeToUtf8Bytes(p);
    using var rsa=RSA.Create();rsa.ImportFromPem(File.ReadAllText(args[6]));
    var envelope=new SignedRelease(Convert.ToBase64String(payload),Convert.ToBase64String(rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pss)));
    File.WriteAllText(args[8],JsonSerializer.Serialize(envelope));Console.WriteLine("Package and signed manifest created.");return;
}
Console.WriteLine("keygen <private.pem> <public.pem>\npack <export-folder> <entry> <version> <rid> <url> <private.pem> <zip> <manifest> <game|launcher> [notes]");
Environment.ExitCode=1;