namespace HoldTheLine.Net.Client;

/// <summary>
/// The one seam that keeps the wire pluggable. <see cref="GameServerClient"/> speaks only these four
/// methods, so swapping WebSocket for a Steam Datagram Relay socket later (M4 platform work) is a new
/// implementation, not a rewrite — the protocol, the client, and BattleScene never know which wire
/// they're on. Text frames only: the protocol is JSON (<see cref="Protocol.ProtocolJson"/>).
/// </summary>
public interface IClientTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);

    ValueTask SendTextAsync(string text, CancellationToken ct);

    /// <summary>Awaits the next complete text frame, or returns null when the peer closes cleanly.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);

    Task CloseAsync();
}
