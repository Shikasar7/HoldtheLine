using System;
using System.Threading.Tasks;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;

namespace HoldTheLine.Game;

/// <summary>
/// The persistent online session (M3 C1). The lobby connects once and stays connected, so the SAME
/// socket carries the profile, deck saves, the matchmaking queue, and the ranked match itself — the
/// connection that queued is the connection the match runs on. A fresh <see cref="RemoteGameHost"/> is
/// armed per match (before entering the queue / a room) so it captures the incoming match_started; the
/// battle scene then attaches to <see cref="Remote"/>. Static + cross-scene, mirroring <see cref="GameConfig"/>.
///
/// <para>Threading: <see cref="GameServerClient.MessageReceived"/> fires on the WS receive thread, so the
/// lobby-level events below fire there too — subscribers (scenes) must marshal any UI touch with
/// Callable.CallDeferred, exactly as the battle scene already does. match_started is NOT surfaced as an
/// event: callers await <c>Remote.WaitForMatchAsync()</c>, which the RemoteGameHost completes once it has
/// applied the snapshot — no fragile handler-ordering.</para>
/// </summary>
public static class Session
{
    public static GameServerClient? Client { get; private set; }
    public static RemoteGameHost? Remote { get; private set; }
    public static Profile? Profile { get; private set; }
    public static bool Connected => Client is { State: ConnectionState.Connected };

    private static Hello? _hello;

    // Lobby-level server pushes (WS thread — subscribers marshal to the main thread).
    public static event Action<Profile>? ProfileUpdated;
    public static event Action<QueueStatus>? QueueStatusReceived;
    public static event Action<RatingChange>? RatingChanged;
    public static event Action<DeckSaved>? DeckSavedOk;
    public static event Action<DeckError>? DeckSaveFailed;
    public static event Action<Ladder>? LadderReceived;
    public static event Action<RoomCreated>? RoomCreatedOk;
    public static event Action<ErrorMsg>? Errored;
    public static event Action<ConnectionState>? StateChanged;

    /// <summary>Connect + hello (identity + data hash). Idempotent: a no-op if already connected. Returns
    /// null on success, or a human-readable error. Arms the first match host so match_started is captured.</summary>
    public static async Task<string?> ConnectAsync(string serverUrl, string nickname)
    {
        if (Connected)
            return null;

        var client = new GameServerClient(() => new WebSocketTransport());
        Client = client;
        // Remote subscribes to MessageReceived in its ctor — do it FIRST so it applies match_started/events
        // before the lobby handler below sees them.
        Remote = new RemoteGameHost(client);
        client.MessageReceived += OnMessage;
        client.StateChanged += s => StateChanged?.Invoke(s);

        var (guestId, secret) = Identity.Get();
        _hello = new Hello
        {
            GuestId = guestId,
            Secret = secret,
            Name = string.IsNullOrWhiteSpace(nickname) ? "玩家" : nickname,
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            RulesVersion = HoldTheLine.Rules.RulesInfo.Version,
            DataHash = HoldTheLine.Net.DataHash.Compute(GameData.LoadCards(), GameData.LoadLeaders(), GameData.LoadDecks()),
        };

        try
        {
            await client.ConnectAsync(new Uri(serverUrl), _hello);
            return null;
        }
        catch (Exception ex)
        {
            Client = null;
            Remote = null;
            _hello = null;
            return ex.Message;
        }
    }

    /// <summary>Mint a fresh match host on the live client so the NEXT match_started is captured — call
    /// after a match ends / before re-entering the queue.</summary>
    public static void ArmMatchHost()
    {
        if (Client is { } c)
        {
            Remote?.Detach(); // retire the finished match's host so it stops consuming the shared stream
            Remote = new RemoteGameHost(c);
        }
    }

    /// <summary>Turn on transparent reconnect for the current match's resume token (call once a match starts).</summary>
    public static void EnableReconnect()
    {
        if (Remote is { } r && _hello is { } h)
            r.EnableReconnect(h);
    }

    public static Task SendAsync(ClientMessage message) => Client?.SendAsync(message) ?? Task.CompletedTask;

    public static async Task DisconnectAsync()
    {
        var c = Client;
        Remote?.Detach();
        Client = null;
        Remote = null;
        Profile = null;
        _hello = null;
        if (c is not null)
            await c.DisposeAsync();
    }

    private static void OnMessage(ServerMessage msg)
    {
        switch (msg)
        {
            case Profile p: Profile = p; ProfileUpdated?.Invoke(p); break;
            case QueueStatus q: QueueStatusReceived?.Invoke(q); break;
            case RatingChange rc: RatingChanged?.Invoke(rc); break;
            case DeckSaved ds: DeckSavedOk?.Invoke(ds); break;
            case DeckError de: DeckSaveFailed?.Invoke(de); break;
            case Ladder l: LadderReceived?.Invoke(l); break;
            case RoomCreated room: RoomCreatedOk?.Invoke(room); break;
            case ErrorMsg e: Errored?.Invoke(e); break;
        }
    }
}
