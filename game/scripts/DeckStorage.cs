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
    public long UpdatedAt { get; init; }              // unix seconds of the last save (0 on pre-existing files)
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
        // Main file first, then its .bak (crash window of an interrupted save). A copy that fails to
        // parse is quarantined, NOT treated as an empty library — treating it as empty made the next
        // save overwrite the player's entire deck collection with a one-deck list.
        if (TryParse(UserFile.ReadText(Path)) is { } decks)
            return decks;
        UserFile.Quarantine(Path);
        return TryParse(UserFile.ReadText(Path + ".bak")) ?? new List<StoredDeck>();
    }

    private static List<StoredDeck>? TryParse(string? json)
    {
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<StoredDeck>>(json) ?? new List<StoredDeck>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Upsert by <see cref="StoredDeck.Id"/> and persist (stamping <see cref="StoredDeck.UpdatedAt"/>).
    /// Returns the full list after the write.</summary>
    public static List<StoredDeck> Save(StoredDeck deck)
    {
        deck = deck with { UpdatedAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        var all = LoadAll();
        int i = all.FindIndex(d => d.Id == deck.Id);
        if (i >= 0) all[i] = deck; else all.Add(deck);
        Persist(all);
        return all;
    }

    /// <summary>The most recently saved deck, or null when the library is empty — the default pick when
    /// no match has been played yet.</summary>
    public static StoredDeck? NewestEdited() => LoadAll().OrderByDescending(d => d.UpdatedAt).FirstOrDefault();

    /// <summary>
    /// A deck name no other deck (excluding <paramref name="excludeId"/>) already uses. If
    /// <paramref name="desired"/> is taken, its trailing digits are treated as a counter and bumped
    /// until free: 我的卡组1 → 我的卡组2 → 我的卡组3; 狂猎快攻 → 狂猎快攻2.
    /// </summary>
    public static string UniqueName(string desired, string? excludeId = null)
    {
        var taken = LoadAll().Where(d => d.Id != excludeId).Select(d => d.Name).ToHashSet();
        if (!taken.Contains(desired))
            return desired;
        string stem = desired.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        string digits = desired[stem.Length..];
        int n = digits.Length > 0 && int.TryParse(digits, out int parsed) ? parsed + 1 : 2;
        while (taken.Contains(stem + n))
            n++;
        return stem + n;
    }

    public static void Delete(string id)
    {
        var all = LoadAll();
        all.RemoveAll(d => d.Id == id);
        Persist(all);
    }

    /// <summary>Record the server id for a local deck once it has been saved online (matched by the local
    /// id the editor noted before pushing to the server).</summary>
    public static void SetServerId(string localId, string serverId)
    {
        var all = LoadAll();
        int i = all.FindIndex(d => d.Id == localId);
        if (i >= 0) { all[i] = all[i] with { ServerId = serverId }; Persist(all); }
    }

    public static StoredDeck? Get(string id) => LoadAll().FirstOrDefault(d => d.Id == id);

    public static string NewId() => "deck-" + System.Guid.NewGuid().ToString("N")[..10];

    private static void Persist(List<StoredDeck> all) =>
        UserFile.WriteAtomic(Path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
}
