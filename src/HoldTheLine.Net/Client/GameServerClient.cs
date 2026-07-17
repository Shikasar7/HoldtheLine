using HoldTheLine.Net.Protocol;

namespace HoldTheLine.Net.Client;

/// <summary>
/// Client-side connection to the battle server: owns the receive loop, assigns request sequence
/// numbers, and keeps the link alive with heartbeats. It is transport-agnostic (see
/// <see cref="IClientTransport"/>) and message-shape-agnostic above the handshake — every decoded
/// <see cref="ServerMessage"/> is raised on <see cref="MessageReceived"/> for a higher layer
/// (RemoteGameHost in N1, the bot/console in N0) to interpret.
///
/// N0 scope: connect + hello handshake + send + receive-dispatch + heartbeat. Automatic reconnect
/// with resume tokens lands in N3.
/// </summary>
public sealed class GameServerClient : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly IClientTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _seq;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private TaskCompletionSource<HelloOk>? _helloTcs;

    public GameServerClient(IClientTransport transport) => _transport = transport;

    /// <summary>Every decoded server message, in arrival order, on the receive-loop thread.</summary>
    public event Action<ServerMessage>? MessageReceived;

    /// <summary>Raised once when the receive loop ends (clean close or drop). Argument is the fault, if any.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Connect the transport, start the loops, send <paramref name="hello"/>, and await the server's HelloOk.</summary>
    public async Task<HelloOk> ConnectAsync(Uri uri, Hello hello, CancellationToken ct = default)
    {
        _helloTcs = new TaskCompletionSource<HelloOk>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _transport.ConnectAsync(uri, ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _heartbeatLoop = Task.Run(HeartbeatLoopAsync);

        await SendAsync(hello, ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var reg = timeout.Token.Register(() => _helloTcs.TrySetCanceled());
        return await _helloTcs.Task.ConfigureAwait(false);
    }

    /// <summary>Reserve the next sequence number. Use with <see cref="SendWithSeqAsync"/> when the
    /// caller must register a pending reply-handler under the seq *before* the frame goes out (else a
    /// fast loopback reply can arrive before the handler is in place).</summary>
    public int NextSeq() => Interlocked.Increment(ref _seq);

    /// <summary>Assign the next sequence number, encode, and send. Returns the assigned seq.</summary>
    public async Task<int> SendAsync(ClientMessage message, CancellationToken ct = default)
    {
        int seq = NextSeq();
        await SendWithSeqAsync(message, seq, ct).ConfigureAwait(false);
        return seq;
    }

    /// <summary>Send a message stamped with a caller-chosen (pre-reserved) seq.</summary>
    public async Task SendWithSeqAsync(ClientMessage message, int seq, CancellationToken ct = default)
    {
        string json = ProtocolJson.Encode(message with { Seq = seq });
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transport.SendTextAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? fault = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                string? json = await _transport.ReceiveTextAsync(_cts.Token).ConfigureAwait(false);
                if (json is null)
                    break; // peer closed

                var message = ProtocolJson.TryDecodeServer(json);
                if (message is null)
                    continue; // unknown/newer message type — log-and-skip (plan §8-1)

                if (message is HelloOk ok)
                    _helloTcs?.TrySetResult(ok);

                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (Exception ex) { fault = ex; }
        finally
        {
            _helloTcs?.TrySetCanceled();
            Closed?.Invoke(fault);
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, _cts.Token).ConfigureAwait(false);
                await SendAsync(new Ping(), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (Exception) { /* a failed heartbeat surfaces via the receive loop's close */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _transport.CloseAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_receiveLoop ?? Task.CompletedTask, _heartbeatLoop ?? Task.CompletedTask)
                .ConfigureAwait(false);
        }
        catch { /* loops observe cancellation */ }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _sendGate.Dispose();
    }
}
