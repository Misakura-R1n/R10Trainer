using R10.Bridge.Client;
using R10.Core;

namespace R10.Bridge.Runtime;

/// <summary>Explicit bridge mode. It never falls back to process-memory writes.</summary>
public sealed class BridgeTrainerRuntime : ITrainerRuntime, ICardManagementRuntime
{
    internal static readonly IReadOnlyDictionary<FeatureId, (string Name, int Capability, BattleRules Rule)> ComponentFeatures =
        new Dictionary<FeatureId, (string, int, BattleRules)>
        {
            [FeatureId.Hp] = ("hp_guard", 4, BattleRules.None),
            [FeatureId.NoDuplicateCards] = ("no_duplicate_cards", 32, BattleRules.None),
            [FeatureId.Resources] = ("resources_full", 256, BattleRules.None),
            [FeatureId.Redraw] = ("food_redraw", 64, BattleRules.None),
            [FeatureId.Redistribute] = ("auto_redistribute", 128, BattleRules.None),
            [FeatureId.ApFull] = ("ap_full", BridgeState.BattleCapability, BattleRules.ApFull),
            [FeatureId.UnlimitedSkills] = ("unlimited_skills", BridgeState.BattleCapability, BattleRules.UnlimitedSkills),
            [FeatureId.StatusImmunity] = ("status_immunity", BridgeState.BattleCapability, BattleRules.StatusImmunity),
            [FeatureId.RepeatActions] = ("repeat_actions", BridgeState.BattleCapability, BattleRules.RepeatActions),
            [FeatureId.UnlimitedActions] = ("unlimited_actions", BridgeState.BattleCapability, BattleRules.UnlimitedActions)
        };
    internal static readonly IReadOnlyDictionary<FeatureId,string> ValueFeatures = new Dictionary<FeatureId,string>
    {
        [FeatureId.Food]="food", [FeatureId.Medal]="medal", [FeatureId.Friendship]="friendship",
        [FeatureId.Unit]="unit", [FeatureId.Star]="star"
    };
    private readonly IBridgeClient _client;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _startGate = new();
    private Task? _loop;
    private BridgeState? _observed;
    private string? _ownedSession;
    private readonly HashSet<FeatureId> _owned = [];
    private bool _closed;
    private int _closeRequests;
    private TrainerSnapshot _current = TrainerSnapshot.Empty with { RuntimeMode = "bridge" };
    public TrainerSnapshot Current => Volatile.Read(ref _current);
    public event Action<TrainerSnapshot>? SnapshotChanged;

