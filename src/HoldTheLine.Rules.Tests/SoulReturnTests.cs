using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.4 (Rules 0.9.0): 归魂 — during your own turn, each friendly minion death hands your
/// surviving 归魂 units 1 辉尘 (mana), capped at 2 firings per unit per turn.</summary>
public class SoulReturnTests
{
    [Fact]
    public void Soul_return_gains_mana_on_a_friendly_death_during_your_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var soul = TestKit.Place(state, 0, "t_soul", new Cell(0, 0));
        var victim = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        int sacrifice = TestKit.GiveCard(state, 0, "t_sacrifice"); // destroy a friendly

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sacrifice, TargetUnitId = victim.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Contains(r.Events, e => e is ManaGainedEvent { Seat: 0, Amount: 1 });
        Assert.Equal(4, r.State!.Player(0).Mana); // 5 - 2 (sacrifice) + 1 (归魂)
        Assert.Equal(1, r.State!.FindUnit(soul.EntityId)!.SoulReturnGainsThisTurn);
    }

    [Fact]
    public void Soul_return_is_capped_at_two_per_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var soul = TestKit.Place(state, 0, "t_soul", new Cell(0, 0));
        var v1 = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        var v2 = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 0));
        var v3 = TestKit.Place(state, 0, "t_vanilla", new Cell(3, 0));
        int s1 = TestKit.GiveCard(state, 0, "t_sacrifice");
        int s2 = TestKit.GiveCard(state, 0, "t_sacrifice");
        int s3 = TestKit.GiveCard(state, 0, "t_sacrifice");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = s1, TargetUnitId = v1.EntityId }).State!;
        state = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = s2, TargetUnitId = v2.EntityId }).State!;
        var last = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = s3, TargetUnitId = v3.EntityId });

        // Third friendly death this turn no longer feeds 归魂 — the counter tops out at the cap.
        Assert.DoesNotContain(last.Events, e => e is ManaGainedEvent);
        Assert.Equal(2, last.State!.FindUnit(soul.EntityId)!.SoulReturnGainsThisTurn);
    }

    [Fact]
    public void Soul_return_does_not_fire_on_the_opponents_turn()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1; // opponent's turn
        var soul = TestKit.Place(state, 0, "t_soul", new Cell(0, 0));
        var victim = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 1));
        var killer = TestKit.Place(state, 1, "t_big", new Cell(1, 2)); // 5/6, adjacent to the victim

        var r = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 1, AttackerEntityId = killer.EntityId, TargetUnitId = victim.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Null(r.State!.FindUnit(victim.EntityId)); // died to the attack
        Assert.DoesNotContain(r.Events, e => e is ManaGainedEvent { Seat: 0 });
        Assert.Equal(0, r.State!.FindUnit(soul.EntityId)!.SoulReturnGainsThisTurn);
    }
}
