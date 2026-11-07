using System.Collections.ObjectModel;

namespace R10.Core;

/// <summary>Explicitly selected UI demonstration. Never discovers or opens any process.</summary>
public sealed class SimulationRuntime : ITrainerRuntime
{
    private readonly Dictionary<FeatureId, FeatureObservation> _features = FeatureCatalog.All.ToDictionary(x => x.Id, _ => new FeatureObservation(CanEdit:true));
    private bool _closed;
    public TrainerSnapshot Current { get; private set; } = TrainerSnapshot.Empty;
    public event Action<TrainerSnapshot>? SnapshotChanged;
    public SimulationRuntime()
    {
        var values = new Dictionary<FeatureId, decimal> { [FeatureId.Difficulty] = 8, [FeatureId.Turn] = 6, [FeatureId.DeathRate] = 12.5m, [FeatureId.Army] = 1200, [FeatureId.Food] = 2, [FeatureId.Medal] = 1, [FeatureId.Friendship] = 2, [FeatureId.Unit] = 35, [FeatureId.Star] = 80 };
        foreach (var (id, value) in values) _features[id] = _features[id] with { Value = value };
        Publish();
    }
    public Task StartAsync() { Publish(); return Task.CompletedTask; }
    public Task<OperationResult> SetValueAsync(FeatureId id, decimal value)
    {
        if (_closed || !FeatureCatalog.ValidValue(id, value)) return Task.FromResult(OperationResult.Fail("数值超出范围。"));
        if (id is FeatureId.Food or FeatureId.Medal or FeatureId.Friendship && _features[FeatureId.Resources].Requested)
            return Task.FromResult(OperationResult.Fail("请先关闭资源全满。"));
        _features[id] = _features[id] with { Value = value, Error = null }; Publish(); return Task.FromResult(OperationResult.Ok);
    }
    public Task<OperationResult> SetToggleAsync(FeatureId id, bool enabled)
    {
        if (_closed || FeatureCatalog.Get(id).Kind != FeatureKind.Toggle) return Task.FromResult(OperationResult.Fail("功能不可用。"));
        _features[id] = _features[id] with { Requested = enabled, Active = enabled };
        if (enabled && id is FeatureId.EnemyKill or FeatureId.EnemyNear)
        {
            var other = id == FeatureId.EnemyKill ? FeatureId.EnemyNear : FeatureId.EnemyKill;
            _features[other] = _features[other] with { Requested = false, Active = false };
        }
        if (id == FeatureId.Resources)
            foreach (var counter in new[] { FeatureId.Food, FeatureId.Medal, FeatureId.Friendship })
                _features[counter] = _features[counter] with { Value = enabled ? 3 : _features[counter].Value, CanEdit = !enabled };
        Publish(); return Task.FromResult(OperationResult.Ok);
    }
    public Task<OperationResult> SetLockAsync(FeatureId id, bool enabled)
    {
        if (_closed || !FeatureCatalog.Get(id).HasLock) return Task.FromResult(OperationResult.Fail("功能不可用。"));
        _features[id] = _features[id] with { LockRequested = enabled, LockActive = enabled }; Publish(); return Task.FromResult(OperationResult.Ok);
    }
    public Task<OperationResult> TryCloseAsync() { _closed = true; Publish(); return Task.FromResult(OperationResult.Ok); }
    private void Publish()
    {
        Current = new(_closed ? ConnectionState.Closed : ConnectionState.Ready, _closed ? "已关闭演示" : "战斗中 · 演示", null, true,
            new ReadOnlyDictionary<FeatureId, FeatureObservation>(new Dictionary<FeatureId, FeatureObservation>(_features)));
        SnapshotChanged?.Invoke(Current);
    }
}
