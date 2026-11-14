namespace R10.Core;

public enum FeatureId
{
    Hp, EnemyKill, EnemyNear, Difficulty, Turn, DeathRate, Army, War, Food, Medal, Friendship,
    Resources, Treasure, Unit, Star, Redraw, Redistribute, NoDuplicateCards,
    PlayerHp, EnemyHp, ApFull, UnlimitedSkills, StatusImmunity, RepeatActions, UnlimitedActions, ExtraTreasurePick
}
public enum FeatureKind { Toggle, Number, Choice }
public enum FeatureGroup { Battle, Strategy, Resources, Growth }
public enum ConnectionState { Searching, Unsupported, Loading, Ready, RestorePending, Error, Closed }
public sealed record FeatureDefinition(FeatureId Id, string Label, FeatureGroup Group, FeatureKind Kind,
    decimal Minimum = 0, decimal Maximum = 0, decimal Step = 1, string Unit = "", bool HasLock = false, string Help = "");
public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureDefinition> All { get; } = Array.AsReadOnly(new[] {
        new FeatureDefinition(FeatureId.Hp, "HP 不减", FeatureGroup.Battle, FeatureKind.Toggle, Help:"定时将我方 HP 恢复至上限，无法保证抵挡瞬间致死伤害。"),
        new FeatureDefinition(FeatureId.PlayerHp, "我方 HP", FeatureGroup.Battle, FeatureKind.Number, 1, int.MaxValue, Help:"设置本场战斗的当前 HP。HP 保护继续处理后续伤害。"),
        new FeatureDefinition(FeatureId.EnemyHp, "敌方 HP", FeatureGroup.Battle, FeatureKind.Number, 1, int.MaxValue, Help:"设置本场战斗的当前 HP。开启敌人濒死或直死时暂停手动编辑。"),
        new FeatureDefinition(FeatureId.ApFull, "AP 全满", FeatureGroup.Battle, FeatureKind.Toggle, Help:"按队伍当前 AP 上限补满。技能正常扣费，在回合开始和技能结算后补满。"),
        new FeatureDefinition(FeatureId.UnlimitedSkills, "限次技能不限次", FeatureGroup.Battle, FeatureKind.Toggle, Help:"允许已用尽次数的技能继续使用，按游戏规则结算 AP。重新展开技能列表可刷新费用显示。"),
        new FeatureDefinition(FeatureId.StatusImmunity, "我方异常免疫", FeatureGroup.Battle, FeatureKind.Toggle, Help:"解除并阻止我方普通战斗的负面状态，包括角色的眩晕与石化。"),
        new FeatureDefinition(FeatureId.RepeatActions, "同回合重复行动", FeatureGroup.Battle, FeatureKind.Toggle, Help:"同回合可再次点击已行动的角色选择技能。卡面可能仍显示行动结束。"),
        new FeatureDefinition(FeatureId.UnlimitedActions, "解除我方行动次数上限", FeatureGroup.Battle, FeatureKind.Toggle, Help:"解除普通战斗中对我方施加的全队行动次数上限。"),
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
        new FeatureDefinition(FeatureId.Resources, "资源全满", FeatureGroup.Resources, FeatureKind.Toggle, Help:"新版组件将食券 / 金块和友情补至 3，勋章补至 2，第三枚仍由游戏发放并提升部队等级。"),
        new FeatureDefinition(FeatureId.NoDuplicateCards, "随机掉落不重复", FeatureGroup.Resources, FeatureKind.Toggle, Help:"排除当前存档已拥有的同一张卡，并避免同批随机奖励重复；保留固定奖励与消耗型奖励，无卡可抽时补餐券。需要安装游戏组件；下一批奖励生效。"),
        new FeatureDefinition(FeatureId.Treasure, "宝箱发现率 +100%", FeatureGroup.Resources, FeatureKind.Toggle, Help:"需要安装游戏组件；结算奖励时将本次队伍发现加成设为 100%，不能保证每次都有宝箱。"),
        new FeatureDefinition(FeatureId.ExtraTreasurePick, "宝箱卡牌可获取＋1", FeatureGroup.Resources, FeatureKind.Toggle, Help:"下次选卡时可多领取一张，只有两张候选的宝箱会提供三张。与同名周目奖励共用一次加成。"),
        new FeatureDefinition(FeatureId.Unit, "部队等级", FeatureGroup.Growth, FeatureKind.Number, 0, 999),
        new FeatureDefinition(FeatureId.Star, "角色★等级", FeatureGroup.Growth, FeatureKind.Choice, 10, 200, Help:"批量修改已创建角色；清空经验、可能降级，也可能影响继承。"),
        new FeatureDefinition(FeatureId.Redraw, "食券重抽", FeatureGroup.Growth, FeatureKind.Toggle, Help:"选「暂不使用」可重抽，正常使用仍扣食券。实机效果待验证。"),
        new FeatureDefinition(FeatureId.Redistribute, "自动战斗重配", FeatureGroup.Growth, FeatureKind.Toggle, Help:"允许多次重新分配。实机效果待验证。")
    });
    public static FeatureDefinition Get(FeatureId id) => All.First(x => x.Id == id);
    public static bool IsBattleHp(FeatureId id) => id is FeatureId.PlayerHp or FeatureId.EnemyHp;
    internal static bool RequiresGameComponent(FeatureId id) => IsBattleHp(id) ||
        id is FeatureId.NoDuplicateCards or FeatureId.Treasure or FeatureId.ExtraTreasurePick or FeatureId.ApFull or FeatureId.UnlimitedSkills
            or FeatureId.StatusImmunity or FeatureId.RepeatActions or FeatureId.UnlimitedActions;
    public static bool ValidValue(FeatureId id, decimal value)
    {
        var f = Get(id);
        return f.Kind != FeatureKind.Toggle && value >= f.Minimum && value <= f.Maximum &&
               (value - f.Minimum) % f.Step == 0 && (id != FeatureId.Star || value is 10 or 40 or 80 or 200);
    }
}
public sealed record FeatureObservation(decimal? Value = null, bool Requested = false, bool Active = false,
    bool CanEdit = false, bool LockRequested = false, bool LockActive = false, string? Error = null, string? Detail = null,
    string? Help = null, string? ActiveDescription = null, decimal? Maximum = null)
{
    public string? StatusText { get; init; }
}
public sealed record TrainerSnapshot(ConnectionState Connection, string Status, int? ProcessId,
    bool IsSimulation, IReadOnlyDictionary<FeatureId, FeatureObservation> Features, string? Error = null,
    string RuntimeMode = "memory", string? Notice = null)
{
    public GameLoadEpoch? LoadEpoch { get; init; }
    public bool CardManagementAvailable { get; init; }
    public bool CardBatchReadAvailable { get; init; }
    public bool ItemStarAvailable { get; init; }
    public int CardContext { get; init; }
    public BattleEditContext? Battle { get; init; }
    public static TrainerSnapshot Empty { get; } = new(ConnectionState.Searching, "等待游戏连接", null, false,
        FeatureCatalog.All.ToDictionary(x => x.Id, _ => new FeatureObservation()));
}
public sealed record GameLoadEpoch(int ProcessId, string Session, long Generation, bool Ready);
public sealed record BattleEditContext(GameLoadEpoch Epoch, int Id);
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
    Task<OperationResult> SetBattleHpAsync(FeatureId id, decimal value, BattleEditContext expected) =>
        Task.FromResult(OperationResult.Fail("此连接不支持战斗 HP 编辑。"));
    Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled);
    Task<OperationResult> SetLockAsync(FeatureId id, bool enabled);
    Task<OperationResult> TryCloseAsync();
}
