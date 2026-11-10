namespace R10.Core;

public enum FeatureId { Hp, EnemyKill, EnemyNear, Difficulty, Turn, DeathRate, Army, War, Food, Medal, Friendship, Resources, Treasure, Unit, Star, Redraw, Redistribute, NoDuplicateCards }
public enum FeatureKind { Toggle, Number, Choice }
public enum FeatureGroup { Battle, Strategy, Resources, Growth }
public enum ConnectionState { Searching, Unsupported, Loading, Ready, RestorePending, Error, Closed }
public sealed record FeatureDefinition(FeatureId Id, string Label, FeatureGroup Group, FeatureKind Kind,
    decimal Minimum = 0, decimal Maximum = 0, decimal Step = 1, string Unit = "", bool HasLock = false, string Help = "");
public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureDefinition> All { get; } = Array.AsReadOnly(new[] {
        new FeatureDefinition(FeatureId.Hp, "HP 不减", FeatureGroup.Battle, FeatureKind.Toggle, Help:"定时将我方 HP 恢复至上限，无法保证抵挡瞬间致死伤害。"),
        new FeatureDefinition(FeatureId.EnemyKill, "敌人直死", FeatureGroup.Battle, FeatureKind.Toggle),
        new FeatureDefinition(FeatureId.EnemyNear, "敌人濒死", FeatureGroup.Battle, FeatureKind.Toggle),
        new FeatureDefinition(FeatureId.Difficulty, "难度", FeatureGroup.Battle, FeatureKind.Number, -9999, 9999),
        new FeatureDefinition(FeatureId.Turn, "回合", FeatureGroup.Strategy, FeatureKind.Number, 0, 999, HasLock:true, Help:"只修改回合数字，不会倒退剧情。"),
        new FeatureDefinition(FeatureId.DeathRate, "人类死亡率", FeatureGroup.Strategy, FeatureKind.Number, 0, 100, .1m, "%", true, "累计人类死亡率。接入组件后读档会取消旧锁定；未接入组件时请在读档前关闭、读档后重开。"),
        new FeatureDefinition(FeatureId.Army, "四国兵力", FeatureGroup.Strategy, FeatureKind.Number, 0, 99999999, 1, "×千", true, "应用时将四国兵力设为同一数值；锁定时分别保持四国当前兵力。"),
        new FeatureDefinition(FeatureId.War, "四国战果全满", FeatureGroup.Strategy, FeatureKind.Toggle, Help:"持续补满四国战果，关闭只停止后续修改。"),
        new FeatureDefinition(FeatureId.Food, "食券 / 金块", FeatureGroup.Resources, FeatureKind.Number, 0, 3, Help:"第一部食券与第二部金块共用的计数。"),
        new FeatureDefinition(FeatureId.Medal, "勋章", FeatureGroup.Resources, FeatureKind.Number, 0, 3),
        new FeatureDefinition(FeatureId.Friendship, "友情", FeatureGroup.Resources, FeatureKind.Number, 0, 3, Help:"可用友情次数，不改变角色好感或至交。"),
        new FeatureDefinition(FeatureId.Resources, "资源全满", FeatureGroup.Resources, FeatureKind.Toggle, Help:"持续补满食券 / 金块、勋章和友情，不含四国战果。"),
        new FeatureDefinition(FeatureId.NoDuplicateCards, "随机掉落不重复", FeatureGroup.Resources, FeatureKind.Toggle, Help:"排除当前存档已拥有的同一张卡，并避免同批随机奖励重复；保留固定奖励与消耗型奖励，无卡可抽时补餐券。需要安装游戏组件；下一批奖励生效。"),
        new FeatureDefinition(FeatureId.Treasure, "宝箱发现率 +100%", FeatureGroup.Resources, FeatureKind.Toggle, Help:"增加发现加成，不能保证每次都有宝箱。"),
        new FeatureDefinition(FeatureId.Unit, "部队等级", FeatureGroup.Growth, FeatureKind.Number, 0, 999),
        new FeatureDefinition(FeatureId.Star, "角色★等级", FeatureGroup.Growth, FeatureKind.Choice, 10, 200, Help:"批量修改已创建角色；清空经验、可能降级，也可能影响继承。"),
        new FeatureDefinition(FeatureId.Redraw, "食券重抽", FeatureGroup.Growth, FeatureKind.Toggle, Help:"选「暂不使用」可重抽，正常使用仍扣食券。实机效果待验证。"),
        new FeatureDefinition(FeatureId.Redistribute, "自动战斗重配", FeatureGroup.Growth, FeatureKind.Toggle, Help:"允许多次重新分配。实机效果待验证。")
    });
    public static FeatureDefinition Get(FeatureId id) => All.First(x => x.Id == id);
    public static bool ValidValue(FeatureId id, decimal value)
    {
        var f = Get(id);
        return f.Kind != FeatureKind.Toggle && value >= f.Minimum && value <= f.Maximum &&
               (value - f.Minimum) % f.Step == 0 && (id != FeatureId.Star || value is 10 or 40 or 80 or 200);
    }
}
public sealed record FeatureObservation(decimal? Value = null, bool Requested = false, bool Active = false,
    bool CanEdit = false, bool LockRequested = false, bool LockActive = false, string? Error = null, string? Detail = null,
    string? Help = null, string? ActiveDescription = null);
public sealed record TrainerSnapshot(ConnectionState Connection, string Status, int? ProcessId,
    bool IsSimulation, IReadOnlyDictionary<FeatureId, FeatureObservation> Features, string? Error = null,
    string RuntimeMode = "memory", string? Notice = null)
{
    public GameLoadEpoch? LoadEpoch { get; init; }
    public bool CardManagementAvailable { get; init; }
    public int CardContext { get; init; }
    public static TrainerSnapshot Empty { get; } = new(ConnectionState.Searching, "等待游戏连接", null, false,
        FeatureCatalog.All.ToDictionary(x => x.Id, _ => new FeatureObservation()));
}
public sealed record GameLoadEpoch(int ProcessId, string Session, long Generation, bool Ready);
public interface IGameLoadObserver { void ObserveGameLoad(GameLoadEpoch? epoch); }
public sealed record OperationResult(bool Success, string? Error = null)
{
    public static OperationResult Ok { get; } = new(true);
    public static OperationResult Fail(string message) => new(false, message);
}
public interface ITrainerRuntime
{
    TrainerSnapshot Current { get; }
    event Action<TrainerSnapshot>? SnapshotChanged;
    Task StartAsync();
    Task<OperationResult> SetValueAsync(FeatureId id, decimal value);
    // A delayed confirmation must carry its original authority to the execution boundary.
    Task<OperationResult> SetValueForEpochAsync(FeatureId id, decimal value, GameLoadEpoch expected) =>
        Task.FromResult(OperationResult.Fail("此连接不支持绑定读档状态的确认操作。"));
    Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled);
    Task<OperationResult> SetLockAsync(FeatureId id, bool enabled);
    Task<OperationResult> TryCloseAsync();
}
