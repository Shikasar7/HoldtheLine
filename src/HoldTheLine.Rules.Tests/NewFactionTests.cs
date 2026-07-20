using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

// docs/06 §3 + docs/07 X0: 架设 / 贯穿 / destroy / 友方目标 / 新选择器 / ally_order_played.

public class EmplacementTests
{
    [Fact]
    public void Emplaced_unit_cannot_move()
    {
        var state = TestKit.NewGame();
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 0));

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = turret.EntityId, To = new Cell(2, 1) });

        Assert.Equal(RuleErrorCode.Emplaced, result.Error!.Code);
    }

    [Fact]
    public void Move_bonus_cannot_free_an_emplaced_unit()
    {
        var state = TestKit.NewGame();
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 0));
        state.FindUnit(turret.EntityId)!.BonusMovement = 3; // pretend a move_bonus landed on it

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = turret.EntityId, To = new Cell(2, 1) });

        Assert.Equal(RuleErrorCode.Emplaced, result.Error!.Code);
    }

    [Fact]
    public void Enumerator_never_offers_a_move_for_an_emplaced_unit()
    {
        var state = TestKit.NewGame();
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 0));

        var legal = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders);

        Assert.DoesNotContain(legal, c => c is MoveUnitCommand m && m.UnitEntityId == turret.EntityId);
    }

    [Fact]
    public void Emplaced_unit_deploys_normally_on_the_home_row()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_barricade");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new Cell(2, 0), result.State!.UnitAt(new Cell(2, 0))!.Cell);
    }

    [Fact]
    public void Emplaced_unit_can_still_attack_in_range()
    {
        var state = TestKit.NewGame();
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 0)); // Range 2, emplaced
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = turret.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(target.EntityId)!.CurrentHp); // 3 - 2
    }

    [Fact]
    public void Emplaced_unit_takes_one_extra_from_effect_damage()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var turret = TestKit.Place(state, 1, "t_turret", new Cell(2, 2)); // 2/4 架设
        int zap = TestKit.GiveCard(state, 0, "t_zap");                    // order: 2 damage

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = turret.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(turret.EntityId)!.CurrentHp); // 4 - (2+1)
    }

    [Fact]
    public void Recall_order_moves_only_orders_from_graveyard_to_hand()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        state.Player(0).Graveyard.AddRange(["t_vanilla", "t_zap"]); // one unit, one order
        int handBefore = state.Player(0).Hand.Count;
        int recaller = TestKit.GiveCard(state, 0, "t_recaller");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = recaller, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        var hand = result.State!.Player(0).Hand;
        Assert.Equal(handBefore + 1, hand.Count); // recaller given+played nets zero; +t_zap recalled
        Assert.Contains(hand, c => c.CardId == "t_zap");
        Assert.Equal(["t_vanilla"], result.State.Player(0).Graveyard); // unit stays dead
        Assert.Contains(result.Events, e => e is CardDrawnEvent { CardId: "t_zap" });
    }

    [Fact]
    public void Recall_order_is_silent_on_an_orderless_graveyard()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        state.Player(0).Graveyard.Add("t_vanilla"); // units only
        int recaller = TestKit.GiveCard(state, 0, "t_recaller");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = recaller, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.DoesNotContain(result.Events, e => e is CardDrawnEvent);
        Assert.Equal(["t_vanilla"], result.State!.Player(0).Graveyard);
    }

    [Fact]
    public void Recall_order_on_a_full_hand_leaves_it_in_the_graveyard()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        state.Player(0).Graveyard.Add("t_zap");
        int recaller = TestKit.GiveCard(state, 0, "t_recaller");
        while (state.Player(0).Hand.Count < 10) // recaller leaves hand on play → 9 remain = full
            TestKit.GiveCard(state, 0, "t_vanilla");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = recaller, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(result.Events, e => e is CardBurnedEvent { CardId: "t_zap" });
        Assert.DoesNotContain(result.State!.Player(0).Hand, c => c.CardId == "t_zap");
        // 0.7.0: an order recalled into a full hand stays in the graveyard rather than leaving the game.
        Assert.Contains("t_zap", result.State.Player(0).Graveyard);
    }

    [Fact]
    public void Emplaced_unit_takes_normal_damage_from_attacks()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2 atk
        var turret = TestKit.Place(state, 1, "t_turret", new Cell(2, 2));    // 2/4 架设

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = turret.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(turret.EntityId)!.CurrentHp); // 4 - 2, no bonus
    }
}

