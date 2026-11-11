using R10.Core.Memory;

namespace R10.Core.Data;

/// <summary>Retains unresolved data faults across connections owned by a single trainer.</summary>
public sealed class DataWriteFaults
{
    private readonly Dictionary<int, (long? CreationFileTime, string Message)> _faults = [];

    internal string? GetForConnection(GameConnection connection)
    {
        var (pid, creation) = connection.ProcessIdentity;
        if (!_faults.TryGetValue(pid, out var fault)) return null;

        // A verified new process with the same PID proves the previous process exited.
        if (creation.HasValue && fault.CreationFileTime.HasValue && creation != fault.CreationFileTime)
        {
            _faults.Remove(pid);
            return null;
        }
        return fault.Message;
    }

    internal void Record(GameConnection connection, string message)
    {
        var (pid, creation) = connection.ProcessIdentity;
        _faults[pid] = (creation, message);
    }

    internal void ConfirmExit(GameConnection connection)
    {
        var (pid, creation) = connection.ProcessIdentity;
        if (_faults.TryGetValue(pid, out var fault) && fault.CreationFileTime == creation)
            _faults.Remove(pid);
    }
}
