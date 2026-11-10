using System.Diagnostics;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;

namespace R10.Core.Memory;

public sealed class GameConnection : IDisposable
{
    public const string SupportedExeHash = "39A508C05B13AFC5427F0B722FCE5C4EDECED126587E037D585CF0E70D279297";
    public const string SupportedAinHash = "7ED22B2854DCB93B6E447198E7CB682CD05B90CF6AB3226091D80003D52DD48A";
    // The installer recompresses the exact same AI2 payload with the bundled .NET runtime.
    // Its compressed bytes may differ from Python/zlib, but no script byte may differ.
    public const string SupportedBridgeAinPlainHash = "6FDB129167C8FAD210168D3E6EFF8D398001AE397D84D535A741CF961047A06F";
    public const string SupportedBridgeAinFingerprint = "bridge-v1:" + SupportedBridgeAinPlainHash;
    public const int SupportedBridgeAinPlainLength = 28727376;
    public const uint SupportedBridgeCodeLength = 14579760;
    private static readonly Dictionary<int, string> BridgeAinFingerprints = new()
    {
        [SupportedBridgeAinPlainLength] = SupportedBridgeAinFingerprint,
        [28727422] = "bridge-v1:C0989948D8B10CCDFC5638A77FEF3E44B0F100AA92C609915F5DE361BB7007EC",
        [28724545] = "bridge-v1:7C674704D331F697339643A541958B259B8B7179D72346CA9C573F5A4B5829AF"
    };
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
                string ainPath = Path.Combine(Path.GetDirectoryName(memory.ExecutablePath)!, "Rance10.ain");
                exe = OpenVerified(memory.ExecutablePath, SupportedExeHash);
                ain = new FileStream(ainPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var fingerprint = ReadAinFingerprint(ain, allowBridge);
                if (!IsSupportedAinFingerprint(fingerprint, allowBridge))
                    throw new NotSupportedException("Rance10.ain 与已适配的中文 BugFix 版或 HP 保护组件不一致，已禁用写入。");
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
        string.Equals(fingerprint, SupportedAinHash, StringComparison.OrdinalIgnoreCase) ||
        (allowBridge && IsBridgeAinFingerprint(fingerprint));
    internal static bool IsBridgeAinFingerprint(string fingerprint) =>
        BridgeAinFingerprints.Values.Contains(fingerprint, StringComparer.OrdinalIgnoreCase);
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
        if (!allowBridge || compressedHash == SupportedAinHash || stream.Length is < 16 or > 64 * 1024 * 1024) return compressedHash;
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        int plainLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..12]);
        if (!header[..8].SequenceEqual("AI2\0\0\0\0\0"u8) ||
            !BridgeAinFingerprints.TryGetValue(plainLength, out var expectedFingerprint) ||
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
            return fingerprint == expectedFingerprint ? expectedFingerprint : compressedHash;
        }
        catch (InvalidDataException) { return compressedHash; }
        finally { stream.Position = 0; }
    }
    private static FileStream OpenVerified(string path, string expected)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(file));
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"{Path.GetFileName(path)} 与已适配的中文 BugFix 版不一致，已禁用写入。");
            file.Position = 0; return file;
        }
        catch { file.Dispose(); throw; }
    }
    public void Dispose() { Memory.Dispose(); _ain?.Dispose(); _exe?.Dispose(); }
}
