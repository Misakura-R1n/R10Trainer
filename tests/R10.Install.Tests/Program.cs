using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using R10.Bridge.Install;
using R10.Core.Memory;

int passed=0,failed=0,skipped=0;
void Check(bool condition,string message) { if (!condition) throw new Exception(message); }
async Task Reject(Func<Task> action)
{
    try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { return; }
    throw new Exception("Expected rejection");
}
async Task Test(string name,Func<Task> action)
{
    try { int previousSkips=skipped; await action(); if (skipped==previousSkips) { Console.WriteLine("PASS "+name); passed++; } }
    catch (Exception error) { Console.WriteLine("FAIL "+name+": "+error); failed++; }
}

await Test("install/restore preserves every original byte and existing save configuration",async()=>
{
    using var f=new Fixture(); var installer=f.Installer();
    var initial=await installer.InspectAsync(f.Game); Check(initial.CanInstall && !initial.IsInstalled,"initial status");
    var result=await installer.InstallAsync(f.Game); Check(result.IsInstalled,"install status");
    Check(AinPatch.Hash(AinPatch.Decode(File.ReadAllBytes(f.File("Rance10.ain"))))==f.Profile.TargetPlainSha256,"output identity");
    var ini=File.ReadAllBytes(f.File("AliceStart.ini"));
    Check(Encoding.Latin1.GetString(ini).Replace("    \"R10Bridge.dll\",\r\n","")==Encoding.Latin1.GetString(f.Ini),"only DLL line changes");
    Check((await installer.InspectAsync(f.Game)).CanRestore,"restore offered");
    await installer.RestoreAsync(f.Game); f.AssertOriginal();
    Check(Directory.EnumerateDirectories(f.Game,".r10trainer-hp.restored-*").Any(),"backup retained");
});
await Test("repeat installation is idempotent",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    var before=File.ReadAllBytes(f.File("Rance10.ain")); await installer.InstallAsync(f.Game);
    Check(before.SequenceEqual(File.ReadAllBytes(f.File("Rance10.ain"))),"AIN changed twice");
});
await Test("new component package offers old installation recovery without overwriting",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    var before=File.ReadAllBytes(f.File("Rance10.ain"));
    File.WriteAllText(Path.Combine(f.Package,"bridge","R10Bridge.dll"),"new component version");
    var status=await installer.InspectAsync(f.Game);
    Check(status.CanRestore && !status.IsInstalled && !status.CanInstall,"old component must offer restore first");
    Check(before.SequenceEqual(File.ReadAllBytes(f.File("Rance10.ain"))),"inspect changed game");
    await installer.RestoreAsync(f.Game); f.AssertOriginal();
});
await Test("install again after restore builds fresh backup for current INI",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game); await installer.RestoreAsync(f.Game);
    var configured=f.Ini.Concat(Encoding.ASCII.GetBytes("; custom setting\r\n")).ToArray(); File.WriteAllBytes(f.File("AliceStart.ini"),configured);
    await installer.InstallAsync(f.Game); await installer.RestoreAsync(f.Game);
    Check(configured.SequenceEqual(File.ReadAllBytes(f.File("AliceStart.ini"))),"user setting lost");
});
await Test("AIN-only upgrade with unchanged DLL is detected and restores before reinstall",async()=>
{
    using var f=new Fixture(); await f.Installer().InstallAsync(f.Game);
    var oldAin=File.ReadAllBytes(f.File("Rance10.ain"));
    var sourcePlain=AinPatch.Decode(f.Ain); var addition=Encoding.ASCII.GetBytes("authored-hook-v2");
    var target=sourcePlain.Concat(addition).ToArray();
    var profile=f.Profile with { TargetPlainSha256=AinPatch.Hash(target), TargetPlainLength=target.Length };
    var recipe=new PatchRecipe(1,profile.SourceSha256,AinPatch.Hash(sourcePlain),profile.TargetPlainSha256,sourcePlain.Length,target.Length,
        profile.ExeSha256,AinPatch.Hash(f.Dll),[new(sourcePlain.Length,0,AinPatch.Hash([]),Convert.ToBase64String(addition))]);
    File.WriteAllBytes(f.PatchPath,JsonSerializer.SerializeToUtf8Bytes(recipe,AinPatch.Json));
    var newer=new BridgeInstaller(f.Package,[profile]);
    var status=await newer.InspectAsync(f.Game);
    Check(status.State==BridgeInstallState.RecoveryRequired && status.CanRestore && !status.IsInstalled && !status.CanInstall,"AIN-only upgrade reported current");
    Check(!(await newer.InstallAsync(f.Game)).IsInstalled,"direct install mislabeled old AIN as current");
    Check(File.ReadAllBytes(f.File("Rance10.ain")).SequenceEqual(oldAin),"old install overwritten without restoration");
    // A missing/corrupt new recipe must not strand recovery of a known owned old installation.
    File.WriteAllText(f.PatchPath,"corrupt recipe");
    Check((await newer.InspectAsync(f.Game)).CanRestore,"corrupt recipe blocked old-version recovery");
    File.Delete(f.PatchPath);
    Check((await newer.InspectAsync(f.Game)).CanRestore,"missing recipe blocked old-version recovery");
    await newer.RestoreAsync(f.Game); f.AssertOriginal();
    File.WriteAllBytes(f.PatchPath,JsonSerializer.SerializeToUtf8Bytes(recipe,AinPatch.Json));
    Check((await newer.InstallAsync(f.Game)).IsInstalled && (await newer.InspectAsync(f.Game)).IsInstalled,"new script not recognized after reinstall");
    Check(AinPatch.Hash(AinPatch.Decode(File.ReadAllBytes(f.File("Rance10.ain"))))==profile.TargetPlainSha256,"wrong installed script");
    await newer.RestoreAsync(f.Game); f.AssertOriginal();
});
await Test("unknown DLL is never replaced",async()=>
{
    using var f=new Fixture(); var unknown=Encoding.ASCII.GetBytes("other owner"); File.WriteAllBytes(f.File("R10Bridge.dll"),unknown);
    await Reject(()=>f.Installer().InstallAsync(f.Game)); Check(File.ReadAllBytes(f.File("R10Bridge.dll")).SequenceEqual(unknown),"DLL changed");
    Check(File.ReadAllBytes(f.File("Rance10.ain")).SequenceEqual(f.Ain),"AIN changed");
});
await Test("unknown AIN and EXE reject before modifications",async()=>
{
    using var f=new Fixture(); File.AppendAllText(f.File("Rance10.ain"),"other patch");
    await Reject(()=>f.Installer().InstallAsync(f.Game)); Check(!Directory.Exists(f.Journal),"journal created");
    File.WriteAllBytes(f.File("Rance10.ain"),f.Ain); File.AppendAllText(f.File("Rance10.exe"),"other executable");
    await Reject(()=>f.Installer().InstallAsync(f.Game)); Check(!File.Exists(f.File("R10Bridge.dll")),"DLL created");
});
await Test("restore preflights all files before touching any",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    var ain=File.ReadAllBytes(f.File("Rance10.ain")); File.AppendAllText(f.File("AliceStart.ini"),"; user edited\r\n");
    await Reject(()=>installer.RestoreAsync(f.Game));
    Check(File.ReadAllBytes(f.File("Rance10.ain")).SequenceEqual(ain) && File.Exists(f.File("R10Bridge.dll")),"partial restore on unknown input");
});
await Test("corrupt original backup prevents every restore write",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    File.AppendAllText(Path.Combine(f.Journal,"Rance10.ain.original"),"corruption");
    await Reject(()=>installer.RestoreAsync(f.Game)); Check(File.Exists(f.File("R10Bridge.dll")),"DLL removed despite corrupt backup");
});
await Test("failed installation rolls back already installed DLL",async()=>
{
    using var f=new Fixture(); bool injected=false;
    var installer=f.Installer(point=>{ if (!injected && point=="installed:R10Bridge.dll") { injected=true; throw new IOException("injected failure"); } });
    await Reject(()=>installer.InstallAsync(f.Game)); f.AssertOriginal();
});
await Test("failed installation after AIN replacement rolls back both targets",async()=>
{
    using var f=new Fixture(); bool injected=false;
    var installer=f.Installer(point=>{ if (!injected && point=="installed:Rance10.ain") { injected=true; throw new IOException("injected failure"); } });
    await Reject(()=>installer.InstallAsync(f.Game)); f.AssertOriginal();
});
await Test("interrupted mixed original/installed state can be restored",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    File.WriteAllBytes(f.File("AliceStart.ini"),f.Ini); // A crash between atomic replacements.
    var state=await installer.InspectAsync(f.Game); Check(state.State==BridgeInstallState.RecoveryRequired && state.CanRestore,"recovery not offered");
    await installer.RestoreAsync(f.Game); f.AssertOriginal();
});
await Test("failed rollback retains journal and later restore succeeds",async()=>
{
    using var f=new Fixture();
    var installer=f.Installer(point=>{ if (point is "installed:Rance10.ain" or "restored:AliceStart.ini") throw new IOException("failure"); });
    await Reject(()=>installer.InstallAsync(f.Game)); Check(Directory.Exists(f.Journal),"recovery journal lost");
    await f.Installer().RestoreAsync(f.Game); f.AssertOriginal();
});
await Test("pre-journal failure leaves original game intact",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(point=>{ if (point=="before-journal") throw new IOException("failure"); });
    await Reject(()=>installer.InstallAsync(f.Game)); f.AssertOriginal(); Check(!Directory.Exists(f.Journal),"published incomplete journal");
});
await Test("portable patch corruption rejects before game writes",async()=>
{
    using var f=new Fixture(); var patch=JsonSerializer.Deserialize<PatchRecipe>(File.ReadAllBytes(f.PatchPath),AinPatch.Json)!;
    File.WriteAllBytes(f.PatchPath,JsonSerializer.SerializeToUtf8Bytes(patch with { Edits=[new(5,10,AinPatch.Hash([1]),"YWJj")] },AinPatch.Json));
    await Reject(()=>f.Installer().InstallAsync(f.Game)); f.AssertOriginal();
});
await Test("missing portable DLL and malformed INI reject",async()=>
{
    using var f=new Fixture(); File.Delete(Path.Combine(f.Package,"bridge","R10Bridge.dll"));
    await Reject(()=>f.Installer().InstallAsync(f.Game)); f.AssertOriginal();
    File.WriteAllBytes(Path.Combine(f.Package,"bridge","R10Bridge.dll"),f.Dll);
    File.WriteAllText(f.File("AliceStart.ini"),"GameName=\"Fixture\"\r\nLoadDLL={}\r\n");
    await Reject(()=>f.Installer().InstallAsync(f.Game)); Check(!File.Exists(f.File("R10Bridge.dll")),"unexpected DLL");
});
await Test("recovery remains available if the distributed component asset is missing",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    File.Delete(Path.Combine(f.Package,"bridge","R10Bridge.dll")); File.Delete(f.PatchPath);
    Check((await installer.InspectAsync(f.Game)).CanRestore,"recovery depends on package assets");
    await installer.RestoreAsync(f.Game); f.AssertOriginal();
});
await Test("redirected or missing CodeName is rejected before modifications",async()=>
{
    using var f=new Fixture(); var installer=f.Installer();
    foreach (var replacement in new[]{"CodeName = \"other.ain\"\r\n",""})
    {
        File.WriteAllText(f.File("AliceStart.ini"),Encoding.Latin1.GetString(f.Ini).Replace("CodeName = \"Rance10.ain\"\r\n",replacement),Encoding.Latin1);
        await Reject(()=>installer.InstallAsync(f.Game));
        Check(!File.Exists(f.File("R10Bridge.dll")) && File.ReadAllBytes(f.File("Rance10.ain")).SequenceEqual(f.Ain),"redirected config modified game");
        Check(!(await installer.InspectAsync(f.Game)).CanInstall,"redirected config offered install");
    }
});
await Test("duplicate CodeName is rejected even if values agree",async()=>
{
    using var f=new Fixture(); File.AppendAllText(f.File("AliceStart.ini"),"CodeName = \"Rance10.ain\"\r\n");
    await Reject(()=>f.Installer().InstallAsync(f.Game));
    Check(!File.Exists(f.File("R10Bridge.dll")) && !Directory.Exists(f.Journal),"duplicate config modified game");
});
await Test("sandbox GameName containing R10Bridge is preserved through installation and restoration",async()=>
{
    using var f=new Fixture(); var installer=f.Installer();
    byte[] configured=Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(f.Ini).Replace("GameName = \"Fixture\"","GameName = \"R10BridgeSandbox\""));
    File.WriteAllBytes(f.File("AliceStart.ini"),configured);
    Check((await installer.InspectAsync(f.Game)).CanInstall,"sandbox name was mistaken for DLL declaration");
    await installer.InstallAsync(f.Game);
    Check(Encoding.Latin1.GetString(File.ReadAllBytes(f.File("AliceStart.ini"))).Contains("GameName = \"R10BridgeSandbox\""),"save namespace changed");
    await installer.RestoreAsync(f.Game);
    Check(configured.SequenceEqual(File.ReadAllBytes(f.File("AliceStart.ini"))),"sandbox INI not restored exactly");
});
await Test("existing DLL declaration in mixed list is rejected",async()=>
{
    using var f=new Fixture();
    File.WriteAllBytes(f.File("AliceStart.ini"),Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(f.Ini).Replace("\"PCInfo.dll\",","\"PCInfo.dll\", \"R10Bridge.dll\",")));
    await Reject(()=>f.Installer().InstallAsync(f.Game)); Check(!File.Exists(f.File("R10Bridge.dll")),"preexisting declaration allowed mutation");
});
await Test("recognizes prepared matching component without claiming restoration",async()=>
{
    using var f=new Fixture(); await f.Installer().InstallAsync(f.Game); Directory.Move(f.Journal,f.Journal+".outside");
    var result=await f.Installer().InspectAsync(f.Game); Check(result.IsInstalled && !result.CanRestore,"unowned component status");
    await Reject(()=>f.Installer().RestoreAsync(f.Game));
});
await Test("malicious manifest cannot escape reserved backup directory",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    var path=Path.Combine(f.Journal,"manifest.json"); File.WriteAllText(path,File.ReadAllText(path).Replace("Rance10.ain","../victim"));
    await Reject(()=>installer.RestoreAsync(f.Game)); Check(File.Exists(f.File("R10Bridge.dll")),"restore proceeded");
});
await Test("decompression rejects declared oversized payload",()=>
{
    var raw=AinPatch.Encode(Encoding.ASCII.GetBytes("payload")); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8),int.MaxValue);
    bool rejected=false; try { _=AinPatch.Decode(raw); } catch (InvalidDataException) { rejected=true; }
    Check(rejected,"decompression bound absent"); return Task.CompletedTask;
});
await Test("manifest with missing original INI identity or null entry is rejected",async()=>
{
    using var f=new Fixture(); var installer=f.Installer(); await installer.InstallAsync(f.Game);
    var path=Path.Combine(f.Journal,"manifest.json"); string original=File.ReadAllText(path);
    var data=System.Text.Json.Nodes.JsonNode.Parse(original)!;
    data["files"]![2]!["originalHash"]=null; File.WriteAllText(path,data.ToJsonString());
    await Reject(()=>installer.RestoreAsync(f.Game)); Check(File.Exists(f.File("AliceStart.ini")),"INI deleted");
    data=System.Text.Json.Nodes.JsonNode.Parse(original)!; data["files"]![1]=null; File.WriteAllText(path,data.ToJsonString());
    await Reject(()=>installer.RestoreAsync(f.Game));
    Check((await installer.InspectAsync(f.Game)).State==BridgeInstallState.Error,"bad manifest did not produce error status");
});
await Test("source reparse point is rejected when platform permits fixture",async()=>
{
    using var f=new Fixture(); string linked=Path.Combine(f.Root,"linked");
    try { Directory.CreateSymbolicLink(linked,f.Game); }
    catch (Exception error) when (error is UnauthorizedAccessException or IOException) { skipped++; Console.WriteLine("SKIP symlink fixture: "+error.Message); return; }
    await Reject(()=>f.Installer().InstallAsync(linked)); f.AssertOriginal();
});

