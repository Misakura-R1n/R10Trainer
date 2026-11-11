using System.Text.Json;

namespace R10.Core.Memory;

public sealed record GameVersion(string Id, string Name, string ExeSha256, string SourceSha256,
    string SourcePlainSha256, string TargetPlainSha256, int TargetPlainLength)
{
    internal string BridgeFingerprint => "bridge-v1:" + TargetPlainSha256;
}

public static class GameVersions
{
    public static IReadOnlyList<GameVersion> All { get; } = Load();
    public static GameVersion BugFix { get; } = All.Single(version => version.Id == "bugfix");

    private static IReadOnlyList<GameVersion> Load()
    {
        using var stream = typeof(GameVersions).Assembly.GetManifestResourceStream("R10.Core.Memory.GameVersions.json")
            ?? throw new InvalidOperationException("缺少游戏版本清单。");
        var versions = JsonSerializer.Deserialize<GameVersion[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("游戏版本清单无效。");
        return Array.AsReadOnly(versions);
    }
}
