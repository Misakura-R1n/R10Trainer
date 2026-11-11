using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using R10.Core.Memory;

namespace R10.Bridge.Install;

public enum BridgeInstallState { NotInstalled, Installed, RecoveryRequired, Unsupported, Error }
public sealed record BridgeInstallStatus(BridgeInstallState State, string Message, bool CanInstall, bool CanRestore, bool IsInstalled);

/// <summary>Local, version-pinned installer. It never reads or edits saves, and never launches the game.</summary>
public sealed class BridgeInstaller
{
    private const string JournalName=".r10trainer-hp";
    private static readonly string[] TargetNames=["R10Bridge.dll","Rance10.ain","AliceStart.ini"];
    private readonly string _package;
    private readonly IReadOnlyList<GameVersion> _versions;
    private readonly Action<string>? _failurePoint;
    public BridgeInstaller(string packageDirectory) : this(packageDirectory,GameVersions.All) { }
    internal BridgeInstaller(string packageDirectory,IReadOnlyList<GameVersion> versions,Action<string>? failurePoint=null)
    { _package=Path.GetFullPath(packageDirectory); _versions=versions; _failurePoint=failurePoint; }

    public Task<BridgeInstallStatus> InspectAsync(string gameDirectory) => Task.Run(()=>
    {
        try { return Inspect(ValidateDirectory(gameDirectory)); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or JsonException or Win32Exception)
        { return new(BridgeInstallState.Error,error.Message,false,false,false); }
    });
    public Task<BridgeInstallStatus> InstallAsync(string gameDirectory) => Task.Run(()=>WithLock(gameDirectory,Install));
    public Task<BridgeInstallStatus> RestoreAsync(string gameDirectory) => Task.Run(()=>WithLock(gameDirectory,Restore));

