using System.Text.Json;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Rules.Cards;

/// <summary>
/// Immutable card registry. Validates all data at construction — a bad card file fails loudly
/// at startup, never mid-match.
/// </summary>
public sealed class CardDatabase
{
    private readonly Dictionary<string, CardDefinition> _cards;

    public CardDatabase(IEnumerable<CardDefinition> cards)
    {
        _cards = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            if (!_cards.TryAdd(card.Id, card))
                throw new InvalidDataException($"Duplicate card id '{card.Id}'.");
            Validate(card);
        }
        // Cross-references (e.g. summon target ids) can only be checked once every card is loaded.
        foreach (var card in _cards.Values)
            foreach (var spec in card.Effects)
                if (spec.Action == "summon" && (spec.SummonCardId is null || !_cards.ContainsKey(spec.SummonCardId)))
                    throw new InvalidDataException($"Card '{card.Id}': summon references unknown card '{spec.SummonCardId}'.");
    }

    public IReadOnlyCollection<CardDefinition> All => _cards.Values;

    public CardDefinition Get(string id) =>
        _cards.TryGetValue(id, out var def)
            ? def
            : throw new KeyNotFoundException($"Unknown card id '{id}'.");

    public bool TryGet(string id, out CardDefinition def) => _cards.TryGetValue(id, out def!);

    /// <summary>Parses a JSON array of card definitions.</summary>
    public static IReadOnlyList<CardDefinition> ParseJson(string json) =>
        JsonSerializer.Deserialize<List<CardDefinition>>(json, RulesJson.Options)
            ?? throw new InvalidDataException("Card JSON deserialized to null.");

    /// <summary>Loads every *.json file in a directory (each file: a JSON array of cards).</summary>
    public static CardDatabase LoadFromDirectory(string directory)
    {
        var cards = new List<CardDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            cards.AddRange(ParseJson(File.ReadAllText(file)));
        return new CardDatabase(cards);
    }

    private static void Validate(CardDefinition card)
    {
        if (string.IsNullOrWhiteSpace(card.Name))
            throw new InvalidDataException($"Card '{card.Id}' has no name.");
        if (card.Cost < 0 || card.Cost > 10)
            throw new InvalidDataException($"Card '{card.Id}' has invalid cost {card.Cost}.");
        if (card.Type == CardType.Unit && card.Hp <= 0)
            throw new InvalidDataException($"Unit '{card.Id}' must have Hp > 0.");
        if (card.Type is CardType.Structure or CardType.Equipment)
            throw new InvalidDataException($"Card '{card.Id}': type {card.Type} is reserved and not implemented in the prototype.");

        foreach (var spec in card.Effects)
        {
            if (spec.Trigger == "leader_skill")
                throw new InvalidDataException($"Card '{card.Id}': 'leader_skill' is for leaders, not cards.");
            if (!EffectSpec.KnownTriggers.Contains(spec.Trigger))
                throw new InvalidDataException($"Card '{card.Id}': unknown trigger '{spec.Trigger}'.");
            if (!EffectSpec.KnownActions.Contains(spec.Action))
                throw new InvalidDataException($"Card '{card.Id}': unknown action '{spec.Action}'.");
            if (!EffectSpec.KnownTargets.Contains(spec.Target))
                throw new InvalidDataException($"Card '{card.Id}': unknown target '{spec.Target}'.");
            if (!EffectSpec.KnownDurations.Contains(spec.Duration))
                throw new InvalidDataException($"Card '{card.Id}': unknown duration '{spec.Duration}'.");
            if (card.Type == CardType.Order && spec.Target is "self" or "adjacent_allies" or "adjacent_enemies")
                throw new InvalidDataException($"Order '{card.Id}': target '{spec.Target}' requires a source unit.");
            if (card.Type == CardType.Order && spec.Trigger != "play")
                throw new InvalidDataException($"Order '{card.Id}': orders only support the 'play' trigger.");
            if (card.Type == CardType.Unit && spec.Trigger == "play")
                throw new InvalidDataException($"Unit '{card.Id}': units use 'battlecry'/'deathrattle'/'ally_order_played'/'self_moved', not 'play'.");
            if (card.Type == CardType.Order && spec.Trigger == "self_moved")
                throw new InvalidDataException($"Order '{card.Id}': 'self_moved' is a unit trigger (orders don't move).");
            // Reactive triggers fire from a source unit with no secondary target prompt (docs/06 §3.1, docs/10 §6#1).
            if (spec.Trigger is "ally_order_played" or "self_moved" && !EffectSpec.OnCastTargets.Contains(spec.Target))
                throw new InvalidDataException(
                    $"Card '{card.Id}': {spec.Trigger} target must be self/adjacent_allies/adjacent_enemies, got '{spec.Target}'.");

            if (spec.Action == "grant_keyword")
            {
                if (spec.GrantKeyword is null)
                    throw new InvalidDataException($"Card '{card.Id}': grant_keyword needs a 'keyword'.");
                if (spec.GrantKeyword == Keyword.Hidden)
                    throw new InvalidDataException($"Card '{card.Id}': granting Hidden (伏兵) is deferred.");
                if (spec.GrantKeyword is Keyword.Swift or Keyword.Range && spec.GrantKeywordValue < 1)
                    throw new InvalidDataException($"Card '{card.Id}': granting {spec.GrantKeyword} needs keyword_value >= 1.");
            }
            if (spec.Action == "summon" && spec.Amount < 1)
                throw new InvalidDataException($"Card '{card.Id}': summon needs amount >= 1.");
            if (spec.AmountMax != 0 && (spec.Action is not ("damage" or "sear") || spec.AmountMax <= spec.Amount))
                throw new InvalidDataException($"Card '{card.Id}': amount_max is for damage/sear and must exceed amount.");

            // --- 伤害类型 & 锚点/引导 (docs/21 §1.1–1.2, Rules 0.9.0) ---
            if (!EffectSpec.KnownSchools.Contains(spec.School))
                throw new InvalidDataException($"Card '{card.Id}': unknown school '{spec.School}'.");
            if (!EffectSpec.KnownAnchors.Contains(spec.Anchor))
                throw new InvalidDataException($"Card '{card.Id}': unknown anchor '{spec.Anchor}'.");
            if (spec.AnchorRange < 0)
                throw new InvalidDataException($"Card '{card.Id}': anchor_range cannot be negative.");
            if (spec.IsSelfAnchor)
            {
                if (spec.Trigger != "battlecry")
                    throw new InvalidDataException($"Card '{card.Id}': 锚 (self anchor) is a battlecry rule, got trigger '{spec.Trigger}'.");
                if (!spec.NeedsUnitTarget || spec.AnchorRange < 1)
                    throw new InvalidDataException($"Card '{card.Id}': 锚·N needs a unit target and anchor_range >= 1.");
            }
            if (spec.IsChannel && spec.Trigger != "play")
                throw new InvalidDataException($"Card '{card.Id}': 引导 (channel) is an order rule, got trigger '{spec.Trigger}'.");
        }

        foreach (var kw in card.Keywords)
        {
            if (kw.Keyword is Keyword.Swift or Keyword.Range && kw.Value < 1)
                throw new InvalidDataException($"Card '{card.Id}': keyword {kw.Keyword} requires a value >= 1.");
            if (kw.Keyword == Keyword.Hidden)
                throw new InvalidDataException($"Card '{card.Id}': Hidden (伏兵) is deferred and not implemented in P1.");
        }
    }
}
