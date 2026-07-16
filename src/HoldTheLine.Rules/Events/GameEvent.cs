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
[JsonDerivedType(typeof(UnitBuffedEvent), "unit_buffed")]
[JsonDerivedType(typeof(LeaderDamagedEvent), "leader_damaged")]
[JsonDerivedType(typeof(UnitDiedEvent), "unit_died")]
[JsonDerivedType(typeof(ManaGainedEvent), "mana_gained")]
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

/// <summary>Overdraw at the 10-card hand limit destroys the card. Publicly visible (as in Hearthstone).</summary>
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
    /// <summary>Damage actually applied after HoldFast/Shield. 0 when fully absorbed.</summary>
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
    public bool ShieldAbsorbed { get; init; }
}

public sealed record UnitBuffedEvent : GameEvent
{
    public required int UnitEntityId { get; init; }
    public required int AtkDelta { get; init; }
    public required int HpDelta { get; init; }
    public required int NewAtk { get; init; }
    public required int NewHp { get; init; }
}

public sealed record LeaderDamagedEvent : GameEvent
{
    public required int Seat { get; init; }
    public required int Amount { get; init; }
    public required int NewHp { get; init; }
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

public sealed record GameEndedEvent : GameEvent
{
    /// <summary>-1 for a draw.</summary>
    public required int WinnerSeat { get; init; }
    public required string Reason { get; init; }
}
