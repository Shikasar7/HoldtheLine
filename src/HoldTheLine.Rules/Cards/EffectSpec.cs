namespace HoldTheLine.Rules.Cards;

/// <summary>
/// Data-driven effect primitive (plan §4.3). Triggers/actions/targets are validated at load time
/// by <see cref="CardDatabase"/>; unknown values are a data error, never a silent no-op.
/// P1 implements the minimal set below; P2 extends actions/targets without changing this shape.
/// The shape is frozen at switch point S1 — changes require a Fable review.
/// </summary>
public sealed record EffectSpec
{
    /// <summary>battlecry | deathrattle (units), play (orders).</summary>
    public required string Trigger { get; init; }

    /// <summary>damage | buff | draw | gain_mana.</summary>
    public required string Action { get; init; }

    /// <summary>self | target_unit | adjacent_allies | adjacent_enemies | none.</summary>
    public string Target { get; init; } = "none";

    /// <summary>Generic magnitude: damage amount, cards drawn, mana gained.</summary>
    public int Amount { get; init; }

    /// <summary>Buff deltas (action == buff).</summary>
    public int Atk { get; init; }
    public int Hp { get; init; }

    public static readonly IReadOnlySet<string> KnownTriggers = new HashSet<string> { "battlecry", "deathrattle", "play" };
    public static readonly IReadOnlySet<string> KnownActions = new HashSet<string> { "damage", "buff", "draw", "gain_mana" };
    public static readonly IReadOnlySet<string> KnownTargets = new HashSet<string> { "self", "target_unit", "adjacent_allies", "adjacent_enemies", "none" };
}
