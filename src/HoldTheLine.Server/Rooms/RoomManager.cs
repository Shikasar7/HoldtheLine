using System.Collections.Concurrent;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// The room registry: share code → <see cref="Room"/>, plus a resume-token index that lets a dropped
/// player re-attach to their in-progress match (plan §5.2). A still-waiting room whose sole occupant
/// drops is discarded immediately; a room whose match is underway is kept alive through the
/// disconnect grace window so the player can reconnect.
/// </summary>
public sealed class RoomManager(ServerOptions opts, GameContent content, DeckSource deckSource)
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Room Room, int Seat)> _byToken = new(StringComparer.Ordinal);

    public Room CreateRoom(ClientConnection host, string deckId)
    {
        deckSource.Resolve(host.GuestId, deckId); // validate the host's pick up front

        for (int attempt = 0; attempt < 8; attempt++)
        {
            var room = new Room(SessionAuth.NewRoomCode(), content, opts, deckSource);
            if (_rooms.TryAdd(room.Code, room))
            {
                room.SetHost(host, deckId);
                return room;
            }
        }
        throw new ProtocolError("server_busy", "Could not allocate a room code, try again.");
    }

    public async Task JoinAsync(string code, ClientConnection guest, string deckId)
    {
        deckSource.Resolve(guest.GuestId, deckId); // validate the joiner's pick
        if (!_rooms.TryGetValue(code, out var room))
            throw new ProtocolError("room_not_found", $"No room '{code}'.");

        await room.JoinAndStartAsync(guest, deckId);

        // Index both resume tokens so either player can reconnect to this match.
        var session = room.Session!;
        _byToken[session.ResumeTokenFor(0)] = (room, 0);
        _byToken[session.ResumeTokenFor(1)] = (room, 1);
    }

    /// <summary>Re-attach a freshly-connected client to its match by resume token. False if the token is
    /// unknown / the match is already over.</summary>
    public bool TryReconnect(string resumeToken, ClientConnection conn)
    {
        if (!_byToken.TryGetValue(resumeToken, out var loc))
            return false;
        if (loc.Room.Session is not { } session || session.IsOver)
            return false;

        conn.Room = loc.Room;
        conn.Seat = loc.Seat;
        session.Reattach(loc.Seat, conn); // pump sends resync_ok + notifies the opponent
        return true;
    }

    /// <summary>Clean up when a connection ends. A waiting room is discarded once empty; a live match
    /// starts its grace window (keep for reconnect); a finished match is torn down.</summary>
    public void OnDisconnect(ClientConnection conn)
    {
        if (conn.Room is not { } room)
            return;

        if (!room.Started)
        {
            if (room.RemoveIfWaiting(conn))
                _rooms.TryRemove(room.Code, out _);
        }
        else if (room.Session is { IsOver: false } session)
        {
            session.OnConnectionDropped(conn); // grace window; room stays for reconnect
        }
        else
        {
            DiscardRoom(room);
        }
        conn.Room = null;
    }

    private void DiscardRoom(Room room)
    {
        room.Teardown();
        if (room.Session is { } session)
        {
            _byToken.TryRemove(session.ResumeTokenFor(0), out _);
            _byToken.TryRemove(session.ResumeTokenFor(1), out _);
        }
        _rooms.TryRemove(room.Code, out _);
    }

    public Room? FindRoom(string code) => _rooms.TryGetValue(code, out var room) ? room : null;

    public int RoomCount => _rooms.Count;
}
