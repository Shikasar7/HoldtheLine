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
            if (!EffectSpec.KnownTriggers.Contains(spec.Trigger))
                throw new InvalidDataException($"Card '{card.Id}': unknown trigger '{spec.Trigger}'.");
            if (!EffectSpec.KnownActions.Contains(spec.Action))
                throw new InvalidDataException($"Card '{card.Id}': unknown action '{spec.Action}'.");
            if (!EffectSpec.KnownTargets.Contains(spec.Target))
                throw new InvalidDataException($"Card '{card.Id}': unknown target '{spec.Target}'.");
            if (card.Type == CardType.Order && spec.Target is "self" or "adjacent_allies" or "adjacent_enemies")
                throw new InvalidDataException($"Order '{card.Id}': target '{spec.Target}' requires a source unit.");
            if (card.Type == CardType.Order && spec.Trigger != "play")
                throw new InvalidDataException($"Order '{card.Id}': orders only support the 'play' trigger.");
            if (card.Type == CardType.Unit && spec.Trigger == "play")
                throw new InvalidDataException($"Unit '{card.Id}': units use 'battlecry'/'deathrattle', not 'play'.");
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
