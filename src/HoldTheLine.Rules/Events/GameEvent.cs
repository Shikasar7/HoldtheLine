using System.Text.Json.Serialization;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Events;

/// <summary>
/// Atomic facts produced by resolution. The presentation layer consumes ONLY these (plus
/// PlayerView snapshots) — it never reads GameState. Every event supports per-seat redaction so
/// hidden information physically cannot reach the wrong client.
/// FROZEN at switch point S1: adding an event type or field requires a Fable review.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(GameStartedEvent), "game_started")]
[JsonDerivedType(typeof(TurnStartedEvent), "turn_started")]
[JsonDerivedType(typeof(TurnEndedEvent), "turn_ended")]
[JsonDerivedType(typeof(CardDrawnEvent), "card_drawn")]
[JsonDerivedType(typeof(CardBurnedEvent), "card_burned")]
[JsonDerivedType(typeof(FatigueEvent), "fatigue")]
[JsonDerivedType(typeof(CardPlayedEvent), "card_played")]
[JsonDerivedType(typeof(UnitDeployedEvent), "unit_deployed")]
[JsonDerivedType(typeof(UnitMovedEvent), "unit_moved")]
[JsonDerivedType(typeof(AttackedEvent), "attacked")]
[JsonDerivedType(typeof(UnitDamagedEvent), "unit_damaged")]
[JsonDerivedType(typeof(UnitHealedEvent), "unit_healed")]
[JsonDerivedType(typeof(UnitBuffedEvent), "unit_buffed")]
[JsonDerivedType(typeof(UnitKeywordGrantedEvent), "unit_keyword_granted")]
[JsonDerivedType(typeof(UnitMoveBonusEvent), "unit_move_bonus")]
[JsonDerivedType(typeof(LeaderDamagedEvent), "leader_damaged")]
[JsonDerivedType(typeof(PressureTideEvent), "pressure_tide")]
[JsonDerivedType(typeof(LeaderSkillUsedEvent), "leader_skill_used")]
[JsonDerivedType(typeof(UnitDiedEvent), "unit_died")]
[JsonDerivedType(typeof(ManaGainedEvent), "mana_gained")]
[JsonDerivedType(typeof(MulliganResolvedEvent), "mulligan_resolved")]
[JsonDerivedType(typeof(MulliganCompletedEvent), "mulligan_completed")]
[JsonDerivedType(typeof(GameEndedEvent), "game_ended")]
public abstract record GameEvent
{
    /// <summary>Monotonic per-match sequence, assigned by the resolver.</summary>
    public int Sequence { get; set; }

    /// <summary>Returns the version of this event a given seat is allowed to see.</summary>
    public virtual GameEvent RedactFor(int viewerSeat) => this;
}

public sealed record GameStartedEvent : GameEvent
{
    public required int FirstSeat { get; init; }
    public required int LeaderHp { get; init; }
}

public sealed record TurnStartedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int TurnNumber { get; init; }
    public required int Mana { get; init; }
    public required int ManaMax { get; init; }
}

public sealed record TurnEndedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int TurnNumber { get; init; }
}

public sealed record CardDrawnEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int CardEntityId { get; init; }
    /// <summary>Null when redacted for the opponent.</summary>
    public string? CardId { get; init; }

    public override GameEvent RedactFor(int viewerSeat) =>
        viewerSeat == Seat ? this : this with { CardId = null };
}

/// <summary>A card could not enter a full (9-card) hand. Since 0.7.0 it goes to the owner's graveyard
/// rather than leaving the game; this event still reports the "couldn't hold it" beat. Publicly visible.</summary>
public sealed record CardBurnedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required string CardId { get; init; }
}

public sealed record FatigueEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Amount { get; init; }
}

public sealed record CardPlayedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int CardEntityId { get; init; }
    public required string CardId { get; init; }
    public required int ManaSpent { get; init; }
}

