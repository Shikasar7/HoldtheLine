using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.9 (Rules 0.9.0): a capped ally_order_played self-growth (灰烬侍徒/烬眼先知/烬火唱徒)
/// stacks at most twice per turn; 奥菲兰's uncapped growth and every non-growth on-cast keep firing.</summary>
public class GrowthCapTests
{
    /// <summary>Plays <paramref name="count"/> no-target draw orders in sequence, threading state.</summary>
    private static GameState PlayOrders(GameState state, Resolver resolver, int seat, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int card = TestKit.GiveCard(state, seat, "t_draw2");
            var r = resolver.Execute(state, new PlayCardCommand { Seat = seat, CardEntityId = card });
            Assert.True(r.Success, r.Error?.Message);
            state = r.State!;
        }
        return state;
    }

    [Fact]
    public void Capped_self_growth_stops_after_two_orders()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 30;
        var grower = TestKit.Place(state, 0, "t_oncast_self", new Cell(0, 0)); // 1/2, +1/+0 per order (capped)

        state = PlayOrders(state, TestKit.NewResolver(), 0, 3);

        Assert.Equal(3, state.FindUnit(grower.EntityId)!.Atk); // 1 + min(3, 2) growth
        Assert.Equal(2, state.FindUnit(grower.EntityId)!.OrderGrowthThisTurn);
    }

    [Fact]
    public void Uncapped_self_growth_keeps_stacking()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 30;
        var grower = TestKit.Place(state, 0, "t_oncast_uncapped", new Cell(0, 0)); // 2/6, +1/+0 per order (uncapped)

        state = PlayOrders(state, TestKit.NewResolver(), 0, 3);

        Assert.Equal(5, state.FindUnit(grower.EntityId)!.Atk); // 2 + 3 growth, no cap
    }

    [Fact]
    public void Non_growth_on_cast_is_not_capped()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 30;
        TestKit.Place(state, 0, "t_oncast_ping", new Cell(0, 0)); // pings adjacent enemies for 1 per order
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(0, 1)); // 5/6, adjacent

        state = PlayOrders(state, TestKit.NewResolver(), 0, 3);

        Assert.Equal(3, state.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - 3 pings (fired all 3, no cap)
    }
}
