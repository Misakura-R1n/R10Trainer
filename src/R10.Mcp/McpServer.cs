using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using R10.Bridge.Client;

namespace R10.Mcp;

public sealed class McpServer(IBridgeClient bridge, IGameInput? gameInput = null)
{
    private readonly IGameInput _input = gameInput ?? new GameInput();
    private readonly SemaphoreSlim _mutationGate = new(1);
    private const string ModernVersion = "2026-07-28";
    private static readonly string[] LegacyVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();
    private readonly SemaphoreSlim _outputGate = new(1);
    private string? _legacyVersion;

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var source = input;
        var reader = new BoundedJsonLine(source, BridgeProtocol.MaximumLineBytes);
        var running = new List<Task>();
        try
        {
            while (await reader.ReadAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                foreach (var failed in running.Where(task => task.IsFaulted || task.IsCanceled)) await failed.ConfigureAwait(false);
                running.RemoveAll(task => task.IsCompletedSuccessfully);
                if (running.Count >= 64)
                {
                    await await Task.WhenAny(running).ConfigureAwait(false);
                    running.RemoveAll(task => task.IsCompletedSuccessfully);
                }
                running.Add(DispatchAsync(line, output, lifetime.Token));
            }
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            foreach (var pending in _pending.Values) await pending.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(string line, Stream output, CancellationToken cancellationToken)
    {
        var response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
        if (response is null) return;
        await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await BoundedJsonLine.WriteAsync(output, response.ToJsonString(), BridgeProtocol.MaximumLineBytes, cancellationToken).ConfigureAwait(false); }
        finally { _outputGate.Release(); }
    }

