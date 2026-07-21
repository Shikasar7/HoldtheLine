using System.Text.Json.Serialization;
using HoldTheLine.Rules.Geometry;

namespace HoldTheLine.Rules.Commands;

/// <summary>
/// Player intent. Commands are the ONLY way anything mutates a match — UI, AI, and (later) the
/// network host all speak this shape, so every command must stay serializable and versioned.
/// FROZEN at switch point S1: adding a command type or field requires a Fable review.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PlayCardCommand), "play_card")]
[JsonDerivedType(typeof(MoveUnitCommand), "move_unit")]
[JsonDerivedType(typeof(AttackCommand), "attack")]
[JsonDerivedType(typeof(UseLeaderSkillCommand), "use_leader_skill")]
[JsonDerivedType(typeof(EndTurnCommand), "end_turn")]
[JsonDerivedType(typeof(ConcedeCommand), "concede")]
[JsonDerivedType(typeof(MulliganCommand), "mulligan")]
public abstract record Command
{
    public required int Seat { get; init; }
    public int SchemaVersion { get; init; } = 1;
}

public sealed record PlayCardCommand : Command
{
    public required int CardEntityId { get; init; }
    /// <summary>Deploy cell for units.</summary>
    public Cell? TargetCell { get; init; }
    /// <summary>Target for effects with target == "target_unit".</summary>
    public int? TargetUnitId { get; init; }
    /// <summary>引导 (docs/21 §1.2, Rules 0.9.0): the friendly minion channeling a 引导·N order — the
    /// range origin and the source of any channeler amplification/discount. Null for non-channel plays.
    /// Additive/nullable: pre-0.9.0 command logs omit it and deserialize to null, so replays are unchanged.</summary>
    public int? ChannelerUnitId { get; init; }
}

/// <summary>One orthogonal step. Swift N units issue N of these per turn.</summary>
public sealed record MoveUnitCommand : Command
{
    public required int UnitEntityId { get; init; }
    public required Cell To { get; init; }
}

public sealed record AttackCommand : Command
{
    public required int AttackerEntityId { get; init; }
    public int? TargetUnitId { get; init; }
    /// <summary>Attack the enemy leader instead of a unit (requires standing on the enemy home row).</summary>
    public bool TargetLeader { get; init; }
    /// <summary>Obsolete since 0.6.0 (践踏 is now melee splash, no cell-occupy choice). Kept so pre-0.6.0
    /// clients/logs still deserialize; the resolver ignores it.</summary>
    public bool OccupyCellOnKill { get; init; }
}

public sealed record UseLeaderSkillCommand : Command
{
    public int? TargetUnitId { get; init; }
    public Cell? TargetCell { get; init; }
}

public sealed record EndTurnCommand : Command;

public sealed record ConcedeCommand : Command;

/// <summary>起手重抽 (docs/11): swap out a chosen subset of the opening hand, once. Legal only during the
/// mulligan phase; bypasses the active-seat check (both seats act). An empty list = keep everything.</summary>
public sealed record MulliganCommand : Command
{
    /// <summary>EntityIds of the hand cards to replace; empty = keep all.</summary>
    public required IReadOnlyList<int> ReplacedEntityIds { get; init; }
}
