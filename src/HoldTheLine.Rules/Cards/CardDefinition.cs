using System.Text.Json.Serialization;

namespace HoldTheLine.Rules.Cards;

public enum CardType
{
    Unit,
    /// <summary>指令卡 — one-shot effect.</summary>
    Order,
    /// <summary>战术卡(阵地)— reserved, not implemented in the prototype.</summary>
    Structure,
    /// <summary>装备卡 — reserved, not implemented in the prototype.</summary>
    Equipment,
}

public enum Rarity { Common, Rare, Epic, Legendary, Token }

/// <summary>
/// Static card data, loaded from JSON (game/data/cards). Adding a card must never require
/// engine changes — anything beyond keywords + EffectSpec combinations is out of prototype scope.
/// </summary>
public sealed record CardDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Faction { get; init; } = "neutral";
    public CardType Type { get; init; } = CardType.Unit;
    public Rarity Rarity { get; init; } = Rarity.Common;
    public int Cost { get; init; }
    public int Atk { get; init; }
    public int Hp { get; init; }
    public IReadOnlyList<KeywordSpec> Keywords { get; init; } = [];
    public IReadOnlyList<EffectSpec> Effects { get; init; } = [];
    /// <summary>成长 (docs/21 §1.8): if set, this unit transforms into another card after enough steps (雏凤→凤凰).</summary>
    public GrowthSpec? Growth { get; init; }
    /// <summary>模块规格 (docs/20 §2.1): set on 掘世匠会 Equipment cards — how this module contributes when installed
    /// on the 工造炮台. Null on every non-module card.</summary>
    public ModuleSpec? Module { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Prompt fragment for the AI-art pipeline (plan §9.4). Lives with the card so art is regenerable.</summary>
    public string ArtPrompt { get; init; } = "";

    public bool HasKeyword(Keyword k) => Keywords.Any(s => s.Keyword == k);

    public int KeywordValue(Keyword k) => Keywords.FirstOrDefault(s => s.Keyword == k)?.Value ?? 0;

    /// <summary>熔剑祭士 (docs/21 §3.2): this unit's battlecry offers the 2-order sacrifice, so the client must
    /// route the deploy through its sacrifice picker. Replaces the client's magic-string effect matching.</summary>
    public bool NeedsSacrificePicker => Effects.Any(e => e.Trigger == "battlecry" && e.Action == "sacrifice_equip");

    /// <summary>Whether playing this card deals effect damage on cast (a play/battlecry effect of the damage
    /// family) — drives the client's red "作用目标" targeting prompt. See <see cref="EffectSpec.DealsDamage"/>.</summary>
    public bool DealsDamageOnPlay => Effects.Any(e => e.Trigger is "play" or "battlecry" && e.DealsDamage);
}

/// <summary>成长规格 (docs/21 §1.8): after <see cref="Turns"/> growth steps the unit transforms into
/// <see cref="IntoCardId"/> at full stats with statuses cleared.</summary>
public sealed record GrowthSpec
{
    public required int Turns { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("into_card_id")]
    public required string IntoCardId { get; init; }
}

/// <summary>
/// 模块规格 (docs/20 §2.1): a 掘世匠会 Equipment card's contribution when installed on the 工造炮台. The turret
/// derives its whole panel from the union of its installed modules' specs (RecomputeTurret), so a module needs
/// no Effects/triggers. Numeric fields (Atk/Hp/Range/Move) stack across installed modules (镜像叠加);
/// GrantKeywords/OnHit/Lifesteal/ExtraAttacks/Immobile/Deathrattle are switches (镜像重复不叠加, §2.1 规则3).
/// </summary>
public sealed record ModuleSpec
{
    /// <summary>数值类 (可叠): 攻/上限血/射程/移速 加成.</summary>
    public int Atk { get; init; }
    public int Hp { get; init; }
    public int Range { get; init; }
    public int Move { get; init; }

    /// <summary>开关类关键词 (贯穿 …) granted to the turret while installed.</summary>
    [JsonPropertyName("grant_keywords")]
    public IReadOnlyList<Keyword> GrantKeywords { get; init; } = [];

    /// <summary>远程命中后触发: none | split(分裂) | frag(溅射Ⅰ 固定1) | blast(溅射Ⅱ ⌈atk/2⌉) | concussion(震撼弹迟缓).
    /// frag/blast 同系分级, 同装取最高 (S5b).</summary>
    [JsonPropertyName("on_hit")]
    public string OnHit { get; init; } = "none";

    /// <summary>吸血分级: none | fixed(吸血Ⅰ 固定回1) | half(吸血Ⅱ 回 ⌊atk/2⌋). 同装取最高 (S5b).</summary>
    public string Lifesteal { get; init; } = "none";

    /// <summary>快速装填机: +1 次每回合攻击 (开关; 镜像无增益, S9b).</summary>
    [JsonPropertyName("extra_attacks")]
    public int ExtraAttacks { get; init; }

    /// <summary>架设平台: 授予 架设(不能移动)+坚守. 炮台仍豁免架设的效果伤 +1 (S10).</summary>
    public bool Immobile { get; init; }

    /// <summary>模块亡语: none | failsafe_pod (自毁保险舱). Fires when the TURRET dies (docs/20 §S7).</summary>
    public string Deathrattle { get; init; } = "none";

    public static readonly IReadOnlySet<string> KnownOnHit = new HashSet<string> { "none", "split", "frag", "blast", "concussion" };
    public static readonly IReadOnlySet<string> KnownLifesteal = new HashSet<string> { "none", "fixed", "half" };
    public static readonly IReadOnlySet<string> KnownDeathrattle = new HashSet<string> { "none", "failsafe_pod" };
}