    public async Task<JsonObject?> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false })
                as JsonObject ?? throw new JsonException();
        }
        catch (JsonException) { return RpcError(null, -32700, "Parse error"); }
        var id = request["id"]?.DeepClone();
        var method = String(request["method"]);
        if (String(request["jsonrpc"]) != "2.0" || method is null)
            return RpcError(id, -32600, "Invalid request");
        if (!request.ContainsKey("id"))
        {
            if (method == "notifications/cancelled" && request["params"] is JsonObject notification && notification["requestId"] is { } cancelledId &&
                _pending.TryGetValue(cancelledId.ToJsonString(), out var pending))
                await pending.CancelAsync().ConfigureAwait(false);
            return null;
        }
        if (id is not JsonValue value || !(value.TryGetValue<string>(out _) || value.TryGetValue<long>(out _)))
            return RpcError(id, -32600, "Request ID must be a string or integer");
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var key = id.ToJsonString();
        if (!_pending.TryAdd(key, requestLifetime)) return RpcError(id, -32600, "Duplicate in-flight request ID");
        try
        {
            var parameters = request["params"] as JsonObject ?? new JsonObject();
            if (request["params"] is not null and not JsonObject) return RpcError(id, -32602, "Params must be an object");
            var meta = parameters["_meta"] as JsonObject;
            var requestedVersion = String(meta?["io.modelcontextprotocol/protocolVersion"]);
            var modern = requestedVersion is not null;
            if (modern)
            {
                if (requestedVersion != ModernVersion)
                    return RpcError(id, -32022, "Unsupported protocol version", new { supported = SupportedVersions(), requested = requestedVersion });
                if (meta?["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
                    return RpcError(id, -32602, "Missing clientCapabilities metadata");
            }
            if (method == "initialize" && !modern)
            {
                var version = String(parameters["protocolVersion"]);
                if (version is null || parameters["capabilities"] is not JsonObject || parameters["clientInfo"] is not JsonObject)
                    return RpcError(id, -32602, "Invalid initialize parameters");
                _legacyVersion = LegacyVersions.Contains(version, StringComparer.Ordinal) ? version : LegacyVersions[0];
                return RpcResult(id, Node(new { protocolVersion = _legacyVersion, capabilities = new { tools = new { listChanged = false } }, serverInfo = ServerInfo(), instructions = Instructions }), false);
            }
            if (method == "ping") return RpcResult(id, new JsonObject(), modern);
            if (!modern && _legacyVersion is null) return RpcError(id, -32600, "Initialize first, or provide modern request metadata");
            JsonObject result;
            switch (method)
            {
                case "server/discover" when modern:
                    result = Node(new { supportedVersions = SupportedVersions(), capabilities = new { tools = new { listChanged = false } }, instructions = Instructions });
                    break;
                case "tools/list":
                    if (parameters["cursor"] is not null) return RpcError(id, -32602, "Pagination cursor is not supported");
                    result = Node(new { tools = Tools() });
                    break;
                case "tools/call":
                    result = await CallToolAsync(parameters, requestLifetime.Token).ConfigureAwait(false);
                    break;
                default: return RpcError(id, -32601, "Method not found");
            }
            return RpcResult(id, result, modern);
        }
        catch (ToolParameterException error) { return RpcError(id, -32602, error.Message); }
        catch (OperationCanceledException) { return RpcError(id, -32800, "Request cancelled; an already submitted game command is not undone"); }
        catch (Exception error) { return RpcError(id, -32603, "Internal error", new { message = error.Message }); }
        finally { _pending.TryRemove(key, out _); }
    }

    private async Task<JsonObject> CallToolAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var name = String(parameters["name"]) ?? throw new ToolParameterException("Tool name is required");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        if (parameters["arguments"] is not null and not JsonObject) throw new ToolParameterException("Tool arguments must be an object");
        string? operationSession = null;
        var mutation = name is "input" or "load_save" or "end_turn" or "use_skill" or "set_feature" or "set_value";
        if (mutation) await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (name)
            {
                case "load_save":
                case "end_turn":
                case "use_skill":
                    Only(arguments, name == "load_save" ? ["session", "generation", "slot", "wait_ms"] : name == "use_skill" ? ["session", "generation", "party_index", "skill_index", "wait_ms"] : ["session", "generation", "wait_ms"]);
                    operationSession = RequiredSession(arguments);
                    var action = name == "load_save" ? 2 : name == "end_turn" ? 3 : 4;
                    var arg1 = name == "load_save" ? BoundedInteger(arguments, "slot", 0, 9998) : name == "use_skill" ? BoundedInteger(arguments, "party_index", 0, 6) : 0;
                    var arg2 = name == "use_skill" ? BoundedInteger(arguments, "skill_index", 0, 1) : 0;
                    var actionWait = arguments.ContainsKey("wait_ms") ? BoundedInteger(arguments, "wait_ms", 0, 5000) : 0;
                    var actionCommand = await bridge.ActionForSessionAsync(operationSession, action, arg1, arg2, PositiveInteger(arguments, "generation", true), cancellationToken).ConfigureAwait(false);
                    return actionWait == 0 || actionCommand.IsTerminal ? ToolResult(CommandEnvelope(actionCommand, operationSession)) : await WaitForCommandAsync(actionCommand, operationSession, actionWait, cancellationToken).ConfigureAwait(false);
                case "input":
                    Only(arguments, "session", "generation", "action", "key", "x", "y", "duration_ms");
                    operationSession = RequiredSession(arguments);
                    var inputRequest = new GameInputRequest(String(arguments["action"]) ?? "", String(arguments["key"]),
                        arguments.ContainsKey("x") ? BoundedInteger(arguments, "x", 0, 1919) : 0,
                        arguments.ContainsKey("y") ? BoundedInteger(arguments, "y", 0, 1079) : 0,
                        arguments.ContainsKey("duration_ms") ? BoundedInteger(arguments, "duration_ms", 30, 2000) : 100);
                    GameInput.Validate(inputRequest);
                    if (inputRequest.Action != "key" && (!arguments.ContainsKey("x") || !arguments.ContainsKey("y")))
                        throw new ToolParameterException("Mouse input requires x and y in game-canvas coordinates.");
                    if (inputRequest.Action == "key" && (arguments.ContainsKey("x") || arguments.ContainsKey("y")))
                        throw new ToolParameterException("Keyboard input must omit mouse coordinates.");
                    var inputState = await bridge.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    if (inputState.Session != operationSession) throw new BridgeException("session_changed", "Read the current game session before input.");
                    if (inputState.LoadGeneration != PositiveInteger(arguments, "generation", true)) throw new BridgeException("generation_mismatch", "Read state after loading before input.");
                    if (inputState.Phase != 1 || inputState.GameThreadResponsive != true) throw new BridgeException("game_not_ready", "Input requires a responsive game outside loading.");
                    if (inputState.LastCommandState == "queued") throw new BridgeException("command_pending", "Observe the pending semantic command before sending input.");
                    if (bridge.IsSimulation && gameInput is null) throw new BridgeException("simulation_input_denied", "A simulated bridge cannot send real Windows input.");
                    await _input.ExecuteAsync(inputState, inputRequest, cancellationToken).ConfigureAwait(false);
                    return ToolResult(Envelope(true, "input_sent", session: operationSession, source: "windows_input"));
                case "get_state":
                    Only(arguments);
                    var state = await bridge.GetStateAsync(cancellationToken).ConfigureAwait(false);
                    return ToolResult(Envelope(true, "observed", state: state));
                case "get_command_result":
                    Only(arguments, "id", "session");
                    operationSession = RequiredSession(arguments);
                    var commandId = PositiveInteger(arguments, "id");
                    try
                    {
                        var command = await bridge.GetCommandResultForSessionAsync(operationSession, commandId, cancellationToken).ConfigureAwait(false);
                        return ToolResult(CommandEnvelope(command, operationSession));
                    }
                    catch (BridgeException error)
                    {
                        return ToolResult(Envelope(false, "unknown", command: new BridgeCommand(commandId, "unknown"), outcomeUnknown: true,
                            error: new { code = error.Code, message = error.Message }, live: false, session: operationSession));
                    }
                case "set_value":
                    Only(arguments,"feature","value","generation","wait_ms","session");
                    operationSession=RequiredSession(arguments);
                    string dataFeature=String(arguments["feature"]) ?? "";
                    int dataValue=BoundedInteger(arguments,"value",0,999);
                    if (!BridgeClient.ValidDataValue(dataFeature,dataValue)) throw new ToolParameterException("Unsupported feature or value");
                    int dataWait=arguments.ContainsKey("wait_ms") ? BoundedInteger(arguments,"wait_ms",0,5000) : 0;
                    var dataCommand=await bridge.SetValueForSessionAsync(operationSession,dataFeature,dataValue,PositiveInteger(arguments,"generation",true),cancellationToken).ConfigureAwait(false);
                    return dataWait==0 || dataCommand.IsTerminal ? ToolResult(CommandEnvelope(dataCommand,operationSession)) : await WaitForCommandAsync(dataCommand,operationSession,dataWait,cancellationToken).ConfigureAwait(false);
                case "set_feature":
                    Only(arguments, "feature", "enabled", "generation", "wait_ms", "session");
                    operationSession = RequiredSession(arguments);
                    if (String(arguments["feature"]) is not ("hp_guard" or "no_duplicate_cards" or "food_redraw" or "auto_redistribute" or "resources_full" or "extra_treasure_pick")) throw new ToolParameterException("Unsupported feature");
                    if (arguments["enabled"] is not JsonValue enabledNode || !enabledNode.TryGetValue<bool>(out var enabled))
                        throw new ToolParameterException("enabled must be boolean");
                    var generation = PositiveInteger(arguments, "generation", allowZero: true);
                    var waitMs = arguments.ContainsKey("wait_ms") ? PositiveInteger(arguments, "wait_ms", allowZero: true) : 0;
                    if (waitMs > 5000) throw new ToolParameterException("wait_ms must be between 0 and 5000");
                    var accepted = await bridge.SetFeatureForSessionAsync(operationSession, String(arguments["feature"])!, enabled, generation, cancellationToken).ConfigureAwait(false);
                    if (waitMs == 0 || accepted.IsTerminal) return ToolResult(CommandEnvelope(accepted, operationSession));
                    return await WaitForCommandAsync(accepted, operationSession, (int)waitMs, cancellationToken).ConfigureAwait(false);
                default: throw new ToolParameterException($"Unknown tool: {name}");
            }
        }
        catch (BridgeException error)
        {
            return ToolResult(Envelope(false, error.OutcomeUnknown ? "unknown" : "error", error: new { code = error.Code, message = error.Message },
                outcomeUnknown: error.OutcomeUnknown, live: false, session: operationSession));
        }
        finally { if (mutation) _mutationGate.Release(); }
    }

    private async Task<JsonObject> WaitForCommandAsync(BridgeCommand command, string session, int waitMs, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(waitMs);
        try
        {
            while (!command.IsTerminal)
            {
                await Task.Delay(50, wait.Token).ConfigureAwait(false);
                command = await bridge.GetCommandResultForSessionAsync(session, command.Id, wait.Token).ConfigureAwait(false);
            }
            return ToolResult(CommandEnvelope(command, session));
        }
        catch (Exception error) when (error is BridgeException or OperationCanceledException)
        {
            if (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                return ToolResult(Envelope(true, "queued", command: command, outcomeUnknown: true,
                    error: new { code = "wait_expired", message = "The command was accepted; its final result has not been observed. Query the same command ID and session." }, session: session));
            var code = error is BridgeException bridgeError ? bridgeError.Code : "cancelled";
            return ToolResult(Envelope(false, "unknown", command: command, outcomeUnknown: true,
                error: new { code, message = error.Message }, live: false, session: session));
        }
    }

    private JsonObject CommandEnvelope(BridgeCommand command, string session) => Envelope(command.State != "rejected", command.State, command: command,
        outcomeUnknown: !command.IsTerminal, error: command.Error is null ? null : new { code = "command_rejected", message = command.Error }, session: session);

    private JsonObject Envelope(bool ok, string outcome, BridgeState? state = null, BridgeCommand? command = null,
        object? error = null, bool outcomeUnknown = false, bool live = true, string? session = null, string? source = null) => Node(new
        {
            ok, source = bridge.IsSimulation ? "simulation" : source ?? "native_pipe", isSimulation = bridge.IsSimulation,
            live = live && !bridge.IsSimulation, processId = state?.Pid ?? SessionProcessId(session) ?? bridge.ProcessId,
            session = state?.Session ?? session, observedAtUtc = DateTimeOffset.UtcNow,
            outcome, outcomeUnknown, effectVerification = "not_verified", state, command, error
        });

    private static JsonObject ToolResult(JsonObject payload) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() }),
        ["structuredContent"] = payload,
        ["isError"] = payload["ok"]?.GetValue<bool>() != true
    };

    private static object[] Tools() =>
    [
        new { name = "load_save", description = "Load an existing ordinary save file index (LocalSaveNN.asd, 0..9998) through the game's ResetLoadManager on the game thread. Replaces current unsaved progress. Requires controlsAvailable. Applied means the load was invoked; observe loadGeneration, lastLoadOutcome and lastCompletedGeneration to confirm completion. Never automatically retry.",
            inputSchema = Schema(new { session = new { type = "string" }, generation = new { type = "integer", minimum = 0 }, slot = new { type = "integer", minimum = 0, maximum = 9998 }, wait_ms = new { type = "integer", minimum = 0, maximum = 5000 } }, "session", "generation", "slot"), outputSchema = OutputSchema(),
            annotations = new { readOnlyHint = false, destructiveHint = true, idempotentHint = false, openWorldHint = false } },
        new { name = "end_turn", description = "Invoke the normal battle turn-end handler on the game thread. Requires controlsAvailable and battleReady; rejects blocked/changed contexts. Applied means the handler was invoked, not that all enemy animations finished.",
            inputSchema = Schema(new { session = new { type = "string" }, generation = new { type = "integer", minimum = 0 }, wait_ms = new { type = "integer", minimum = 0, maximum = 5000 } }, "session", "generation"), outputSchema = OutputSchema(),
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = false, openWorldHint = false } },
        new { name = "use_skill", description = "Use a battle leader's skill through the normal OnClickSkill handler. Internal party_index 0..6; screen slots left to right map to 5,3,1,0,2,4,6. skill_index 0/1 is upper/lower skill. Uses the game's IsUsable checks (AP, status and limits). Requires controlsAvailable and battleReady. Applied means scheduled, not independently verified damage.",
            inputSchema = Schema(new { session = new { type = "string" }, generation = new { type = "integer", minimum = 0 }, party_index = new { type = "integer", minimum = 0, maximum = 6 }, skill_index = new { type = "integer", minimum = 0, maximum = 1 }, wait_ms = new { type = "integer", minimum = 0, maximum = 5000 } }, "session", "generation", "party_index", "skill_index"), outputSchema = OutputSchema(),
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = false, openWorldHint = false } },
        new { name = "input", description = "Send bounded keyboard or mouse input only to the selected game window. Coordinates are 1920x1080 game-canvas units. key/mouse buttons are held for duration_ms then released, including cancellation. Focus changes abort; input_sent does not prove the intended game effect. Never replay an unknown outcome automatically.",
            inputSchema = Schema(new { session = new { type = "string" }, generation = new { type = "integer", minimum = 0 }, action = new { type = "string", @enum = new[] { "key", "mouse_move", "mouse_left", "mouse_right" } }, key = new { type = "string" }, x = new { type = "integer", minimum = 0, maximum = 1919 }, y = new { type = "integer", minimum = 0, maximum = 1079 }, duration_ms = new { type = "integer", minimum = 30, maximum = 2000, @default = 100 } }, "session", "generation", "action"), outputSchema = OutputSchema(),
            annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = false, openWorldHint = false } },
        new { name = "get_state", description = "Read the selected R10 native bridge snapshot. Missing HP is null; readiness and live transport do not prove gameplay effect.",
            inputSchema = Schema(new { }), outputSchema = OutputSchema(), annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false } },
        new { name = "set_value", description = "Set a finite typed game value on the game thread. Requires dataAvailable and dataReady. food/medal/friendship: 0..3 (disable resources_full first); unit: 0..999; star: 10/40/80/200, changes all created characters and clears experience, can reduce levels. Star uses the game's maximum and next-experience calculation. Read state first; never retry an unknown result automatically.",
            inputSchema = Schema(new { session=new { type="string" }, feature=new { type="string", @enum=new[] { "food","medal","friendship","unit","star" } }, value=new { type="integer",minimum=0,maximum=999 }, generation=new { type="integer",minimum=0 }, wait_ms=new { type="integer",minimum=0,maximum=5000 } },"session","feature","value","generation"), outputSchema=OutputSchema(),
            annotations=new { readOnlyHint=false,destructiveHint=true,idempotentHint=false,openWorldHint=false } },
        new { name = "set_feature", description = "Queue a component feature in the supplied process session and load generation; requires its corresponding availability capability in get_state. Card changes apply to the next reward batch. queued is not applied. wait_ms polls the same ID and session, never resubmits.",
            inputSchema = Schema(new { session = new { type = "string", minLength = 1 }, feature = new { type = "string", @enum = new[] { "hp_guard", "no_duplicate_cards", "food_redraw", "auto_redistribute", "resources_full", "extra_treasure_pick" } }, enabled = new { type = "boolean" }, generation = new { type = "integer", minimum = 0 }, wait_ms = new { type = "integer", minimum = 0, maximum = 5000, @default = 0 } }, "session", "feature", "enabled", "generation"),
            outputSchema = OutputSchema(), annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = false, openWorldHint = false } },
        new { name = "get_command_result", description = "Read one of the native bridge's last 16 command results. command_not_found does not mean the command never ran.",
            inputSchema = Schema(new { session = new { type = "string", minLength = 1 }, id = new { type = "integer", minimum = 1 } }, "session", "id"), outputSchema = OutputSchema(),
            annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false } }
    ];

    private static object Schema(object properties, params string[] required) => new { type = "object", properties, required, additionalProperties = false };
    private static object OutputSchema() => new { type = "object", properties = new { ok = new { type = "boolean" }, live = new { type = "boolean" }, isSimulation = new { type = "boolean" }, outcome = new { type = "string" }, outcomeUnknown = new { type = "boolean" } }, required = new[] { "ok", "live", "isSimulation", "outcome", "outcomeUnknown" } };
    private static string[] SupportedVersions() => [ModernVersion, .. LegacyVersions];
    private static object ServerInfo() => new { name = "r10-bridge", version = "0.2.0" };
    private const string Instructions = "Controls only the explicitly selected R10 game. Read get_state before mutations and use its session and loadGeneration. input sends bounded foreground keys/mouse; semantic load_save/end_turn/use_skill require controlsAvailable, battle actions additionally require battleReady. Query commands using their original session even after restart. Applied means the game-thread handler ran, not that gameplay effects or animations completed. Never automatically retry a mutation after a lost reply. No raw memory, script evaluation, or simulator fallback is available.";
    private static int BoundedInteger(JsonObject arguments, string name, int min, int max)
    {
        var value = PositiveInteger(arguments, name, min == 0);
        if (value < min || value > max) throw new ToolParameterException($"{name} must be between {min} and {max}");
        return (int)value;
    }
    private static JsonObject Node(object value) => JsonSerializer.SerializeToNode(value, JsonOptions)!.AsObject();
    private static JsonObject RpcResult(JsonNode? id, JsonObject result, bool modern)
    {
        if (modern)
        {
            result["resultType"] = "complete";
            result["_meta"] = new JsonObject { ["io.modelcontextprotocol/serverInfo"] = JsonSerializer.SerializeToNode(ServerInfo(), JsonOptions) };
        }
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    }
    private static JsonObject RpcError(JsonNode? id, int code, string message, object? data = null) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = Node(new { code, message, data })
    };
    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string RequiredSession(JsonObject arguments) => String(arguments["session"]) is { Length: > 0 } session
        ? session : throw new ToolParameterException("session must be a nonempty string from get_state");
    private static int? SessionProcessId(string? session) => session is not null && int.TryParse(session.Split(':')[0], out var pid) && pid > 0 ? pid : null;
    private static long PositiveInteger(JsonObject arguments, string name, bool allowZero = false) =>
        arguments[name] is JsonValue value && value.TryGetValue<long>(out var number) && number >= (allowZero ? 0 : 1)
            ? number : throw new ToolParameterException($"{name} must be a {(allowZero ? "nonnegative" : "positive")} integer");
    private static void Only(JsonObject arguments, params string[] names)
    {
        foreach (var argument in arguments) if (!names.Contains(argument.Key, StringComparer.Ordinal))
            throw new ToolParameterException($"Unknown argument: {argument.Key}");
    }
    private sealed class ToolParameterException(string message) : Exception(message);
}
