using System.Diagnostics;
using System.Runtime.CompilerServices;
using R10.Core;
using R10.Core.Memory;

[assembly: InternalsVisibleTo("R10.Bridge.Tests")]

namespace R10.Bridge.Runtime;

/// <summary>The desktop product's runtime. HP is always a game-thread bridge operation.
/// Other features retain their existing memory implementation in the same selected game.</summary>
public sealed class UnifiedTrainerRuntime : ITrainerRuntime
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
        internal Task<OperationResult>? MemoryClose, BridgeClose;
    }

    public UnifiedTrainerRuntime(string? gameDirectory = null)
        : this(gameDirectory, path => (new TrainerRuntime(() => GameConnection.Discover(path)), new BridgeTrainerRuntime(path))) { }
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

    public Task<OperationResult> SetValueAsync(FeatureId id, decimal value) => ExecuteAsync(id, false, runtime => runtime.SetValueAsync(id, value));
    public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled) => ExecuteAsync(id, false, runtime => runtime.SetLockAsync(id, enabled));
    public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled) => ExecuteAsync(id, true, runtime => runtime.SetToggleAsync(id, enabled));

    private async Task<OperationResult> ExecuteAsync(FeatureId id, bool toggle, Func<ITrainerRuntime, Task<OperationResult>> action)
    {
        if (Volatile.Read(ref _transitions) != 0) return OperationResult.Fail("正在切换游戏或恢复修改，请稍后再操作。");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed || _recovering || Volatile.Read(ref _transitions) != 0) return OperationResult.Fail("正在关闭或恢复修改，请先完成恢复。");
            if (_pair is not { } pair) return OperationResult.Fail("请先选择游戏目录。");
            var memory = pair.Memory.Current;
            var bridge = pair.Bridge.Current;
            if (DifferentProcesses(memory, bridge)) return OperationResult.Fail("游戏进程发生变化，请等待重新连接。");
            if (id is FeatureId.Hp or FeatureId.NoDuplicateCards)
            {
                if (!toggle) return OperationResult.Fail("此组件功能仅提供开关操作。");
                if (!ComponentReady(memory, bridge) || bridge.Features.GetValueOrDefault(id)?.CanEdit != true) return OperationResult.Fail("组件功能尚未就绪，请安装新版组件并启动所选游戏。");
                return await action(pair.Bridge).ConfigureAwait(false);
            }
            return await action(pair.Memory).ConfigureAwait(false);
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
            pair.Bridge.SnapshotChanged -= pair.Changed;
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
        memory.SnapshotChanged += pair.Changed;
        bridge.SnapshotChanged += pair.Changed;
        return pair;
    }
    private static bool DifferentProcesses(TrainerSnapshot memory, TrainerSnapshot bridge) =>
        memory.ProcessId is { } memoryPid && bridge.ProcessId is { } bridgePid && memoryPid != bridgePid &&
        bridge.Connection is ConnectionState.Ready or ConnectionState.Loading or ConnectionState.Unsupported;
    private static bool ComponentReady(TrainerSnapshot memory, TrainerSnapshot bridge) =>
        memory.ProcessId is { } memoryPid && memoryPid == bridge.ProcessId &&
        memory.Connection is ConnectionState.Ready or ConnectionState.Loading &&
        bridge.Connection == ConnectionState.Ready;
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
                if (mismatch || _recovering)
                    foreach (var id in features.Keys.ToArray()) features[id] = features[id] with { CanEdit = false, Active = false, LockActive = false };
                snapshot = memory with
                {
                    RuntimeMode = "unified", Features = features,
                    Connection = _recovering ? ConnectionState.RestorePending : mismatch ? ConnectionState.Loading : memory.Connection,
                    Status = _recovering ? "正在恢复修改" : mismatch ? "正在重新连接游戏" : memory.Status,
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
