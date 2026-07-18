namespace HoldTheLine.Server.Rooms;

/// <summary>
/// A two-seat lobby keyed by a share code. Seat 0 is the creator, seat 1 the joiner; once both are
/// present the room starts its <see cref="MatchSession"/>. Slot mutation is guarded so a join and a
/// disconnect can't race.
/// </summary>
public sealed class Room(string code, GameContent content, ServerOptions opts, DeckSource deckSource)
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
    /// is already full/in progress, or if either deck no longer resolves.</summary>
    public async Task JoinAndStartAsync(ClientConnection guest, string deckId, Func<int, string, (Net.Protocol.ServerMessage?, Net.Protocol.ServerMessage?)>? onEnded = null)
    {
        Data.ResolvedDeck host0, guest1;
        lock (_gate)
        {
            if (_started || _guest != null)
                throw new ProtocolError("room_full", "That room is already full.");
            if (_host == null)
                throw new ProtocolError("room_not_ready", "That room has no host.");
            // Resolve BOTH decks while still "waiting": a deck deleted since create_room throws here,
            // BEFORE the room is marked started, so a failed start can never zombify it as
            // "started, no session" (C0-3).
            host0 = deckSource.Resolve(_host.GuestId, _hostDeck!);
            guest1 = deckSource.Resolve(guest.GuestId, deckId);
            _guest = guest;
            _guestDeck = deckId;
            _started = true;
        }
        guest.Room = this;
        guest.Seat = 1;

        try
        {
            Session = MatchSession.Create(content, opts, _host!, host0, _guest!, guest1, onEnded);
            await Session.SendMatchStartedAsync();
            Session.Begin(); // start the first turn clock
        }
        catch
        {
            // Roll back so a mid-start failure doesn't leave the room stuck (defence in depth).
            Session?.Stop();
            Session = null;
            guest.Room = null;
            lock (_gate) { _guest = null; _guestDeck = null; _started = false; }
            throw;
        }
    }

    /// <summary>Was this connection an occupant of this room?</summary>
    public bool Contains(ClientConnection conn)
    {
        lock (_gate)
            return _host == conn || _guest == conn;
    }

    /// <summary>True once the match is underway (an occupant drop here means abandonment, not just leaving a lobby).</summary>
    public bool Started
    {
        get { lock (_gate) return _started; }
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

    /// <summary>Stop the match pump and release the room. N1: any mid-match drop ends the match (no
    /// grace); N3 replaces this with a reconnect window keyed on the resume token.</summary>
    public void Teardown() => Session?.Stop();
}
