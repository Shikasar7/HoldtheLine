using System.Text.Json.Serialization;

namespace HoldTheLine.Rules.Cards;

/// <summary>
/// Data-driven effect primitive (plan §4.3). Triggers/actions/targets are validated at load time
/// by <see cref="CardDatabase"/>; unknown values are a data error, never a silent no-op.
/// P2 extends the action/target/duration vocabularies per the card-spec §5 build list. The one
/// shape change Fable pre-approved: the optional <see cref="Duration"/> field (docs/04 §5).
/// </summary>
public sealed record EffectSpec
{
    /// <summary>battlecry | deathrattle | ally_order_played | self_moved (units), play (orders), leader_skill (leaders).</summary>
    public required string Trigger { get; init; }

    /// <summary>damage | sear | buff | draw | gain_mana | heal | grant_keyword | summon | move_bonus | destroy | recall_order.</summary>
    public required string Action { get; init; }

    /// <summary>See <see cref="KnownTargets"/>. Spatial selectors (column_enemies) read the command's target cell.</summary>
    public string Target { get; init; } = "none";

    /// <summary>Generic magnitude: damage amount, cards drawn, mana gained, healing, summon count, movement bonus.</summary>
    public int Amount { get; init; }

    /// <summary>Buff deltas (action == buff).</summary>
    public int Atk { get; init; }
    public int Hp { get; init; }

    /// <summary>How long a grant_keyword / (future) temp buff lasts. permanent | end_of_turn | your_next_turn.</summary>
    public string Duration { get; init; } = "permanent";

    /// <summary>Which keyword to grant (action == grant_keyword). JSON key "keyword", snake_case value.</summary>
    [JsonPropertyName("keyword")]
    public Keyword? GrantKeyword { get; init; }

    /// <summary>Value for a granted Swift/Range keyword.</summary>
    [JsonPropertyName("keyword_value")]
    public int GrantKeywordValue { get; init; }

    /// <summary>Card id to summon (action == summon); <see cref="Amount"/> is the count.</summary>
    [JsonPropertyName("summon_card_id")]
    public string? SummonCardId { get; init; }

    public static readonly IReadOnlySet<string> KnownTriggers = new HashSet<string>
        { "battlecry", "deathrattle", "play", "leader_skill", "ally_order_played", "self_moved" };

    public static readonly IReadOnlySet<string> KnownActions = new HashSet<string>
        { "damage", "sear", "buff", "draw", "gain_mana", "heal", "grant_keyword", "boost_range", "summon", "move_bonus", "destroy", "recall_order" };

    public static readonly IReadOnlySet<string> KnownTargets = new HashSet<string>
        { "none", "self", "target_unit", "target_unit_own_half", "target_unit_ally",
          "adjacent_allies", "adjacent_enemies",
          "column_enemies", "row_enemies", "column_allies", "cell_cross_all", "unit_cross_all",
          "allies_home_row", "all_allies", "all_ally_emplacements" };

    public static readonly IReadOnlySet<string> KnownDurations = new HashSet<string>
        { "permanent", "end_of_turn", "your_next_turn" };

    /// <summary>Targets the reactive triggers (ally_order_played, self_moved) may use — they fire without a
    /// player prompt, so their targeting must be implicit: either around the source unit (self/adjacent_*)
    /// or targetless (none, e.g. a recall/draw/summon that reads ownerSeat). docs/06 §3.1, docs/10 §6#1.</summary>
    public static readonly IReadOnlySet<string> OnCastTargets = new HashSet<string>
        { "none", "self", "adjacent_allies", "adjacent_enemies" };

    /// <summary>Targets the caller must supply an explicit unit for.</summary>
    public bool NeedsUnitTarget => Target is "target_unit" or "target_unit_own_half" or "target_unit_ally" or "unit_cross_all";

    /// <summary>Targets that read the command's target cell (spatial selectors).</summary>
    public bool NeedsCellTarget => Target is "column_enemies" or "row_enemies" or "column_allies" or "cell_cross_all";
}