    public BridgeTrainerRuntime(string gameDirectory) : this(new DiscoveringBridgeClient(gameDirectory)) { }
    public BridgeTrainerRuntime(IBridgeClient client) => _client=client;
    public Task StartAsync() { lock (_startGate) { if (!_closed) _loop ??= Task.Run(PollAsync); } return Task.CompletedTask; }
    public Task<OperationResult> SetValueAsync(FeatureId id,decimal value) =>
        SetValueCoreAsync(id,value,null,FeatureCatalog.IsBattleHp(id) ? Current.Battle : null);
    public Task<OperationResult> SetValueForEpochAsync(FeatureId id,decimal value,GameLoadEpoch expected) =>
        SetValueCoreAsync(id,value,expected ?? throw new ArgumentNullException(nameof(expected)));
    public Task<OperationResult> SetBattleHpAsync(FeatureId id, decimal value, BattleEditContext expected) =>
        FeatureCatalog.IsBattleHp(id) ? SetValueCoreAsync(id, value, null, expected) :
        Task.FromResult(OperationResult.Fail("此功能不是战斗 HP。"));
    private async Task<OperationResult> SetValueCoreAsync(FeatureId id,decimal value,GameLoadEpoch? expected,
        BattleEditContext? battle = null)
    {
        var feature = ValueFeatures.GetValueOrDefault(id);
        if ((battle is null && feature is null) || !FeatureCatalog.ValidValue(id,value)) return OperationResult.Fail("数值或功能不受支持。");
        if (Volatile.Read(ref _closeRequests)>0) return OperationResult.Fail("正在关闭组件连接。");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed || Volatile.Read(ref _closeRequests)>0) return OperationResult.Fail("组件连接已关闭或正在关闭。");
            var state=await _client.GetStateAsync().ConfigureAwait(false);
            if (state.Session is null) return OperationResult.Fail("游戏组件会话尚未就绪。");
            BridgeCommand command;
            if (battle is not null)
            {
                if (ReadBattle(state) != battle) return OperationResult.Fail("战斗目标已变化，请重新输入。");
                var (current, maximum) = ReadHp(state, id);
                if (current is not > 0 || maximum is not > 0 || value > maximum.Value)
                    return OperationResult.Fail("HP 须在 1 到当前上限之间，且战斗目标须存活。");
                command = await _client.SetBattleHpForSessionAsync(battle.Epoch.Session, battle.Epoch.Generation,
                    battle.Id, id == FeatureId.PlayerHp ? 0 : 1, (int)value).ConfigureAwait(false);
            }
            else
            {
                if (!CanWriteData(state)) return OperationResult.Fail("请安装新版组件并等待游戏数据就绪。");
                if (expected is not null && (!expected.Ready || expected.ProcessId!=state.Pid ||
                    expected.Session!=state.Session || expected.Generation!=state.LoadGeneration))
                    return OperationResult.Fail("游戏会话或读档状态已变化，请重新确认后应用。");
                if (state.ResourcesFull && id is FeatureId.Food or FeatureId.Medal or FeatureId.Friendship)
                    return OperationResult.Fail("请先关闭资源全满，再单独修改此数值。");
                command=await _client.SetValueForSessionAsync(expected?.Session ?? state.Session,feature!, (int)value,
                    expected?.Generation ?? state.LoadGeneration).ConfigureAwait(false);
            }
            command=await AwaitCommandAsync(state.Session,command).ConfigureAwait(false);
            try { await RefreshAsync().ConfigureAwait(false); } catch (BridgeException error) { PublishError(error); }
            return CommandResult(command);
        }
        catch (BridgeException error)
        {
            PublishError(error);
            return OperationResult.Fail(error.OutcomeUnknown ? "写入结果未知，请检查游戏状态；不要直接重复提交。" : error.Message);
        }
        finally { _gate.Release(); }
    }
    public Task<OperationResult> SetLockAsync(FeatureId id,bool enabled) => Task.FromResult(OperationResult.Fail("桥接模式暂未接入锁定功能。"));

    public async Task<CardReply> ExecuteCardAsync(CardAccess expected, CardRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed || Volatile.Read(ref _closeRequests) > 0)
                throw new BridgeException("closing", "组件连接正在关闭。");
            var state = await _client.GetStateAsync().ConfigureAwait(false);
            if (!MatchesCardAccess(state, expected))
                throw new BridgeException("card_context_changed", "游戏会话、读档或场景已变化，请刷新卡片列表。");
            var command = await _client.SubmitCardForSessionAsync(expected.Epoch.Session, expected.Epoch.Generation,
                expected.Context, request).ConfigureAwait(false);
            command = await AwaitCommandAsync(expected.Epoch.Session, command).ConfigureAwait(false);
            if (command.State != "applied")
                throw new BridgeException("card_rejected", "卡片操作被拒绝，请核对当前场景、卡片或★上限。");

            try
            {
                if (!MatchesCardAccess(await _client.GetStateAsync().ConfigureAwait(false), expected))
                    throw new BridgeException("card_context_changed", "游戏会话、读档或场景已变化，请刷新卡片列表。");
                if (command.Cards is not { Rows.Count: > 0 } cards || cards.Rows[0].Index != request.Index ||
                    (request.Operation != CardOperation.Page && (cards.Rows.Count != 1 || cards.Rows[0].Key != request.Key)) ||
                    (request.Operation == CardOperation.Grant && !cards.Rows[0].Owned) ||
                    (request.Operation == CardOperation.SetStar && cards.Star != request.Star))
                    throw new BridgeException("invalid_card_result", "返回的卡片与请求不一致。");
                return cards;
            }
            catch (BridgeException error) when (request.Writes)
            {
                throw new BridgeException(error.Code, error.Message, true, error);
            }
        }
        finally { _gate.Release(); }
    }

    private static bool MatchesCardAccess(BridgeState state, CardAccess expected) =>
        expected.Epoch.Ready && state.CardManagementAvailable && state.ScriptVersion == 1 && state.Phase == 1 &&
        state.GameThreadResponsive == true && state.CardContext > 0 && state.CardContext == expected.Context &&
        state.Session == expected.Epoch.Session && state.Pid == expected.Epoch.ProcessId &&
        state.LoadGeneration == expected.Epoch.Generation;

    public async Task<OperationResult> SetToggleAsync(FeatureId id,bool enabled)
    {
        if (!ComponentFeatures.ContainsKey(id)) return OperationResult.Fail("此功能未接入游戏组件。");
        if (Volatile.Read(ref _closeRequests)>0) return OperationResult.Fail("正在确认桥接关闭，请稍后再操作。");
        await _gate.WaitAsync().ConfigureAwait(false);
        bool previouslyOwned=_owned.Contains(id);
        string? previousSession=_ownedSession;
        try
        {
            if (_closed) return OperationResult.Fail("连接已关闭。");
            if (Volatile.Read(ref _closeRequests)>0) return OperationResult.Fail("正在确认桥接关闭，请稍后再操作。");
            var state=await _client.GetStateAsync().ConfigureAwait(false);
            if (_ownedSession is not null && _ownedSession!=state.Session) { _owned.Clear(); _ownedSession=null; previouslyOwned=false; previousSession=null; }
            if (!CanWrite(state, id) || (id==FeatureId.Resources && enabled && !CanWriteData(state))) return OperationResult.Fail("游戏组件尚未就绪，或当前版本不支持此功能。");
            if (enabled) { _owned.Add(id); _ownedSession=state.Session; }
            var command=await ChangeFeatureAsync(state,id,enabled).ConfigureAwait(false);
            if (command.State=="rejected" && enabled) { if (previouslyOwned) _owned.Add(id); else _owned.Remove(id); _ownedSession=previousSession; }
            if (command.State=="applied" && !enabled) { _owned.Remove(id); if (_owned.Count==0) _ownedSession=null; }
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (BridgeException error) { PublishError(error); } // The command result remains known even if the following snapshot fails.
            return CommandResult(command);
        }
        catch (BridgeException error)
        {
            if (enabled && !error.OutcomeUnknown) { if (previouslyOwned) _owned.Add(id); else _owned.Remove(id); _ownedSession=previousSession; }
            PublishError(error);
            return OperationResult.Fail(error.OutcomeUnknown ? "命令结果未知，保留关闭责任，请检查当前状态后再操作。" : error.Message);
        }
        finally { _gate.Release(); }
    }
    public async Task<OperationResult> TryCloseAsync()
    {
        Interlocked.Increment(ref _closeRequests);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return OperationResult.Ok;
            foreach (var id in _owned.ToArray())
            {
                try
                {
                    var state=await _client.GetStateAsync().ConfigureAwait(false);
                    if (state.Session==_ownedSession)
                    {
                        var result=CommandResult(await ChangeFeatureAsync(state,id,false).ConfigureAwait(false));
                        if (!result.Success) return result;
                    }
                    _owned.Remove(id);
                    // A changed/exited process cannot retain the old DLL's toggle.
                }
                catch (BridgeException error) when (error.Code is "game_not_running" or "session_changed") { }
                catch (BridgeException error) { PublishError(error); return OperationResult.Fail("尚未确认组件功能关闭："+error.Message); }
            }
            _closed=true; await _stop.CancelAsync().ConfigureAwait(false);
            Publish(new(ConnectionState.Closed,"桥接已关闭",null,_client.IsSimulation,EmptyFeatures(),RuntimeMode:"bridge"));
            return OperationResult.Ok;
        }
        finally { _gate.Release(); Interlocked.Decrement(ref _closeRequests); }
    }
    private async Task<BridgeCommand> ChangeFeatureAsync(BridgeState state,FeatureId id,bool enabled)
    {
        if (state.Session is null) throw new BridgeException("session_required","桥接未提供进程会话标识。");
        var command=await _client.SetFeatureForSessionAsync(state.Session,ComponentFeatures[id].Name,enabled,state.LoadGeneration).ConfigureAwait(false);
        return await AwaitCommandAsync(state.Session,command).ConfigureAwait(false);
    }
    private async Task<BridgeCommand> AwaitCommandAsync(string session,BridgeCommand command)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
        try
        {
            while (!command.IsTerminal)
            {
                await Task.Delay(25,deadline.Token).ConfigureAwait(false);
                command=await _client.GetCommandResultForSessionAsync(session,command.Id,deadline.Token).ConfigureAwait(false);
            }
            return command;
        }
        catch (Exception error) when (error is BridgeException or OperationCanceledException)
        {
            throw new BridgeException("command_result_unknown",$"命令 {command.Id} 已提交，但尚未确认最终结果。",true,error);
        }
    }
    private static OperationResult CommandResult(BridgeCommand command) => command.State=="applied" ? OperationResult.Ok : OperationResult.Fail(command.State=="queued"
            ? $"命令 {command.Id} 仍在等待游戏线程处理。" : command.Error ?? "游戏状态变化，命令已拒绝。");
    private async Task PollAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try { await RefreshAsync().ConfigureAwait(false); }
                catch (BridgeException error) { PublishError(error); }
                catch (Exception error)
                {
                    // A transient discovery/client failure must not leave a stale Ready snapshot forever.
                    PublishError(new BridgeException("runtime_error","桥接轮询异常："+error.Message,inner:error));
                }
                finally { _gate.Release(); }
                await Task.Delay(250,_stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task RefreshAsync()
    {
        var state=await _client.GetStateAsync(_stop.Token).ConfigureAwait(false);
        if (_ownedSession is not null && _ownedSession!=state.Session) { _owned.Clear(); _ownedSession=null; }
        _observed=state;
        var features=EmptyFeatures();
        bool ready=CanWrite(state);
        string hp=state.PlayerHp is { } value ? $"我方 HP {value} / {state.PlayerHpMax?.ToString() ?? "?"}" : "当前没有战斗 HP 快照";
        features[FeatureId.Hp]=new(Requested:state.HpGuard,Active:state.HpGuard && ready,CanEdit:ready,
            Detail:$"{hp}；已拦截 {state.BlockedHits?.ToString() ?? "?"} 次伤害。",
            Help:"在已接入的战斗伤害结算点保护我方 HP。保留治疗、初始化与结果结算；不主动补满血量。",
            ActiveDescription:"游戏线程已应用保护开关；具体伤害路径的验证范围见说明。");
        bool cardsReady=CanWrite(state,FeatureId.NoDuplicateCards);
        features[FeatureId.NoDuplicateCards]=new(Requested:state.NoDuplicateCards,Active:state.NoDuplicateCards && cardsReady,CanEdit:cardsReady,
            Detail:cardsReady ? $"已排除 {state.FilteredCards ?? 0} 个候选；餐券补位 {state.FallbackCards ?? 0} 次。下一批奖励生效。" : "请安装新版游戏组件并重新启动游戏。",
            Help:FeatureCatalog.Get(FeatureId.NoDuplicateCards).Help,ActiveDescription:"游戏线程已应用开关；固定奖励和消耗型奖励保留原规则。");
        foreach (var id in new[] { FeatureId.Redraw, FeatureId.Redistribute })
        {
            bool available=CanWrite(state,id), enabled=id==FeatureId.Redraw ? state.FoodRedraw : state.AutoRedistribute;
            features[id]=new(Requested:enabled,Active:enabled && available,CanEdit:available,
                Detail:available ? "游戏线程控制脚本分支，无需扫描或改写运行中的指令。" : "请安装新版游戏组件并重新启动游戏。",
                Help:FeatureCatalog.Get(id).Help,ActiveDescription:"游戏线程已应用开关；具体玩法验证范围见说明。");
        }
        bool dataReady=CanWriteData(state);
        features[FeatureId.Resources]=new(Requested:state.ResourcesFull,Active:state.ResourcesFull && dataReady,
            CanEdit:CanWrite(state,FeatureId.Resources) && (dataReady || state.ResourcesFull),
            Detail:"在游戏更新时补足资源，保留正常消费流程；关闭不回退已改数值。", Help:FeatureCatalog.Get(FeatureId.Resources).Help);
        foreach (var id in ValueFeatures.Keys)
        {
            long? current=id switch { FeatureId.Food=>state.Food, FeatureId.Medal=>state.Medal, FeatureId.Friendship=>state.Friendship, FeatureId.Unit=>state.Unit, _=>null };
            bool full=state.ResourcesFull && id is FeatureId.Food or FeatureId.Medal or FeatureId.Friendship;
            features[id]=new(Value:dataReady ? current : null,CanEdit:dataReady && !full,
                Detail:full ? "请先关闭资源全满。" : dataReady ? "通过游戏自身方法修改。" : "请安装新版组件并等待游戏数据就绪。",
                Help:FeatureCatalog.Get(id).Help);
        }
        var battle = ReadBattle(state);
        bool battleRulesReady = CanWrite(state, FeatureId.ApFull);
        const string upgradeBattle = "请安装新版游戏组件并重新启动游戏。";
        foreach (var (id, definition) in ComponentFeatures)
        {
            if (definition.Rule == BattleRules.None) continue;
            bool requested = state.BattleRules.HasFlag(definition.Rule);
            bool isAp = id == FeatureId.ApFull;
            features[id] = new(Value: isAp ? state.Ap : null, Maximum: isAp ? state.ApMax : null,
                Requested: requested, Active: requested && battleRulesReady && state.OrdinaryBattle, CanEdit: battleRulesReady,
                Detail: !battleRulesReady ? upgradeBattle : !state.OrdinaryBattle ? "等待普通战斗" : null,
                ActiveDescription: "战斗开关已生效。");
        }
        foreach (var id in new[] { FeatureId.PlayerHp, FeatureId.EnemyHp })
        {
            var (current, maximum) = ReadHp(state, id);
            bool editable = battle is not null && current is > 0 && maximum is > 0;
            features[id] = new(Value: state.OrdinaryBattle ? current : null, Maximum: state.OrdinaryBattle ? maximum : null,
                CanEdit: editable, Detail: editable ? null : battleRulesReady ? "等待我方操作阶段" : upgradeBattle);
        }
        var connection=state.Phase!=1 ? ConnectionState.Loading : state.HeartbeatAgeMs is null or <0 or >2000 ? ConnectionState.Error : ComponentFeatures.Keys.Any(id=>CanWrite(state,id)) ? ConnectionState.Ready : ConnectionState.Unsupported;
        Publish(new(connection,$"桥接 · PID {state.Pid} · 读档 {state.LoadGeneration}",state.Pid,_client.IsSimulation,features,
            connection==ConnectionState.Error ? "游戏线程心跳未知或暂停，已停用操作。" : connection==ConnectionState.Unsupported ? "当前脚本尚未提供兼容的组件功能入口。" : null,"bridge",
            "组件提供游戏线程功能与状态读取。") {
                LoadEpoch=ReadEpoch(state), Battle=battle,
                CardManagementAvailable = state.CardManagementAvailable,
                CardContext = state.CardManagementAvailable && state.Phase == 1 && state.GameThreadResponsive == true ? state.CardContext : 0
            });
    }
    private static (long? Current, long? Maximum) ReadHp(BridgeState state, FeatureId id) =>
        id == FeatureId.PlayerHp ? (state.PlayerHp, state.PlayerHpMax) : (state.EnemyHp, state.EnemyHpMax);
    private static GameLoadEpoch? ReadEpoch(BridgeState state) =>
        state.Session is { } session && state.ScriptVersion==1 && (state.Capabilities.GetValueOrDefault() & 8)!=0
            ? new(state.Pid,session,state.LoadGeneration,state.Phase==1 && state.HeartbeatAgeMs is >=0 and <=2000) : null;
    private static BattleEditContext? ReadBattle(BridgeState state) =>
        state.BattleFeaturesAvailable && state.OrdinaryBattle && state.BattleReady && state.BattleContext > 0 &&
        ReadEpoch(state) is { Ready: true } epoch ? new(epoch, state.BattleContext) : null;
    private static bool CanWriteData(BridgeState state) => CanWrite(state,FeatureId.Resources) && state.DataReady;
    private static bool CanWrite(BridgeState state,FeatureId id=FeatureId.Hp) => state.Phase==1 && state.ScriptVersion==1 && ComponentFeatures.TryGetValue(id,out var feature) && (state.Capabilities.GetValueOrDefault() & feature.Capability)!=0 && state.HeartbeatAgeMs is >=0 and <=2000;
    private static Dictionary<FeatureId,FeatureObservation> EmptyFeatures() => FeatureCatalog.All.ToDictionary(x=>x.Id,_=>new FeatureObservation(Detail:"此功能尚未接入桥接模式。"));
    private void PublishError(BridgeException error)
    {
        var connection=error.Code=="game_not_running" ? ConnectionState.Searching : ConnectionState.Error;
        Publish(new(connection,"等待游戏副本桥接",_observed?.Pid,_client.IsSimulation,EmptyFeatures(),error.Message,"bridge",
            "请运行配置的游戏副本；桥接模式不会回退到外部内存写入。"));
    }
    private void Publish(TrainerSnapshot snapshot)
    {
        Volatile.Write(ref _current,snapshot);
        var observers=SnapshotChanged;
        if (observers is null) return;
        foreach (Action<TrainerSnapshot> observer in observers.GetInvocationList())
        {
            try { observer(snapshot); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Bridge snapshot observer failed: {0}",error); }
        }
    }
}
