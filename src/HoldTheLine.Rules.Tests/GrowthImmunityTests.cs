using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.1/§1.8 (Rules 0.9.0): 免疫薪炎 zeroes spell.* damage but a 薪炎 hit still accelerates a
/// 成长 unit; growth also ticks at your turn start, transforming the unit in place (雏凤 → 灰烬凤凰).</summary>
public class GrowthImmunityTests
{
    [Fact]
    public void Kindle_immune_zeros_spell_damage_but_still_accelerates_growth()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var chick = TestKit.Place(state, 1, "t_chick", new Cell(2, 2)); // 免疫薪炎 grower
        int zap = TestKit.GiveCard(state, 0, "t_channel_zap"); // 2 薪炎

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = chick.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        var after = r.State!.FindUnit(chick.EntityId)!;
        Assert.Equal(2, after.CurrentHp);         // immune → 0 damage
        Assert.Equal(1, after.GrowthProgress);    // but the burn advanced its growth
        Assert.Contains(r.Events, e => e is UnitGrowthEvent { Progress: 1 });
    }

    [Fact]
    public void Physical_damage_still_hurts_a_kindle_immune_unit()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2 atk, physical
        var chick = TestKit.Place(state, 1, "t_chick", new Cell(2, 2));       // 2/2

        var r = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = chick.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Null(r.State!.FindUnit(chick.EntityId)); // 2 physical damage killed it — no immunity to physical
    }

    [Fact]
    public void A_kindle_hit_that_completes_growth_transforms_the_unit()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var chick = TestKit.Place(state, 1, "t_chick", new Cell(2, 2));
        state.FindUnit(chick.EntityId)!.GrowthProgress = 1; // one short of the threshold (2)
        int zap = TestKit.GiveCard(state, 0, "t_channel_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = chick.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        var grown = r.State!.FindUnit(chick.EntityId)!; // same EntityId, new form
        Assert.Equal("t_phoenix", grown.CardId);
        Assert.Equal(5, grown.Atk);
        Assert.Equal(6, grown.CurrentHp);
        Assert.True(grown.HasKeyword(Keyword.KindleImmune));
        Assert.Contains(r.Events, e => e is UnitTransformedEvent { IntoCardId: "t_phoenix" });
    }

    [Fact]
    public void Growth_ticks_at_your_turn_start_and_transforms()
    {
        var state = TestKit.NewGame();
        var chick = TestKit.Place(state, 0, "t_chick", new Cell(2, 0));
        state.FindUnit(chick.EntityId)!.GrowthProgress = 1; // one turn-start away from transforming
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // → seat 1
        var back = resolver.Execute(state, new EndTurnCommand { Seat = 1 });     // → seat 0 turn start: growth 1 → 2 → transform

        var grown = back.State!.FindUnit(chick.EntityId)!;
        Assert.Equal("t_phoenix", grown.CardId);
        Assert.Contains(back.Events, e => e is UnitTransformedEvent);
    }
}
