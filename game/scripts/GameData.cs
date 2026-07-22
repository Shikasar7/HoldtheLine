using Godot;
using HoldTheLine.Rules.Cards;

namespace HoldTheLine.Game;

/// <summary>
/// Loads card / leader / deck data from res://data using Godot's FileAccess (works in the editor
/// and in exported builds, unlike raw OS paths). Feeds JSON strings to the rules-layer parsers.
/// </summary>
public static class GameData
{
    // Per-file load failures since the last TakeLoadErrors(). A damaged data file used to throw straight
    // through the calling scene's _Ready and freeze it half-built with no player-visible message; now it
    // is skipped + logged here, and the menu's startup self-check turns the record into a dialog.
    private static readonly List<string> _loadErrors = new();

    /// <summary>Drain the accumulated data-load failures (for the menu's "数据损坏" startup dialog).</summary>
    public static IReadOnlyList<string> TakeLoadErrors()
    {
        var errs = _loadErrors.Distinct().ToList();
        _loadErrors.Clear();
        return errs;
    }

    public static CardDatabase LoadCards()
    {
        var defs = new List<CardDefinition>();
        foreach (var (path, json) in ReadJsonDir("res://data/cards"))
            ParseInto(defs, path, json, CardDatabase.ParseJson);
        return new CardDatabase(defs);
    }

    public static LeaderDatabase LoadLeaders()
    {
        var defs = new List<LeaderDefinition>();
        foreach (var (path, json) in ReadJsonDir("res://data/leaders"))
            ParseInto(defs, path, json, LeaderDatabase.ParseJson);
        return new LeaderDatabase(defs);
    }

    public static IReadOnlyList<DeckList> LoadDecks()
    {
        var decks = new List<DeckList>();
        foreach (var (path, json) in ReadJsonDir("res://data/decks"))
            ParseInto(decks, path, json, DeckLibrary.ParseJson);
        return decks;
    }

    private static void ParseInto<T>(List<T> into, string path, string json, Func<string, IReadOnlyList<T>> parse)
    {
        try
        {
            into.AddRange(parse(json));
        }
        catch (Exception ex)
        {
            GD.PushError($"GameData: {path} failed to parse — skipped: {ex.Message}");
            _loadErrors.Add($"{path}: {ex.Message}");
        }
    }

    /// <summary>The editable on-standee status table. Prefers res://data/status_catalog.tres (managed in the
    /// Inspector); falls back to the code-built default so the board still renders if the .tres is absent.</summary>
    public static StatusCatalog LoadStatusCatalog()
    {
        const string path = "res://data/status_catalog.tres";
        if (ResourceLoader.Exists(path) && GD.Load<StatusCatalog>(path) is { Statuses.Count: > 0 } cat)
            return cat;
        return StatusCatalog.BuildDefault();
    }

    /// <summary>The editable faction metadata table (docs/22 批次D4). Prefers res://data/faction_catalog.tres
    /// (managed in the Inspector); a missing or broken .tres warns and falls back to the code-built default so
    /// every faction surface still renders.</summary>
    public static FactionCatalog LoadFactionCatalog()
    {
        const string path = "res://data/faction_catalog.tres";
        if (ResourceLoader.Exists(path))
        {
            if (GD.Load<FactionCatalog>(path) is { Factions.Count: > 0 } cat)
                return cat;
            GD.PushWarning($"GameData: {path} 加载失败或为空 — 回退到内置阵营表");
        }
        return FactionCatalog.BuildDefault();
    }

    /// <summary>The editable keyword display-text table (docs/22 批次D4). Prefers res://data/keyword_catalog.tres;
    /// a missing or broken .tres warns and falls back to the code-built default.</summary>
    public static KeywordCatalog LoadKeywordCatalog()
    {
        const string path = "res://data/keyword_catalog.tres";
        if (ResourceLoader.Exists(path))
        {
            if (GD.Load<KeywordCatalog>(path) is { Keywords.Count: > 0 } cat)
                return cat;
            GD.PushWarning($"GameData: {path} 加载失败或为空 — 回退到内置关键词表");
        }
        return KeywordCatalog.BuildDefault();
    }

    private static IEnumerable<(string Path, string Json)> ReadJsonDir(string resDir)
    {
        using var dir = DirAccess.Open(resDir);
        if (dir is null)
        {
            GD.PushError($"GameData: cannot open {resDir}");
            _loadErrors.Add($"{resDir}: 目录缺失或无法打开");
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
                _loadErrors.Add($"{path}: 无法读取");
                continue;
            }
            yield return (path, f.GetAsText());
        }
    }
}
