namespace HoldTheLine.Server.Rooms;

/// <summary>
/// A two-seat lobby keyed by a share code. Seat 0 is the creator, seat 1 the joiner; once both are
/// present the room starts its <see cref="MatchSession"/>. Slot mutation is guarded so a join and a
/// disconnect can't race.
/// </summary>
public sealed class Room(string code, GameContent content)
{
    private readonly object _gate = new();
    private ClientConnection? _host;
    private string? _hostDeck;
    private ClientConnection? _guest;
    private string? _guestDeck;
    private bool _started;

    public string Code { get; } = code;
    public MatchSession? Session { get; private set; }

    public void SetHost(ClientConnection host, string deckId)
    {
        lock (_gate)
        {
            _host = host;
            _hostDeck = deckId;
        }
        host.Room = this;
        host.Seat = 0;
    }

    /// <summary>Seat the joiner and kick off the match. Throws <see cref="ProtocolError"/> if the room
    /// is already full or in progress.</summary>
    public async Task JoinAndStartAsync(ClientConnection guest, string deckId)
    {
        lock (_gate)
        {
            if (_started || _guest != null)
                throw new ProtocolError("room_full", "That room is already full.");
            if (_host == null)
                throw new ProtocolError("room_not_ready", "That room has no host.");
            _guest = guest;
            _guestDeck = deckId;
            _started = true;
        }
        guest.Room = this;
        guest.Seat = 1;

        Session = MatchSession.Create(content, _host!, _hostDeck!, _guest!, _guestDeck!);
        await Session.SendMatchStartedAsync();
    }

    /// <summary>Remove a still-waiting occupant (pre-match). Returns true if the room is now empty and
    /// should be discarded. No-op once the match has started (that path is N3 reconnect handling).</summary>
    public bool RemoveIfWaiting(ClientConnection conn)
    {
        lock (_gate)
        {
            if (_started)
                return false;
            if (_host == conn) { _host = null; _hostDeck = null; }
            if (_guest == conn) { _guest = null; _guestDeck = null; }
            return _host == null && _guest == null;
        }
    }
}
