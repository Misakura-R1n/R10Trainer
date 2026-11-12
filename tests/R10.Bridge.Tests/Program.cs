using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using R10.Bridge.Client;
using R10.Mcp;

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error}"); }
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
async Task<BridgeException> Error(Func<Task> action)
{
    try { await action(); } catch (BridgeException error) { return error; }
    throw new InvalidOperationException("Expected BridgeException");
}
string Ok(object payload)
{
    var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
    node["ok"] = true; node["protocol"] = 1; node["build"] = "r10-bridge-v1";
    return node.ToJsonString();
}
string State() => Ok(new { pid = 42, gameThreadId = 9, heartbeat = 15, frame = 1, loadGeneration = 3, phase = 1,
    hpGuard = false, lastCommandId = 0, lastCommandState = "none", playerHp = (long?)null, playerHpMax = (long?)null, enemyHp = (long?)null });
JsonObject Request(string method, object? parameters = null, int id = 1) => new()
{
    ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(parameters ?? new { })
};
async Task Initialize(McpServer server) => _ = await server.HandleLineAsync(Request("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }).ToJsonString());
async Task<JsonObject> Call(McpServer server, string name, object? args = null) =>
    (await server.HandleLineAsync(Request("tools/call", new { name, arguments = args ?? new { } }, 2).ToJsonString()))!;
JsonObject Content(JsonObject result) => result["result"]!["structuredContent"]!.AsObject();

await Test("state preserves unknown HP and simulation provenance", async () =>
{
    var transport = new FixtureTransport(_ => State());
    var client = new BridgeClient(42, transport);
    var state = await client.GetStateAsync();
    Check(state.PlayerHp is null && state.EnemyHp is null && state.LoadGeneration == 3 && state.PhaseName == "ready" && client.IsSimulation);
    Check(transport.Requests.Single().GetProperty("op").GetString() == "get_state");
});
await Test("set_feature sends exact generation and only queues", async () =>
{
    var transport = new FixtureTransport(_ => Ok(new { id = 7, state = "queued" }));
    var client = new BridgeClient(42, transport);
    foreach (string feature in new[] { "hp_guard", "extra_treasure_pick" })
    {
        var command = await client.SetFeatureAsync(feature, true, 3);
        Check(command.Id == 7 && !command.IsTerminal);
        Check(transport.Requests.Last().GetRawText() == JsonSerializer.Serialize(new { op = "set_feature", feature, enabled = true, generation = 3 }));
    }
});
await Test("typed values validate bounds and bind exact session and generation", async () =>
{
    var transport=new FixtureTransport(_=>Ok(new { id=7,state="queued" }),"42:123");
    var client=new BridgeClient(42,transport);
    await client.SetValueForSessionAsync("42:123","star",200,3);
    Check(transport.Requests.Single().GetRawText()=="{\"op\":\"set_value\",\"feature\":\"star\",\"value\":200,\"generation\":3}");
    Check((await Error(()=>client.SetValueForSessionAsync("42:other","food",3,3))).Code=="session_changed");
    foreach(var (feature,value) in new[] {("food",4),("medal",-1),("friendship",4),("unit",1000),("star",99),("unknown",1)})
        Check((await Error(()=>client.SetValueForSessionAsync("42:123",feature,value,3))).Code=="invalid_value");
    Check(transport.Requests.Count==1);
});
await Test("battle HP carries the observed context, side and generation", async () =>
{
    var transport = new FixtureTransport(_ => Ok(new { id = 8, state = "queued" }), "42:123");
    var client = new BridgeClient(42, transport);
    await client.SetBattleHpForSessionAsync("42:123", 3, 24, 1, 500);
    var sent = transport.Requests.Single();
    Check(sent.GetProperty("op").GetString() == "set_battle_hp" && sent.GetProperty("generation").GetInt32() == 3 &&
        sent.GetProperty("context").GetInt32() == 24 && sent.GetProperty("argument").GetInt32() == 1 && sent.GetProperty("value").GetInt32() == 500);
    Check((await Error(() => client.SetBattleHpForSessionAsync("42:123", 3, 24, 1, 0))).Code == "invalid_battle_hp");
    Check((await Error(() => client.SetBattleHpForSessionAsync("42:other", 3, 24, 0, 200))).Code == "session_changed");
    Check(transport.Requests.Count == 1);
});
await Test("unsupported feature never reaches transport", async () =>
{
    var transport = new FixtureTransport(_ => State());
    Check((await Error(() => new BridgeClient(42, transport).SetFeatureAsync("food", true, 3))).Code == "unsupported_feature");
    Check(transport.Requests.Count == 0);
});
await Test("card command carries session, generation, context and the exact card/character key", async () =>
{
    var transport = new FixtureTransport(_ => Ok(new { id = 7, state = "queued" }), "42:123");
    var client = new BridgeClient(42, transport);
    var request = new CardRequest(CardOperation.SetStar, 2, "4C76343220C0BCCBB9.C0BCCBB9", 47);
    await client.SubmitCardForSessionAsync("42:123", 3, 5, request);
    var sent = transport.Requests.Single();
    Check(sent.GetProperty("generation").GetInt32() == 3 && sent.GetProperty("context").GetInt32() == 5 &&
        sent.GetProperty("key").GetString() == request.Key && sent.GetProperty("argument2").GetInt32() == 47);
    Check((await Error(() => client.SubmitCardForSessionAsync("42:other", 3, 5, request))).Code == "session_changed");
    Check((await Error(() => client.SubmitCardForSessionAsync("42:123", 3, 5, request with { Star = -1 }))).Code == "invalid_card_request");
    Check(transport.Requests.Count == 1);
});
await Test("card readback decodes CP936 and rejects malformed or noncontiguous rows", async () =>
{
    object Row(int index, string idHex) => new { index, flags = 7, idHex, characterHex = "C0BCCBB9" };
    var client = new BridgeClient(42, new FixtureTransport(_ => Ok(new
    {
        id = 7, state = "applied", cardTotal = 1134, cardRows = new[] { Row(2,"4C76343220C0BCCBB9") },
        star = 47, exp = 0, nextExp = 4525, maxStar = 999
    })));
    var result = (await client.GetCommandResultAsync(7)).Cards!;
    Check(result.Rows.Single().Id == "Lv42 兰斯" && result.Rows.Single().CharacterId == "兰斯" &&
        result.Star == 47 && result.Rows[0].Owned);
    foreach (object[] rows in new[] { new[] { Row(0,"X") }, new[] { Row(0,"41"), Row(2,"42") } })
    {
        var malformed = new BridgeClient(42, new FixtureTransport(_ => Ok(new { id = 7, state = "applied", cardTotal = 3, cardRows = rows })));
        Check((await Error(() => malformed.GetCommandResultAsync(7))).Code == "invalid_response");
    }
});
await Test("compact ownership readback requires the exact bounded flag range", async () =>
{
    BridgeClient Client(string flags) => new(42, new FixtureTransport(_ =>
        Ok(new { id = 7, state = "applied", cardTotal = 99, cardStart = 96, cardOwned = flags })));
    var result = (await Client("010").GetCommandResultAsync(7)).Cards!;
    Check(result.Total == 99 && result.Rows.Count == 0 && result.Ownership == new CardOwnershipPage(96, "010"));
    foreach (string invalid in new[] { "", "01", "0101", "012" })
        Check((await Error(() => Client(invalid).GetCommandResultAsync(7))).Code == "invalid_response");
});
await Test("card readback decodes Japanese text and preserves the raw identity key", async () =>
{
    BridgeClient Client(int codePage, string idHex) => new(42, new FixtureTransport(_ => Ok(new
    {
        id = 7, state = "applied", cardCodePage = codePage, cardTotal = 1,
        cardRows = new[] { new { index = 0, flags = 7, idHex, characterHex = "838983938358" } }
    })));
    var row = (await Client(932, "4C76343220838983938358").GetCommandResultAsync(7)).Cards!.Rows.Single();
    Check(row.Id == "Lv42 ランス" && row.CharacterId == "ランス" && row.Key == "4C76343220838983938358.838983938358");
    Check((await Error(() => Client(65001, "41").GetCommandResultAsync(7))).Code == "invalid_response");
    Check((await Error(() => Client(932, "83").GetCommandResultAsync(7))).Code == "invalid_response");
});
await Test("state rejects wrong PID", async () => Check((await Error(() => new BridgeClient(43, new FixtureTransport(_ => State())).GetStateAsync())).Code == "response_pid_mismatch"));
await Test("state rejects malformed numeric fields without leaking parsing exceptions", async () =>
{
    var bad = JsonNode.Parse(State())!; bad["loadGeneration"] = "3";
    Check((await Error(() => new BridgeClient(42, new FixtureTransport(_ => bad.ToJsonString())).GetStateAsync())).Code == "invalid_response");
});
await Test("state carries heartbeat freshness and guard counters", async () =>
{
    var json = JsonNode.Parse(State())!; json["heartbeatAgeMs"] = 2001; json["blockedHits"] = 5; json["lastRequestedHp"] = 0; json["scriptVersion"] = 1; json["capabilities"] = 15;
    json["extraTreasurePick"] = true;
    var state = await new BridgeClient(42, new FixtureTransport(_ => json.ToJsonString())).GetStateAsync();
    Check(state.GameThreadResponsive == false && state.BlockedHits == 5 && state.LastRequestedHp == 0 && state.ScriptVersion == 1 && state.HpGuardAvailable && state.ExtraTreasurePick);
});
await Test("unadvertised HP hook remains unavailable", async () => Check(!(await new BridgeClient(42, new FixtureTransport(_ => State())).GetStateAsync()).HpGuardAvailable));
await Test("load failure evidence survives ready phase and negative quick-load slot", async () =>
{
    var json = JsonNode.Parse(State())!; json["loadKind"] = 2; json["loadSlot"] = -1; json["lastCompletedGeneration"] = 2; json["lastLoadOutcome"] = 2;
    var state = await new BridgeClient(42, new FixtureTransport(_ => json.ToJsonString())).GetStateAsync();
    Check(state.PhaseName == "ready" && state.LastLoadOutcome == 2 && state.LoadSlot == -1 && state.LastCompletedGeneration == 2);
    json["lastCompletedGeneration"] = -1;
    Check((await Error(() => new BridgeClient(42, new FixtureTransport(_ => json.ToJsonString())).GetStateAsync())).Code == "invalid_response");
});
await Test("discovery binds a process session and rejects recycled PID commands", async () =>
{
    var resolver = new FixtureResolver(new BridgeProcessIdentity(42, 123));
    var transports = new List<FixtureTransport>();
    var client = DiscoveringBridgeClient.CreateForTesting(resolver, identity =>
    {
        var transport = new FixtureTransport(request => request.GetProperty("op").GetString() == "get_state" ? State() : Ok(new { id = 1, state = "queued" }), identity.Session);
        transports.Add(transport); return new BridgeClient(identity.ProcessId, transport);
    });
    var state = await client.GetStateAsync(); Check(state.Session == "42:123" && client.IsSimulation);
    await client.SetFeatureForSessionAsync(state.Session!, "hp_guard", true, state.LoadGeneration);
    resolver.Identity = new BridgeProcessIdentity(42, 456);
    Check((await Error(() => client.SetFeatureForSessionAsync(state.Session!, "hp_guard", false, 3))).Code == "session_changed");
    Check((await Error(() => client.GetCommandResultForSessionAsync(state.Session!, 1))).Code == "session_changed");
    Check(transports.Count == 1 && transports[0].Requests.Count == 2);
    Check((await client.GetStateAsync()).Session == "42:456");
    Check((await Error(() => client.GetCommandResultForSessionAsync("42:123", 1))).Code == "session_changed");
    Check(transports[1].Requests.Count == 1);
});
await Test("discovery requires an explicit session and never falls back to old process", async () =>
{
    var resolver = new FixtureResolver(new BridgeProcessIdentity(42, 123));
    var transport = new FixtureTransport(_ => State());
    var client = DiscoveringBridgeClient.CreateForTesting(resolver, _ => new BridgeClient(42, transport));
    await client.GetStateAsync();
    Check((await Error(() => client.SetFeatureAsync("hp_guard", true, 3))).Code == "session_required");
    resolver.Error = new BridgeException("game_not_running", "fixture stopped");
    Check((await Error(() => client.SetFeatureForSessionAsync("42:123", "hp_guard", true, 3))).Code == "game_not_running");
    Check(transport.Requests.Count == 1);
});
await Test("successful response needs exact protocol/build", async () =>
{
    Check((await Error(() => new BridgeClient(42, new FixtureTransport(_ => "{\"ok\":true,\"protocol\":2,\"build\":\"other\"}")).PingAsync())).Code == "unsupported_bridge");
});
await Test("malformed mutation acknowledgement leaves outcome unknown", async () =>
{
    var transport = new FixtureTransport(_ => Ok(new { id = 0, state = "queued" }));
    Check((await Error(() => new BridgeClient(42, transport).SetFeatureAsync("hp_guard", true, 3))).OutcomeUnknown);
    Check(transport.Requests.Count == 1);
});
await Test("explicit mutation rejection is known", async () =>
{
    var error = await Error(() => new BridgeClient(42, new FixtureTransport(_ => "{\"ok\":false,\"error\":\"generation_mismatch\"}")).SetFeatureAsync("hp_guard", true, 3));
    Check(!error.OutcomeUnknown && error.Message == "generation_mismatch");
});
await Test("command result checks ID correlation", async () => Check((await Error(() => new BridgeClient(42, new FixtureTransport(_ => Ok(new { id = 8, state = "applied" }))).GetCommandResultAsync(7))).Code == "command_id_mismatch"));
await Test("framing accepts UTF8 and CRLF", async () =>
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"x\":\"测试\"}\r\n"));
    Check(await new BoundedJsonLine(stream, 100).ReadAsync() == "{\"x\":\"测试\"}");
});
await Test("framing preserves buffered lines and UTF8 split across blocks", async () =>
{
    string first = new string('x', 4095) + "测试";
    byte[] data = Encoding.UTF8.GetBytes(first + "\r\n{}\n\n");
    using var stream = new MemoryStream(data);
    var reader = new BoundedJsonLine(stream, 4102); // The first line reaches the exact byte limit, including CR.
    Check(await reader.ReadAsync() == first);
    Check(stream.Position == data.Length && await reader.ReadAsync() == "{}");
    Check(await reader.ReadAsync() == "" && await reader.ReadAsync() is null);
});
await Test("framing rejects overlimit and missing newline", async () =>
{
    foreach (var text in new[] { "12345\n", "1234\r\n", "1234" })
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        try { await new BoundedJsonLine(stream, 4).ReadAsync(); throw new Exception("Expected framing error"); }
        catch (InvalidDataException) { }
    }
});
await Test("framing rejects invalid UTF8", async () =>
{
    using var stream = new MemoryStream([0xFF, (byte)'\n']);
    try { await new BoundedJsonLine(stream, 16).ReadAsync(); throw new Exception("Expected a decoding error"); }
    catch (DecoderFallbackException) { }
});
await Test("MCP lists only implemented tools", async () =>
{
    var server = new McpServer(new BridgeClient(42, new FixtureTransport(_ => State()))); await Initialize(server);
    var result = await server.HandleLineAsync(Request("tools/list").ToJsonString());
    var names = result!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
    Check(names.SequenceEqual(new[] { "load_save", "end_turn", "use_skill", "input", "get_state", "set_value", "set_feature", "get_command_result" }));
});
await Test("MCP fixture cannot claim live and preserves structured result", async () =>
{
    var server = new McpServer(new BridgeClient(42, new FixtureTransport(_ => State()))); await Initialize(server);
    var result = await Call(server, "get_state"); var content = Content(result);
    Check(content["live"]!.GetValue<bool>() == false && content["isSimulation"]!.GetValue<bool>() && content["state"]!["playerHp"] is null);
    Check(JsonNode.DeepEquals(content, JsonNode.Parse(result["result"]!["content"]![0]!["text"]!.GetValue<string>())));
});
await Test("MCP rejects missing typed arguments and extra mutation params", async () =>
{
    var transport = new FixtureTransport(_ => State()); var server = new McpServer(new BridgeClient(42, transport)); await Initialize(server);
    Check((await Call(server, "set_value"))["error"]!["code"]!.GetValue<int>() == -32602);
    Check((await Call(server, "set_feature", new { session = "42:123", feature = "hp_guard", enabled = true, generation = 3, extra = 1 }))["error"]!["code"]!.GetValue<int>() == -32602);
    Check(transport.Requests.Count == 0);
});
await Test("MCP typed value uses one queued submission and polls same command", async () =>
{
    var transport=new FixtureTransport(request=>Ok(new { id=7,state=request.GetProperty("op").GetString()=="set_value" ? "queued" : "applied" }));
    var server=new McpServer(new BridgeClient(42,transport)); await Initialize(server);
    var result=Content(await Call(server,"set_value",new {session="42:123",feature="unit",value=80,generation=3,wait_ms=500}));
    Check(result["outcome"]!.GetValue<string>()=="applied" && transport.Requests.Count(x=>x.GetProperty("op").GetString()=="set_value")==1);
});
await Test("MCP mutation requires matching session", async () =>
{
    var transport = new FixtureTransport(_ => State()); var server = new McpServer(new BridgeClient(42, transport)); await Initialize(server);
    Check((await Call(server, "set_feature", new { feature = "hp_guard", enabled = true, generation = 3 }))["error"]!["code"]!.GetValue<int>() == -32602);
    var result = Content(await Call(server, "set_feature", new { session = "42:999", feature = "hp_guard", enabled = true, generation = 3 }));
    Check(!result["ok"]!.GetValue<bool>() && result["error"]!["code"]!.GetValue<string>() == "session_changed" && transport.Requests.Count == 0);
});
await Test("MCP queued is not applied and wait polls only", async () =>
{
    var transport = new FixtureTransport(request => Ok(new { id = 7, state = request.GetProperty("op").GetString() == "set_feature" ? "queued" : "applied" }));
    var server = new McpServer(new BridgeClient(42, transport)); await Initialize(server);
    var result = Content(await Call(server, "set_feature", new { session = "42:123", feature = "hp_guard", enabled = true, generation = 3, wait_ms = 500 }));
    Check(result["outcome"]!.GetValue<string>() == "applied" && !result["outcomeUnknown"]!.GetValue<bool>());
    Check(transport.Requests.Count(request => request.GetProperty("op").GetString() == "set_feature") == 1);
    Check(result["effectVerification"]!.GetValue<string>() == "not_verified");
});
await Test("MCP bounded wait retains command ID and unknown outcome", async () =>
{
    var transport = new FixtureTransport(_ => Ok(new { id = 7, state = "queued" }));
    var server = new McpServer(new BridgeClient(42, transport)); await Initialize(server);
    var result = Content(await Call(server, "set_feature", new { session = "42:123", feature = "hp_guard", enabled = true, generation = 3, wait_ms = 75 }));
    Check(result["outcome"]!.GetValue<string>() == "queued" && result["outcomeUnknown"]!.GetValue<bool>() && result["command"]!["id"]!.GetValue<long>() == 7);
    Check(transport.Requests.Count(request => request.GetProperty("op").GetString() == "set_feature") == 1);
});
await Test("MCP cancellation keeps submitted command identifiable and never resubmits", async () =>
{
    var transport = new FixtureTransport(_ => Ok(new { id = 7, state = "queued" }));
    var server = new McpServer(new BridgeClient(42, transport)); await Initialize(server);
    var pending = Call(server, "set_feature", new { session = "42:123", feature = "hp_guard", enabled = true, generation = 3, wait_ms = 5000 });
    await server.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2}}");
    var result = Content(await pending);
    Check(result["outcomeUnknown"]!.GetValue<bool>() && result["command"]!["id"]!.GetValue<long>() == 7);
    Check(transport.Requests.Count(request => request.GetProperty("op").GetString() == "set_feature") == 1);
});
await Test("MCP evicted result is unknown, not never executed", async () =>
{
    var server = new McpServer(new BridgeClient(42, new FixtureTransport(_ => "{\"ok\":false,\"error\":\"command_not_found\"}"))); await Initialize(server);
    var result = Content(await Call(server, "get_command_result", new { session = "42:123", id = 7 }));
    Check(result["outcomeUnknown"]!.GetValue<bool>() && result["outcome"]!.GetValue<string>() == "unknown");
});
await Test("modern MCP discovery and metadata validation", async () =>
{
    var server = new McpServer(new BridgeClient(42, new FixtureTransport(_ => State())));
    var request = Request("server/discover"); request["params"]!["_meta"] = new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28", ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject() };
    var response = await server.HandleLineAsync(request.ToJsonString());
    Check(response!["result"]!["resultType"]!.GetValue<string>() == "complete");
    request["params"]!["_meta"]!["io.modelcontextprotocol/protocolVersion"] = "2099-01-01";
    Check((await server.HandleLineAsync(request.ToJsonString()))!["error"]!["code"]!.GetValue<int>() == -32022);
});
await Test("MCP malformed input and notifications", async () =>
{
    var server = new McpServer(new BridgeClient(42, new FixtureTransport(_ => State())));
    Check((await server.HandleLineAsync("{"))!["error"]!["code"]!.GetValue<int>() == -32700);
    Check(await server.HandleLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") is null);
});
await Test("MCP stdio writes JSON lines only", async () =>
{
    var server = new McpServer(new BridgeClient(42, new FixtureTransport(_ => State())));
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(Request("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }).ToJsonString() + "\n" + Request("tools/list", id: 2).ToJsonString() + "\n"));
    using var output = new MemoryStream(); await server.RunAsync(input, output);
    var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Check(lines.Length == 2); foreach (var line in lines) Check(JsonNode.Parse(line)!["jsonrpc"]!.GetValue<string>() == "2.0");
});
await Test("MCP stdio rejects duplicate JSON keys and keeps serving requests", async () =>
{
    var server = new McpServer(new BridgeClient(42, new FixtureTransport(_ => State())));
    const string requests = """
        {"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}
        {"jsonrpc":"2.0","id":3,"method":"ping","params":{"nested":{"key":1,"key":2}}}
        {"jsonrpc":"2.0","id":4,"method":"ping"}
        """;
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests + "\n"));
    using var output = new MemoryStream();
    await server.RunAsync(input, output);
    var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    Check(replies.Length == 3);
    Check(replies.Count(reply => reply.ContainsKey("id") && reply["id"] is null &&
        reply["error"]?["code"]?.GetValue<int>() == -32700) == 2);
    Check(replies.Single(reply => reply["id"]?.GetValue<int>() == 4)["result"] is JsonObject);
});
if (OperatingSystem.IsWindows())
{
    await Test("pipe server PID mismatch prevents mutation send (fixture)", async () =>
    {
        var wrongPid = Environment.ProcessId == 2147483000 ? 2147483001 : 2147483000;
        using var server = new NamedPipeServerStream(BridgeProtocol.PipeName(wrongPid), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        var error = await Error(() => new BridgeClient(wrongPid).SetFeatureAsync("hp_guard", true, 3));
        await connected;
        Check(error.Code == "server_pid_mismatch" && !error.OutcomeUnknown);
        Check(await new BoundedJsonLine(server, 4096).ReadAsync() is null);
    });
    await Test("real named pipe transport validates PID and reads a multiblock response (fixture)", async () =>
    {
        using var server = new NamedPipeServerStream(BridgeProtocol.PipeName(Environment.ProcessId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var host = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            Check(await new BoundedJsonLine(server, 4096).ReadAsync() == "{\"op\":\"ping\"}");
            await BoundedJsonLine.WriteAsync(server, Ok(new { payload = new string('x', 32 * 1024) }), BridgeProtocol.MaximumLineBytes);
        });
        await new BridgeClient(Environment.ProcessId).PingAsync(); await host;
    });
    await Test("pipe timeout after mutation send is unknown and never retries (fixture)", async () =>
    {
        using var server = new NamedPipeServerStream(BridgeProtocol.PipeName(Environment.ProcessId), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Task.Run(async () => { await server.WaitForConnectionAsync(); await new BoundedJsonLine(server, 4096).ReadAsync(); received.SetResult(); });
        var client = new BridgeClient(Environment.ProcessId, TimeSpan.FromMilliseconds(200));
        var error = await Error(() => client.SetFeatureAsync("hp_guard", true, 3));
        await host; Check(received.Task.IsCompletedSuccessfully && error.OutcomeUnknown && error.Code == "timeout");
    });
}
await RuntimeTests.Run(Test);
await ConfirmationTests.Run(Test);
await ControlTests.Run(Test);
await UnifiedRuntimeTests.Run(Test);
Console.WriteLine($"{passed} passed, {failed} failed. All cases use protocol/pipe fixtures; no real game was accessed.");
return failed == 0 ? 0 : 1;

sealed class FixtureTransport(Func<JsonElement, string> response, string session = "42:123") : IBridgeTransport
{
    public bool IsSimulation => true;
    public string? Session => session;
    public List<JsonElement> Requests { get; } = [];
    public Task<JsonElement> ExchangeAsync(JsonElement request, bool mutation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.Clone());
        using var document = JsonDocument.Parse(response(request));
        return Task.FromResult(document.RootElement.Clone());
    }
}

sealed class FixtureResolver(BridgeProcessIdentity identity) : IGameProcessResolver
{
    public BridgeProcessIdentity Identity { get; set; } = identity;
    public BridgeException? Error { get; set; }
    public BridgeProcessIdentity Resolve() => Error is not null ? throw Error : Identity;
}
