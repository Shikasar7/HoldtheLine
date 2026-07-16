namespace HoldTheLine.Rules.Cards;

/// <summary>
/// Core keywords (GDD §4, 2026-07-16 revision). JSON serialization uses snake_case names,
/// e.g. CheapShot → "cheap_shot".
/// </summary>
public enum Keyword
{
    /// <summary>冲锋 — may move and attack on the turn it is deployed.</summary>
    Charge,

    /// <summary>突袭 — may attack (but not move) on the turn it is deployed.</summary>
    Assault,

    /// <summary>疾行 N — movement per turn is N (Value) instead of 1.</summary>
    Swift,

    /// <summary>射程 N — attacks along its row/column up to N (Value) cells, no line blockers, no retaliation taken.</summary>
    Range,

    /// <summary>守护 — adjacent enemy units that attack must target an adjacent Guard.</summary>
    Guard,

    /// <summary>坚守 — takes 1 less damage while it has not moved since its owner's last turn start.</summary>
    HoldFast,

    /// <summary>践踏 — after destroying a unit with a melee attack, may occupy the vacated cell.</summary>
    Trample,

    /// <summary>驻防 — bonus while on its owner's home row. (Effect payload wired in P2.)</summary>
    Garrison,

    /// <summary>偷袭 — its melee attacks receive no retaliation.</summary>
    CheapShot,

    /// <summary>持盾 — ignores the first instance of damage it would take.</summary>
    Shield,

    /// <summary>伏兵 — untargetable until it deals damage; revealed by adjacent enemies. (Deferred; not implemented in P1.)</summary>
    Hidden,
}

/// <summary>A keyword plus its numeric parameter (used by Swift/Range; 0 for the rest).</summary>
public sealed record KeywordSpec(Keyword Keyword, int Value = 0);
