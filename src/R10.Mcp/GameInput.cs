using System.Diagnostics;
using System.Runtime.InteropServices;
using R10.Bridge.Client;

namespace R10.Mcp;

public sealed record GameInputRequest(string Action, string? Key = null, int X = 0, int Y = 0, int DurationMs = 100);
public interface IGameInput
{
    Task ExecuteAsync(BridgeState state, GameInputRequest request, CancellationToken cancellationToken);
}

/// <summary>Bounded foreground input for the explicitly selected game process only.</summary>
public sealed class GameInput : IGameInput
{
    private readonly SemaphoreSlim _gate = new(1);
    public static ushort KeyCode(string key) => key.ToLowerInvariant() switch
    {
        "enter" => 13, "escape" => 27, "space" => 32, "tab" => 9,
        "left" => 37, "up" => 38, "right" => 39, "down" => 40,
        "shift" => 16, "control" => 17,
        { Length: 1 } s when char.IsAsciiLetterOrDigit(s[0]) => char.ToUpperInvariant(s[0]),
        _ => throw new BridgeException("invalid_input", "Unsupported game key.")
    };
    public static void Validate(GameInputRequest request)
    {
        if (request.Action is not ("key" or "mouse_move" or "mouse_left" or "mouse_right"))
            throw new BridgeException("invalid_input", "Unknown input action.");
        if (request.DurationMs is < 30 or > 2000) throw new BridgeException("invalid_input", "duration_ms must be 30..2000; held input is always released at the end.");
        if (request.Action == "key") _ = KeyCode(request.Key ?? "");
        else if (request.X is < 0 or >= 1920 || request.Y is < 0 or >= 1080 || request.Key is not null)
            throw new BridgeException("invalid_input", "Mouse coordinates use the 1920x1080 game canvas; key must be omitted.");
    }
    public async Task ExecuteAsync(BridgeState state, GameInputRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        if (!OperatingSystem.IsWindows()) throw new BridgeException("unsupported_platform", "Game input requires Windows.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        NativeInput? release = null;
        try
        {
            using var process = Process.GetProcessById(state.Pid);
            if ($"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}" != state.Session)
                throw new BridgeException("session_changed", "Game process changed before input.");
            var window = process.MainWindowHandle;
            if (window == 0 || !IsWindowVisible(window) || IsIconic(window))
                throw new BridgeException("game_window_unavailable", "Restore the selected game window before input.");
            if (GetForegroundWindow() != window && !SetForegroundWindow(window))
                throw new BridgeException("game_not_foreground", "The selected game could not be focused.");
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            EnsureForeground(window, state.Pid);
            if (request.Action != "key")
            {
                // DPI awareness is thread-scoped; no await while the context is changed.
                var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
                try
                {
                    if (!GetClientRect(window, out var rect)) throw new BridgeException("input_geometry_failed", "Cannot inspect game client area.");
                    var point = new Point { X = request.X * (rect.Right - rect.Left) / 1920, Y = request.Y * (rect.Bottom - rect.Top) / 1080 };
                    if (!ClientToScreen(window, ref point)) throw new BridgeException("input_geometry_failed", "Cannot map game coordinates.");
                    if (GetAncestor(WindowFromPoint(point), 2) != window) throw new BridgeException("game_occluded", "The click position is covered by another window.");
                    EnsureForeground(window, state.Pid);
                    if (!SetCursorPos(point.X, point.Y)) throw new BridgeException("input_failed", "Mouse positioning failed.");
                }
                finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
                if (request.Action == "mouse_move") return;
            }
            EnsureForeground(window, state.Pid);
            var down = request.Action == "key" ? Keyboard(KeyCode(request.Key!), false) : Mouse(request.Action == "mouse_left" ? 2u : 8u);
            release = request.Action == "key" ? Keyboard(KeyCode(request.Key!), true) : Mouse(request.Action == "mouse_left" ? 4u : 16u);
            Send(down);
            for (int elapsed = 0; elapsed < request.DurationMs; elapsed += 20)
            {
                await Task.Delay(Math.Min(20, request.DurationMs - elapsed), cancellationToken).ConfigureAwait(false);
                EnsureForeground(window, state.Pid);
            }
        }
        catch (OperationCanceledException error) when (release.HasValue)
        { throw new BridgeException("input_cancelled", "Input may already have reached the game; do not replay automatically.", true, error); }
        catch (BridgeException error) when (release.HasValue && !error.OutcomeUnknown)
        { throw new BridgeException(error.Code, error.Message, true, error); }
        finally
        {
            try { if (release.HasValue) Send(release.Value); }
            finally { _gate.Release(); }
        }
    }
    private static void EnsureForeground(IntPtr window, int pid)
    {
        GetWindowThreadProcessId(window, out var actual);
        if (actual != pid || GetForegroundWindow() != window)
            throw new BridgeException("game_focus_changed", "Input stopped because the selected game lost focus.");
    }
    private static NativeInput Keyboard(ushort key, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = (up ? 2u : 0u) | (key is >= 37 and <= 40 ? 1u : 0u) } } };
    private static NativeInput Mouse(uint flags) => new() { Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
    private static void Send(NativeInput input)
    { if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1) throw new BridgeException("input_failed", "Windows did not confirm input delivery; do not replay automatically.", true); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
