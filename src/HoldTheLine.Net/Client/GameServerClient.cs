using HoldTheLine.Net.Protocol;

namespace HoldTheLine.Net.Client;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// Thrown from <see cref="GameServerClient.ConnectAsync"/> when the server rejects the handshake with an
/// <see cref="ErrorMsg"/> instead of a hello_ok (version_mismatch / data_mismatch / client_outdated /
/// bad_identity / bad_resume). Carries the machine-readable <see cref="Code"/> so the caller can branch —
/// notably to raise the docs/15 forced-update prompt — rather than surfacing an opaque timeout.
/// </summary>
public sealed class HandshakeRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Client-side connection to the battle server: owns the receive loop, assigns request sequence
/// numbers, keeps the link alive with heartbeats, and — when <see cref="AutoReconnect"/> is set —
/// transparently re-establishes a dropped connection with exponential backoff, re-sending a
/// resume-token hello (N3). It is transport-agnostic: a fresh <see cref="IClientTransport"/> is minted
/// per (re)connect via the factory, so a Steam transport slots in the same way (plan §6, §9).
///
/// Every decoded <see cref="ServerMessage"/> is raised on <see cref="MessageReceived"/> for a higher
/// layer (RemoteGameHost) to interpret; connection lifecycle surfaces via <see cref="StateChanged"/>.
/// </summary>
public sealed class GameServerClient : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] ReconnectBackoff =
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
         TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)];

    private readonly Func<IClientTransport> _transportFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private volatile IClientTransport? _transport;
    private Uri? _uri;
    private int _seq;
    private Task? _supervisor;
    private Task? _heartbeatLoop;
    private TaskCompletionSource<HelloOk>? _helloTcs;
    private Exception? _lastFault;
    private bool _disposed;

    public GameServerClient(Func<IClientTransport> transportFactory) => _transportFactory = transportFactory;

    /// <summary>Single-shot convenience (no reconnect): reuses the one transport for the initial connect only.</summary>
    public GameServerClient(IClientTransport transport) : this(() => transport) { }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>When true, a dropped connection is retried with backoff, re-sending
    /// <see cref="ReconnectHelloProvider"/>'s hello. Enable only after a match has started (the resume
    /// token is issued in match_started).</summary>
    public bool AutoReconnect { get; set; }

    /// <summary>Builds the hello sent on each reconnect — must carry the resume token.</summary>
    public Func<Hello>? ReconnectHelloProvider { get; set; }

    public event Action<ServerMessage>? MessageReceived;
    public event Action<ConnectionState>? StateChanged;

    /// <summary>Raised once when the connection is finally given up (no reconnect, or reconnect failed).</summary>
    public event Action<Exception?>? Closed;

    public async Task<HelloOk> ConnectAsync(Uri uri, Hello hello, CancellationToken ct = default)
    {
        _uri = uri;
        SetState(ConnectionState.Connecting);
        _helloTcs = new TaskCompletionSource<HelloOk>(TaskCreationOptions.RunContinuationsAsynchronously);

        _transport = _transportFactory();
        await _transport.ConnectAsync(uri, ct).ConfigureAwait(false);
        _supervisor = Task.Run(SupervisorAsync);
        _heartbeatLoop = Task.Run(HeartbeatLoopAsync);

        await SendAsync(hello, ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var reg = timeout.Token.Register(() => _helloTcs.TrySetCanceled());
        var ok = await _helloTcs.Task.ConfigureAwait(false);
        SetState(ConnectionState.Connected);
        return ok;
    }

    public int NextSeq() => Interlocked.Increment(ref _seq);

    public async Task<int> SendAsync(ClientMessage message, CancellationToken ct = default)
    {
        int seq = NextSeq();
        await SendWithSeqAsync(message, seq, ct).ConfigureAwait(false);
        return seq;
    }

    public async Task SendWithSeqAsync(ClientMessage message, int seq, CancellationToken ct = default)
    {
        var transport = _transport ?? throw new InvalidOperationException("Not connected.");
        string json = ProtocolJson.Encode(message with { Seq = seq });
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await transport.SendTextAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Test/diagnostic hook: drop the current transport to exercise the reconnect path.</summary>
    public Task SimulateDropAsync() => _transport?.CloseAsync() ?? Task.CompletedTask;

    /// <summary>Owns the connection for its whole life: read until the transport closes, then reconnect
    /// (if enabled) and read again. Exits only on dispose or a give-up.</summary>
    private async Task SupervisorAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await ReceiveLoopAsync(_transport!).ConfigureAwait(false);
            if (_cts.IsCancellationRequested)
                return;

            if (!AutoReconnect || ReconnectHelloProvider is null)
            {
                Closed?.Invoke(_lastFault);
                return;
            }

            SetState(ConnectionState.Reconnecting);
            if (!await ReconnectAsync().ConfigureAwait(false))
            {
                SetState(ConnectionState.Failed);
                Closed?.Invoke(_lastFault);
                return;
            }
            SetState(ConnectionState.Connected);
        }
    }

    private async Task ReceiveLoopAsync(IClientTransport transport)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                string? json = await transport.ReceiveTextAsync(_cts.Token).ConfigureAwait(false);
                if (json is null)
                    return; // this transport closed

                var message = ProtocolJson.TryDecodeServer(json);
                if (message is null)
                    continue;

                if (message is HelloOk ok)
                    _helloTcs?.TrySetResult(ok);
                // A handshake-phase ErrorMsg (server rejects before hello_ok) faults the pending connect so
                // the caller sees the real reason immediately instead of the 10s hello timeout. Gated to the
                // initial connect (State == Connecting): mid-session ErrorMsgs (auth, etc.) and reconnect-time
                // rejections don't touch the TCS — the latter avoids an unobserved faulted task nobody awaits.
                else if (message is ErrorMsg err && State == ConnectionState.Connecting)
                    _helloTcs?.TrySetException(new HandshakeRejectedException(err.Code, err.Message));

                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (Exception ex) { _lastFault = ex; }
    }

    private async Task<bool> ReconnectAsync()
    {
        foreach (var delay in ReconnectBackoff)
        {
            if (_cts.IsCancellationRequested)
                return false;
            try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            try
            {
                var transport = _transportFactory();
                await transport.ConnectAsync(_uri!, _cts.Token).ConfigureAwait(false);
                _transport = transport;
                _helloTcs = new TaskCompletionSource<HelloOk>(TaskCreationOptions.RunContinuationsAsynchronously);
                await SendAsync(ReconnectHelloProvider!()).ConfigureAwait(false);
                return true; // supervisor re-enters ReceiveLoop to read hello_ok + resync_ok
            }
            catch (Exception ex)
            {
                _lastFault = ex; // try the next backoff step
            }
        }
        return false;
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, _cts.Token).ConfigureAwait(false);
                try { await SendAsync(new Ping(), _cts.Token).ConfigureAwait(false); }
                catch { /* mid-reconnect send can fail; the receive side drives recovery */ }
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _helloTcs?.TrySetCanceled();
        if (_transport is { } t)
            await t.CloseAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_supervisor ?? Task.CompletedTask, _heartbeatLoop ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch { /* loops observe cancellation */ }
        if (_transport is { } t2)
            await t2.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _sendGate.Dispose();
    }
}
