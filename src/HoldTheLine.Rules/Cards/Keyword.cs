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

    /// <summary>射程 N — attacks any cell within N (Value) orthogonal steps, over any body; takes retaliation
    /// only when the target can reach back (i.e. the attacker is inside the target's own range/adjacency).</summary>
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

    /// <summary>跃障 — may move to a straight-line distance-2 empty cell in one step, crossing an intervening unit.</summary>
    Leap,

    /// <summary>围猎 — its melee attacks deal +1 damage when another friendly unit is adjacent to the target.</summary>
    PackTactics,

    /// <summary>伏兵 — untargetable until it deals damage; revealed by adjacent enemies. (Deferred; not implemented in P1.)</summary>
    Hidden,

    /// <summary>架设 — cannot move (movement is rejected outright; Leap/move_bonus are silent no-ops); takes +1
    /// from EFFECT damage (orders/skills/battlecries — never attacks), because bolted-down guns cannot dodge
    /// a barrage. Deploys/summons normally.</summary>
    Emplacement,

    /// <summary>贯穿 — on a ranged attack aligned with the target (same row/col), the cell one step directly
    /// behind the target (away from the attacker) takes equal damage — friend or foe, no retaliation.</summary>
    Pierce,

    /// <summary>重新部署 — a transient permission (granted, never innate; end_of_turn) that lets an 架设
    /// (Emplacement) unit take one normal move this turn. Inert on a non-emplacement unit, which can already
    /// move. Lifts only the emplacement move-block; ordinary movement rules (one step, MovementPerTurn) apply,
    /// so a bolted-down gun repositions exactly one cell and is immovable again next turn (docs/10 §11).</summary>
    Mobilized,
}

/// <summary>A keyword plus its numeric parameter (used by Swift/Range; 0 for the rest).</summary>
public sealed record KeywordSpec(Keyword Keyword, int Value = 0);
