using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class TurnFlowTests
{
    private static GameState EndTurns(GameState state, int count)
    {
        var resolver = TestKit.NewResolver();
        for (int i = 0; i < count; i++)
        {
            var result = resolver.Execute(state, new EndTurnCommand { Seat = state.ActiveSeat });
            Assert.True(result.Success, result.Error?.Message);
            state = result.State!;
        }
        return state;
    }

    [Fact]
    public void End_turn_alternates_seats_and_increments_turn_number()
    {
        var state = TestKit.NewGame();
        state = EndTurns(state, 1);
        Assert.Equal(1, state.ActiveSeat);
        Assert.Equal(2, state.TurnNumber);
        state = EndTurns(state, 1);
        Assert.Equal(0, state.ActiveSeat);
        Assert.Equal(3, state.TurnNumber);
    }

    [Fact]
    public void Mana_ramps_by_one_per_own_turn_and_caps_at_10()
    {
        var state = TestKit.NewGame(deck0: Enumerable.Repeat("t_vanilla", 30).ToList(),
                                    deck1: Enumerable.Repeat("t_vanilla", 30).ToList());
        state = EndTurns(state, 2);
        Assert.Equal(2, state.Player(0).ManaMax);
        state = EndTurns(state, 24);
        Assert.Equal(10, state.Player(0).ManaMax);
        Assert.Equal(10, state.Player(1).ManaMax);
    }

    [Fact]
    public void Acting_out_of_turn_is_rejected()
    {
        var state = TestKit.NewGame();
        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 1 });
        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.NotYourTurn, result.Error!.Code);
    }

    [Fact]
    public void Empty_deck_draw_deals_escalating_fatigue()
    {
        var state = TestKit.NewGame();
        state.Player(0).Deck.Clear();
        state = EndTurns(state, 2); // back to seat 0, who must draw
        Assert.Equal(1, state.Player(0).Fatigue);
        Assert.Equal(24, state.Player(0).LeaderHp);
        state = EndTurns(state, 2);
        Assert.Equal(2, state.Player(0).Fatigue);
        Assert.Equal(22, state.Player(0).LeaderHp);
    }

    [Fact]
    public void Overdraw_at_10_cards_burns_the_card()
    {
        var state = TestKit.NewGame();
        while (state.Player(1).Hand.Count < 10)
            TestKit.GiveCard(state, 1, "t_vanilla");

        int deckBefore = state.Player(1).Deck.Count;
        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 0 });
        Assert.True(result.Success);

        Assert.Contains(result.Events, e => e is CardBurnedEvent { Seat: 1 });
        Assert.Equal(10, result.State!.Player(1).Hand.Count);
        Assert.Equal(deckBefore - 1, result.State.Player(1).Deck.Count);
    }

    [Fact]
    public void Commands_after_game_end_are_rejected()
    {
        var state = TestKit.NewGame();
        var resolver = TestKit.NewResolver();
        var conceded = resolver.Execute(state, new ConcedeCommand { Seat = 0 });
        Assert.True(conceded.Success);
        Assert.Equal(1, conceded.State!.Result!.WinnerSeat);

        var after = resolver.Execute(conceded.State, new EndTurnCommand { Seat = 1 });
        Assert.False(after.Success);
        Assert.Equal(RuleErrorCode.GameOver, after.Error!.Code);
    }

    [Fact]
    public void Concede_is_legal_out_of_turn()
    {
        var state = TestKit.NewGame(); // seat 0 active
        var result = TestKit.NewResolver().Execute(state, new ConcedeCommand { Seat = 1 });
        Assert.True(result.Success);
        Assert.Equal(0, result.State!.Result!.WinnerSeat);
        Assert.Equal("concede", result.State.Result.Reason);
    }
}
