using System.Diagnostics;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace R10.Core.Memory;

public sealed class GameConnection : IDisposable
{
    public static readonly string SupportedExeHash = GameVersions.BugFix.ExeSha256;
    public static readonly string SupportedAinHash = GameVersions.BugFix.SourceSha256;
    // The installer recompresses the exact same AI2 payload with the bundled .NET runtime.
    // Its compressed bytes may differ from Python/zlib, but no script byte may differ.
    public static readonly string SupportedBridgeAinPlainHash = GameVersions.BugFix.TargetPlainSha256;
    public static readonly string SupportedBridgeAinFingerprint = GameVersions.BugFix.BridgeFingerprint;
    public static readonly int SupportedBridgeAinPlainLength = GameVersions.BugFix.TargetPlainLength;
    public const uint SupportedBridgeCodeLength = 14582408;
    private static readonly (int Length, string Fingerprint)[] LegacyBridgeAinIdentities =
    [
        (28727376, "bridge-v1:6016DF12F4F8C47C596AF8A7951C6149201768CF3996CA0C49D1E13D2FE12D4A"),
        (28727376, "bridge-v1:6FDB129167C8FAD210168D3E6EFF8D398001AE397D84D535A741CF961047A06F"),
        (28727422, "bridge-v1:C0989948D8B10CCDFC5638A77FEF3E44B0F100AA92C609915F5DE361BB7007EC"),
        (28724545, "bridge-v1:7C674704D331F697339643A541958B259B8B7179D72346CA9C573F5A4B5829AF")
    ];
    private static readonly (int Length, string Fingerprint)[] BridgeAinIdentities =
        [.. GameVersions.All.Select(version => (version.TargetPlainLength, version.BridgeFingerprint)), .. LegacyBridgeAinIdentities];
    private readonly FileStream? _exe, _ain;
    public IProcessMemory Memory { get; }
    public string AinPath { get; }
    public GameConnection(IProcessMemory memory, string ainPath) { Memory = memory; AinPath = ainPath; }
    private GameConnection(NativeProcessMemory memory, string ainPath, FileStream exe, FileStream ain)
        : this(memory, ainPath) { _exe = exe; _ain = ain; }
    public static GameConnection? Discover() => DiscoverCore(null, false);
    public static GameConnection? Discover(string gameDirectory) => DiscoverCore(NormalizeDirectory(gameDirectory), true);
    private static GameConnection? DiscoverCore(string? gameDirectory, bool allowBridge)
    {
        var processes = Process.GetProcessesByName("Rance10");
        try
        {
            var matches = new List<Process>();
            foreach (var process in processes)
            {
                if (gameDirectory is null) { matches.Add(process); continue; }
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && string.Equals(Path.GetFullPath(path), Path.Combine(gameDirectory, "Rance10.exe"), StringComparison.OrdinalIgnoreCase))
                        matches.Add(process);
                }
                catch (InvalidOperationException) { } // Exited while enumerating.
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException)
                { throw new IOException("无法核验游戏进程路径，请检查双方的权限级别。", error); }
            }
            if (matches.Count == 0) return null;
            if (matches.Count != 1) throw new IOException("检测到多个目标 Rance10 进程，请只保留要修改的游戏。");
            var memory = new NativeProcessMemory(matches[0].Id);
            FileStream? exe = null, ain = null;
            try
            {
                if (gameDirectory is not null && !string.Equals(Path.GetFullPath(memory.ExecutablePath), Path.Combine(gameDirectory, "Rance10.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("连接期间游戏路径发生变化，已取消连接。");
                string directory = Path.GetDirectoryName(memory.ExecutablePath)!;
                string ainPath = Path.Combine(directory, "Rance10.ain");
                exe = new FileStream(memory.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string exeHash = Convert.ToHexString(SHA256.HashData(exe));
                if (!(allowBridge ? GameVersions.All.Any(version => version.ExeSha256 == exeHash) : exeHash == SupportedExeHash))
                    throw new NotSupportedException("Rance10.exe 不属于已适配的游戏版本。");
                ain = new FileStream(ainPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var fingerprint = ReadAinFingerprint(ain, allowBridge);
                if (!IsSupportedGameVersion(exeHash, fingerprint, allowBridge))
                    throw new NotSupportedException("Rance10.exe 和 Rance10.ain 的组合不属于已适配的游戏版本。");
                using (var ini = new FileStream(Path.Combine(directory, "AliceStart.ini"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (ini.Length is < 1 or > 1024 * 1024) throw new IOException("游戏启动配置大小无效。");
                    using var reader = new StreamReader(ini, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
                    GameStartConfiguration.ValidateCodeName(reader.ReadToEnd());
                }
                if (!memory.IsAlive) throw new IOException("游戏在版本验证期间退出。");
                return new(memory, ainPath, exe, ain);
            }
            catch { exe?.Dispose(); ain?.Dispose(); memory.Dispose(); throw; }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("请选择游戏的绝对目录。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public static bool IsSupportedAinFingerprint(string fingerprint, bool allowBridge = true) =>
        Matches(fingerprint, SupportedAinHash) ||
        (allowBridge && (GameVersions.All.Any(version => Matches(fingerprint, version.SourceSha256)) || IsBridgeAinFingerprint(fingerprint)));
    public static bool IsSupportedGameVersion(string exeHash, string fingerprint, bool allowBridge = true)
    {
        if (!allowBridge) return Matches(exeHash, SupportedExeHash) && Matches(fingerprint, SupportedAinHash);
        return GameVersions.All.Any(version => Matches(exeHash, version.ExeSha256) &&
                   (Matches(fingerprint, version.SourceSha256) || Matches(fingerprint, version.BridgeFingerprint))) ||
               (Matches(exeHash, SupportedExeHash) && LegacyBridgeAinIdentities.Any(item => Matches(item.Fingerprint, fingerprint)));
    }
    private static bool Matches(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    internal static bool IsBridgeAinFingerprint(string fingerprint) =>
        BridgeAinIdentities.Any(item => Matches(item.Fingerprint, fingerprint));
    public static string ReadAinFingerprint(string path, bool allowBridge = true)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadAinFingerprint(stream, allowBridge);
    }
    private static string ReadAinFingerprint(Stream stream, bool allowBridge)
    {
        stream.Position = 0;
        if (stream.Length > 64 * 1024 * 1024) return "unsupported-size";
        string compressedHash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        if (!allowBridge || stream.Length < 16 || GameVersions.All.Any(version => version.SourceSha256 == compressedHash)) return compressedHash;
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        int plainLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        if (!header[..8].SequenceEqual("AI2\0\0\0\0\0"u8) ||
            !BridgeAinIdentities.Any(item => item.Length == plainLength) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]) != stream.Length - 16) { stream.Position = 0; return compressedHash; }
        try
        {
            using var decoded = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int total = 0, count;
            while ((count = decoded.Read(buffer)) != 0)
            {
                total = checked(total + count);
                if (total > plainLength) return compressedHash;
                hash.AppendData(buffer.AsSpan(0, count));
            }
            if (total != plainLength) return compressedHash;
            string fingerprint = "bridge-v1:" + Convert.ToHexString(hash.GetHashAndReset());
            return BridgeAinIdentities.Contains((plainLength, fingerprint)) ? fingerprint : compressedHash;
        }
        catch (InvalidDataException) { return compressedHash; }
        finally { stream.Position = 0; }
    }
    public void Dispose() { Memory.Dispose(); _ain?.Dispose(); _exe?.Dispose(); }
}
