using System.Collections.Concurrent;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// The room registry: share code → <see cref="Room"/>. One instance per server, shared across all
/// connections (registered as a DI singleton). Rooms live only as long as they're occupied — a
/// still-waiting room whose sole occupant drops is discarded (plan §5.1 recycling; the 10-minute
/// idle sweep is deferred to N3 alongside reconnection).
/// </summary>
public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);

    public Room CreateRoom(ClientConnection host, string deckId, GameContent content)
    {
        if (content.FindDeck(deckId) is null)
            throw new ProtocolError("unknown_deck", $"No deck '{deckId}'.");

        for (int attempt = 0; attempt < 8; attempt++)
        {
            var room = new Room(SessionAuth.NewRoomCode(), content);
            if (_rooms.TryAdd(room.Code, room))
            {
                room.SetHost(host, deckId);
                return room;
            }
        }
        throw new ProtocolError("server_busy", "Could not allocate a room code, try again.");
    }

    public async Task JoinAsync(string code, ClientConnection guest, string deckId, GameContent content)
    {
        if (content.FindDeck(deckId) is null)
            throw new ProtocolError("unknown_deck", $"No deck '{deckId}'.");
        if (!_rooms.TryGetValue(code, out var room))
            throw new ProtocolError("room_not_found", $"No room '{code}'.");

        await room.JoinAndStartAsync(guest, deckId);
    }

    /// <summary>Clean up when a connection ends. A still-waiting room is discarded once empty; a room
    /// whose match had started is torn down entirely (N1 has no reconnect grace — that's N3).</summary>
    public void OnDisconnect(ClientConnection conn)
    {
        if (conn.Room is not { } room)
            return;

        if (room.Started)
        {
            room.Teardown();
            _rooms.TryRemove(room.Code, out _);
        }
        else if (room.RemoveIfWaiting(conn))
        {
            _rooms.TryRemove(room.Code, out _);
        }
        conn.Room = null;
    }

    /// <summary>Test/inspection hook: the room for a code, if it still exists.</summary>
    public Room? FindRoom(string code) => _rooms.TryGetValue(code, out var room) ? room : null;

    public int RoomCount => _rooms.Count;
}