public class PierceTests
{
    [Fact]
    public void Pierce_strikes_the_cell_directly_behind_a_straight_shot()
    {
        var state = TestKit.NewGame();
        var piercer = TestKit.Place(state, 0, "t_piercer", new Cell(2, 0)); // 3/3 Range 2, 贯穿
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 1));  // 2/3
        var behind = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));  // 2/3 directly behind

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = piercer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(target.EntityId)); // 3 dmg → dead
        Assert.Null(result.State.FindUnit(behind.EntityId));  // pierced for 3 → dead
    }

    [Fact]
    public void Pierce_hits_a_friendly_unit_behind_the_target()
    {
        var state = TestKit.NewGame();
        var piercer = TestKit.Place(state, 0, "t_piercer", new Cell(2, 0));
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 1));
        var ownBehind = TestKit.Place(state, 0, "t_big", new Cell(2, 2)); // 5/6 friendly caught in the line

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = piercer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(ownBehind.EntityId)!.CurrentHp); // 6 - 3, friendly fire
    }

    [Fact]
    public void Pierce_does_not_fire_on_a_diagonal_shot()
    {
        var state = TestKit.NewGame();
        var piercer = TestKit.Place(state, 0, "t_piercer", new Cell(2, 0));
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(3, 1));  // diagonal, 2 steps away
        var bystander = TestKit.Place(state, 1, "t_vanilla", new Cell(3, 2)); // no defined "behind" for a diagonal

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = piercer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(target.EntityId));               // target still takes its 3
        Assert.Equal(3, result.State.FindUnit(bystander.EntityId)!.CurrentHp); // untouched — no pierce
    }

    [Fact]
    public void Pierce_behind_the_board_edge_is_a_noop()
    {
        var state = TestKit.NewGame();
        var piercer = TestKit.Place(state, 0, "t_piercer", new Cell(2, 1));
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 3)); // behind would be (2,4), off-board

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = piercer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(target.EntityId));
    }

    [Fact]
    public void Pierce_respects_the_behind_units_shield()
    {
        var state = TestKit.NewGame();
        var piercer = TestKit.Place(state, 0, "t_piercer", new Cell(2, 0));
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 1));
        var shielded = TestKit.Place(state, 1, "t_shield", new Cell(2, 2)); // 2/2 持盾

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = piercer.EntityId, TargetUnitId = target.EntityId });

        var behind = result.State!.FindUnit(shielded.EntityId)!;
        Assert.Equal(2, behind.CurrentHp);   // shield ate the pierce hit
        Assert.False(behind.ShieldActive);
    }
}

public class DestroyTests
{
    [Fact]
    public void Sacrifice_destroys_a_friendly_unit_and_fires_its_deathrattle()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var fodder = TestKit.Place(state, 0, "t_bomber", new Cell(2, 1)); // 1/1, deathrattle 2 to adjacent enemies
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));  // adjacent to the fodder
        int sac = TestKit.GiveCard(state, 0, "t_sacrifice");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sac, TargetUnitId = fodder.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(fodder.EntityId));                 // destroyed
        Assert.Equal(1, result.State.FindUnit(enemy.EntityId)!.CurrentHp);    // 3 - 2 deathrattle
    }

    [Fact]
    public void Destroy_bypasses_shield_and_hold_fast()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var shielded = TestKit.Place(state, 0, "t_shield", new Cell(2, 1)); // 持盾 would stop damage — but not destroy
        int sac = TestKit.GiveCard(state, 0, "t_sacrifice");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sac, TargetUnitId = shielded.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(shielded.EntityId)); // gone despite the shield
    }

    [Fact]
    public void Sacrifice_rejects_an_enemy_target()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        int sac = TestKit.GiveCard(state, 0, "t_sacrifice");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sac, TargetUnitId = enemy.EntityId });

        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code); // target_unit_ally must be friendly
    }

    [Fact]
    public void Sacrifice_rejects_a_missing_target()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int sac = TestKit.GiveCard(state, 0, "t_sacrifice");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sac, TargetUnitId = 9999 });

        Assert.Equal(RuleErrorCode.UnknownEntity, result.Error!.Code);
    }
}

