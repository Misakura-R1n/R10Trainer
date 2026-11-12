using System.Diagnostics;
using System.Runtime.CompilerServices;
using R10.Bridge.Client;
using R10.Core;
using R10.Core.Data;
using R10.Core.Memory;

[assembly: InternalsVisibleTo("R10.Bridge.Tests")]

namespace R10.Bridge.Runtime;

/// <summary>The desktop product's runtime. HP is always a game-thread bridge operation.
/// Component features never fall back to external writes; other features retain their memory implementation.</summary>
public sealed class UnifiedTrainerRuntime : ITrainerRuntime, ICardManagementRuntime
{
    public const string HpHelp = "在已接入的战斗伤害结算点保护我方 HP。保留治疗、初始化与结果结算；不主动补满血量。首次使用请安装游戏组件并重新启动游戏。";
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _stateGate = new();
    private readonly Func<string, (ITrainerRuntime Memory, ITrainerRuntime Bridge)> _factory;
    private Pair? _pair;
    private string? _gameDirectory;
    private bool _started, _closed, _recovering;
    private string? _recoveryError;
    private int _transitions;
    private TrainerSnapshot _current = Empty();
    public string? GameDirectory => Volatile.Read(ref _gameDirectory);
    public TrainerSnapshot Current => Volatile.Read(ref _current);
    public event Action<TrainerSnapshot>? SnapshotChanged;

    private sealed class Pair(ITrainerRuntime memory, ITrainerRuntime bridge)
    {
        internal ITrainerRuntime Memory { get; } = memory;
        internal ITrainerRuntime Bridge { get; } = bridge;
        internal Action<TrainerSnapshot>? Changed;
        internal Action<TrainerSnapshot>? BridgeChanged;
        internal Task<OperationResult>? MemoryClose, BridgeClose;
    }

