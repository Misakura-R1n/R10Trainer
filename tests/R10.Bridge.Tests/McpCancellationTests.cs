using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using R10.Bridge.Client;
using R10.Mcp;

static class McpCancellationTests
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("MCP late cancellation keeps the stdio session serving later pings", async () =>
        {
            var server = new McpServer(new BridgeClient(42, new WaitingTransport()));
            CaptureDisposedLifetime(server, "7");
            var replies = await RunLines(server, """
                {"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}
                {"jsonrpc":"2.0","id":8,"method":"ping"}
                """);
            Check(replies.Length == 1 && replies[0]["id"]?.GetValue<int>() == 8 && replies[0]["result"] is JsonObject);
        });
        await test("MCP shutdown tolerates a request disposed after the pending snapshot", async () =>
        {
            var server = new McpServer(new BridgeClient(42, new WaitingTransport()));
            CaptureDisposedLifetime(server, "7");
            var replies = await RunLines(server, """
                {"jsonrpc":"2.0","id":8,"method":"ping"}
                """);
            Check(replies.Length == 1 && replies[0]["id"]?.GetValue<int>() == 8 && replies[0]["result"] is JsonObject);
        });
        await test("MCP cancellation still stops an active request and serves a later ping", async () =>
        {
            var transport = new WaitingTransport();
            var server = new McpServer(new BridgeClient(42, transport));
            var replies = await RunLines(server, """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{}}}
                {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_state","arguments":{}}}
                {"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}
                {"jsonrpc":"2.0","id":3,"method":"ping"}
                """);
            Check(transport.Started && transport.Cancelled && replies.Length == 3);
            Check(replies.Single(reply => reply["id"]?.GetValue<int>() == 2)["error"]?["code"]?.GetValue<int>() == -32800);
            Check(replies.Single(reply => reply["id"]?.GetValue<int>() == 3)["result"] is JsonObject);
        });
    }

    private static void CaptureDisposedLifetime(McpServer server, string id)
    {
        // Reproduce the race's endpoint without timing: the notifier/shutdown
        // already captured a lifetime, then its owner disposed it before CancelAsync.
        // Keeping that reference in the fixture map exercises the real RunAsync paths
        // without adding a test hook to production or relying on a probabilistic race.
        var field = typeof(McpServer).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MCP pending lifetime registry was not found.");
        var pending = (ConcurrentDictionary<string, CancellationTokenSource>)field.GetValue(server)!;
        var lifetime = new CancellationTokenSource();
        lifetime.Dispose();
        Check(pending.TryAdd(id, lifetime));
    }

    private static async Task<JsonObject[]> RunLines(McpServer server, string lines)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(lines + "\n"));
        using var output = new MemoryStream();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await server.RunAsync(input, output, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("MCP cancellation assertion failed.");
    }

    private sealed class WaitingTransport : IBridgeTransport
    {
        public bool IsSimulation => true;
        public string Session => "42:fixture";
        public bool Started { get; private set; }
        public bool Cancelled { get; private set; }

        public async Task<JsonElement> ExchangeAsync(JsonElement request, bool mutation, CancellationToken cancellationToken)
        {
            Check(!mutation && request.GetProperty("op").GetString() == "get_state");
            Started = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("The request should only finish through cancellation.");
        }
    }
}
