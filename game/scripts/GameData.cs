using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// Loads card / leader / deck data from res://data using Godot's FileAccess (works in the editor
/// and in exported builds, unlike raw OS paths). Feeds JSON strings to the rules-layer parsers.
/// </summary>
public static class GameData
{
    public static CardDatabase LoadCards()
    {
        var defs = new List<CardDefinition>();
        foreach (var json in ReadJsonDir("res://data/cards"))
            defs.AddRange(CardDatabase.ParseJson(json));
        return new CardDatabase(defs);
    }

    public static LeaderDatabase LoadLeaders()
    {
        var defs = new List<LeaderDefinition>();
        foreach (var json in ReadJsonDir("res://data/leaders"))
            defs.AddRange(LeaderDatabase.ParseJson(json));
        return new LeaderDatabase(defs);
    }

    public static IReadOnlyList<DeckList> LoadDecks()
    {
        var decks = new List<DeckList>();
        foreach (var json in ReadJsonDir("res://data/decks"))
            decks.AddRange(DeckLibrary.ParseJson(json));
        return decks;
    }

    private static IEnumerable<string> ReadJsonDir(string resDir)
    {
        using var dir = DirAccess.Open(resDir);
        if (dir is null)
        {
            GD.PushError($"GameData: cannot open {resDir}");
            yield break;
        }

        var files = new List<string>(dir.GetFiles());
        files.Sort(string.CompareOrdinal);
        foreach (var file in files)
        {
            // Godot may present .json as .json.import-less; also ignore remap files.
            if (!file.EndsWith(".json"))
                continue;
            string path = $"{resDir}/{file}";
            using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (f is null)
            {
                GD.PushError($"GameData: cannot read {path}");
                continue;
            }
            yield return f.GetAsText();
        }
    }
}
