using R10.Bridge.Client;
using R10.Mcp;

var pidText = Environment.GetEnvironmentVariable("R10_BRIDGE_PID");
var gameDirectory = Environment.GetEnvironmentVariable("R10_BRIDGE_GAME_DIR");
if (args.Length == 2 && args[0] == "--pid") { pidText = args[1]; gameDirectory = null; }
else if (args.Length == 2 && args[0] == "--game-dir") { gameDirectory = args[1]; pidText = null; }
else if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: R10.Mcp --pid <game-process-id> | --game-dir <absolute-directory> (or R10_BRIDGE_PID / R10_BRIDGE_GAME_DIR).");
    return 2;
}
var hasPid = int.TryParse(pidText, out var pid) && pid > 0;
if (!hasPid && string.IsNullOrWhiteSpace(gameDirectory))
{
    Console.Error.WriteLine("An explicit positive game PID or absolute game directory is required.");
    return 2;
}
try
{
    IBridgeClient client = hasPid ? new BridgeClient(pid) : new DiscoveringBridgeClient(gameDirectory!);
    var server = new McpServer(client);
    await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"R10 MCP stopped: {error.Message}");
    return 1;
}