string? Arg(string name) { int index=Array.IndexOf(args,name); return index>=0 && index+1<args.Length ? args[index+1] : null; }
var sourceAins=args.Select((arg,index)=>(arg,index)).Where(item=>item.arg=="--source-ain")
    .Select(item=>Path.GetFullPath(args[item.index+1])).ToArray();
foreach (string sourceAin in sourceAins)
{
    await Test($"portable install and byte-exact restore: {Path.GetFileName(Path.GetDirectoryName(sourceAin))}",async()=>
    {
        string patchPath=Arg("--patch") ?? throw new ArgumentException("--patch required"),dllPath=Arg("--dll") ?? throw new ArgumentException("--dll required");
        using var f=new Fixture(false);
        string sourceDir=Path.GetDirectoryName(sourceAin)!;
        string primaryDir=Path.GetDirectoryName(sourceAins[0])!;
        string engineDir=File.Exists(Path.Combine(sourceDir,"Rance10.exe")) ? sourceDir : primaryDir;
        var sourceFiles=new Dictionary<string,string>
        {
            ["Rance10.exe"]=Path.Combine(engineDir,"Rance10.exe"),
            ["Rance10.ain"]=sourceAin,
            ["AliceStart.ini"]=Path.Combine(engineDir,"AliceStart.ini")
        };
        var expected=sourceFiles.ToDictionary(item=>item.Key,item=>File.ReadAllBytes(item.Value));
        string exeHash=AinPatch.Hash(expected["Rance10.exe"]), sourceHash=AinPatch.Hash(expected["Rance10.ain"]);
        var version=GameVersions.All.Single(v=>v.ExeSha256==exeHash && v.SourceSha256==sourceHash);
        foreach (var (name,bytes) in expected) File.WriteAllBytes(f.File(name),bytes);
        File.Copy(patchPath,f.PatchPath,true); File.Copy(dllPath,Path.Combine(f.Package,"bridge","R10Bridge.dll"),true);
        var installer=new BridgeInstaller(f.Package);
        if (version.Id=="japanese-original")
        {
            string chineseExe=Path.Combine(primaryDir,"Rance10.exe");
            if (AinPatch.Hash(File.ReadAllBytes(chineseExe))==GameVersions.BugFix.ExeSha256)
            {
                File.Copy(chineseExe,f.File("Rance10.exe"),true);
                Check((await installer.InspectAsync(f.Game)).State==BridgeInstallState.Unsupported,"crossed EXE/AIN pair recognized");
                await Reject(()=>installer.InstallAsync(f.Game));
                Check(!Directory.Exists(f.Journal) && !File.Exists(f.File("R10Bridge.dll")),"crossed pair modified");
                Check(File.ReadAllBytes(f.File("Rance10.ain")).SequenceEqual(expected["Rance10.ain"]),"crossed pair AIN changed");
                File.WriteAllBytes(f.File("Rance10.exe"),expected["Rance10.exe"]);
            }
        }
        Check(GameConnection.IsSupportedGameVersion(exeHash,GameConnection.ReadAinFingerprint(f.File("Rance10.ain"))),"runtime source identity");
        var initial=await installer.InspectAsync(f.Game);
        Check(initial.CanInstall && initial.Message.Contains(version.Name),"correct source version detected");
        Check((await installer.InstallAsync(f.Game)).IsInstalled,"component installed");
        var installed=await installer.InspectAsync(f.Game);
        Check(installed.IsInstalled && installed.CanRestore && !installed.CanInstall,"component recognized");
        string fingerprint=GameConnection.ReadAinFingerprint(f.File("Rance10.ain"));
        Check(fingerprint=="bridge-v1:"+version.TargetPlainSha256 && GameConnection.IsSupportedGameVersion(exeHash,fingerprint),"runtime installed identity");
        foreach (var otherEngine in GameVersions.All.Where(v=>v.ExeSha256!=exeHash).Select(v=>v.ExeSha256).Distinct())
            Check(!GameConnection.IsSupportedGameVersion(otherEngine,sourceHash) && !GameConnection.IsSupportedGameVersion(otherEngine,fingerprint),"runtime accepted crossed EXE/AIN pair");
        string originalIni=Encoding.Latin1.GetString(expected["AliceStart.ini"]);
        string installedIni=Encoding.Latin1.GetString(File.ReadAllBytes(f.File("AliceStart.ini")));
        Check(installedIni.Replace("    \"R10Bridge.dll\",\r\n","").Replace("    \"R10Bridge.dll\",\n","")==originalIni,"only INI declaration changes");
        await installer.RestoreAsync(f.Game);
        foreach (var (name,bytes) in expected) { Check(bytes.SequenceEqual(File.ReadAllBytes(f.File(name))),"roundtrip "+name); Check(bytes.SequenceEqual(File.ReadAllBytes(sourceFiles[name])),"source changed "+name); }
        Check(!File.Exists(f.File("R10Bridge.dll")),"DLL remains");
        Check((await installer.InspectAsync(f.Game)).CanInstall,"restored source recognized");
    });
}
Console.WriteLine($"RESULT: {passed} passed, {failed} failed, {skipped} skipped; fixtures and temporary file copies only, no game launched.");
return failed==0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    internal string Root { get; }=Path.Combine(Path.GetTempPath(),"R10InstallTests-"+Guid.NewGuid().ToString("N"));
    internal string Game=>Path.Combine(Root,"game");
    internal string Package=>Path.Combine(Root,"package");
    internal string Journal=>Path.Combine(Game,".r10trainer-hp");
    internal string PatchPath=>Path.Combine(Package,"bridge","hp-patch.json");
    internal byte[] Exe { get; }=Encoding.ASCII.GetBytes("fixture executable");
    internal byte[] Dll { get; }=Encoding.ASCII.GetBytes("fixture bridge library");
    internal byte[] Ini { get; }=Encoding.Latin1.GetBytes("; \u00d6\u00d0\u00ce\u00c4\r\nGameName = \"Fixture\"\r\nCodeName = \"Rance10.ain\"\r\nSaveFolder = \"SaveData\"\r\nLoadDLL = {\r\n    \"PCInfo.dll\",\r\n}\r\n");
    internal byte[] Ain { get; }
    internal GameVersion Profile { get; }
    internal Fixture(bool populate=true)
    {
        Directory.CreateDirectory(Game); Directory.CreateDirectory(Path.Combine(Package,"bridge"));
        byte[] plain=Encoding.ASCII.GetBytes("VERS fixture-game-script-contents"),replacement=Encoding.ASCII.GetBytes("authored-hook");
        byte[] target=plain.Concat(replacement).ToArray(); Ain=AinPatch.Encode(plain);
        Profile=new("fixture","测试版本",AinPatch.Hash(Exe),AinPatch.Hash(Ain),AinPatch.Hash(plain),AinPatch.Hash(target),target.Length);
        if (!populate) return;
        System.IO.File.WriteAllBytes(File("Rance10.exe"),Exe); System.IO.File.WriteAllBytes(File("Rance10.ain"),Ain); System.IO.File.WriteAllBytes(File("AliceStart.ini"),Ini);
        System.IO.File.WriteAllBytes(Path.Combine(Package,"bridge","R10Bridge.dll"),Dll);
        var recipe=new PatchRecipe(1,Profile.SourceSha256,AinPatch.Hash(plain),Profile.TargetPlainSha256,plain.Length,target.Length,Profile.ExeSha256,AinPatch.Hash(Dll),[new(plain.Length,0,AinPatch.Hash([]),Convert.ToBase64String(replacement))]);
        System.IO.File.WriteAllBytes(PatchPath,JsonSerializer.SerializeToUtf8Bytes(recipe,AinPatch.Json));
    }
    internal BridgeInstaller Installer(Action<string>? failure=null)=>new(Package,[Profile],failure);
    internal string File(string name)=>Path.Combine(Game,name);
    internal void AssertOriginal()
    {
        if (!System.IO.File.ReadAllBytes(File("Rance10.ain")).SequenceEqual(Ain) || !System.IO.File.ReadAllBytes(File("AliceStart.ini")).SequenceEqual(Ini) || System.IO.File.Exists(File("R10Bridge.dll"))) throw new Exception("Original game bytes differ");
    }
    public void Dispose()
    {
        string absolute=Path.GetFullPath(Root),temporary=Path.GetFullPath(Path.GetTempPath());
        if (absolute.StartsWith(temporary,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("R10InstallTests-",StringComparison.Ordinal)) Directory.Delete(absolute,true);
    }
}
