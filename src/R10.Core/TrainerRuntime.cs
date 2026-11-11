using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using R10.Core.Data;
using R10.Core.Memory;
using R10.Core.Patching;

namespace R10.Core;

/// <summary>A single logical worker owns the process, all commands, and every recovery obligation.</summary>
public sealed class TrainerRuntime : ITrainerRuntime, IGameLoadObserver
{
    private sealed record Work(Func<OperationResult> Action, TaskCompletionSource<OperationResult> Completion);
    private readonly Channel<Work> _commands = Channel.CreateBounded<Work>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<GameConnection?> _discover;
    private readonly Func<IProcessMemory, string, R10PatchEngine> _createPatches;
    private readonly Func<long> _clock;
    private readonly Func<int, Task> _waitForWork;
    private readonly GameLoadFence _loadFence;
    private long _loadRevision;
    private readonly object _closeGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private TaskCompletionSource<OperationResult>? _closeCompletion;
    private int _closeRequested;
    private readonly Dictionary<FeatureId, bool> _requested = [], _lockRequested = [], _active = [], _lockActive = [];
    private readonly Dictionary<FeatureId, DataLock> _locks = [];
    private readonly Dictionary<FeatureId, string> _errors = [];
    private IReadOnlyDictionary<FeatureId, decimal?> _values = new Dictionary<FeatureId, decimal?>();
    private GameConnection? _connection;
    private R10DataEngine? _data;
    private R10PatchEngine? _patches;
    private SceneObservation? _scene;
    private long _generation = -1, _nextFast, _nextSlow, _nextDiscovery, _nextPublish;
    private PatchObservation? _publishedRedraw, _publishedRedistribute;
    private bool _publishedLocating, _publishedBattleReady;
    private int _stableGame, _stableBattle, _started;
    private volatile bool _closing;
    private bool _patchWorkRequested;
    private volatile bool _closed;
    private string? _error, _dataFault;
    private ConnectionState _state = ConnectionState.Searching;
    private TrainerSnapshot _current = TrainerSnapshot.Empty;
    public TrainerSnapshot Current => Volatile.Read(ref _current);
    public event Action<TrainerSnapshot>? SnapshotChanged;
    public TrainerRuntime(Func<GameConnection?>? connectionFactory = null)
        : this(connectionFactory ?? GameConnection.Discover, (memory, path) => new R10PatchEngine(memory, path)) { }
    internal TrainerRuntime(Func<GameConnection?> connectionFactory, Func<IProcessMemory, string, R10PatchEngine> patchFactory,
        Func<long>? clock = null, Func<int, Task>? waitForWork = null)
    {
        _discover = connectionFactory; _createPatches = patchFactory;
        _clock = clock ?? (() => Environment.TickCount64);
        _loadFence = new(_clock);
        _waitForWork = waitForWork ?? WaitForWorkAsync;
    }
    public void ObserveGameLoad(GameLoadEpoch? epoch) => _loadFence.Update(epoch);
    private void ObserveLoadRevision()
    {
        long revision=_loadFence.Revision;
        if (revision==_loadRevision) return;
        _loadRevision=revision;
        foreach (var id in new[] { FeatureId.Turn, FeatureId.DeathRate, FeatureId.Army })
        {
            if (_lockRequested.GetValueOrDefault(id)) _errors[id]="读档或游戏会话已变化，旧锁定已取消；请确认新值后重新锁定。";
            _lockRequested[id]=false; _lockActive[id]=false; _locks.Remove(id);
        }
    }
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = Task.Run(LoopAsync);
        return Task.CompletedTask;
    }
    public Task<OperationResult> SetValueAsync(FeatureId id, decimal value) => Queue(() => Execute(id, () =>
    {
        if (FeatureCatalog.IsBattleHp(id)) throw new InvalidOperationException("此功能需要游戏组件。");
        if (!FeatureCatalog.ValidValue(id, value)) throw new ArgumentOutOfRangeException(nameof(value), "数值超出范围或步进不正确。");
        EnsureReady(id);
        if (id is FeatureId.Food or FeatureId.Medal or FeatureId.Friendship && IsRequested(FeatureId.Resources))
            throw new InvalidOperationException("请先关闭资源全满，再单独修改此数值。");
        _data!.WriteValue(id, value);
        if (_lockRequested.GetValueOrDefault(id)) _locks[id] = _data.CaptureLock(id,_loadFence.CaptureGuard(_connection!.Memory.ProcessId));
        _values = _data.ReadValues();
    }));
    public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled) => Queue(() => Execute(id, () =>
    {
        if (FeatureCatalog.Get(id).Kind != FeatureKind.Toggle) throw new ArgumentException("此功能不是开关。");
        if (id is FeatureId.Redraw or FeatureId.Redistribute)
        {
            if (_patches == null) { if (!enabled) return; throw new InvalidOperationException("尚未连接游戏。"); }
            if (_closing && enabled) throw new InvalidOperationException("正在恢复修改，请先完成关闭。");
            _patches.SetRequested(id, enabled); _patchWorkRequested = true; return;
        }
        if (id == FeatureId.NoDuplicateCards || FeatureCatalog.RequiresBattleComponent(id))
            throw new InvalidOperationException("此功能需要游戏组件。");
        if (enabled && (_connection == null || _closing)) throw new InvalidOperationException("尚未连接游戏，或正在恢复修改。");
        if (enabled && _dataFault != null) throw new InvalidOperationException(_dataFault);
        _requested[id] = enabled; _active[id] = false;
        if (enabled && id is FeatureId.EnemyKill or FeatureId.EnemyNear)
        {
            var other = id == FeatureId.EnemyKill ? FeatureId.EnemyNear : FeatureId.EnemyKill;
            _requested[other] = false; _active[other] = false; _errors.Remove(other);
        }
    }));
    public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled) => Queue(() => Execute(id, () =>
    {
        if (!FeatureCatalog.Get(id).HasLock) throw new ArgumentException("此功能不能锁定。");
        if (!enabled) { _lockRequested[id] = false; _locks.Remove(id); _lockActive[id] = false; return; }
        EnsureReady(id);
        _locks[id] = _data!.CaptureLock(id,_loadFence.CaptureGuard(_connection!.Memory.ProcessId)); _lockRequested[id] = true; _lockActive[id] = false;
    }));
    public Task<OperationResult> TryCloseAsync()
    {
        if (_closed || Volatile.Read(ref _started) == 0) { _closed = true; return Task.FromResult(OperationResult.Ok); }
        lock (_closeGate)
        {
            if (_closed) return Task.FromResult(OperationResult.Ok);
            if (_closeCompletion != null) return _closeCompletion.Task;
            _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _closeRequested, 1);
            WakeWorker();
            return _closeCompletion.Task;
        }
    }
    private Task<OperationResult> Queue(Func<OperationResult> action)
    {
        if (_closed || _closing || Volatile.Read(ref _closeRequested) != 0)
            return Task.FromResult(OperationResult.Fail("修改器已关闭或正在恢复修改。"));
        var completion = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new(action, completion))) completion.SetResult(OperationResult.Fail("操作过于频繁，请稍后重试。"));
        WakeWorker();
        return completion.Task;
    }
    private void WakeWorker()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    private async Task WaitForWorkAsync(int milliseconds)
    {
        if (milliseconds == 0) await Task.Yield();
        else await _wake.WaitAsync(milliseconds).ConfigureAwait(false);
    }
    private bool ShouldYieldPatchWork() => Volatile.Read(ref _closeRequested) != 0 || _closing || _commands.Reader.TryPeek(out _);
    private void ProcessCloseRequest()
    {
        if (Volatile.Read(ref _closeRequested) == 0) return;
        var result = CloseCore();
        Publish();
        TaskCompletionSource<OperationResult>? completion;
        lock (_closeGate)
        {
            completion = _closeCompletion; _closeCompletion = null;
            Volatile.Write(ref _closeRequested, 0);
        }
        completion?.TrySetResult(result);
    }
    private async Task LoopAsync()
    {
        try
        {
            while (!_closed)
            {
                // Closing is an out-of-band request: it cannot sit behind a full command channel
                // or a partly completed locator. Only the worker performs recovery or disposal.
                ProcessCloseRequest();
                if (_closed) break;
                for (int i = 0; i < 8 && Volatile.Read(ref _closeRequested) == 0 && _commands.Reader.TryRead(out var work); i++)
                {
                    OperationResult result;
                    try { result = _closing ? OperationResult.Fail("修改器正在恢复修改。") : work.Action(); }
                    catch (Exception ex) { result = OperationResult.Fail(ex.Message); _error = ex.Message; }
                    Publish(); work.Completion.TrySetResult(result);
                    if (_closed) break;
                }
                if (_closed) break;
                if (Volatile.Read(ref _closeRequested) != 0) continue;
                bool tickFailed = false;
                try { Tick(); } catch (Exception ex) { tickFailed = true; Suspend(); _error = ex.Message; _state = _closing ? ConnectionState.RestorePending : ConnectionState.Error; }
                Publish(false);
                if (_closed) break;
                // A read-only scan does not wait for the next battle-maintenance deadline.
                // Yield cooperatively between bounded slices without a system timer. Commands
                // and close are processed first on resumption; normal idle waits are wakeable.
                int delay = !tickFailed && !_closing && _connection != null && (_patches?.IsLocating == true || _patchWorkRequested) ? 0 : 75;
                if (Volatile.Read(ref _closeRequested) == 0) await _waitForWork(delay).ConfigureAwait(false);
            }
        }
        finally
        {
            _commands.Writer.TryComplete();
            while (_commands.Reader.TryRead(out var pending)) pending.Completion.TrySetResult(OperationResult.Fail("修改器正在关闭。"));
            lock (_closeGate)
            {
                _closeCompletion?.TrySetResult(_closed ? OperationResult.Ok : OperationResult.Fail("修改器维护循环已停止。"));
                _closeCompletion = null;
            }
        }
    }
    private void Tick()
    {
        ObserveLoadRevision();
        long now = _clock();
        if (_connection != null && !_connection.Memory.IsAlive) { DetachExited(); }
        if (_closing) { CloseCore(); return; }
        if (_connection == null)
        {
            if (now < _nextDiscovery) return;
            _nextDiscovery = now + 2000;
            try
            {
                _connection = _discover();
                if (_connection == null) { _state = ConnectionState.Searching; _error = null; return; }
                _data = new(_connection.Memory); _patches = _createPatches(_connection.Memory, _connection.AinPath);
                _generation = -1; _stableGame = _stableBattle = 0; _nextFast = _nextSlow = 0; _error = null;
            }
            catch (NotSupportedException ex) { _state = ConnectionState.Unsupported; _error = ex.Message; return; }
        }
        bool slow = now >= _nextSlow;
        if (slow)
        {
            _nextSlow = now + 500;
            RefreshScene(true);
            if (GameplayReady)
            {
                foreach (var id in new[] { FeatureId.Treasure, FeatureId.Resources, FeatureId.War }) Maintain(id);
                foreach (var id in new[] { FeatureId.Turn, FeatureId.DeathRate, FeatureId.Army }) MaintainLock(id);
                _values = _data!.ReadValues(); SynchronizeEngine();
            }
            else _values = new Dictionary<FeatureId, decimal?>();
        }
        // Slow maintenance may have blocked since Tick began. Set the next deadline after
        // fast work finishes so a late slice cannot cause adjacent battle-write transactions.
        if (_clock() >= _nextFast)
        {
            try
            {
                if (new[] { FeatureId.Hp, FeatureId.EnemyKill, FeatureId.EnemyNear }.Any(IsRequested))
                {
                    RefreshScene(false);
                    foreach (var id in new[] { FeatureId.Hp, FeatureId.EnemyKill, FeatureId.EnemyNear }) Maintain(id);
                }
            }
            finally { _nextFast = _clock() + 75; }
        }
        // Regular data and battle upkeep run before the read-only locator slice. Requests return
        // after intent is queued; their Active state is published only after the later write/readback.
        if (!ShouldYieldPatchWork() && (slow || _patchWorkRequested || _patches!.IsLocating))
        {
            _patchWorkRequested = false;
            try
            {
                _patches!.Maintain(ShouldYieldPatchWork);
                _patchWorkRequested = !_patches.IsLocating && _patches.LastScanReadBytes != 0;
                _errors.Remove(FeatureId.Redraw); _errors.Remove(FeatureId.Redistribute);
            }
            catch (Exception ex) { _error = ex.Message; }
        }
    }
    private bool GameplayReady => _scene?.GameplayReady == true && _stableGame >= 2;
    private bool BattleReady => GameplayReady && _scene?.BattleReady == true && _stableBattle >= 2;
    private bool IsRequested(FeatureId id) => _requested.GetValueOrDefault(id);
    private void RefreshScene(bool slow)
    {
        ObserveScene(_data!.Refresh(), slow);
    }
    private void SynchronizeEngine()
    {
        if (_data != null && _connection != null && !_closing) ObserveScene(_data.Current, false);
    }
    private void ObserveScene(SceneObservation scene, bool slow)
    {
        _scene = scene;
        if (_scene.Generation != _generation)
        {
            _generation = _scene.Generation; _stableGame = _stableBattle = 0; _locks.Clear(); Suspend();
        }
        if (!_scene.GameplayReady) { _stableGame = _stableBattle = 0; _locks.Clear(); Suspend(); }
        else if (slow) _stableGame = Math.Min(2, _stableGame + 1);
        if (!_scene.BattleReady) { _stableBattle = 0; foreach (var id in new[] { FeatureId.Hp, FeatureId.EnemyKill, FeatureId.EnemyNear }) _active[id] = false; }
        else if (slow) _stableBattle = Math.Min(2, _stableBattle + 1);
        _state = _dataFault != null ? ConnectionState.Error : GameplayReady ? ConnectionState.Ready : ConnectionState.Loading;
        _error = _dataFault ?? _scene.Error;
    }
    private void EnsureReady(FeatureId id)
    {
        ObserveLoadRevision();
        if (_closing || _connection == null || _data == null) throw new InvalidOperationException("游戏未连接或正在关闭。");
        if (_dataFault != null) throw new InvalidOperationException(_dataFault);
        if (!_connection.Memory.IsAlive) { DetachExited(); throw new InvalidOperationException("游戏已退出。"); }
        RefreshScene(false);
        if (!GameplayReady) throw new InvalidOperationException("等待游戏场景稳定后再操作。");
        if (id == FeatureId.DeathRate && !_scene!.DeathRateWritable) throw new InvalidOperationException("当前阶段暂停修改人类死亡率。");
    }
    private void Maintain(FeatureId id)
    {
        _active[id] = false;
        if (_closing || Volatile.Read(ref _closeRequested) != 0) return;
        if (!IsRequested(id) || _dataFault != null) return;
        bool battle = id is FeatureId.Hp or FeatureId.EnemyKill or FeatureId.EnemyNear;
        if (battle ? !BattleReady : !GameplayReady) return;
        try { _data!.MaintainToggle(id); _active[id] = true; _errors.Remove(id); }
        catch (Exception ex) { RecordFailure(id, ex); }
        finally { SynchronizeEngine(); }
    }
    private void MaintainLock(FeatureId id)
    {
        _lockActive[id] = false;
        ObserveLoadRevision();
        if (_connection is null || !_loadFence.Allows(_connection.Memory.ProcessId)) return;
        if (_closing || Volatile.Read(ref _closeRequested) != 0) return;
        if (_dataFault != null || !_lockRequested.GetValueOrDefault(id) || !GameplayReady || (id == FeatureId.DeathRate && !_scene!.DeathRateWritable)) return;
        try
        {
            if (!_locks.TryGetValue(id, out var target)) { _locks[id] = _data!.CaptureLock(id,_loadFence.CaptureGuard(_connection!.Memory.ProcessId)); return; }
            if (!_data!.MaintainLock(target)) { _locks.Remove(id); return; }
            _lockActive[id] = true; _errors.Remove(id);
        }
        catch (Exception ex) { RecordFailure(id, ex); }
        finally { SynchronizeEngine(); }
    }
    private OperationResult Execute(FeatureId id, Action action)
    {
        try { action(); _errors.Remove(id); return OperationResult.Ok; }
        catch (Exception ex) { RecordFailure(id, ex); return OperationResult.Fail(ex.Message); }
        finally { SynchronizeEngine(); }
    }
    private void RecordFailure(FeatureId id, Exception ex)
    {
        _errors[id] = ex.Message;
        if (ex is DataWriteException { RollbackComplete: false })
        {
            _dataFault = "部分数据未能确认恢复，已暂停数据写入。请重启游戏后再连接。" + ex.Message;
            _requested.Clear(); _lockRequested.Clear(); _locks.Clear(); Suspend();
            _state = ConnectionState.Error; _error = _dataFault;
        }
    }
    private void Suspend() { _active.Clear(); _lockActive.Clear(); }
    private void DetachExited()
    {
        _connection!.Dispose(); _connection = null; _data = null; _patches = null; _scene = null;
        _patchWorkRequested = false;
        _requested.Clear(); _lockRequested.Clear(); _locks.Clear(); _errors.Clear(); Suspend();
        _values = new Dictionary<FeatureId, decimal?>(); _state = ConnectionState.Searching; _error = null; _dataFault = null;
    }
    private OperationResult CloseCore()
    {
        _closing = true; _requested.Clear(); _lockRequested.Clear(); _locks.Clear(); Suspend();
        _patches?.CancelRequests();
        try
        {
            if (_connection != null)
            {
                if (_connection.Memory.IsAlive) _patches?.RestoreAll();
                _connection.Dispose(); _connection = null;
            }
            _patches = null; _data = null; _scene = null; _closed = true; _state = ConnectionState.Closed; _error = null;
            return OperationResult.Ok;
        }
        catch (Exception ex) { _state = ConnectionState.RestorePending; _error = "恢复尚未完成：" + ex.Message; return OperationResult.Fail(_error); }
    }
    private void Publish(bool force = true)
    {
        long now = _clock();
        var patchObservations = _patches?.Observations;
        var redraw = patchObservations?.GetValueOrDefault(FeatureId.Redraw);
        var redistribute = patchObservations?.GetValueOrDefault(FeatureId.Redistribute);
        bool locating = _patches?.IsLocating == true;
        // Commands and state changes publish immediately. Progress-only and periodic snapshots
        // stay at the UI cadence even when hundreds of locator slices run between data ticks.
        if (!force && now < _nextPublish && _state == _current.Connection && _error == _current.Error &&
            _connection?.Memory.ProcessId == _current.ProcessId && _publishedBattleReady == BattleReady &&
            _publishedLocating == locating && _publishedRedraw == redraw && _publishedRedistribute == redistribute) return;
        _nextPublish = now + 75;
        _publishedLocating = locating; _publishedBattleReady = BattleReady;
        _publishedRedraw = redraw; _publishedRedistribute = redistribute;
        var features = new Dictionary<FeatureId, FeatureObservation>();
        foreach (var f in FeatureCatalog.All)
        {
            bool patch = f.Id is FeatureId.Redraw or FeatureId.Redistribute;
            bool requested = IsRequested(f.Id), active = _active.GetValueOrDefault(f.Id);
            string? error = _errors.GetValueOrDefault(f.Id);
            if (!patch) error ??= _data?.ReadErrors.GetValueOrDefault(f.Id);
            if (patch && patchObservations?.TryGetValue(f.Id, out var p) == true) { requested = p.Requested; active = p.Active; error ??= p.Error; }
            if (_state is ConnectionState.Error or ConnectionState.RestorePending or ConnectionState.Closed) active = false;
            bool canEdit = !_closing && _connection != null && (f.Kind == FeatureKind.Toggle || GameplayReady);
            if (!patch && _dataFault != null) canEdit = false;
            if (f.Id == FeatureId.NoDuplicateCards || FeatureCatalog.RequiresBattleComponent(f.Id)) canEdit = false;
            if (f.Id == FeatureId.DeathRate && _scene?.DeathRateWritable != true) canEdit = false;
            if (f.Id is FeatureId.Food or FeatureId.Medal or FeatureId.Friendship && IsRequested(FeatureId.Resources)) canEdit = false;
            string? detail = f.Id == FeatureId.Army && GameplayReady && !_values.GetValueOrDefault(f.Id).HasValue
                ? "四国数值不一致或尚未读到统一值" : null;
            if (patch && requested && !active && _patches?.IsLocating == true)
                detail = $"正在定位脚本… {_patches.ScanProgress:P0}";
            features[f.Id] = new(_values.GetValueOrDefault(f.Id), requested, active, canEdit,
                _lockRequested.GetValueOrDefault(f.Id), _lockActive.GetValueOrDefault(f.Id), error,
                detail);
        }
        string status = _state switch
        {
            ConnectionState.Ready => BattleReady ? "战斗中" : "游戏已就绪",
            ConnectionState.Loading => "等待场景稳定", ConnectionState.Unsupported => "版本尚未支持",
            ConnectionState.RestorePending => "正在恢复修改", ConnectionState.Error => "连接或读取失败",
            ConnectionState.Closed => "已断开", _ => "等待游戏连接"
        };
        var snapshot = new TrainerSnapshot(_state, status, _connection?.Memory.ProcessId, false, new ReadOnlyDictionary<FeatureId, FeatureObservation>(features), _error);
        Volatile.Write(ref _current, snapshot);
        if (SnapshotChanged is { } changed)
            foreach (Action<TrainerSnapshot> subscriber in changed.GetInvocationList())
                try { subscriber(snapshot); } catch (Exception ex) { Trace.TraceError("Snapshot subscriber: {0}", ex); }
    }
}
