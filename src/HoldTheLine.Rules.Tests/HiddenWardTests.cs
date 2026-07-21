using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §2 (Rules 0.9.0): 潜行 (Hidden — untargetable by an enemy single-target 指令/战吼, revealed on
/// attack) and 法术护体 (SpellWard — absorbs the next enemy single-target effect, then is consumed).</summary>
public class HiddenWardTests
{
    // ---- 潜行 (Hidden) ----

    [Fact]
    public void Hidden_unit_cannot_be_selected_by_an_enemy_order()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        var hidden = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        hidden.Keywords.Add(new KeywordSpec(Keyword.Hidden));
        int zap = TestKit.GiveCard(state, 1, "t_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 1, CardEntityId = zap, TargetUnitId = hidden.EntityId });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, r.Error!.Code);
    }

    [Fact]
    public void The_owner_can_still_target_their_own_hidden_unit()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var hidden = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        hidden.Keywords.Add(new KeywordSpec(Keyword.Hidden));
        int buff = TestKit.GiveCard(state, 0, "t_grant_shield"); // targets a friendly unit

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = buff, TargetUnitId = hidden.EntityId });

        Assert.True(r.Success, r.Error?.Message);
    }

    [Fact]
    public void Aoe_still_hits_a_hidden_unit()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        var hidden = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // column 2
        hidden.Keywords.Add(new KeywordSpec(Keyword.Hidden));
        int column = TestKit.GiveCard(state, 1, "t_column"); // column_enemies 1 (not a selection)

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 1, CardEntityId = column, TargetCell = new Cell(2, 3) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(2, r.State!.FindUnit(hidden.EntityId)!.CurrentHp); // 3 - 1, AoE ignores 潜行
    }

    [Fact]
    public void Attacking_reveals_a_hidden_unit()
    {
        var state = TestKit.NewGame();
        var hidden = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2)); // 2/3
        hidden.Keywords.Add(new KeywordSpec(Keyword.Hidden));
        var prey = TestKit.Place(state, 1, "t_scout", new Cell(2, 3)); // 1/1, low retaliation

        var r = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = hidden.EntityId, TargetUnitId = prey.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.False(r.State!.FindUnit(hidden.EntityId)!.HasKeyword(Keyword.Hidden)); // 攻击后现形
        Assert.Contains(r.Events, e => e is UnitRevealedEvent);
    }

    [Fact]
    public void Conceal_order_grants_hidden()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ally = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        int card = TestKit.GiveCard(state, 0, "t_stealth");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = ally.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.True(r.State!.FindUnit(ally.EntityId)!.HasKeyword(Keyword.Hidden));
    }

    // ---- 法术护体 (SpellWard) ----

    [Fact]
    public void Spell_ward_absorbs_an_enemy_order_and_is_consumed()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        var warded = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2/3
        warded.Keywords.Add(new KeywordSpec(Keyword.SpellWard));
        int zap = TestKit.GiveCard(state, 1, "t_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 1, CardEntityId = zap, TargetUnitId = warded.EntityId });

        Assert.True(r.Success, r.Error?.Message);         // legal to target — the ward absorbs, not blocks selection
        var after = r.State!.FindUnit(warded.EntityId)!;
        Assert.Equal(3, after.CurrentHp);                 // damage voided
        Assert.False(after.HasKeyword(Keyword.SpellWard)); // consumed
        Assert.Contains(r.Events, e => e is SpellWardConsumedEvent);
    }

    [Fact]
    public void Spell_ward_only_absorbs_one_effect()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        var warded = TestKit.Place(state, 0, "t_big", new Cell(2, 1)); // 5/6
        warded.Keywords.Add(new KeywordSpec(Keyword.SpellWard));
        int z1 = TestKit.GiveCard(state, 1, "t_zap");
        int z2 = TestKit.GiveCard(state, 1, "t_zap");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand { Seat = 1, CardEntityId = z1, TargetUnitId = warded.EntityId }).State!;
        Assert.Equal(6, state.FindUnit(warded.EntityId)!.CurrentHp); // first absorbed

        var r2 = resolver.Execute(state, new PlayCardCommand { Seat = 1, CardEntityId = z2, TargetUnitId = warded.EntityId });
        Assert.Equal(4, r2.State!.FindUnit(warded.EntityId)!.CurrentHp); // second lands (6 - 2)
    }

    [Fact]
    public void Spell_ward_ignores_a_friendly_effect()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var warded = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        warded.Keywords.Add(new KeywordSpec(Keyword.SpellWard));
        int shield = TestKit.GiveCard(state, 0, "t_grant_shield"); // friendly buff

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = shield, TargetUnitId = warded.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        var after = r.State!.FindUnit(warded.EntityId)!;
        Assert.True(after.HasKeyword(Keyword.SpellWard)); // ward intact — not an enemy effect
        Assert.True(after.ShieldActive);                  // the buff went through
    }
}
