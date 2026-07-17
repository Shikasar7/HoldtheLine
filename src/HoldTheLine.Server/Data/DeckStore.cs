using System.Text.Json;

namespace HoldTheLine.Server.Data;

/// <summary>A persisted custom deck (M3 B1). Faction is derived server-side from the card list.</summary>
public sealed record DeckRecord(string Id, string Name, string Faction, string Leader, IReadOnlyList<string> CardIds);

/// <summary>A deck resolved to what a match needs — the flat card-id list and the leader — regardless of
/// whether it came from a player's saved deck or a built-in preconstructed one.</summary>
public sealed record ResolvedDeck(IReadOnlyList<string> CardIds, string Leader);

/// <summary>
/// Per-account custom decks (M3 plan B1). Cards are stored as a JSON id array; ownership is enforced on
/// every read/write by <c>guest_id</c>, so one player can never touch another's decks.
/// </summary>
public sealed class DeckStore
{
    public const int MaxDecksPerAccount = 20;

    private readonly Db _db;

    public DeckStore(Db db)
    {
        _db = db;
        _db.Run(c =>
        {
            AccountStore.Exec(c, """
                CREATE TABLE IF NOT EXISTS decks (
                    id         TEXT PRIMARY KEY,
                    guest_id   TEXT NOT NULL,
                    name       TEXT NOT NULL,
                    faction    TEXT NOT NULL,
                    leader     TEXT NOT NULL,
                    cards_json TEXT NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                """);
            AccountStore.Exec(c, "CREATE INDEX IF NOT EXISTS idx_decks_guest ON decks(guest_id);");
        });
    }

    public IReadOnlyList<DeckRecord> ListFor(string guestId) => _db.Run(c =>
    {
        var list = new List<DeckRecord>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,faction,leader,cards_json FROM decks WHERE guest_id=$g ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("$g", guestId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(Read(r));
        return (IReadOnlyList<DeckRecord>)list;
    });

    public DeckRecord? Get(string guestId, string deckId) => _db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,faction,leader,cards_json FROM decks WHERE guest_id=$g AND id=$i";
        cmd.Parameters.AddWithValue("$g", guestId);
        cmd.Parameters.AddWithValue("$i", deckId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    });

    public int CountFor(string guestId) => _db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM decks WHERE guest_id=$g";
        cmd.Parameters.AddWithValue("$g", guestId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    /// <summary>Create (null deckId) or update an existing owned deck. Returns the id, or null if an update
    /// targeted a deck this guest doesn't own.</summary>
    public string? Save(string guestId, string? deckId, string name, string faction, string leader, IReadOnlyList<string> cardIds)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string json = JsonSerializer.Serialize(cardIds);
        return _db.Run(c =>
        {
            if (deckId is not null)
            {
                using var upd = c.CreateCommand();
                upd.CommandText = "UPDATE decks SET name=$n,faction=$f,leader=$l,cards_json=$c,updated_at=$t WHERE id=$i AND guest_id=$g";
                Bind(upd, ("$i", deckId), ("$g", guestId), ("$n", name), ("$f", faction), ("$l", leader), ("$c", json), ("$t", now));
                return upd.ExecuteNonQuery() > 0 ? deckId : null;
            }

            string id = "deck-" + SessionAuth.NewResumeToken()[..12];
            AccountStore.Exec(c, "INSERT INTO decks(id,guest_id,name,faction,leader,cards_json,updated_at) VALUES($i,$g,$n,$f,$l,$c,$t)",
                ("$i", id), ("$g", guestId), ("$n", name), ("$f", faction), ("$l", leader), ("$c", json), ("$t", now));
            return id;
        });
    }

    public bool Delete(string guestId, string deckId) => _db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM decks WHERE guest_id=$g AND id=$i";
        cmd.Parameters.AddWithValue("$g", guestId);
        cmd.Parameters.AddWithValue("$i", deckId);
        return cmd.ExecuteNonQuery() > 0;
    });

    private static DeckRecord Read(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? []);

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd, params (string Name, object Value)[] args)
    {
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
    }
}
