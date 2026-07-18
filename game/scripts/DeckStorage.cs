using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace HoldTheLine.Game;

/// <summary>One saved deck on this device: a leader + a flat 30-card id list, plus the id the server
/// assigned it once synced (so the same deck is queue-able online).</summary>
public sealed record StoredDeck
{
    public required string Id { get; init; }          // local id (stable across edits)
    public required string Name { get; init; }
    public required string Faction { get; init; }
    public required string Leader { get; init; }
    public required List<string> CardIds { get; init; }
    public string? ServerId { get; init; }            // set once the server has a copy
}

/// <summary>
/// Local deck library (<c>user://decks.json</c>), the source of truth for deck management and offline play.
/// Multiple decks, editable and renameable; each can also be pushed to the server so online queueing sees
/// it. Mirrors <see cref="Identity"/>'s user-file pattern. Presentation-side only — the authoritative deck
/// validation still lives in the rules layer / server.
/// </summary>
public static class DeckStorage
{
    private const string Path = "user://decks.json";

    public static List<StoredDeck> LoadAll()
    {
        if (!Godot.FileAccess.FileExists(Path))
            return new List<StoredDeck>();
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Read);
        if (f == null)
            return new List<StoredDeck>();
        try
        {
            return JsonSerializer.Deserialize<List<StoredDeck>>(f.GetAsText()) ?? new List<StoredDeck>();
        }
        catch
        {
            return new List<StoredDeck>();
        }
    }

    /// <summary>Upsert by <see cref="StoredDeck.Id"/> and persist. Returns the full list after the write.</summary>
    public static List<StoredDeck> Save(StoredDeck deck)
    {
        var all = LoadAll();
        int i = all.FindIndex(d => d.Id == deck.Id);
        if (i >= 0) all[i] = deck; else all.Add(deck);
        Persist(all);
        return all;
    }

    public static void Delete(string id)
    {
        var all = LoadAll();
        all.RemoveAll(d => d.Id == id);
        Persist(all);
    }

    /// <summary>Record the server id for a local deck once it has been saved online (matched by name +
    /// leader, since offline-created decks have no server id yet).</summary>
    public static void SetServerId(string localId, string serverId)
    {
        var all = LoadAll();
        int i = all.FindIndex(d => d.Id == localId);
        if (i >= 0) { all[i] = all[i] with { ServerId = serverId }; Persist(all); }
    }

    public static StoredDeck? Get(string id) => LoadAll().FirstOrDefault(d => d.Id == id);

    public static string NewId() => "deck-" + System.Guid.NewGuid().ToString("N")[..10];

    private static void Persist(List<StoredDeck> all)
    {
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
        if (f != null)
            f.StoreString(JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        else
            GD.PushError($"DeckStorage: cannot write {Path}");
    }
}