public sealed record UnitDeployedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int UnitEntityId { get; init; }
    public required string CardId { get; init; }
    public required Cell Cell { get; init; }
    public required int Atk { get; init; }
    public required int Hp { get; init; }
}

public sealed record UnitMovedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required Cell From { get; init; }
    public required Cell To { get; init; }
}

public sealed record AttackedEvent : GameEvent
{
    public required int AttackerEntityId { get; init; }
    public int? TargetUnitId { get; init; }
    /// <summary>Set (to the defending seat) when the target was a leader.</summary>
    public int? TargetLeaderSeat { get; init; }
}

public sealed record UnitDamagedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    /// <summary>Damage actually applied after HoldFast/福泽/Shield. 0 when fully absorbed.</summary>
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
    public bool ShieldAbsorbed { get; init; }
    /// <summary>守护 (Guardian, 0.8.0): this damage event is part of a redirect. On the spared original target it
    /// carries Amount 0; on the guardian that soaked it, the actual amount. Lets the client tag both "守护-N".</summary>
    public bool GuardRedirect { get; init; }
}

public sealed record UnitHealedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    /// <summary>Health actually restored (0 if already full).</summary>
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
}

public sealed record UnitBuffedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required int AtkDelta { get; init; }
    public required int HpDelta { get; init; }
    public required int NewAtk { get; init; }
    public required int NewHp { get; init; }
    /// <summary>True when this delta is the 驻防 (Garrison) bonus toggling on/off, not a card buff.</summary>
    public bool IsGarrison { get; init; }
}

public sealed record UnitKeywordGrantedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required Cards.Keyword Keyword { get; init; }
    public int Value { get; init; }
    /// <summary>permanent | end_of_turn | your_next_turn.</summary>
    public required string Duration { get; init; }
}

public sealed record UnitMoveBonusEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required int Amount { get; init; }
    public required int NewBonusMovement { get; init; }
}

public sealed record LeaderSkillUsedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required string LeaderId { get; init; }
    public int? TargetUnitId { get; init; }
}

public sealed record LeaderDamagedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
}

/// <summary>
/// 压力潮汐 (GDD §2.7, anti-turtle revision): from round 8, a seat that starts its turn with no
/// unit in the ENEMY half takes escalating leader damage. The follow-up LeaderDamagedEvent
/// carries the HP change; this event exists so clients can present the tide distinctly.
/// </summary>
public sealed record PressureTideEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Round { get; init; }
    public required int Amount { get; init; }
}

public sealed record UnitDiedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required string CardId { get; init; }
    public required Cell Cell { get; init; }
}

public sealed record ManaGainedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Amount { get; init; }
    public required int NewMana { get; init; }
}

/// <summary>
/// One seat resolved its 起手重抽 (docs/11): the replaced cards left hand and their replacements were drawn
/// (as normal <see cref="CardDrawnEvent"/>s). The opponent sees only <see cref="ReplacedCount"/> — which
/// cards were swapped stays hidden.
/// </summary>
public sealed record MulliganResolvedEvent : GameEvent
{
    public required int Seat { get; init; }
    /// <summary>EntityIds of the swapped-out cards; null when redacted for the opponent.</summary>
    public IReadOnlyList<int>? ReplacedEntityIds { get; init; }
    /// <summary>Public (D8): how many cards this seat swapped.</summary>
    public required int ReplacedCount { get; init; }

    public override GameEvent RedactFor(int viewerSeat) =>
        viewerSeat == Seat ? this : this with { ReplacedEntityIds = null };
}

/// <summary>Both seats finished their mulligan; the coin (if any) and the first turn follow. No payload.</summary>
public sealed record MulliganCompletedEvent : GameEvent;

public sealed record GameEndedEvent : GameEvent
{
    /// <summary>-1 for a draw.</summary>
    public required int WinnerSeat { get; init; }
    public required string Reason { get; init; }
}
