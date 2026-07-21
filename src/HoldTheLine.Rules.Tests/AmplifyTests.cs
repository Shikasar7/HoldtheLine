using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.3 (Rules 0.9.0): 蓄能 (amplify_next) + 引导者差异化 (channel deepen/discount). All
/// amplification is school-filtered — only 薪炎 (spell.*) damage is boosted; physical is untouched.</summary>
public class AmplifyTests
{
    // ---- 蓄能 (charge) ----

    [Fact]
    public void Battlecry_charge_banks_spell_charge()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int card = TestKit.GiveCard(state, 0, "t_charge_unit");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(2, r.State!.Player(0).SpellCharge);
        Assert.Contains(r.Events, e => e is SpellChargeChangedEvent { Seat: 0, NewCharge: 2 });
    }

    [Fact]
    public void Charge_amplifies_the_next_kindle_order_then_clears()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2)); // 5/6
        state.Player(0).SpellCharge = 2;
        int card = TestKit.GiveCard(state, 0, "t_channel_zap"); // 2 薪炎 damage

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(2, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - (2 + 2 charge)
        Assert.Equal(0, r.State!.Player(0).SpellCharge);               // consumed
        Assert.Contains(r.Events, e => e is SpellChargeChangedEvent { NewCharge: 0 });
    }

    [Fact]
    public void Charge_is_not_consumed_by_a_non_kindle_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        state.Player(0).SpellCharge = 2;
        int card = TestKit.GiveCard(state, 0, "t_channel_mana"); // channel, but gain_mana (not 薪炎 damage)

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(2, r.State!.Player(0).SpellCharge); // still banked for a real 薪炎 order
    }

    // ---- 引导者 加深 (deepen) ----

    [Fact]
    public void Deepen_channeler_adds_one_to_its_channeled_kindle_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_deepen", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(3, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - (2 + 1 deepen)
    }

    [Fact]
    public void Deepen_and_charge_stack()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var ch = TestKit.Place(state, 0, "t_deepen", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        state.Player(0).SpellCharge = 2;
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(1, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - (2 + 1 deepen + 2 charge)
        Assert.Equal(0, r.State!.Player(0).SpellCharge);
    }

    // ---- 引导者 减费 (discount) ----

    [Fact]
    public void Discount_channeler_shaves_one_mana_off_a_kindle_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 2; // ChannelColumn costs 3 → 2 after discount
        var ch = TestKit.Place(state, 0, "t_discount", new Cell(2, 2));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 3)); // column 2
        int card = TestKit.GiveCard(state, 0, "t_channel_col");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 3), ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(0, r.State!.Player(0).Mana);
        Assert.Contains(r.Events, e => e is CardPlayedEvent { ManaSpent: 2 });
        Assert.Equal(4, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - 2 (discounter gives no deepen)
    }

    [Fact]
    public void Plain_channeler_does_not_discount()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 2;
        var ch = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));
        TestKit.Place(state, 1, "t_big", new Cell(2, 3));
        int card = TestKit.GiveCard(state, 0, "t_channel_col"); // costs 3 > 2

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 3), ChannelerUnitId = ch.EntityId });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.NotEnoughMana, r.Error!.Code);
    }

    [Fact]
    public void Discount_floors_at_one_mana()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 1;
        var ch = TestKit.Place(state, 0, "t_discount", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_channel_zap"); // costs 1 → max(1, 1-1) = 1

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = ch.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(0, r.State!.Player(0).Mana);
        Assert.Contains(r.Events, e => e is CardPlayedEvent { ManaSpent: 1 });
    }

    [Fact]
    public void Enumerator_offers_a_discounted_channel_order_it_could_not_afford_at_full_cost()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 2; // full cost 3 unaffordable; discounted cost 2 affordable
        TestKit.Place(state, 0, "t_discount", new Cell(2, 2));
        TestKit.Place(state, 1, "t_big", new Cell(2, 3));
        int card = TestKit.GiveCard(state, 0, "t_channel_col");

        var legal = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders);

        Assert.Contains(legal, c => c is PlayCardCommand p && p.CardEntityId == card);
    }

    [Fact]
    public void Greedy_ai_prefers_the_deepen_channeler_when_it_improves_damage()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var plain = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 1));
        var deepen = TestKit.Place(state, 0, "t_deepen", new Cell(3, 1));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_channel_zap");
        Command[] choices =
        [
            new PlayCardCommand { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = plain.EntityId },
            new PlayCardCommand { Seat = 0, CardEntityId = card, TargetUnitId = enemy.EntityId, ChannelerUnitId = deepen.EntityId },
        ];

        var pick = Assert.IsType<PlayCardCommand>(GreedyAi.Pick(state, TestKit.Db, TestKit.Leaders, choices));

        Assert.Equal(deepen.EntityId, pick.ChannelerUnitId);
    }

    [Fact]
    public void Greedy_ai_prefers_the_discount_channeler_when_the_effect_is_otherwise_equal()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var plain = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 1));
        var discount = TestKit.Place(state, 0, "t_discount", new Cell(3, 1));
        TestKit.Place(state, 1, "t_big", new Cell(2, 3));
        int card = TestKit.GiveCard(state, 0, "t_channel_col");
        var targetCell = new Cell(2, 3);
        Command[] choices =
        [
            new PlayCardCommand { Seat = 0, CardEntityId = card, TargetCell = targetCell, ChannelerUnitId = plain.EntityId },
            new PlayCardCommand { Seat = 0, CardEntityId = card, TargetCell = targetCell, ChannelerUnitId = discount.EntityId },
        ];

        var pick = Assert.IsType<PlayCardCommand>(GreedyAi.Pick(state, TestKit.Db, TestKit.Leaders, choices));

        Assert.Equal(discount.EntityId, pick.ChannelerUnitId);
    }
}
