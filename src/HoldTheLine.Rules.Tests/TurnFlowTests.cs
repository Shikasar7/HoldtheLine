using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
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

    // ---- 压力潮汐 PressureTide (GDD §2.7, anti-turtle revision) ----

    [Fact]
    public void PressureTide_is_silent_before_round_8()
    {
        var state = TestKit.NewGame(deck0: Enumerable.Repeat("t_vanilla", 30).ToList(),
                                    deck1: Enumerable.Repeat("t_vanilla", 30).ToList());
        state.TurnNumber = 12; // seat 1's next turn = turn 13 → round 7
        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 0 });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(25, result.State!.Player(1).LeaderHp);
        Assert.DoesNotContain(result.Events, e => e is PressureTideEvent);
    }

    [Fact]
    public void PressureTide_bleeds_a_seat_with_no_presence_in_the_enemy_half()
    {
        var state = TestKit.NewGame(deck0: Enumerable.Repeat("t_vanilla", 30).ToList(),
                                    deck1: Enumerable.Repeat("t_vanilla", 30).ToList());
        state.TurnNumber = 14; // seat 1's next turn = turn 15 → round 8
        TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // own half only
        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 0 });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(24, result.State!.Player(1).LeaderHp); // round 8 → 1 damage
        Assert.Contains(result.Events, e => e is PressureTideEvent { Seat: 1, Round: 8, Amount: 1 });
    }

    [Fact]
    public void PressureTide_spares_a_seat_pressing_the_enemy_half()
    {
        var state = TestKit.NewGame(deck0: Enumerable.Repeat("t_vanilla", 30).ToList(),
                                    deck1: Enumerable.Repeat("t_vanilla", 30).ToList());
        state.TurnNumber = 14;
        TestKit.Place(state, 1, "t_vanilla", new Cell(2, 1)); // enemy half (seat 1's enemy = rows 0–1)
        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 0 });

        Assert.Equal(25, result.State!.Player(1).LeaderHp);
        Assert.DoesNotContain(result.Events, e => e is PressureTideEvent);
    }

    [Fact]
    public void PressureTide_damage_escalates_with_the_round()
    {
        var state = TestKit.NewGame(deck0: Enumerable.Repeat("t_vanilla", 30).ToList(),
                                    deck1: Enumerable.Repeat("t_vanilla", 30).ToList());
        state.TurnNumber = 22; // seat 1's next turn = turn 23 → round 12 → 5 damage
        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 0 });

        Assert.Equal(20, result.State!.Player(1).LeaderHp);
        Assert.Contains(result.Events, e => e is PressureTideEvent { Round: 12, Amount: 5 });
    }

    [Fact]
    public void PressureTide_can_end_the_game()
    {
        var state = TestKit.NewGame(deck0: Enumerable.Repeat("t_vanilla", 30).ToList(),
                                    deck1: Enumerable.Repeat("t_vanilla", 30).ToList());
        state.TurnNumber = 14;
        state.Player(1).LeaderHp = 1;
        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 0 });

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.State!.Result);
        Assert.Equal(0, result.State.Result!.WinnerSeat);
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
    public void Overdraw_at_9_cards_sends_the_card_to_the_graveyard()
    {
        var state = TestKit.NewGame();
        while (state.Player(1).Hand.Count < 9)
            TestKit.GiveCard(state, 1, "t_vanilla");

        int deckBefore = state.Player(1).Deck.Count;
        string overflowCard = state.Player(1).Deck[^1].CardId; // top of deck = what gets drawn into the full hand
        Assert.Empty(state.Player(1).Graveyard);

        var result = TestKit.NewResolver().Execute(state, new EndTurnCommand { Seat = 0 });
        Assert.True(result.Success);

        Assert.Contains(result.Events, e => e is CardBurnedEvent { Seat: 1 });
        Assert.Equal(9, result.State!.Player(1).Hand.Count);
        Assert.Equal(deckBefore - 1, result.State.Player(1).Deck.Count);
        // 0.7.0: the overflow card now lands in the graveyard rather than leaving the game.
        Assert.Equal([overflowCard], result.State.Player(1).Graveyard);
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
