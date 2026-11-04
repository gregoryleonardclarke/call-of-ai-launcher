using CallOfAI;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length==2 && args[0]=="--smoke")
{
 var feed=Path.GetFullPath(args[1]);
 var updater=new Updates(Path.Combine(feed,"installed"),File.ReadAllText(Path.Combine(feed,"..","src","release-public.pem")),true);
 var release=await updater.Check("game",CancellationToken.None,new Uri(Path.Combine(feed,"game-"+Updates.Platform+".json")).AbsoluteUri);
 await updater.Install("game",release,new Progress<string>(),CancellationToken.None);
 Console.WriteLine("PASS: real signed game package installed");
 using var game=updater.Play();
 await Task.Delay(12000);
 if(game.HasExited) throw new Exception("Game exited during smoke test: "+game.ExitCode);
 Console.WriteLine("PASS: exported game remained running for 12 seconds");
 game.CloseMainWindow();
 if(!game.WaitForExit(8000))game.Kill();
 return;
}
var root=Path.Combine(Path.GetTempPath(),"call-of-ai-tests-"+Guid.NewGuid());
Directory.CreateDirectory(root);
using var key=RSA.Create(2048);
var service=new Updates(Path.Combine(root,"home"),key.ExportSubjectPublicKeyInfoPem(),true);
var progress=new Progress<string>();
int passed=0;
void Assert(bool condition,string name){if(!condition)throw new Exception("FAILED: "+name);Console.WriteLine("PASS: "+name);passed++;}
async Task Reject(Func<Task> action,string name){bool failed=false;try{await action();}catch(Exception e) when(e is InvalidDataException or OperationCanceledException or IOException){failed=true;}Assert(failed,name);}
byte[] Sign(Package p){var bytes=JsonSerializer.SerializeToUtf8Bytes(p);return JsonSerializer.SerializeToUtf8Bytes(new SignedRelease(Convert.ToBase64String(bytes),Convert.ToBase64String(key.SignData(bytes,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))));}
Package Make(string version,string entry="game.exe",string component="game")
{
 var path=Path.Combine(root,Guid.NewGuid()+".zip");
 using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)){using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write("test game "+version);}
 var bytes=File.ReadAllBytes(path);
 return new Package(version,Updates.Platform,new Uri(path).AbsoluteUri,Convert.ToHexString(SHA256.HashData(bytes)),bytes.Length,"game.exe","Test",component);
}
try
{
 var first=Make("1.0.0");
 Assert(LauncherFlow.Action(null,false,first)==GameAction.Install,"absent game shows INSTALL");
 Assert(LauncherFlow.Action(new Installation("1.0.0","test","game.exe"),false,first)==GameAction.Install,"missing executable shows INSTALL");
 Assert(LauncherFlow.Action(new Installation("0.9.0","test","game.exe"),true,first)==GameAction.Update,"older game shows UPDATE");
 Assert(LauncherFlow.Action(new Installation("1.0.0.0","test","game.exe"),true,first)==GameAction.Play,"current game shows PLAY with normalized version");
 Assert(LauncherFlow.Action(new Installation("2.0.0","test","game.exe"),true,first)==GameAction.Play,"newer installed game is not downgraded");
 var steps=new List<string>();
 Task<Package> CheckFlow(string component){steps.Add("check-"+component);return Task.FromResult(first with{Component=component});}
 Task InstallFlow(Package p){steps.Add("install-launcher");return Task.CompletedTask;}
 void RestartFlow(){steps.Add("restart");}
 var ready=await LauncherFlow.Startup("1.0.0.0",CheckFlow,InstallFlow,RestartFlow);
 Assert(ready!=null && string.Join(",",steps)=="check-launcher,check-game","startup checks launcher before game");
 steps.Clear();
 ready=await LauncherFlow.Startup("0.9.0",CheckFlow,InstallFlow,RestartFlow);
 Assert(ready==null && string.Join(",",steps)=="check-launcher,install-launcher,restart","outdated launcher installs and restarts without game check");
 steps.Clear();
 await Reject(()=>LauncherFlow.Startup("0.9.0",CheckFlow,_=>throw new IOException("download failed"),RestartFlow),"failed launcher update propagates error");
 Assert(string.Join(",",steps)=="check-launcher","failed launcher update never restarts or checks game");
 steps.Clear();
 await Reject(()=>LauncherFlow.Startup("1.0.0",_=>throw new IOException("offline"),InstallFlow,RestartFlow),"failed launcher check blocks startup");
 Assert(steps.Count==0,"failed check never installs or restarts");
 Assert(service.Verify(Sign(first))==first,"signed manifest verification");
 var envelope=JsonSerializer.Deserialize<SignedRelease>(Sign(first))!;
 var forged=envelope with{Payload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(first with{Version="9.0.0"}))};
 await Reject(()=>Task.Run(()=>service.Verify(JsonSerializer.SerializeToUtf8Bytes(forged))),"tampered signature rejected");
 await Reject(()=>Task.Run(()=>service.Verify(Sign(first with{Platform="wrong-platform"}))),"wrong platform rejected");
 await service.Install("game",first,progress,CancellationToken.None);
 Assert(service.Current("game")!.Version=="1.0.0","fresh installation");
 var before=service.Current("game");
 await Reject(()=>service.Install("game",Make("2.0.0") with{Sha256=new string('0',64)},progress,CancellationToken.None),"corrupt download rejected");
 Assert(service.Current("game")==before,"failed update preserves installation");
 await Reject(()=>service.Install("game",Make("2.0.0") with{Bytes=1},progress,CancellationToken.None),"oversize download rejected");
 await Reject(()=>service.Install("game",Make("2.0.0") with{Bytes=99999},progress,CancellationToken.None),"truncated download rejected");
 await Reject(()=>service.Install("game",Make("2.0.0","../escape.exe"),progress,CancellationToken.None),"ZIP traversal rejected");
 await Reject(()=>service.Install("game",Make("2.0.0","other.txt"),progress,CancellationToken.None),"missing executable rejected");
 await Reject(()=>service.Install("game",Make("2.0.0"),progress,new CancellationToken(true)),"cancelled install rejected");
 Assert(service.Current("game")==before,"cancel and extraction failures preserve installation");
 var settings=Path.Combine(root,"home","player-settings.txt"); File.WriteAllText(settings,"keep me");
 await service.Install("game",Make("2.0.0"),progress,CancellationToken.None);
 Assert(service.Current("game")!.Version=="2.0.0","game upgrade");
 await Reject(()=>service.Install("game",first,progress,CancellationToken.None),"release downgrade rejected");
 service.Rollback();Assert(service.Current("game")!.Version=="1.0.0","previous version recovery");
 service.Rollback();Assert(service.Current("game")!.Version=="2.0.0","rollback reversible");
 File.WriteAllText(service.Executable("game",service.Current("game")!),"broken");
 await service.Install("game",Make("2.0.0"),progress,CancellationToken.None);
 Assert(File.ReadAllText(service.Executable("game",service.Current("game")!))=="test game 2.0.0","repair replaces damaged files");
 Assert(File.ReadAllText(settings)=="keep me","player data preserved");
 await Reject(()=>service.Install("launcher",first,progress,CancellationToken.None),"cross-component substitution rejected");
 await service.Install("launcher",Make("0.2.0",component:"launcher"),progress,CancellationToken.None);
 Assert(service.Current("launcher")!.Version=="0.2.0","launcher update staged separately");
 using(var gate=new FileStream(Path.Combine(root,"home","update.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
 await Reject(()=>service.Install("game",Make("3.0.0"),progress,CancellationToken.None),"concurrent update blocked");
 foreach(var path in new[]{"/absolute","C:/bad","a/../bad","a\\bad","../bad"})await Reject(()=>Task.Run(()=>Updates.SafePath(root,path)),"unsafe path rejected: "+path);
 Assert(!Directory.EnumerateFileSystemEntries(Path.Combine(root,"home","game"),".stage-*").Any(),"failed staging cleaned up");
 Console.WriteLine($"ALL {passed} CHECKS PASSED");
}
finally{Directory.Delete(root,true);}