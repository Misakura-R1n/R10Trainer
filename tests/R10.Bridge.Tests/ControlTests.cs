using System.Text.Json;
using System.Text.Json.Nodes;
using R10.Bridge.Client;
using R10.Mcp;

internal static class ControlTests
{
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Control assertion failed"); }
    private static string State(int generation = 3, int phase = 1, int age = 0) => JsonSerializer.Serialize(new
    {
        ok = true, protocol = 1, build = BridgeProtocol.Build, pid = 42, gameThreadId = 9, heartbeat = 10, frame = 1,
        loadGeneration = generation, phase, heartbeatAgeMs = age, hpGuard = false, lastCommandId = 0,
        lastCommandState = "none", capabilities = 31, battleReady = true
    });
    private static async Task<JsonObject> Call(McpServer server, string name, object args) =>
        (await server.HandleLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name, arguments = args } })))!;
    private static async Task<McpServer> Server(FixtureTransport transport, IGameInput? input = null)
    {
        var server = new McpServer(new BridgeClient(42, transport), input);
        await server.HandleLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"controls-test","version":"1"}}}""");
        return server;
    }
    private static JsonNode Payload(JsonObject response) => response["result"]!["structuredContent"]!;
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("semantic controls preserve session, arguments and a single submission", async () =>
        {
            var transport = new FixtureTransport(_ => """{"ok":true,"protocol":1,"build":"r10-bridge-v1","id":5,"state":"queued"}""");
            var server = await Server(transport);
            var r = Payload(await Call(server, "use_skill", new { session = "42:123", generation = 3, party_index = 1, skill_index = 0 }));
            Check(r["outcome"]!.GetValue<string>() == "queued" && r["outcomeUnknown"]!.GetValue<bool>());
            Check(transport.Requests.Single().GetRawText() == """{"op":"action","action":4,"argument":1,"argument2":0,"generation":3}""");
        });
        await test("semantic invalid bounds and stale sessions never submit", async () =>
        {
            var transport = new FixtureTransport(_ => State()); var server = await Server(transport);
            Check((await Call(server, "load_save", new { session = "42:123", generation = 3, slot = 9999 }))["error"]!["code"]!.GetValue<int>() == -32602);
            Check((await Call(server, "use_skill", new { session = "42:123", generation = 3, party_index = 7, skill_index = 0 }))["error"] is not null);
            Check(Payload(await Call(server, "end_turn", new { session = "42:other", generation = 3 }))["error"]!["code"]!.GetValue<string>() == "session_changed");
            Check(transport.Requests.Count == 0);
        });
        await test("malformed semantic acknowledgement stays unknown without resubmit", async () =>
        {
            var transport = new FixtureTransport(_ => """{"ok":true,"protocol":1,"build":"r10-bridge-v1","state":"applied"}""");
            var r = Payload(await Call(await Server(transport), "load_save", new { session = "42:123", generation = 3, slot = 5005 }));
            Check(r["outcomeUnknown"]!.GetValue<bool>() && transport.Requests.Count == 1);
        });
        await test("input accepts bounded hold and never claims verified effect", async () =>
        {
            var input = new InputFixture(); var server = await Server(new FixtureTransport(_ => State()), input);
            var r = Payload(await Call(server, "input", new { session = "42:123", generation = 3, action = "key", key = "enter", duration_ms = 250 }));
            Check(input.Requests.Single().DurationMs == 250 && r["outcome"]!.GetValue<string>() == "input_sent" && r["effectVerification"]!.GetValue<string>() == "not_verified");
        });
        await test("input session, generation, loading and heartbeat guards prevent injection", async () =>
        {
            foreach (var state in new[] { State(4), State(3,2), State(3,1,2001) })
            {
                var input = new InputFixture(); var server = await Server(new FixtureTransport(_ => state), input);
                Check(!Payload(await Call(server, "input", new { session = "42:123", generation = 3, action = "key", key = "enter" }))["ok"]!.GetValue<bool>());
                Check(input.Requests.Count == 0);
            }
        });
        await test("input rejects invalid key, missing coordinates and integer overflow", async () =>
        {
            var input = new InputFixture(); var server = await Server(new FixtureTransport(_ => State()), input);
            Check(!Payload(await Call(server,"input",new { session="42:123",generation=3,action="key",key="win" }))["ok"]!.GetValue<bool>());
            Check((await Call(server,"input",new { session="42:123",generation=3,action="mouse_left",x=20 }))["error"] is not null);
            Check((await Call(server,"input",new { session="42:123",generation=3,action="key",key="enter",duration_ms=long.MaxValue }))["error"]!["code"]!.GetValue<int>() == -32602);
            Check(input.Requests.Count == 0);
        });
        await test("simulation cannot activate real Windows input", async () =>
        {
            var server = await Server(new FixtureTransport(_ => State()));
            Check(Payload(await Call(server,"input",new { session="42:123",generation=3,action="key",key="enter" }))["error"]!["code"]!.GetValue<string>() == "simulation_input_denied");
        });
        await test("input interruption keeps unknown outcome and never retries", async () =>
        {
            var input = new InputFixture { Failure = new BridgeException("game_focus_changed", "Interrupted after down", true) };
            var server = await Server(new FixtureTransport(_ => State()),input);
            Check(Payload(await Call(server,"input",new { session="42:123",generation=3,action="key",key="enter" }))["outcomeUnknown"]!.GetValue<bool>());
            Check(input.Requests.Count == 1);
        });
    }
    private sealed class InputFixture : IGameInput
    {
        public List<GameInputRequest> Requests { get; } = [];
        public BridgeException? Failure { get; init; }
        public Task ExecuteAsync(BridgeState state, GameInputRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Failure is null ? Task.CompletedTask : Task.FromException(Failure); }
    }
}
