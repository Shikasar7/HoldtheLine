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
    private static readonly (string DeckId, AiLevel[] Levels)[] Pool =
    [
        ("iron_wall",          [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
        ("wildpack_hunt",      [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
        ("duskweaver_vesper",  [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
        ("undervault_sunline", [AiLevel.Easy, AiLevel.Normal, AiLevel.Hard]),
    ];

    /// <summary>A random built-in deck id eligible at <paramref name="level"/> (falls back to the whole pool
    /// if a tier were ever left without any eligible deck).</summary>
    public static string PickRandom(AiLevel level)
    {
        var eligible = Pool.Where(p => p.Levels.Contains(level)).Select(p => p.DeckId).ToArray();
        if (eligible.Length == 0)
            eligible = Pool.Select(p => p.DeckId).ToArray();
        return eligible[(int)(GD.Randi() % (uint)eligible.Length)];
    }
}
