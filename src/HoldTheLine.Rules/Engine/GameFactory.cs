using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Engine;

public static class GameFactory
{
    /// <summary>Creates the initial state and the opening event batch (shuffle, opening hands, coin, first turn start).</summary>
    public static (GameState State, IReadOnlyList<GameEvent> Events) CreateGame(MatchConfig config, CardDatabase db)
    {
        foreach (var id in config.Deck0.Concat(config.Deck1).Append(config.CoinCardId))
            if (id.Length > 0)
                _ = db.Get(id); // throws on unknown ids — fail at creation, not mid-match

        if (config.ValidateDecks)
        {
            if (DeckValidator.Validate(config.Deck0, db) is { } e0)
                throw new InvalidDataException($"Deck 0 invalid: {e0.Message}");
            if (DeckValidator.Validate(config.Deck1, db) is { } e1)
                throw new InvalidDataException($"Deck 1 invalid: {e1.Message}");
        }

        var state = new GameState
        {
            TurnNumber = 0,
            ActiveSeat = config.FirstSeat,
            Rng = new DeterministicRng(config.Seed),
            Players =
            [
                new PlayerState { Seat = 0, LeaderId = config.Leader0, LeaderHp = config.LeaderHp },
                new PlayerState { Seat = 1, LeaderId = config.Leader1, LeaderHp = config.LeaderHp },
            ],
        };

        var ctx = new ResolutionContext(state, db);
        ctx.Emit(new GameStartedEvent { FirstSeat = config.FirstSeat, LeaderHp = config.LeaderHp });

        BuildDeck(state, 0, config.Deck0);
        BuildDeck(state, 1, config.Deck1);

        int second = 1 - config.FirstSeat;
        ctx.DrawCards(config.FirstSeat, config.OpeningHandFirst);
        ctx.DrawCards(second, config.OpeningHandSecond);

        if (config.CoinCardId.Length > 0)
        {
            var coin = new CardInstance { EntityId = state.TakeEntityId(), CardId = config.CoinCardId };
            state.Player(second).Hand.Add(coin);
            ctx.Emit(new CardDrawnEvent { Seat = second, CardEntityId = coin.EntityId, CardId = coin.CardId });
        }

        TurnFlow.StartTurn(ctx, config.FirstSeat);
        return (state, ctx.Events);
    }

    private static void BuildDeck(GameState state, int seat, IReadOnlyList<string> cardIds)
    {
        var deck = state.Player(seat).Deck;
        foreach (var id in cardIds)
            deck.Add(new CardInstance { EntityId = state.TakeEntityId(), CardId = id });
        state.Rng.Shuffle(deck);
    }
}
