using System;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;

namespace HoldTheLine.Game;

/// <summary>
/// docs/22 E4: owns the <see cref="Session"/> event subscriptions for ONE lobby flow (ranked queue /
/// host room / join by code). The menu's flows used to pair up += / -= by hand across every exit
/// (success, catch, the cancel button) — the lobby-reconnect hotfix chased exactly that kind of leak.
/// Construct it right where the flow used to subscribe (order matters: e.g. the queue's StateChanged
/// auto-rejoin must NOT be live while EnsureLobbyLinkAsync is still reconnecting), and every exit
/// unsubscribes via using / try-finally instead.
///
/// <para>Not a Node — a plain IDisposable. <see cref="Dispose"/> is idempotent, so a cancel button may
/// detach eagerly (mid-await, before ShowLobby) and the flow's own finally harmlessly disposes again.
/// Handlers still fire on the WS receive thread exactly as before; marshalling stays the callers' job.</para>
/// </summary>
public sealed class LobbyFlow : IDisposable
{
    private readonly Action<QueueStatus>? _onQueueStatus;
    private readonly Action<RoomCreated>? _onRoomCreated;
    private readonly Action<ErrorMsg>? _onErrored;
    private readonly Action<ConnectionState>? _onStateChanged;
    private bool _disposed;

    public LobbyFlow(
        Action<QueueStatus>? onQueueStatus = null,
        Action<RoomCreated>? onRoomCreated = null,
        Action<ErrorMsg>? onErrored = null,
        Action<ConnectionState>? onStateChanged = null)
    {
        _onQueueStatus = onQueueStatus;
        _onRoomCreated = onRoomCreated;
        _onErrored = onErrored;
        _onStateChanged = onStateChanged;

        if (_onQueueStatus != null) Session.QueueStatusReceived += _onQueueStatus;
        if (_onRoomCreated != null) Session.RoomCreatedOk += _onRoomCreated;
        if (_onErrored != null) Session.Errored += _onErrored;
        if (_onStateChanged != null) Session.StateChanged += _onStateChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_onQueueStatus != null) Session.QueueStatusReceived -= _onQueueStatus;
        if (_onRoomCreated != null) Session.RoomCreatedOk -= _onRoomCreated;
        if (_onErrored != null) Session.Errored -= _onErrored;
        if (_onStateChanged != null) Session.StateChanged -= _onStateChanged;
    }
}
