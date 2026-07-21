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

    /// <summary>Optional upper bound for a random magnitude (damage/sear only). When > <see cref="Amount"/>,
    /// the effect rolls once on the match Rng in [Amount, AmountMax] — replay-deterministic (灼痕烙印).</summary>
    [JsonPropertyName("amount_max")]
    public int AmountMax { get; init; }

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

    /// <summary>秘密种类 (docs/21 §1.7, action == add_secret): counter_order (焰誓反制). The reactive payload
    /// (e.g. 3 <see cref="School"/> damage) is read back off this same effect when the secret fires.</summary>
    [JsonPropertyName("secret_kind")]
    public string? SecretKind { get; init; }

    /// <summary>Damage school (docs/21 §1.1): "physical" (default) or "spell.kindle" (法术·薪炎). The
    /// segment before the first dot is the broad class ("spell"); the whole string is the fine grain.
    /// Immunity checks the exact value; 加深/蓄能/引导 amplification filters by the "spell" prefix so
    /// future spell schools inherit the same interfaces. Ignored by non-damage actions.</summary>
    public string School { get; init; } = "physical";

    /// <summary>Directed-damage positioning (docs/21 §1.2). none (default) = no positioning gate;
    /// self = 锚·N, the battlecry's unit target must sit within <see cref="AnchorRange"/> Manhattan steps
    /// of the deploy cell; channel = 引导·N, the order first picks a friendly channeler (carried on
    /// <see cref="Commands.PlayCardCommand.ChannelerUnitId"/>) and any directed target must sit within
    /// <see cref="AnchorRange"/> of THAT unit. A non-directional channel effect (燔火/燎原 — no unit/cell
    /// target) has no range gate; the channeler must merely exist (for amplification/discount).</summary>
    public string Anchor { get; init; } = "none";

    /// <summary>Manhattan reach for a self/channel anchor. 0 = no range gate (non-directional channel).</summary>
    [JsonPropertyName("anchor_range")]
    public int AnchorRange { get; init; }

    /// <summary>焚世巨灵 (docs/21 §3.1): an ally_order_played effect fires only when the played order's printed
    /// cost is at least this (0 = no gate). Parameterises the "4费以上指令牌" condition.</summary>
    [JsonPropertyName("min_order_cost")]
    public int MinOrderCost { get; init; }

    /// <summary>Exempts a self-growth ally_order_played effect from the 每回合 2 次 cap (docs/21 §1.9) —
    /// 奥菲兰's 永焰不熄. Default false: 灰烬侍徒/烬眼先知/烬火唱徒 are capped.</summary>
    public bool Uncapped { get; init; }

    /// <summary>Fires this effect only when the (unit) target sits on a given side (docs/21 §1.8 双模式):
    /// any (default) | enemy | ally. Lets 焰鞭 carry both its enemy-damage and friendly-transfer effects on
    /// one card, each self-selecting by which unit was chosen.</summary>
    [JsonPropertyName("target_side")]
    public string TargetSide { get; init; } = "any";

    /// <summary>channel (docs/21 §1.2): a passive marker read when this unit is chosen as a 引导者 — never
    /// executed by RunTrigger. Its action (deepen/discount) defines the unit's 引导者差异化 bonus.</summary>
    public static readonly IReadOnlySet<string> KnownTriggers = new HashSet<string>
        { "battlecry", "deathrattle", "play", "leader_skill", "ally_order_played", "self_moved", "channel", "ally_died_your_turn",
          // docs/21 §3.1: 薪火回响 (门德) — a passive marker read when you play your first 薪炎 damage order each turn.
          "first_kindle_order_each_turn" };

    public static readonly IReadOnlySet<string> KnownActions = new HashSet<string>
        { "damage", "sear", "buff", "draw", "gain_mana", "heal", "grant_keyword", "boost_range", "summon", "move_bonus", "destroy", "recall_order",
          // docs/21 §1.3: 蓄能 (executable) + the two passive 引导者 markers read by the amplify pipeline.
          "amplify_next", "deepen", "discount",
          // docs/21 §3.1: 燔火's scatter missiles (Amount = missile count, each 1 薪炎; 加深/蓄能 add missiles).
          "damage_scatter",
          // docs/21 §1.6/§1.7: place a 烟幕区 (烟幕弹) or a hidden 烬火陷阱 on the target cell.
          "place_smoke", "place_trap",
          // docs/21 §1.7: set a face-down reactive secret in your 秘密区 (焰誓反制).
          "add_secret",
          // docs/21 §1.8: destroy the primary (ally) target and add its current atk/hp to the 二段目标 (焰鞭).
          "stat_transfer",
          // docs/21 §3.2: 熔剑祭士 battlecry marker — sacrifice 2 hand orders to equip the 熔岩巨剑 (resolver-driven).
          "sacrifice_equip",
          // docs/21 §3.1: 薪火回响 (门德) passive marker — resolver-driven, never executed by RunTrigger.
          "echo_order" };

    public static readonly IReadOnlySet<string> KnownTargetSides = new HashSet<string> { "any", "enemy", "ally" };

    /// <summary>docs/21 §1.7: the reactive secret kinds. counter_order = 焰誓反制.</summary>
    public static readonly IReadOnlySet<string> KnownSecretKinds = new HashSet<string> { "counter_order" };

    public static readonly IReadOnlySet<string> KnownTargets = new HashSet<string>
        { "none", "self", "target_unit", "target_unit_own_half", "target_unit_ally",
          "adjacent_allies", "adjacent_enemies",
          "column_enemies", "row_enemies", "column_allies", "cell_cross_all", "unit_cross_all",
          "allies_home_row", "all_allies", "all_ally_emplacements", "all_enemies",
          // docs/21 §1.6: a single chosen cell carried by the command (place_smoke/place_trap read it, no units).
          "cell" };

    public static readonly IReadOnlySet<string> KnownDurations = new HashSet<string>
        { "permanent", "end_of_turn", "your_next_turn" };

    /// <summary>Damage schools (docs/21 §1.1). "spell.kindle" is the only spell school this patch ships;
    /// the dotted grammar keeps room for "spell.ash" etc. without touching the immunity/amplify plumbing.</summary>
    public static readonly IReadOnlySet<string> KnownSchools = new HashSet<string>
        { "physical", "spell.kindle" };

    public static readonly IReadOnlySet<string> KnownAnchors = new HashSet<string>
        { "none", "self", "channel" };

    /// <summary>Targets the reactive triggers (ally_order_played, self_moved) may use — they fire without a
    /// player prompt, so their targeting must be implicit: either around the source unit (self/adjacent_*)
    /// or targetless (none, e.g. a recall/draw/summon that reads ownerSeat). docs/06 §3.1, docs/10 §6#1.</summary>
    public static readonly IReadOnlySet<string> OnCastTargets = new HashSet<string>
        { "none", "self", "adjacent_allies", "adjacent_enemies",
          // docs/21 §3.1: 焚世巨灵's ally_order_played AoE — implicit (all enemy minions), so no player prompt.
          "all_enemies" };

    /// <summary>Targets the caller must supply an explicit unit for.</summary>
    public bool NeedsUnitTarget => Target is "target_unit" or "target_unit_own_half" or "target_unit_ally" or "unit_cross_all";

    /// <summary>Targets that read the command's target cell (spatial selectors + a bare chosen cell).</summary>
    public bool NeedsCellTarget => Target is "column_enemies" or "row_enemies" or "column_allies" or "cell_cross_all" or "cell";

    /// <summary>锚·N: this effect anchors on the source unit's own cell (a battlecry).</summary>
    public bool IsSelfAnchor => Anchor == "self";

    /// <summary>引导·N: this effect anchors on a chosen friendly channeler (an order).</summary>
    public bool IsChannel => Anchor == "channel";

    /// <summary>Whether this anchored effect actually gates a target by range — a self/channel effect that
    /// picks a unit or cell (非指向 effects like a raw AoE/draw ride along without a range check).</summary>
    public bool HasAnchorRange => Anchor is "self" or "channel" && AnchorRange > 0 && (NeedsUnitTarget || NeedsCellTarget);

    /// <summary>薪炎 (spell.*) damage — the effects 加深/蓄能/引导 amplify and 免疫薪炎 negates (docs/21 §1.1).
    /// Includes 燔火's damage_scatter, whose Amount is a missile count, so the amplification adds missiles.</summary>
    public bool IsSpellDamage => Action is "damage" or "sear" or "damage_scatter" && School.StartsWith("spell", StringComparison.Ordinal);
}
