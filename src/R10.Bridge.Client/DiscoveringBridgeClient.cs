using System.ComponentModel;
using System.Diagnostics;

namespace R10.Bridge.Client;

public sealed record BridgeProcessIdentity(int ProcessId, long StartTimeUtcTicks)
{
    public string Session => $"{ProcessId}:{StartTimeUtcTicks}";
}

public interface IGameProcessResolver
{
    BridgeProcessIdentity Resolve();
}

public sealed class GameDirectoryProcessResolver : IGameProcessResolver
{
    private readonly string _executable;
    public GameDirectoryProcessResolver(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Path.IsPathFullyQualified(gameDirectory))
            throw new ArgumentException("An absolute game directory is required.", nameof(gameDirectory));
        _executable = Path.GetFullPath(Path.Combine(gameDirectory, "Rance10.exe"));
    }

    public BridgeProcessIdentity Resolve()
    {
        if (!OperatingSystem.IsWindows()) throw new BridgeException("unsupported_platform", "Game discovery requires Windows.");
        var matches = new List<BridgeProcessIdentity>();
        Exception? inspectionError = null;
        Process[] processes;
        try { processes = Process.GetProcessesByName("Rance10"); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new BridgeException("process_enumeration_failed", "The game process list could not be inspected.", inner: error);
        }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && string.Equals(Path.GetFullPath(path), _executable, StringComparison.OrdinalIgnoreCase))
                        matches.Add(new BridgeProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (InvalidOperationException) { } // Process exited while enumerating.
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException) { inspectionError = error; }
            }
        }
        if (inspectionError is not null) throw new BridgeException("process_access_denied", "A Rance10 process identity could not be inspected; refusing to guess.", inner: inspectionError);
        if (matches.Count == 0) throw new BridgeException("game_not_running", "No Rance10.exe is running from the selected game directory.");
        if (matches.Count != 1) throw new BridgeException("multiple_game_processes", "More than one Rance10.exe is running from the selected game directory.");
        return matches[0];
    }
}

public sealed class DiscoveringBridgeClient : IBridgeClient
{
    private readonly IGameProcessResolver _resolver;
    private readonly Func<BridgeProcessIdentity, IBridgeClient> _factory;
    private readonly SemaphoreSlim _gate = new(1);
    private IBridgeClient? _client;
    private BridgeProcessIdentity? _identity;
    public int ProcessId => _identity?.ProcessId ?? 0;
    public string? Session => _identity?.Session;
    public bool IsSimulation { get; }

    public DiscoveringBridgeClient(string gameDirectory, TimeSpan? timeout = null)
        : this(new GameDirectoryProcessResolver(gameDirectory), identity => new BridgeClient(identity.ProcessId,
            new NamedPipeBridgeTransport(identity.ProcessId, timeout, identity.Session)), false) { }

    private DiscoveringBridgeClient(IGameProcessResolver resolver, Func<BridgeProcessIdentity, IBridgeClient> factory, bool simulation)
    {
        _resolver = resolver; _factory = factory; IsSimulation = simulation;
    }

    public static DiscoveringBridgeClient CreateForTesting(IGameProcessResolver resolver, Func<BridgeProcessIdentity, IBridgeClient> factory) => new(resolver, factory, true);

    public Task<BridgeState> GetStateAsync(CancellationToken cancellationToken = default) =>
        WithClientAsync(null, client => client.GetStateAsync(cancellationToken), cancellationToken);

    public Task<BridgeCommand> SetFeatureForSessionAsync(string session, string feature, bool enabled, long generation, CancellationToken cancellationToken = default) =>
        WithClientAsync(RequireSession(session), client => client.SetFeatureForSessionAsync(session, feature, enabled, generation, cancellationToken), cancellationToken);

    public Task<BridgeCommand> GetCommandResultForSessionAsync(string session, long id, CancellationToken cancellationToken = default) =>
        WithClientAsync(RequireSession(session), client => client.GetCommandResultForSessionAsync(session, id, cancellationToken), cancellationToken);

    public Task<BridgeCommand> SetValueForSessionAsync(string session, string feature, int value, long generation, CancellationToken cancellationToken = default) =>
        WithClientAsync(RequireSession(session), client => client.SetValueForSessionAsync(session, feature, value, generation, cancellationToken), cancellationToken);

    public Task<BridgeCommand> ActionForSessionAsync(string session, int action, int argument, int argument2, long generation, CancellationToken cancellationToken = default) =>
        WithClientAsync(RequireSession(session), client => client.ActionForSessionAsync(session, action, argument, argument2, generation, cancellationToken), cancellationToken);

    public Task<BridgeCommand> SetFeatureAsync(string feature, bool enabled, long generation, CancellationToken cancellationToken = default) =>
        throw new BridgeException("session_required", "Discovery mode requires SetFeatureForSessionAsync with a session from GetStateAsync.");
    public Task<BridgeCommand> GetCommandResultAsync(long id, CancellationToken cancellationToken = default) =>
        throw new BridgeException("session_required", "Discovery mode requires GetCommandResultForSessionAsync with the original command session.");
    public async Task PingAsync(CancellationToken cancellationToken = default) =>
        _ = await WithClientAsync<object?>(null, async client => { await client.PingAsync(cancellationToken).ConfigureAwait(false); return null; }, cancellationToken).ConfigureAwait(false);

    private async Task<T> WithClientAsync<T>(string? expectedSession, Func<IBridgeClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = _resolver.Resolve();
            if (expectedSession is not null && identity.Session != expectedSession)
                throw new BridgeException("session_changed", "The game process changed; this command belongs to the previous process session.");
            if (_identity != identity || _client is null)
            {
                _client = _factory(identity);
                _identity = identity;
            }
            return await operation(_client).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static string RequireSession(string session) => !string.IsNullOrWhiteSpace(session) ? session : throw new BridgeException("session_required", "A process session is required.");
}
