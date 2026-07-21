using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.8 (Rules 0.9.0): 焰鞭 dual-mode — an enemy target takes 3 薪炎; a friendly target is
/// consumed and its current atk/hp transferred to a distinct 二段目标.</summary>
public class FlameLashTests
{
    [Fact]
    public void Enemy_mode_deals_three_kindle()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_flame_lash");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(3, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - 3
    }

    [Fact]
    public void Friendly_mode_consumes_the_primary_and_transfers_its_stats()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var a = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2)); // primary, 2/3, in range
        var b = TestKit.Place(state, 0, "t_vanilla", new Cell(0, 0)); // 二段目标, 2/3, anywhere
        int card = TestKit.GiveCard(state, 0, "t_flame_lash");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = a.EntityId, SecondaryTargetUnitId = b.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Null(r.State!.FindUnit(a.EntityId)); // A consumed
        var bAfter = r.State!.FindUnit(b.EntityId)!;
        Assert.Equal(4, bAfter.Atk);       // 2 + 2
        Assert.Equal(6, bAfter.CurrentHp); // 3 + 3
        Assert.Contains(r.Events, e => e is StatTransferredEvent { Atk: 2, Hp: 3 });
    }

    [Fact]
    public void Friendly_mode_requires_a_second_target()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var a = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_flame_lash");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = a.EntityId, ChannelerUnitId = ch.EntityId }); // no secondary

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, r.Error!.Code);
    }

    [Fact]
    public void Friendly_mode_secondary_must_be_a_distinct_friendly()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var a = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 3));
        int card = TestKit.GiveCard(state, 0, "t_flame_lash");

        // secondary is the same unit as the primary → illegal
        var same = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = a.EntityId, SecondaryTargetUnitId = a.EntityId, ChannelerUnitId = ch.EntityId });
        Assert.False(same.Success);

        // secondary is an enemy → illegal
        int card2 = TestKit.GiveCard(state, 0, "t_flame_lash");
        var foe = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card2, TargetUnitId = a.EntityId, SecondaryTargetUnitId = enemy.EntityId, ChannelerUnitId = ch.EntityId });
        Assert.False(foe.Success);
    }

    [Fact]
    public void Enumerator_generates_both_modes()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var a = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));   // friendly primary in range
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 0));   // enemy primary in range (dist 2)
        TestKit.GiveCard(state, 0, "t_flame_lash");

        var legal = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders);

        // enemy mode (no secondary)
        Assert.Contains(legal, c => c is PlayCardCommand { TargetUnitId: { } t, SecondaryTargetUnitId: null } && t == enemy.EntityId);
        // friendly mode (primary a → secondary the channeler, both friendly)
        Assert.Contains(legal, c => c is PlayCardCommand { TargetUnitId: { } t, SecondaryTargetUnitId: { } s }
            && t == a.EntityId && s == ch.EntityId);
    }
}
