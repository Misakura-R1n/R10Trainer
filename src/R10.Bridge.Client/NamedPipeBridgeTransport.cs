using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace R10.Bridge.Client;

public sealed class NamedPipeBridgeTransport : IBridgeTransport
{
    private readonly int _processId;
    private readonly TimeSpan _timeout;
    public bool IsSimulation => false;
    public string? Session { get; private set; }

    public NamedPipeBridgeTransport(int processId, TimeSpan? timeout = null, string? expectedSession = null)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        _processId = processId;
        Session = expectedSession;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<JsonElement> ExchangeAsync(JsonElement request, bool mutation, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new BridgeException("unsupported_platform", "The live bridge requires Windows.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var pipe = new NamedPipeClientStream(".", BridgeProtocol.PipeName(_processId), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        var submitted = false;
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualPid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (actualPid != (uint)_processId)
                throw new BridgeException("server_pid_mismatch", "The pipe server does not belong to the selected process.");
            using var process = Process.GetProcessById(_processId);
            var observedSession = new BridgeProcessIdentity(_processId, process.StartTime.ToUniversalTime().Ticks).Session;
            if (Session is not null && Session != observedSession)
                throw new BridgeException("session_changed", "The selected process has restarted; obtain a new state before issuing commands.");
            Session = observedSession;
            timeout.Token.ThrowIfCancellationRequested();
            submitted = true; // A partial send or a lost reply cannot safely be retried.
            await BoundedJsonLine.WriteAsync(pipe, request.GetRawText(), BridgeProtocol.MaximumLineBytes, timeout.Token).ConfigureAwait(false);
            using var buffered = new BufferedStream(pipe, 4096);
            var line = await BoundedJsonLine.ReadAsync(buffered, BridgeProtocol.MaximumLineBytes, timeout.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The bridge closed without a response.");
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.Clone();
        }
        catch (BridgeException) { throw; }
        catch (OperationCanceledException error)
        {
            throw new BridgeException(cancellationToken.IsCancellationRequested ? "cancelled" : "timeout",
                mutation && submitted ? "The command may have been queued; do not automatically resubmit it." : "The bridge request did not complete.",
                mutation && submitted, error);
        }
        catch (Exception error) when (error is IOException or JsonException or Win32Exception or UnauthorizedAccessException or System.Text.DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            throw new BridgeException("transport_error", error.Message, mutation && submitted, error);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
