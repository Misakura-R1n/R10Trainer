using System.Text.Json;

namespace R10.Bridge.Client;

public static class BridgeProtocol
{
    public const int Version = 1;
    public const string Build = "r10-bridge-v1";
    public const int MaximumLineBytes = 65536;
    public static string PipeName(int processId) => $"R10Bridge.{processId}";
}

public sealed class BridgeException(string code, string message, bool outcomeUnknown = false, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

[Flags]
public enum BattleRules { None = 0, ApFull = 1, UnlimitedSkills = 2, StatusImmunity = 4, RepeatActions = 8, UnlimitedActions = 16 }

public sealed record BridgeState(int Pid, long GameThreadId, long Heartbeat, long Frame,
    long LoadGeneration, int Phase, bool HpGuard, long LastCommandId, string LastCommandState,
    long? PlayerHp, long? PlayerHpMax, long? EnemyHp,
    long? HeartbeatAgeMs = null, long? BlockedHits = null, long? LastRequestedHp = null,
    long? ScriptVersion = null, long? Capabilities = null,
    long? LoadKind = null, long? LoadSlot = null, long? LastCompletedGeneration = null, long? LastLoadOutcome = null)
{
    public string? Session { get; init; }
    public bool BattleReady { get; init; }
    public const int BattleCapability = 1024;
    public bool BattleFeaturesAvailable => (Capabilities.GetValueOrDefault() & BattleCapability) != 0;
    public bool OrdinaryBattle { get; init; }
    public int BattleContext { get; init; }
    public BattleRules BattleRules { get; init; }
    public long? Ap { get; init; }
    public long? ApMax { get; init; }
    public long? EnemyHpMax { get; init; }
    public bool NoDuplicateCards { get; init; }
    public bool ResourcesFull { get; init; }
    public bool DataReady { get; init; }
    public long? Food { get; init; }
    public long? Medal { get; init; }
    public long? Friendship { get; init; }
    public long? Unit { get; init; }
    public bool DataAvailable => (Capabilities.GetValueOrDefault() & 256) != 0;
    public bool CardManagementAvailable => (Capabilities.GetValueOrDefault() & 512) != 0;
    public int CardContext { get; init; }
    public bool CardBatchReadAvailable { get; init; }
    public bool ItemStarAvailable { get; init; }
    public bool FoodRedraw { get; init; }
    public bool AutoRedistribute { get; init; }
    public const int ExtraTreasurePickCapability = 8192;
    public bool ExtraTreasurePick { get; init; }
    public const int TreasureFindCapability = 32768;
    public bool TreasureFind { get; init; }
    public bool FoodRedrawAvailable => (Capabilities.GetValueOrDefault() & 64) != 0;
    public bool AutoRedistributeAvailable => (Capabilities.GetValueOrDefault() & 128) != 0;
    public bool PatchManagesRerolls => (Capabilities.GetValueOrDefault() & 4096) != 0;
    public long? FilteredCards { get; init; }
    public long? FallbackCards { get; init; }
    public bool NoDuplicateCardsAvailable => (Capabilities.GetValueOrDefault() & 32) != 0;
    public bool ControlsAvailable => Capabilities.HasValue && (Capabilities.Value & 16) != 0;
    public string PhaseName => Phase switch { 1 => "ready", 2 => "loading", _ => "unknown" };
    public bool? GameThreadResponsive => HeartbeatAgeMs.HasValue ? HeartbeatAgeMs is >=0 and <=2000 && GameThreadId > 0 : null;
    public bool HpGuardAvailable => Capabilities.HasValue && (Capabilities.Value & 4) != 0;
}

public sealed record BridgeCommand(long Id, string State, string? Error = null)
{
    public bool IsTerminal => State is "applied" or "rejected";
    public CardReply? Cards { get; init; }
}

public enum CardOperation { Page = 10, Inspect, Grant, SetStar, CatalogPage = 21, OwnershipPage, ReadItemStar, SetItemStar }

public sealed record CardEntry(int Index, string Id, string CharacterId, int Flags, string Key)
{
    public bool Owned => (Flags & 1) != 0;
    public bool Collectible => (Flags & 2) != 0;
    public bool HasRoleGrowth => (Flags & 4) != 0;
    public bool CanGrant => Collectible && !Owned;
}

public sealed record CardRequest(CardOperation Operation, int Index, string Key = "", int Star = 0)
{
    public const int PageSize = 32;
    public const int CatalogPageSize = 3 * PageSize;
    public const int OwnershipPageSize = 8 * PageSize;
    public const int RowLimit = 4096;
    public const int MaximumItemStar = 999;
    public bool IsPage => Operation is CardOperation.Page or CardOperation.CatalogPage or CardOperation.OwnershipPage;
    public bool IsItemStar => Operation is CardOperation.ReadItemStar or CardOperation.SetItemStar;
    public bool Writes => Operation is CardOperation.Grant or CardOperation.SetStar or CardOperation.SetItemStar;
}

public sealed record CardOwnershipPage(int Start, string Flags);
public sealed record ItemStarLevel(int Base, int Effective);

public sealed record CardReply(int Total, IReadOnlyList<CardEntry> Rows, int? Star = null,
    int Experience = 0, int NextExperience = 0, int MaxStar = 0)
{
    public CardOwnershipPage? Ownership { get; init; }
    public ItemStarLevel? ItemStar { get; init; }
}

public interface IBridgeClient
{
    int ProcessId { get; }
    bool IsSimulation { get; }
    string? Session { get; }
    Task<BridgeState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<BridgeCommand> SetFeatureAsync(string feature, bool enabled, long generation, CancellationToken cancellationToken = default);
    Task<BridgeCommand> GetCommandResultAsync(long id, CancellationToken cancellationToken = default);
    Task PingAsync(CancellationToken cancellationToken = default);
    Task<BridgeCommand> SetFeatureForSessionAsync(string session, string feature, bool enabled, long generation, CancellationToken cancellationToken = default);
    Task<BridgeCommand> GetCommandResultForSessionAsync(string session, long id, CancellationToken cancellationToken = default);
    Task<BridgeCommand> SetValueForSessionAsync(string session, string feature, int value, long generation, CancellationToken cancellationToken = default) =>
        throw new BridgeException("unsupported_operation", "This bridge client does not support typed data operations.");
    Task<BridgeCommand> SetBattleHpForSessionAsync(string session, long generation, int context, int side, int value, CancellationToken cancellationToken = default) =>
        throw new BridgeException("unsupported_operation", "This bridge client does not support battle HP editing.");
    Task<BridgeCommand> ActionForSessionAsync(string session, int action, int argument, int argument2, long generation, CancellationToken cancellationToken = default) =>
        throw new BridgeException("unsupported_operation", "This bridge client does not support semantic controls.");
    Task<BridgeCommand> SubmitCardForSessionAsync(string session, long generation, int context, CardRequest request, CancellationToken cancellationToken = default) =>
        throw new BridgeException("unsupported_operation", "This bridge client does not support card management.");
}

// Injectable only for protocol tests. Production always uses the PID-verified Windows pipe transport.
public interface IBridgeTransport
{
    bool IsSimulation { get; }
    string? Session { get; }
    Task<JsonElement> ExchangeAsync(JsonElement request, bool mutation, CancellationToken cancellationToken);
}