    public UnifiedTrainerRuntime(string? gameDirectory = null)
        : this(gameDirectory, GameConnection.Discover, path => new BridgeTrainerRuntime(path)) { }
    internal UnifiedTrainerRuntime(string? gameDirectory, Func<string, GameConnection?> discover, Func<string, ITrainerRuntime> createBridge)
        : this(gameDirectory, CreateRuntimeFactory(discover, createBridge)) { }
    private static Func<string, (ITrainerRuntime Memory, ITrainerRuntime Bridge)> CreateRuntimeFactory(
        Func<string, GameConnection?> discover, Func<string, ITrainerRuntime> createBridge)
    {
        var dataWriteFaults = new DataWriteFaults();
        return path => (new TrainerRuntime(() => discover(path), dataWriteFaults), createBridge(path));
    }
    internal UnifiedTrainerRuntime(string? gameDirectory, Func<string, (ITrainerRuntime Memory, ITrainerRuntime Bridge)> factory)
    {
        _factory = factory;
        if (gameDirectory is not null)
        {
            _gameDirectory = GameConnection.NormalizeDirectory(gameDirectory);
            _pair = CreatePair(_gameDirectory);
        }
        Refresh();
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed || _started) return;
            _started = true;
            if (_pair is { } pair) await Task.WhenAll(pair.Memory.StartAsync(), pair.Bridge.StartAsync()).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Refresh();
    }

    /// <summary>Null disconnects and releases file handles for component installation.
    /// A failed close retains both old runtimes and prevents new writes until recovery succeeds.</summary>
    public async Task<OperationResult> SelectGameAsync(string? path)
    {
        string? selected;
        try { selected = path is null ? null : GameConnection.NormalizeDirectory(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return OperationResult.Fail(error.Message); }
        Interlocked.Increment(ref _transitions);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return OperationResult.Fail("修改器已关闭。");
            var result = await ClosePairAsync().ConfigureAwait(false);
            if (!result.Success) return result;
            var next = selected is null ? null : CreatePair(selected);
            lock (_stateGate) { _pair = next; _gameDirectory = selected; _recovering = false; _recoveryError = null; }
            if (_started && next is not null) await Task.WhenAll(next.Memory.StartAsync(), next.Bridge.StartAsync()).ConfigureAwait(false);
            return OperationResult.Ok;
        }
        catch (Exception error)
        {
            lock (_stateGate) { _recovering = true; _recoveryError = error.Message; }
            return OperationResult.Fail(error.Message);
        }
        finally { Interlocked.Decrement(ref _transitions); _gate.Release(); Refresh(); }
    }

    public Task<OperationResult> SetValueAsync(FeatureId id, decimal value) =>
        ExecuteAsync(id, false, (runtime, bridge) => PrepareValue(runtime, bridge, id, value), bindLoad: true);
    public Task<OperationResult> SetValueForEpochAsync(FeatureId id, decimal value, GameLoadEpoch expected) =>
        ExecuteAsync(id, false, (runtime, _) => () => runtime.SetValueForEpochAsync(id, value, expected));
    public Task<OperationResult> SetBattleHpAsync(FeatureId id, decimal value, BattleEditContext expected) =>
        ExecuteAsync(id, false, (runtime, _) => () => runtime.SetBattleHpAsync(id, value, expected));
    public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled) => ExecuteAsync(id, false, (runtime, _) =>
    {
        if (!enabled || runtime is not TrainerRuntime memory) return () => runtime.SetLockAsync(id, enabled);
        var contextValid = memory.CaptureDataRequest();
        return () => memory.SetLockAsync(id, true, contextValid);
    }, bindLoad: enabled);
    public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled) =>
        ExecuteAsync(id, true, (runtime, _) => () => runtime.SetToggleAsync(id, enabled));

    private static Func<Task<OperationResult>> PrepareValue(ITrainerRuntime runtime, TrainerSnapshot bridge, FeatureId id, decimal value)
    {
        if (runtime is TrainerRuntime memory)
        {
            var contextValid = memory.CaptureDataRequest();
            return () => memory.SetValueAsync(id, value, contextValid);
        }
        if (FeatureCatalog.IsBattleHp(id))
            return bridge.Battle is { } battle ? () => runtime.SetBattleHpAsync(id, value, battle) :
                () => Task.FromResult(OperationResult.Fail("战斗目标尚未就绪。"));
        if (BridgeTrainerRuntime.ValueFeatures.ContainsKey(id))
            return bridge.LoadEpoch is { Ready: true } epoch ? () => runtime.SetValueForEpochAsync(id, value, epoch) :
                () => Task.FromResult(OperationResult.Fail("等待游戏组件确认读档状态。"));
        return () => runtime.SetValueAsync(id, value);
    }

    public async Task<CardReply> ExecuteCardAsync(CardAccess expected, CardRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed || _recovering || Volatile.Read(ref _transitions) != 0 ||
                _pair is not { } pair || pair.Bridge is not ICardManagementRuntime cards ||
                !ComponentReady(pair.Memory.Current, pair.Bridge.Current))
                throw new BridgeException("card_unavailable", "卡片管理尚未就绪，请等待所选游戏连接。");
            return await cards.ExecuteCardAsync(expected, request).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<OperationResult> ExecuteAsync(FeatureId id, bool toggle,
        Func<ITrainerRuntime, TrainerSnapshot, Func<Task<OperationResult>>> prepare, bool bindLoad = false)
    {
        if (Volatile.Read(ref _transitions) != 0) return OperationResult.Fail("正在切换游戏或恢复修改，请稍后再操作。");
        Pair? pair;
        lock (_stateGate) pair = _pair;
        if (pair is null) return OperationResult.Fail("请先选择游戏目录。");

        var submittedBridge = pair.Bridge.Current;
        var submittedPid = pair.Memory.Current.ProcessId;
        if (bindLoad && submittedBridge.LoadEpoch is { Ready: false })
            return OperationResult.Fail("游戏正在读档或组件状态未确认，请等待游戏就绪。");
        bool componentToggle = BridgeTrainerRuntime.ComponentFeatures.ContainsKey(id);
        bool component = componentToggle || BridgeTrainerRuntime.ValueFeatures.ContainsKey(id) || FeatureCatalog.IsBattleHp(id);
        Func<Task<OperationResult>> action;
        try { action = prepare(component ? pair.Bridge : pair.Memory, submittedBridge); }
        catch (Exception error) { return OperationResult.Fail(error.Message); }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed || _recovering || Volatile.Read(ref _transitions) != 0) return OperationResult.Fail("正在关闭或恢复修改，请先完成恢复。");
            if (!ReferenceEquals(_pair, pair)) return OperationResult.Fail("所选游戏已变化，请重新操作。");
            var memory = pair.Memory.Current;
            var bridge = pair.Bridge.Current;
            if (DifferentProcesses(memory, bridge)) return OperationResult.Fail("游戏进程发生变化，请等待重新连接。");
            if (bindLoad && (submittedPid != memory.ProcessId || submittedBridge.LoadEpoch != bridge.LoadEpoch))
                return OperationResult.Fail("游戏会话或读档状态已变化，请重新确认后应用。");
            if (component)
            {
                if (toggle != componentToggle) return OperationResult.Fail("此组件功能不支持该操作。");
                if (id == FeatureId.EnemyHp && EnemyHpOverridden(memory)) return OperationResult.Fail("请先关闭敌人濒死或直死。");
                if (!ComponentReady(memory, bridge) || bridge.Features.GetValueOrDefault(id)?.CanEdit != true) return OperationResult.Fail("组件功能尚未就绪，请安装新版组件并启动所选游戏。");
            }
            return await action().ConfigureAwait(false);
        }
        catch (Exception error) { return OperationResult.Fail(error.Message); }
        finally { _gate.Release(); Refresh(); }
    }

    public async Task<OperationResult> TryCloseAsync()
    {
        Interlocked.Increment(ref _transitions);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return OperationResult.Ok;
            var result = await ClosePairAsync().ConfigureAwait(false);
            if (!result.Success) return result;
            lock (_stateGate) { _closed = true; _pair = null; _recovering = false; _recoveryError = null; }
            return OperationResult.Ok;
        }
        finally { Interlocked.Decrement(ref _transitions); _gate.Release(); Refresh(); }
    }

    private async Task<OperationResult> ClosePairAsync()
    {
        if (_pair is not { } pair) return OperationResult.Ok;
        lock (_stateGate) { _recovering = true; _recoveryError = null; }
        Refresh();
        // Keep pending tasks after timeout: retry observes the same recovery attempt, never
        // abandons an owner or schedules duplicate shutdown operations on a busy runtime.
        if (pair.MemoryClose is null || (pair.MemoryClose.IsCompletedSuccessfully && !pair.MemoryClose.Result.Success))
            pair.MemoryClose = AttemptCloseAsync(pair.Memory);
        if (pair.BridgeClose is null || (pair.BridgeClose.IsCompletedSuccessfully && !pair.BridgeClose.Result.Success))
            pair.BridgeClose = AttemptCloseAsync(pair.Bridge);
        try
        {
            var results = await Task.WhenAll(pair.MemoryClose, pair.BridgeClose).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (results.Any(result => !result.Success))
            {
                var message = string.Join("；", results.Where(result => !result.Success).Select(result => result.Error ?? "恢复尚未完成"));
                lock (_stateGate) _recoveryError = message;
                return OperationResult.Fail(message);
            }
            pair.Memory.SnapshotChanged -= pair.Changed;
            pair.Bridge.SnapshotChanged -= pair.BridgeChanged;
            return OperationResult.Ok;
        }
        catch (TimeoutException)
        {
            const string message = "恢复操作尚未结束，已保留原连接；请稍后重试。";
            lock (_stateGate) _recoveryError = message;
            return OperationResult.Fail(message);
        }
    }
    private static async Task<OperationResult> AttemptCloseAsync(ITrainerRuntime runtime)
    {
        try { return await runtime.TryCloseAsync().ConfigureAwait(false); }
        catch (Exception error) { return OperationResult.Fail(error.Message); }
    }
    private Pair CreatePair(string path)
    {
        var (memory, bridge) = _factory(path);
        var pair = new Pair(memory, bridge);
        pair.Changed = _ => { lock (_stateGate) { if (!ReferenceEquals(_pair, pair)) return; } Refresh(); };
        pair.BridgeChanged = snapshot =>
        {
            lock (_stateGate) { if (!ReferenceEquals(_pair,pair)) return; }
            if (memory is IGameLoadObserver observer) observer.ObserveGameLoad(snapshot.LoadEpoch);
            Refresh();
        };
        if (memory is IGameLoadObserver initialObserver) initialObserver.ObserveGameLoad(bridge.Current.LoadEpoch);
        memory.SnapshotChanged += pair.Changed;
        bridge.SnapshotChanged += pair.BridgeChanged;
        return pair;
    }
    private static bool DifferentProcesses(TrainerSnapshot memory, TrainerSnapshot bridge) =>
        memory.ProcessId is { } memoryPid && bridge.ProcessId is { } bridgePid && memoryPid != bridgePid &&
        bridge.Connection is ConnectionState.Ready or ConnectionState.Loading or ConnectionState.Unsupported;
    private static bool ComponentReady(TrainerSnapshot memory, TrainerSnapshot bridge) =>
        memory.ProcessId is { } memoryPid && memoryPid == bridge.ProcessId &&
        memory.Connection is ConnectionState.Ready or ConnectionState.Loading &&
        bridge.Connection == ConnectionState.Ready && bridge.LoadEpoch?.Ready != false;
    private static bool EnemyHpOverridden(TrainerSnapshot memory) =>
        memory.Features.GetValueOrDefault(FeatureId.EnemyKill)?.Requested == true ||
        memory.Features.GetValueOrDefault(FeatureId.EnemyNear)?.Requested == true;
    private static TrainerSnapshot Empty() => TrainerSnapshot.Empty with
    {
        RuntimeMode = "unified", Status = "请选择游戏", Notice = "选择 Rance10.exe，之后可直接在修改器中启用 HP 保护。",
        Features = FeatureCatalog.All.ToDictionary(feature => feature.Id, feature => new FeatureObservation(Help: feature.Id == FeatureId.Hp ? HpHelp : null))
    };
    private void Refresh()
    {
        TrainerSnapshot snapshot;
        lock (_stateGate)
        {
            if (_closed) snapshot = Empty() with { Connection = ConnectionState.Closed, Status = "已断开", Notice = null };
            else if (_pair is not { } pair) snapshot = Empty();
            else
            {
                var memory = pair.Memory.Current;
                var bridge = pair.Bridge.Current;
                bool ready = ComponentReady(memory, bridge), mismatch = DifferentProcesses(memory, bridge);
                bool loadPending = bridge.LoadEpoch is { Ready: false } epoch && epoch.ProcessId == memory.ProcessId;
                var features = memory.Features.ToDictionary(item => item.Key, item => item.Value);
                var hp = bridge.Features.GetValueOrDefault(FeatureId.Hp) ?? new();
                features[FeatureId.Hp] = hp with
                {
                    Help = HpHelp, CanEdit = ready && !_recovering && hp.CanEdit, Active = ready && !_recovering && hp.Active,
                    Detail = ready && hp.CanEdit ? hp.Detail : "HP 保护未连接；安装组件后请重新启动所选游戏。",
                    ActiveDescription = "保护开关已在游戏中生效；不主动补满血量。"
                };
                var cards = bridge.Features.GetValueOrDefault(FeatureId.NoDuplicateCards) ?? new();
                features[FeatureId.NoDuplicateCards] = cards with { CanEdit=ready && !_recovering && cards.CanEdit,
                    Active=ready && !_recovering && cards.Active, Help=FeatureCatalog.Get(FeatureId.NoDuplicateCards).Help };
                var componentRows = BridgeTrainerRuntime.ComponentFeatures.Keys.Where(id => id is not (FeatureId.Hp or FeatureId.NoDuplicateCards))
                    .Concat(BridgeTrainerRuntime.ValueFeatures.Keys).Concat([FeatureId.PlayerHp, FeatureId.EnemyHp]);
                foreach (var id in componentRows)
                {
                    var item=bridge.Features.GetValueOrDefault(id) ?? new();
                    bool overridden = id == FeatureId.EnemyHp && EnemyHpOverridden(memory);
                    features[id]=item with { CanEdit=ready && !_recovering && item.CanEdit && !overridden,
                        Active=ready && !_recovering && item.Active, Help=FeatureCatalog.Get(id).Help,
                        Detail=overridden ? "请先关闭敌人濒死或直死" : item.Detail };
                }
                if (mismatch || _recovering || loadPending)
                    foreach (var id in features.Keys.ToArray())
                    {
                        var item = features[id];
                        bool canSetToggle = !mismatch && !_recovering && FeatureCatalog.Get(id).Kind == FeatureKind.Toggle && item.CanEdit;
                        features[id] = item with { CanEdit = canSetToggle, Active = false, LockActive = false };
                    }
                bool waitingForLoad = loadPending && memory.Connection == ConnectionState.Ready;
                snapshot = memory with
                {
                    RuntimeMode = "unified", Features = features,
                    LoadEpoch = ready && !_recovering && !mismatch ? bridge.LoadEpoch : null,
                    CardManagementAvailable = ready && !_recovering && !mismatch && bridge.CardManagementAvailable,
                    CardContext = ready && !_recovering && !mismatch ? bridge.CardContext : 0,
                    Battle = ready && !_recovering && !mismatch ? bridge.Battle : null,
                    Connection = _recovering ? ConnectionState.RestorePending : mismatch || waitingForLoad ? ConnectionState.Loading : memory.Connection,
                    Status = _recovering ? "正在恢复修改" : mismatch ? "正在重新连接游戏" :
                        waitingForLoad ? "等待游戏就绪" : memory.Status,
                    Error = _recoveryError ?? memory.Error,
                    Notice = _recovering ? "正在关闭功能并释放游戏连接。" : mismatch ? "游戏进程发生变化，等待重新确认。" :
                        ready ? null : "HP 保护尚未连接。可在游戏设置中安装组件，再启动所选游戏；其他功能仍按各自条件使用。"
                };
            }
            Volatile.Write(ref _current, snapshot);
        }
        lock (_stateGate)
        {
            // Two child workers may publish concurrently. Never deliver an older composed
            // snapshot after a newer failure/disconnect has already replaced it.
            if (!ReferenceEquals(_current, snapshot)) return;
            if (SnapshotChanged is { } observers)
                foreach (Action<TrainerSnapshot> observer in observers.GetInvocationList())
                    try { observer(snapshot); } catch (Exception error) { Trace.TraceError("Unified runtime observer: {0}", error); }
        }
    }
}
