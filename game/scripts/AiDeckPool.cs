using System.Linq;
using Godot;
using HoldTheLine.Rules.Ai;

namespace HoldTheLine.Game;

/// <summary>
/// The pool of decks a "random opponent" vs-AI match can draw from (docs/12 C3). v1: all four built-in
/// preconstructed decks are available at every tier. <c>Levels</c> is the extension seam — to give Easy
/// straightforward builds and Hard combo builds later, only this table changes.
/// </summary>
public static class AiDeckPool
{
    private static readonly (string DeckId, string Faction, AiLevel[] Levels)[] Pool =
    [
        ("iron_wall",          "iron_vow",   [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
        ("wildpack_hunt",      "wildpack",   [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
        ("duskweaver_vesper",  "duskweaver", [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
        ("undervault_sunline", "undervault", [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
    ];

    /// <summary>The faction of a built-in pool deck, or null if the id is not a pool deck.</summary>
    public static string? FactionOf(string deckId)
    {
        foreach (var p in Pool)
            if (p.DeckId == deckId)
                return p.Faction;
        return null;
    }

    /// <summary>A random built-in deck id eligible at <paramref name="level"/>. When <paramref name="excludeFaction"/>
    /// is given, a "random opponent" prefers a DIFFERENT faction than the player's (so picking 随机对手 no longer
    /// keeps mirroring your own faction ~1/4 of the time). Falls back gracefully: if excluding leaves nothing, the
    /// exclusion is dropped; if a tier were ever left with no eligible deck, the whole pool is used.</summary>
    public static string PickRandom(AiLevel level, string? excludeFaction = null)
    {
        var atTier = Pool.Where(p => p.Levels.Contains(level)).ToArray();
        if (atTier.Length == 0)
            atTier = Pool;

        var eligible = excludeFaction != null
            ? atTier.Where(p => p.Faction != excludeFaction).ToArray()
            : atTier;
        if (eligible.Length == 0) // player's faction was the only option at this tier — allow the mirror rather than fail
            eligible = atTier;

        return eligible[(int)(GD.Randi() % (uint)eligible.Length)].DeckId;
    }
}