    private BridgeInstallStatus WithLock(string directory,Func<string,string,BridgeInstallStatus> action)
    {
        directory=ValidateDirectory(directory);
        string lockName="Local\\R10Trainer.HpInstall."+AinPatch.Hash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()));
        using var gate=new Mutex(false,lockName);
        bool owned=false;
        try
        {
            try { owned=gate.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { owned=true; }
            if (!owned) throw new IOException("另一个修改器正在处理此游戏目录，请稍后重试。");
            EnsureNotRunning(directory);
            // Exclusive access also prevents the executable being opened for launch during replacement.
            using var executable=new FileStream(Path.Combine(directory,"Rance10.exe"),FileMode.Open,FileAccess.Read,FileShare.None);
            string exeHash=Convert.ToHexString(SHA256.HashData(executable));
            if (!_versions.Any(version=>AinPatch.Same(exeHash,version.ExeSha256))) throw new InvalidDataException("Rance10.exe 不属于已适配的游戏版本。");
            return action(directory,exeHash);
        }
        finally { if (owned) gate.ReleaseMutex(); }
    }

    private BridgeInstallStatus Inspect(string directory)
    {
        string? running=RunningReason(directory);
        bool stopped=running is null;
        string exeHash=HashFile(Path.Combine(directory,"Rance10.exe"));
        if (!_versions.Any(version=>AinPatch.Same(exeHash,version.ExeSha256)))
            return new(BridgeInstallState.Unsupported,"Rance10.exe 不属于已适配的游戏版本。",false,false,false);
        string journal=Path.Combine(directory,JournalName);
        if (Directory.Exists(journal))
        {
            var (manifest,version)=ReadManifest(journal,exeHash);
            ValidateBackups(journal,manifest);
            var states=GetOwnedStates(directory,manifest);
            bool installed=states.All(x=>x.Installed);
            // Restore uses the journal, so an old or missing package cannot strand recovery.
            if (installed && !MatchesCurrentComponent(directory,version))
                return new(BridgeInstallState.RecoveryRequired,running ?? "已安装组件与当前版本不一致，或组件安装包不完整。请先恢复安装前文件，再安装新版组件。",false,stopped,false);
            return new(installed ? BridgeInstallState.Installed : BridgeInstallState.RecoveryRequired,
                installed ? running ?? $"{version.Name}的游戏组件已安装。" : running ?? "检测到未完成的组件操作，请先恢复安装前文件。",
                false,stopped,installed);
        }
        if (File.Exists(journal)) throw new IOException("游戏组件备份目录被同名文件占用。");
        byte[] ain=ReadBounded(Path.Combine(directory,"Rance10.ain"),64*1024*1024);
        byte[] ini=ReadBounded(Path.Combine(directory,"AliceStart.ini"),1024*1024);
        GameStartConfiguration.ValidateCodeName(Encoding.Latin1.GetString(ini));
        var sourceVersion=FindVersion(exeHash,AinPatch.Hash(ain));
        string dllPath=Path.Combine(directory,"R10Bridge.dll");
        if (sourceVersion is null)
        {
            if (File.Exists(dllPath) && HasBridgeDeclaration(ini))
            {
                var plain=AinPatch.Decode(ain);
                string plainHash=AinPatch.Hash(plain);
                var installedVersion=_versions.SingleOrDefault(version=>AinPatch.Same(exeHash,version.ExeSha256)
                    && plain.Length==version.TargetPlainLength && AinPatch.Same(plainHash,version.TargetPlainSha256));
                if (installedVersion is not null && AinPatch.Same(HashFile(dllPath),ReadPackage(installedVersion).Patch.DllSha256))
                    return new(BridgeInstallState.Installed,$"{installedVersion.Name}的游戏组件已可用。当前目录没有可恢复的备份。",false,false,true);
            }
            return new(BridgeInstallState.Unsupported,"Rance10.exe 和 Rance10.ain 的组合不属于已适配的游戏版本。",false,false,false);
        }
        if (File.Exists(dllPath) || Directory.Exists(dllPath))
            return new(BridgeInstallState.Unsupported,"目录中已有 R10Bridge.dll，无法确认来源，未覆盖此文件。",false,false,false);
        _=InstrumentIni(ini);
        _=ReadPackage(sourceVersion);
        return new(BridgeInstallState.NotInstalled,running ?? $"已识别{sourceVersion.Name}。安装时会备份当前游戏文件。",stopped,false,false);
    }

    private BridgeInstallStatus Install(string directory,string exeHash)
    {
        string journal=Path.Combine(directory,JournalName);
        if (Directory.Exists(journal))
        {
            var (existing,installedVersion)=ReadManifest(journal,exeHash); ValidateBackups(journal,existing);
            if (GetOwnedStates(directory,existing).All(x=>x.Installed))
                return MatchesCurrentComponent(directory,installedVersion)
                    ? new(BridgeInstallState.Installed,"游戏组件已经安装。",false,true,true)
                    : new(BridgeInstallState.RecoveryRequired,"已安装组件与当前版本不一致，或组件安装包不完整。请先恢复安装前文件，再安装新版组件。",false,true,false);
            throw new IOException("上次组件操作尚未完成，请先恢复安装前文件。");
        }
        if (File.Exists(journal)) throw new IOException("备份目录名称被文件占用。");
        string ainPath=Path.Combine(directory,"Rance10.ain"),iniPath=Path.Combine(directory,"AliceStart.ini"),dllPath=Path.Combine(directory,"R10Bridge.dll");
        foreach (string target in TargetNames) RejectReparse(Path.Combine(directory,target));
        if (File.Exists(dllPath) || Directory.Exists(dllPath)) throw new IOException("目录中已有 R10Bridge.dll，未覆盖未知文件。");
        var ain=ReadBounded(ainPath,64*1024*1024); var ini=ReadBounded(iniPath,1024*1024);
        var version=FindVersion(exeHash,AinPatch.Hash(ain))
            ?? throw new InvalidDataException("Rance10.exe 和 Rance10.ain 的组合不属于已适配的游戏版本。");
        var (patch,dll)=ReadPackage(version);
        var patchedAin=AinPatch.Apply(ain,patch); var patchedIni=InstrumentIni(ini);
        byte[]?[] originals=[null,ain,ini]; byte[][] replacements=[dll,patchedAin,patchedIni];
        var manifest=new InstallManifest(1,exeHash,TargetNames.Select((name,index)=>new InstalledFile(name,
            originals[index] is { } original ? AinPatch.Hash(original) : null,AinPatch.Hash(replacements[index]))).ToArray());
        string staging=Path.Combine(directory,JournalName+".preparing-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        for (int i=0;i<TargetNames.Length;i++)
        {
            if (originals[i] is { } original) WriteNew(Path.Combine(staging,TargetNames[i]+".original"),original);
            WriteNew(Path.Combine(staging,TargetNames[i]+".installed"),replacements[i]);
        }
        WriteNew(Path.Combine(staging,"manifest.json"),JsonSerializer.SerializeToUtf8Bytes(manifest,AinPatch.Json));
        ValidateBackups(staging,manifest);
        _=GetOwnedStates(directory,manifest); // All original files still match, before the journal is published.
        _failurePoint?.Invoke("before-journal");
        Directory.Move(staging,journal);
        try
        {
            foreach (var file in manifest.Files)
            {
                ReplaceOwned(directory,journal,file,true);
                _failurePoint?.Invoke("installed:"+file.Name);
            }
            if (!GetOwnedStates(directory,manifest).All(x=>x.Installed)) throw new IOException("组件安装后校验失败。");
            return new(BridgeInstallState.Installed,$"{version.Name}的游戏组件已安装。启动游戏后即可使用组件功能。",false,true,true);
        }
        catch (Exception error)
        {
            try { RestoreFiles(directory,journal,manifest); }
            catch (Exception rollback) { throw new IOException("组件操作未完成，备份已保留。请使用“恢复安装前文件”重试。原错误："+error.Message+"；恢复错误："+rollback.Message,error); }
            throw new IOException("组件安装未完成，已恢复原文件。"+error.Message,error);
        }
    }

    private BridgeInstallStatus Restore(string directory,string exeHash)
    {
        string journal=Path.Combine(directory,JournalName);
        if (!Directory.Exists(journal)) throw new IOException("没有此修改器建立的安装前备份。");
        var (manifest,version)=ReadManifest(journal,exeHash); ValidateBackups(journal,manifest);
        RestoreFiles(directory,journal,manifest);
        return new(BridgeInstallState.NotInstalled,$"已恢复{version.Name}安装组件前的文件。备份已归档保留。",true,false,false);
    }

    private void RestoreFiles(string directory,string journal,InstallManifest manifest)
    {
        _=GetOwnedStates(directory,manifest); // Preflight all targets; an unknown change prevents every restore write.
        foreach (var file in manifest.Files.Reverse())
        {
            ReplaceOwned(directory,journal,file,false);
            _failurePoint?.Invoke("restored:"+file.Name);
        }
        if (!GetOwnedStates(directory,manifest).All(x=>x.Original)) throw new IOException("原文件恢复后校验失败。");
        Directory.Move(journal,Path.Combine(directory,JournalName+".restored-"+Guid.NewGuid().ToString("N")));
    }

    private static void ReplaceOwned(string directory,string journal,InstalledFile file,bool install)
    {
        string target=Path.Combine(directory,file.Name);
        RejectReparse(target);
        var state=GetOwnedState(target,file);
        if (install ? state.Installed : state.Original) return;
        string? expected=install ? file.InstalledHash : file.OriginalHash;
        if (expected is null)
        {
            // Only the exact DLL recorded by this installer is eligible for deletion.
            if (!state.Installed) throw new IOException("组件文件已变化，未删除。");
            File.Delete(target); return;
        }
        string backup=Path.Combine(journal,file.Name+(install ? ".installed" : ".original"));
        var bytes=ReadBounded(backup,64*1024*1024);
        if (!AinPatch.Same(AinPatch.Hash(bytes),expected)) throw new IOException("组件备份已变化，停止操作。");
        string temporary=Path.Combine(directory,".r10trainer-"+Guid.NewGuid().ToString("N")+".tmp");
        WriteNew(temporary,bytes);
        // Recheck immediately before atomic replacement; never overwrite a newly unknown file.
        var current=GetOwnedState(target,file);
        if (install ? current.Installed : current.Original) { File.Delete(temporary); return; }
        if (File.Exists(target)) File.Replace(temporary,target,null);
        else File.Move(temporary,target);
        if (!AinPatch.Same(HashFile(target),expected)) throw new IOException("替换后的文件校验失败。");
    }

    private bool MatchesCurrentComponent(string directory,GameVersion version)
    {
        // The journal proves ownership, not currency. Compare the installed script against
        // this app's supported plain identity, independent of compression or old backup hashes.
        // Do not require a readable patch recipe to offer restoration of an owned installation.
        var ain=ReadBounded(Path.Combine(directory,"Rance10.ain"),64*1024*1024);
        if (!AinPatch.Same(AinPatch.Hash(AinPatch.Decode(ain)),version.TargetPlainSha256)) return false;
        string packageDll=Path.Combine(_package,"bridge","R10Bridge.dll");
        return File.Exists(packageDll) && AinPatch.Same(HashFile(packageDll),HashFile(Path.Combine(directory,"R10Bridge.dll")));
    }

    private (PatchRecipe Patch,byte[] Dll) ReadPackage(GameVersion version)
    {
        string patchPath=Path.Combine(_package,"bridge","hp-patch.json"),dllPath=Path.Combine(_package,"bridge","R10Bridge.dll");
        RejectReparse(patchPath); RejectReparse(dllPath);
        var patch=AinPatch.Read(patchPath,version);
        var dll=ReadBounded(dllPath,1024*1024);
        if (!AinPatch.Same(AinPatch.Hash(dll),patch.DllSha256)) throw new IOException("游戏组件 DLL 校验失败，请重新解压完整修改器。");
        return (patch,dll);
    }
    private GameVersion? FindVersion(string exeHash,string? sourceHash) =>
        _versions.SingleOrDefault(version=>AinPatch.Same(exeHash,version.ExeSha256) && AinPatch.Same(sourceHash,version.SourceSha256));

    private (InstallManifest Manifest,GameVersion Version) ReadManifest(string journal,string exeHash)
    {
        RejectReparse(journal);
        var bytes=ReadBounded(Path.Combine(journal,"manifest.json"),16*1024);
        var manifest=JsonSerializer.Deserialize<InstallManifest>(bytes,AinPatch.Json) ?? throw new IOException("组件备份清单无效。");
        if (manifest.Schema!=1 || !AinPatch.Same(manifest.ExeHash,exeHash) || manifest.Files is null || manifest.Files.Length!=3
            || manifest.Files.Any(x=>x is null || !ValidHash(x.InstalledHash) || (x.OriginalHash is not null && !ValidHash(x.OriginalHash)))
            || !manifest.Files.Select(x=>x.Name).SequenceEqual(TargetNames) || manifest.Files[0].OriginalHash is not null
            || manifest.Files[1].OriginalHash is null || manifest.Files[2].OriginalHash is null
            || FindVersion(exeHash,manifest.Files[1].OriginalHash) is not { } version)
            throw new IOException("组件备份清单不属于此版本，停止操作。");
        return (manifest,version);
    }
    private static bool ValidHash(string? hash) => hash is not null && hash.Length==64 && hash.All(Uri.IsHexDigit);
    private static void ValidateBackups(string journal,InstallManifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            if (file.OriginalHash is { } original && !AinPatch.Same(HashFile(Path.Combine(journal,file.Name+".original")),original)) throw new IOException("原版备份校验失败，停止操作。");
            if (!AinPatch.Same(HashFile(Path.Combine(journal,file.Name+".installed")),file.InstalledHash)) throw new IOException("组件备份校验失败，停止操作。");
        }
    }
    private static OwnedState[] GetOwnedStates(string directory,InstallManifest manifest) => manifest.Files.Select(file=>GetOwnedState(Path.Combine(directory,file.Name),file)).ToArray();
    private static OwnedState GetOwnedState(string path,InstalledFile file)
    {
        RejectReparse(path);
        string? current=File.Exists(path) ? HashFile(path) : null;
        bool original=AinPatch.Same(current,file.OriginalHash),installed=AinPatch.Same(current,file.InstalledHash);
        if (Directory.Exists(path) || (!original && !installed)) throw new IOException(file.Name+" 与原件和本次组件都不一致，未覆盖未知修改。");
        return new(original,installed);
    }
    internal static byte[] InstrumentIni(byte[] raw)
    {
        // Latin-1 is a reversible byte mapping for the original text and line endings.
        var text=Encoding.Latin1.GetString(raw);
        GameStartConfiguration.ValidateCodeName(text);
        var matches=Regex.Matches(text,@"(?m)^([ \t]*LoadDLL[ \t]*=[ \t]*\{)(\r?\n)");
        if (matches.Count!=1 || !Regex.IsMatch(text,@"(?m)^[ \t]*GameName[ \t]*=[ \t]*""[^""\r\n]+""")) throw new IOException("启动配置格式不受支持，未修改文件。");
        var match=matches[0]; int insertion=match.Index+match.Groups[1].Length;
        int blockEnd=text.IndexOf('}',match.Index+match.Length);
        if (blockEnd<0) throw new IOException("启动配置的 LoadDLL 区块未结束，未修改文件。");
        var declarations=Regex.Replace(text[(match.Index+match.Length)..blockEnd],@"(?m);[^\r\n]*","");
        if (Regex.IsMatch(declarations,""""(?:[^"\r\n]*[\\/])?R10Bridge\.dll"""",RegexOptions.IgnoreCase))
            throw new IOException("启动配置中已有 游戏组件声明，无法确认来源。");
        return Encoding.Latin1.GetBytes(text.Insert(insertion,match.Groups[2].Value+"    \"R10Bridge.dll\","));
    }
    private static bool HasBridgeDeclaration(byte[] raw) => Regex.IsMatch(Encoding.Latin1.GetString(raw),"""(?m)^[ \t]*"R10Bridge\.dll"[ \t]*,?[ \t]*\r?$""",RegexOptions.IgnoreCase);
    private static string ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择游戏所在的完整目录。");
        directory=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("游戏目录不存在。");
        RejectReparse(directory);
        foreach (string name in TargetNames.Append("Rance10.exe").Append(JournalName)) RejectReparse(Path.Combine(directory,name));
        return directory;
    }
    private static void RejectReparse(string path)
    {
        var current=Path.GetFullPath(path);
        while (current.Length>0)
        {
            try { if ((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("不接受符号链接或目录联接："+current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent=Path.GetDirectoryName(current); if (parent is null || parent==current) break; current=parent;
        }
    }
    private static byte[] ReadBounded(string path,int maximum)
    {
        RejectReparse(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (stream.Length<1 || stream.Length>maximum) throw new IOException("文件大小不受支持："+Path.GetFileName(path));
        byte[] data=new byte[checked((int)stream.Length)]; stream.ReadExactly(data); return data;
    }
    private static string HashFile(string path)
    {
        RejectReparse(path);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void WriteNew(string path,byte[] data)
    {
        RejectReparse(path);
        using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
        stream.Write(data); stream.Flush(true);
    }
    private static void EnsureNotRunning(string directory)
    { if (RunningReason(directory) is { } reason) throw new IOException(reason); }
    private static string? RunningReason(string directory)
    {
        string exe=Path.Combine(directory,"Rance10.exe");
        foreach (var process in Process.GetProcessesByName("Rance10"))
        {
            using (process)
            {
                try { if (string.Equals(process.MainModule?.FileName,exe,StringComparison.OrdinalIgnoreCase)) return "请先关闭游戏，再安装或恢复 游戏组件。"; }
                catch (InvalidOperationException) { }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException) { return "无法确认游戏是否已关闭，请使用与游戏相同的权限再试。"; }
            }
        }
        return null;
    }
    private sealed record InstalledFile(string Name,string? OriginalHash,string InstalledHash);
    private sealed record InstallManifest(int Schema,string ExeHash,InstalledFile[] Files);
    private sealed record OwnedState(bool Original,bool Installed);
}
