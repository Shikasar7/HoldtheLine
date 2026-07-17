using System.Net.WebSockets;

namespace HoldTheLine.Net.Client;

/// <summary>
/// The M2 wire: a client-side WebSocket carrying UTF-8 JSON text frames (framing via
/// <see cref="WebSocketText"/>). This is the only <see cref="IClientTransport"/> the prototype
/// ships; a SteamSocketTransport slots in beside it without touching anything above.
/// </summary>
public sealed class WebSocketTransport : IClientTransport
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

    public ValueTask SendTextAsync(string text, CancellationToken ct) =>
        WebSocketText.SendAsync(_socket, text, ct);

    public Task<string?> ReceiveTextAsync(CancellationToken ct) =>
        WebSocketText.ReceiveAsync(_socket, ct);

    public Task CloseAsync() => SafeCloseAsync();

    private async Task SafeCloseAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (WebSocketException) { /* already gone */ }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async ValueTask DisposeAsync()
    {
        await SafeCloseAsync().ConfigureAwait(false);
        _socket.Dispose();
    }
}
