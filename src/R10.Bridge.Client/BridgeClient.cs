using System.Text.Json;

namespace R10.Bridge.Client;

public sealed class BridgeClient : IBridgeClient
{
    private readonly IBridgeTransport _transport;
    public int ProcessId { get; }
    public bool IsSimulation => _transport.IsSimulation;
    public string? Session => _transport.Session;

    public BridgeClient(int processId, TimeSpan? timeout = null)
        : this(processId, new NamedPipeBridgeTransport(processId, timeout)) { }

    public BridgeClient(int processId, IBridgeTransport transport)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        ProcessId = processId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<BridgeState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(new { op = "get_state" }, false, cancellationToken).ConfigureAwait(false);
        var pid = Integer(response, "pid");
        if (pid != ProcessId) throw new BridgeException("response_pid_mismatch", "The bridge state belongs to a different process.");
        var phase = Integer(response, "phase");
        if (phase is < 0 or > 2) throw Invalid("phase");
        return new BridgeState(ProcessId, Integer(response, "gameThreadId"), Integer(response, "heartbeat"), Integer(response, "frame"),
            Integer(response, "loadGeneration"), (int)phase, Boolean(response, "hpGuard"), Integer(response, "lastCommandId"),
            Text(response, "lastCommandState"), NullableInteger(response, "playerHp"), NullableInteger(response, "playerHpMax"), NullableInteger(response, "enemyHp"),
            NullableInteger(response, "heartbeatAgeMs"), NullableInteger(response, "blockedHits"), NullableInteger(response, "lastRequestedHp"),
            NullableInteger(response, "scriptVersion"), NullableInteger(response, "capabilities"),
            NullableNonnegativeInteger(response, "loadKind"), NullableInteger(response, "loadSlot"),
            NullableNonnegativeInteger(response, "lastCompletedGeneration"), NullableNonnegativeInteger(response, "lastLoadOutcome"))
        { NoDuplicateCards = response.TryGetProperty("noDuplicateCards", out var cards) && cards.ValueKind == JsonValueKind.True, FilteredCards = NullableInteger(response,"filteredCards"), FallbackCards = NullableInteger(response,"fallbackCards"), Session = Session, BattleReady = response.TryGetProperty("battleReady", out var ready) && ready.ValueKind == JsonValueKind.True };
    }

    public async Task<BridgeCommand> ActionForSessionAsync(string session, int action, int argument, int argument2, long generation, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        if (action is < 2 or > 4 || generation < 0 ||
            (action == 2 && (argument is < 0 or > 9998 || argument2 != 0)) ||
            (action == 3 && (argument != 0 || argument2 != 0)) ||
            (action == 4 && (argument is < 0 or > 6 || argument2 is < 0 or > 1)))
            throw new BridgeException("invalid_action", "Invalid semantic action arguments.");
        var response = await ExchangeAsync(new { op = "action", action, argument, argument2, generation }, true, cancellationToken).ConfigureAwait(false);
        try { return ParseCommand(response); }
        catch (BridgeException error) { throw new BridgeException(error.Code, error.Message, true, error); }
    }

    public async Task<BridgeCommand> SetFeatureAsync(string feature, bool enabled, long generation, CancellationToken cancellationToken = default)
    {
        if (feature is not ("hp_guard" or "no_duplicate_cards")) throw new BridgeException("unsupported_feature", "Unsupported bridge feature.");
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        var response = await ExchangeAsync(new { op = "set_feature", feature, enabled, generation }, true, cancellationToken).ConfigureAwait(false);
        try { return ParseCommand(response); }
        catch (BridgeException error) { throw new BridgeException(error.Code, error.Message, true, error); }
    }

    public async Task<BridgeCommand> GetCommandResultAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        var response = await ExchangeAsync(new { op = "get_command_result", id }, false, cancellationToken).ConfigureAwait(false);
        var command = ParseCommand(response);
        if (command.Id != id) throw new BridgeException("command_id_mismatch", "The bridge returned a different command result.");
        return command;
    }

    public async Task PingAsync(CancellationToken cancellationToken = default) =>
        _ = await ExchangeAsync(new { op = "ping" }, false, cancellationToken).ConfigureAwait(false);

    public Task<BridgeCommand> SetFeatureForSessionAsync(string session, string feature, bool enabled, long generation, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        return SetFeatureAsync(feature, enabled, generation, cancellationToken);
    }

    public Task<BridgeCommand> GetCommandResultForSessionAsync(string session, long id, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        return GetCommandResultAsync(id, cancellationToken);
    }

    private void ValidateSession(string session)
    {
        if (string.IsNullOrEmpty(session) || Session is null || Session != session)
            throw new BridgeException("session_changed", "The command session does not match this process. Read get_state again.");
    }

    private async Task<JsonElement> ExchangeAsync(object request, bool mutation, CancellationToken cancellationToken)
    {
        var response = await _transport.ExchangeAsync(JsonSerializer.SerializeToElement(request), mutation, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.ValueKind != JsonValueKind.Object) throw Invalid("response");
            var ok = Boolean(response, "ok");
            if (!ok) throw new BridgeException("bridge_rejected", Text(response, "error"));
            if (Integer(response, "protocol") != BridgeProtocol.Version || Text(response, "build") != BridgeProtocol.Build)
                throw new BridgeException("unsupported_bridge", "The bridge protocol or build is not supported.");
            return response;
        }
        catch (BridgeException error) when (mutation && error.Code != "bridge_rejected")
        {
            throw new BridgeException(error.Code, error.Message, true, error);
        }
    }

    private static BridgeCommand ParseCommand(JsonElement response)
    {
        var id = Integer(response, "id");
        var state = Text(response, "state");
        if (id <= 0 || state is not ("queued" or "applied" or "rejected")) throw Invalid("command");
        var error = response.TryGetProperty("error", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        return new BridgeCommand(id, state, error);
    }

    private static long Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0
        ? number : throw Invalid(name);
    private static long? NullableInteger(JsonElement root, string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw Invalid(name);
    private static long? NullableNonnegativeInteger(JsonElement root, string name)
    {
        var number = NullableInteger(root, name);
        return number is <0 ? throw Invalid(name) : number;
    }
    private static bool Boolean(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Invalid(name);
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw Invalid(name);
    private static BridgeException Invalid(string name) => new("invalid_response", $"The bridge response has an invalid or missing {name} field.");
}
