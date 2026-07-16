using System.Text.Json;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Rules.Cards;

public sealed record DeckEntry
{
    public required string Id { get; init; }
    public int Count { get; init; } = 1;
}

/// <summary>A preconstructed deck: a leader plus card counts. Expands to the flat 30-card id list.</summary>
public sealed record DeckList
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Faction { get; init; } = "neutral";
    public string Leader { get; init; } = "";
    public IReadOnlyList<DeckEntry> Cards { get; init; } = [];

    public IReadOnlyList<string> Expand()
    {
        var ids = new List<string>();
        foreach (var entry in Cards)
            for (int i = 0; i < entry.Count; i++)
                ids.Add(entry.Id);
        return ids;
    }
}

public static class DeckLibrary
{
    public static IReadOnlyList<DeckList> ParseJson(string json) =>
        JsonSerializer.Deserialize<List<DeckList>>(json, RulesJson.Options)
            ?? throw new InvalidDataException("Deck JSON deserialized to null.");

    public static IReadOnlyList<DeckList> LoadFromDirectory(string directory)
    {
        var decks = new List<DeckList>();
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                decks.AddRange(ParseJson(File.ReadAllText(file)));
        return decks;
    }
}