public class NewSelectorTests
{
    [Fact]
    public void Row_enemies_hits_only_that_rows_enemies()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var inRow = TestKit.Place(state, 1, "t_vanilla", new Cell(1, 2));
        var inRow2 = TestKit.Place(state, 1, "t_vanilla", new Cell(3, 2));
        var friendlyInRow = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));
        var offRow = TestKit.Place(state, 1, "t_vanilla", new Cell(1, 3));
        int order = TestKit.GiveCard(state, 0, "t_row");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetCell = new Cell(0, 2) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(inRow.EntityId)!.CurrentHp);      // 3 - 2
        Assert.Equal(1, result.State.FindUnit(inRow2.EntityId)!.CurrentHp);
        Assert.Equal(3, result.State.FindUnit(friendlyInRow.EntityId)!.CurrentHp); // enemies only
        Assert.Equal(3, result.State.FindUnit(offRow.EntityId)!.CurrentHp);        // other row untouched
    }

    [Fact]
    public void Cell_cross_all_hits_both_sides_in_a_plus()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var center = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        var up = TestKit.Place(state, 0, "t_big", new Cell(2, 1));    // friendly, in the cross
        var left = TestKit.Place(state, 1, "t_vanilla", new Cell(1, 2));
        var down = TestKit.Place(state, 0, "t_big", new Cell(2, 3));  // friendly, in the cross
        var offCross = TestKit.Place(state, 1, "t_vanilla", new Cell(0, 0));
        int order = TestKit.GiveCard(state, 0, "t_cross");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetCell = new Cell(2, 2) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(center.EntityId)!.CurrentHp); // 3 - 2
        Assert.Equal(4, result.State.FindUnit(up.EntityId)!.CurrentHp);      // 6 - 2 friendly fire
        Assert.Equal(1, result.State.FindUnit(left.EntityId)!.CurrentHp);
        Assert.Equal(4, result.State.FindUnit(down.EntityId)!.CurrentHp);
        Assert.Equal(3, result.State.FindUnit(offCross.EntityId)!.CurrentHp); // outside the plus
    }

    [Fact]
    public void Cell_cross_all_clips_at_the_board_corner()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var corner = TestKit.Place(state, 1, "t_vanilla", new Cell(0, 0));
        int order = TestKit.GiveCard(state, 0, "t_cross");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetCell = new Cell(0, 0) }); // neighbours (-1,0)/(0,-1) clip out

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(corner.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Column_allies_buffs_only_friendly_units_in_the_column()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var ally1 = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 0));
        var ally2 = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemyInCol = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        var allyOffCol = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 1));
        int order = TestKit.GiveCard(state, 0, "t_col_ally");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(ally1.EntityId)!.Atk);       // 2 + 1
        Assert.Equal(3, result.State.FindUnit(ally2.EntityId)!.Atk);
        Assert.Equal(2, result.State.FindUnit(enemyInCol.EntityId)!.Atk);   // enemy in column untouched
        Assert.Equal(2, result.State.FindUnit(allyOffCol.EntityId)!.Atk);   // other column untouched
    }
}

public class AllyOrderPlayedTests
{
    [Fact]
    public void On_cast_self_buff_grows_with_each_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var grower = TestKit.Place(state, 0, "t_oncast_self", new Cell(0, 0)); // 1/2, +1/+0 per order
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));
        var resolver = TestKit.NewResolver();

        int zap1 = TestKit.GiveCard(state, 0, "t_zap");
        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap1, TargetUnitId = enemy.EntityId }).State!;
        Assert.Equal(2, state.FindUnit(grower.EntityId)!.Atk); // 1 + 1

        int zap2 = TestKit.GiveCard(state, 0, "t_zap");
        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap2, TargetUnitId = enemy.EntityId }).State!;
        Assert.Equal(3, state.FindUnit(grower.EntityId)!.Atk); // 2 + 1
    }

    [Fact]
    public void On_cast_pinger_hits_adjacent_enemies_after_an_order()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        TestKit.Place(state, 0, "t_oncast_ping", new Cell(2, 1)); // 1/3, on cast: 1 dmg to adjacent enemies
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // adjacent
        int draw = TestKit.GiveCard(state, 0, "t_draw2"); // targetless order

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = draw });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 3 - 1
    }

    [Fact]
    public void The_military_coin_triggers_ally_order_played()
    {
        var state = TestKit.NewGame();
        var grower = TestKit.Place(state, 0, "t_oncast_self", new Cell(0, 0));
        int coin = TestKit.GiveCard(state, 0, "neutral_coin");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = coin });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(grower.EntityId)!.Atk); // coin is an Order → fires the engine
    }

    [Fact]
    public void Token_orders_leave_the_game_instead_of_entering_the_graveyard()
    {
        var state = TestKit.NewGame();
        int coin = TestKit.GiveCard(state, 0, "neutral_coin");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = coin });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.State!.Player(0).Graveyard); // recall_order must never see the coin again
    }

    [Fact]
    public void A_leader_skill_does_not_trigger_ally_order_played()
    {
        var state = TestKit.NewGame();
        state.Player(0).LeaderId = "leader_valen";
        state.Player(0).Mana = 5;
        var grower = TestKit.Place(state, 0, "t_oncast_self", new Cell(0, 0));

        var result = TestKit.NewResolver().Execute(state, new UseLeaderSkillCommand
        { Seat = 0, TargetUnitId = grower.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(grower.EntityId)!.Atk); // unchanged — skills aren't Orders
    }

    [Fact]
    public void An_opponents_order_does_not_trigger_your_units()
    {
        var state = TestKit.NewGame();
        var grower = TestKit.Place(state, 0, "t_oncast_self", new Cell(0, 0)); // seat 0's unit
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // seat 1's turn
        state.Player(1).Mana = 5;
        int draw = TestKit.GiveCard(state, 1, "t_draw2");
        var played = resolver.Execute(state, new PlayCardCommand { Seat = 1, CardEntityId = draw });
        Assert.True(played.Success, played.Error?.Message);
        state = played.State!;

        Assert.Equal(1, state.FindUnit(grower.EntityId)!.Atk); // seat 0's engine stays quiet
    }
}
