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
