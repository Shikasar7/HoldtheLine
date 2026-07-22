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
        {
            // docs/20 §6-U6 迁移: a rework can REMOVE cards (掘世匠会 砍了 13+ 张), so a player's saved deck may
            // reference ids the current data no longer knows. Reject it up front with a fixable message + the
            // missing count, instead of letting an unknown id crash game creation deeper in. 不自动替换成等价卡.
            var removed = saved.CardIds.Where(id => !content.Cards.TryGet(id, out _)).Distinct(StringComparer.Ordinal).ToList();
            if (removed.Count > 0)
                throw new ProtocolError("deck_needs_repair",
                    $"卡组「{saved.Name}」含 {removed.Count} 张已移除的卡牌,请在牌组编辑器中修复后再对战。");
            return new ResolvedDeck(saved.CardIds, saved.Leader);
        }
        if (content.FindDeck(deckId) is { } built)
            return new ResolvedDeck(built.Expand(), built.Leader);
        throw new ProtocolError("unknown_deck", $"No deck '{deckId}'.");
    }
}
