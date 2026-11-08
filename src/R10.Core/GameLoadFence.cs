namespace R10.Core;

/// <summary>Once a component is observed, silence never silently re-enables legacy locks.</summary>
internal sealed class GameLoadFence(Func<long> clock)
{
    private readonly object _gate = new();
    private GameLoadEpoch? _identity, _current;
    private long _observedAt, _revision;
    public long Revision { get { lock (_gate) return _revision; } }
    public void Update(GameLoadEpoch? epoch)
    {
        lock (_gate)
        {
            if (epoch is not null && (_identity is null || epoch.ProcessId != _identity.ProcessId ||
                epoch.Session != _identity.Session || epoch.Generation != _identity.Generation))
            { _revision++; _identity=epoch; }
            _current=epoch; _observedAt=clock();
        }
    }
    public bool Allows(int pid) { lock (_gate) return AllowsCore(pid); }
    private bool AllowsCore(int pid) => _identity is null ||
        (_current is { Ready:true } epoch && epoch.ProcessId==pid && clock()-_observedAt is >=0 and <=1000);
    public Func<bool> CaptureGuard(int pid)
    {
        lock (_gate)
        {
            if (!AllowsCore(pid)) throw new InvalidOperationException("读档或组件状态未确认，暂停锁定，请等待重新连接。");
            long revision=_revision;
            return () => { lock (_gate) return _revision==revision && AllowsCore(pid); };
        }
    }
}
