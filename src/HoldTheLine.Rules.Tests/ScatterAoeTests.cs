using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §3.1 (Rules 0.9.0): 燔火 (damage_scatter — random 薪炎 missiles, +1 per 加深/蓄能) and
/// 燎原 (all_enemies 薪炎 灼蚀). Missile rolls run on the match Rng, so replays are deterministic (§7).</summary>
public class ScatterAoeTests
{
    // ---- 燔火 (scatter) ----

    [Fact]
    public void Scatter_deals_one_damage_per_missile_total()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var e1 = TestKit.Place(state, 1, "t_big", new Cell(1, 2)); // 5/6
        var e2 = TestKit.Place(state, 1, "t_big", new Cell(3, 2)); // 5/6
        int card = TestKit.GiveCard(state, 0, "t_scatter"); // 3 missiles

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        int dealt = (6 - r.State!.FindUnit(e1.EntityId)!.CurrentHp) + (6 - r.State!.FindUnit(e2.EntityId)!.CurrentHp);
        Assert.Equal(3, dealt); // 3 missiles, no wasted overkill (both survive)
    }

    [Fact]
    public void Scatter_distribution_is_replay_deterministic()
    {
        var state = TestKit.NewGame(seed: 20260721);
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var e1 = TestKit.Place(state, 1, "t_big", new Cell(1, 2));
        var e2 = TestKit.Place(state, 1, "t_big", new Cell(3, 2));
        int card = TestKit.GiveCard(state, 0, "t_scatter");
        var cmd = new PlayCardCommand { Seat = 0, CardEntityId = card, ChannelerUnitId = ch.EntityId };

        // Same input state + same command → identical split (the resolver clones, so it re-rolls the same Rng).
        var a = TestKit.NewResolver().Execute(state, cmd);
        var b = TestKit.NewResolver().Execute(state, cmd);

        Assert.Equal(a.State!.FindUnit(e1.EntityId)!.CurrentHp, b.State!.FindUnit(e1.EntityId)!.CurrentHp);
        Assert.Equal(a.State!.FindUnit(e2.EntityId)!.CurrentHp, b.State!.FindUnit(e2.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Charge_adds_missiles_to_a_scatter()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(1, 2)); // 5/6, only target
        state.Player(0).SpellCharge = 2;
        int card = TestKit.GiveCard(state, 0, "t_scatter"); // 3 + 2 charge = 5 missiles

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(1, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - 5 missiles
        Assert.Equal(0, r.State!.Player(0).SpellCharge);               // consumed
    }

    [Fact]
    public void Scatter_needs_a_channeler()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        TestKit.Place(state, 1, "t_big", new Cell(1, 2));
        int card = TestKit.GiveCard(state, 0, "t_scatter");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = card });

        Assert.False(r.Success);
    }

    // ---- 燎原 (all_enemies sear) ----

    [Fact]
    public void All_enemies_sear_hits_every_enemy()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var e1 = TestKit.Place(state, 1, "t_big", new Cell(0, 2));
        var e2 = TestKit.Place(state, 1, "t_big", new Cell(2, 3));
        var e3 = TestKit.Place(state, 1, "t_big", new Cell(4, 2));
        int card = TestKit.GiveCard(state, 0, "t_all_sear"); // 2 灼蚀 to all enemies

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(4, r.State!.FindUnit(e1.EntityId)!.CurrentHp); // 6 - 2
        Assert.Equal(4, r.State!.FindUnit(e2.EntityId)!.CurrentHp);
        Assert.Equal(4, r.State!.FindUnit(e3.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Deepen_channeler_amplifies_all_enemies_sear()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_deepen", new Cell(2, 1)); // +1 to channeled 薪炎
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(0, 2));
        int card = TestKit.GiveCard(state, 0, "t_all_sear");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(3, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - (2 + 1 deepen)
    }
}
