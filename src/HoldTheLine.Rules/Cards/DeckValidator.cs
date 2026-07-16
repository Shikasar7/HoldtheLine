using HoldTheLine.Rules.Engine;

namespace HoldTheLine.Rules.Cards;

/// <summary>Constructed-deck rules (GDD §3.1, revised): 30 cards; copy caps 4/3/2/1 by rarity.</summary>
public static class DeckValidator
{
    public const int DeckSize = 30;

    public static int MaxCopies(Rarity rarity) => rarity switch
    {
        Rarity.Common => 4,
        Rarity.Rare => 3,
        Rarity.Epic => 2,
        Rarity.Legendary => 1,
        _ => 0, // tokens (e.g. the coin) can never be deck-built
    };

    /// <summary>Returns null when the deck is legal, otherwise the first violation found.</summary>
    public static RuleError? Validate(IReadOnlyList<string> deck, CardDatabase db)
    {
        if (deck.Count != DeckSize)
            return new RuleError(RuleErrorCode.InvalidDeck, $"Deck must contain {DeckSize} cards, found {deck.Count}.");

        foreach (var group in deck.GroupBy(id => id, StringComparer.Ordinal))
        {
            if (!db.TryGet(group.Key, out var def))
                return new RuleError(RuleErrorCode.InvalidDeck, $"Unknown card id '{group.Key}'.");
            int cap = MaxCopies(def.Rarity);
            if (group.Count() > cap)
                return new RuleError(RuleErrorCode.InvalidDeck,
                    $"Card '{group.Key}' ({def.Rarity}) appears {group.Count()} times; the cap is {cap}.");
        }
        return null;
    }
}
