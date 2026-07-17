using HoldTheLine.Server.Data;
using HoldTheLine.Server.Rooms;

namespace HoldTheLine.Server;

/// <summary>
/// Resolves a deck id — as it appears in create_room / join_room / join_queue — to the card list and
/// leader a match needs (M3 B1). A player's own saved deck wins; a built-in preconstructed deck is the
/// fallback (so the starter decks still work). Unknown ids raise a <see cref="ProtocolError"/>.
/// </summary>
public sealed class DeckSource(DeckStore decks, GameContent content)
{
    public ResolvedDeck Resolve(string guestId, string deckId)
    {
        if (decks.Get(guestId, deckId) is { } saved)
            return new ResolvedDeck(saved.CardIds, saved.Leader);
        if (content.FindDeck(deckId) is { } built)
            return new ResolvedDeck(built.Expand(), built.Leader);
        throw new ProtocolError("unknown_deck", $"No deck '{deckId}'.");
    }
}
