using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §3.1 (Rules 0.9.0): 薪火回响 (门德 — echoes your FIRST 薪炎 damage order each turn) and
/// 焚世巨灵 (after a 4費以上 order, all enemies take 1 薪炎).</summary>
public class EchoBehemothTests
{
    // ---- 薪火回响 ----

    [Fact]
    public void Echo_duplicates_the_first_kindle_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var echo = TestKit.Place(state, 0, "t_echo", new Cell(2, 1)); // channels too
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2)); // 5/6
        int zap = TestKit.GiveCard(state, 0, "t_channel_zap"); // 2 薪炎

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = enemy.EntityId, ChannelerUnitId = echo.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(2, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - (2 + 2 echo)
        Assert.Contains(r.Events, e => e is OrderEchoedEvent { Seat: 0 });
    }

    [Fact]
    public void Only_the_first_kindle_order_each_turn_is_echoed()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 20;
        var echo = TestKit.Place(state, 0, "t_echo", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_guardian", new Cell(2, 2)); // 2/8, survives
        int z1 = TestKit.GiveCard(state, 0, "t_channel_zap");
        int z2 = TestKit.GiveCard(state, 0, "t_channel_zap");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = z1, TargetUnitId = enemy.EntityId, ChannelerUnitId = echo.EntityId }).State!; // 8 → 4 (echoed)
        Assert.Equal(4, state.FindUnit(enemy.EntityId)!.CurrentHp);

        var r2 = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = z2, TargetUnitId = enemy.EntityId, ChannelerUnitId = echo.EntityId }); // 4 → 2 (no echo)
        Assert.Equal(2, r2.State!.FindUnit(enemy.EntityId)!.CurrentHp);
        Assert.DoesNotContain(r2.Events, e => e is OrderEchoedEvent);
    }

    [Fact]
    public void Echo_fizzles_when_the_target_already_died()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var echo = TestKit.Place(state, 0, "t_echo", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // 2/3, dies to the first hit
        int lash = TestKit.GiveCard(state, 0, "t_flame_lash"); // 3 薪炎 enemy mode

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = lash, TargetUnitId = enemy.EntityId, ChannelerUnitId = echo.EntityId });

        Assert.True(r.Success, r.Error?.Message);           // the echo段 just fizzles — no crash
        Assert.Null(r.State!.FindUnit(enemy.EntityId));      // dead from the first 3
        Assert.Contains(r.Events, e => e is OrderEchoedEvent);
    }

    // ---- 焚世巨灵 ----

    [Fact]
    public void Behemoth_pings_all_enemies_after_a_four_cost_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        TestKit.Place(state, 0, "t_behemoth", new Cell(0, 0));
        var e1 = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        var e2 = TestKit.Place(state, 1, "t_big", new Cell(4, 2));
        int order = TestKit.GiveCard(state, 0, "t_big_order"); // cost 4

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(5, r.State!.FindUnit(e1.EntityId)!.CurrentHp); // 6 - 1
        Assert.Equal(5, r.State!.FindUnit(e2.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Behemoth_stays_silent_on_a_cheap_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        TestKit.Place(state, 0, "t_behemoth", new Cell(0, 0));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        int order = TestKit.GiveCard(state, 0, "t_draw2"); // cost 2 < 4

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(6, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // untouched
    }
}
