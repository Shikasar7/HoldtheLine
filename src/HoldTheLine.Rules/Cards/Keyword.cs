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

    /// <summary>嘲讽 — adjacent enemy units that attack must target an adjacent Taunt first. (Was named
    /// 守护/Guard through 0.7.x; renamed to its true role so 守护 could become the redirect keyword below.)</summary>
    Taunt,

    /// <summary>坚守 — takes 1 less damage while it has not moved since its owner's last turn start.</summary>
    HoldFast,

    /// <summary>践踏 — its melee attacks also deal the attacker's Atk to every unit adjacent to the
    /// target's cell (friend or foe; the attacker itself excepted; no retaliation from splash).</summary>
    Trample,

    /// <summary>驻防 — bonus while on its owner's home row. (Effect payload wired in P2.)</summary>
    Garrison,

    /// <summary>偷袭 — its melee attacks receive no retaliation.</summary>
    CheapShot,

    /// <summary>持盾 — ignores the first instance of damage it would take.</summary>
    Shield,

    /// <summary>跃障 — may move to a straight-line distance-2 empty cell in one step, crossing an intervening unit.</summary>
    Leap,

    /// <summary>围猎 — its melee attacks deal +2 damage when another friendly unit is adjacent to the target.</summary>
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

    /// <summary>福泽 — an aura: every FRIENDLY unit orthogonally adjacent to this one takes 1 less damage
    /// (stacks with 坚守; the 福泽 unit itself is not affected — only its neighbours). 0.8.0.</summary>
    Blessing,

    /// <summary>守护 — when a FRIENDLY unit orthogonally adjacent to this one would take damage, that damage is
    /// redirected here instead, resolved through THIS unit's own reductions (坚守/福泽/持盾). Only the original
    /// target redirects — redirected damage on the guardian is not re-redirected, so there is no loop. 0.8.0.</summary>
    Guardian,
}

/// <summary>A keyword plus its numeric parameter (used by Swift/Range; 0 for the rest).</summary>
public sealed record KeywordSpec(Keyword Keyword, int Value = 0);
