using System.IO;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

// docs/10 §6: the second-batch primitives — 灼蚀 (sear) / self_moved / all_ally_emplacements.

public class SearTests
{
    [Fact]
    public void Sear_ignores_hold_fast_reduction()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var holder = TestKit.Place(state, 1, "t_holder", new Cell(2, 2)); // 2/4 坚守, hasn't moved
        int sear = TestKit.GiveCard(state, 0, "t_sear");                   // order: 3 灼蚀

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sear, TargetUnitId = holder.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(holder.EntityId)!.CurrentHp); // 4 - 3 (坚守 -1 skipped)
    }

    [Fact]
    public void Normal_damage_still_respects_hold_fast()
    {
        // Guards against a regression: only sear should bypass 坚守, plain 'damage' must not.
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var holder = TestKit.Place(state, 1, "t_holder", new Cell(2, 2)); // 2/4 坚守
        int zap = TestKit.GiveCard(state, 0, "t_zap");                     // order: 2 damage

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = holder.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(holder.EntityId)!.CurrentHp); // 4 - (2-1)
    }

    [Fact]
    public void Sear_is_still_absorbed_by_shield()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var shielded = TestKit.Place(state, 1, "t_shield", new Cell(2, 2)); // 2/2 持盾
        int sear = TestKit.GiveCard(state, 0, "t_sear");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sear, TargetUnitId = shielded.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        var unit = result.State!.FindUnit(shielded.EntityId)!;
        Assert.Equal(2, unit.CurrentHp);     // shield ate the whole hit
        Assert.False(unit.ShieldActive);
    }

    [Fact]
    public void Sear_still_stacks_the_emplacement_plus_one()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var barricade = TestKit.Place(state, 1, "t_barricade", new Cell(2, 2)); // 1/5 守护+架设, no 坚守
        int sear = TestKit.GiveCard(state, 0, "t_sear");                        // 3 灼蚀

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = sear, TargetUnitId = barricade.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(barricade.EntityId)!.CurrentHp); // 5 - (3+1 架设)
    }
}

public class SelfMovedTests
{
    [Fact]
    public void Self_moved_buffs_the_mover_after_a_step()
    {
        var state = TestKit.NewGame();
        var runner = TestKit.Place(state, 0, "t_moved_self", new Cell(2, 1)); // 2/2, self_moved: +1/+0

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = runner.EntityId, To = new Cell(2, 2) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(runner.EntityId)!.Atk); // 2 + 1
    }

    [Fact]
    public void Self_moved_uses_the_destination_cell_for_adjacency()
    {
        var state = TestKit.NewGame();
        var tracker = TestKit.Place(state, 0, "t_moved_ping", new Cell(2, 1)); // 2/3, self_moved: 1 to adj enemies
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 3));      // adjacent only AFTER the move

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = tracker.EntityId, To = new Cell(2, 2) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 3 - 1, pinged from the new cell
    }

    [Fact]
    public void Self_moved_atk_gains_cap_at_two_per_turn()
    {
        var state = TestKit.NewGame();
        var runner = TestKit.Place(state, 0, "t_moved_self", new Cell(2, 0)); // 2/2, self_moved: +1/+0
        runner.BonusMovement = 5; // plenty of steps this turn
        var resolver = TestKit.NewResolver();

        foreach (var to in new[] { new Cell(2, 1), new Cell(2, 2), new Cell(2, 3), new Cell(2, 2) })
        {
            var step = resolver.Execute(state, new MoveUnitCommand
            { Seat = 0, UnitEntityId = state.FindUnit(runner.EntityId)!.EntityId, To = to });
            Assert.True(step.Success, step.Error?.Message);
            state = step.State!;
        }

        Assert.Equal(4, state.FindUnit(runner.EntityId)!.Atk); // 2 + 1 + 1, moves 3 & 4 capped out

        // The cap resets at the owner's next turn start.
        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!;
        state = resolver.Execute(state, new EndTurnCommand { Seat = 1 }).State!;
        var again = resolver.Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = runner.EntityId, To = new Cell(2, 3) });
        Assert.True(again.Success, again.Error?.Message);
        Assert.Equal(5, again.State!.FindUnit(runner.EntityId)!.Atk);
    }

    [Fact]
    public void A_plain_unit_move_does_not_fire_self_moved()
    {
        var state = TestKit.NewGame();
        var vanilla = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 3));

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = vanilla.EntityId, To = new Cell(2, 2) });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(enemy.EntityId)!.CurrentHp); // untouched — vanilla has no trigger
    }
}

public class AllyEmplacementSelectorTests
{
    [Fact]
    public void All_ally_emplacements_buffs_only_friendly_emplaced_units()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var turret1 = TestKit.Place(state, 0, "t_turret", new Cell(1, 0));   // 2/4 架设
        var turret2 = TestKit.Place(state, 0, "t_turret", new Cell(3, 0));   // 2/4 架设
        var mobile = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 0));   // friendly, NOT emplaced
        var enemyTurret = TestKit.Place(state, 1, "t_turret", new Cell(2, 2)); // enemy 架设
        int order = TestKit.GiveCard(state, 0, "t_emp_buff");                 // buff all_ally_emplacements +0/+2

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(6, result.State!.FindUnit(turret1.EntityId)!.CurrentHp);     // 4 + 2
        Assert.Equal(6, result.State.FindUnit(turret2.EntityId)!.CurrentHp);      // 4 + 2
        Assert.Equal(3, result.State.FindUnit(mobile.EntityId)!.CurrentHp);       // unchanged — not emplaced
        Assert.Equal(4, result.State.FindUnit(enemyTurret.EntityId)!.CurrentHp);  // unchanged — enemy
    }
}

public class RedeployTests
{
    [Fact]
    public void Mobilized_emplacement_can_take_one_step()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 1)); // 架设, pinned by default
        int order = TestKit.GiveCard(state, 0, "t_redeploy");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = order, TargetUnitId = turret.EntityId }).State!;
        var moved = resolver.Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = turret.EntityId, To = new Cell(2, 2) });

        Assert.True(moved.Success, moved.Error?.Message);
        Assert.Equal(new Cell(2, 2), moved.State!.FindUnit(turret.EntityId)!.Cell);
    }

    [Fact]
    public void Mobilized_emplacement_still_moves_only_one_cell()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 1));
        int order = TestKit.GiveCard(state, 0, "t_redeploy");
        var resolver = TestKit.NewResolver();
        state = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order, TargetUnitId = turret.EntityId }).State!;
        state = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = turret.EntityId, To = new Cell(2, 2) }).State!;

        var second = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = turret.EntityId, To = new Cell(2, 3) });

        Assert.Equal(RuleErrorCode.NoMovementLeft, second.Error!.Code); // one step only, MovementPerTurn = 1
    }

    [Fact]
    public void Emplacement_without_redeploy_is_still_pinned()
    {
        var state = TestKit.NewGame();
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 1));

        var result = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = turret.EntityId, To = new Cell(2, 2) });

        Assert.Equal(RuleErrorCode.Emplaced, result.Error!.Code);
    }

    [Fact]
    public void Enumerator_offers_a_move_for_a_mobilized_emplacement()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 1));
        int order = TestKit.GiveCard(state, 0, "t_redeploy");
        var resolver = TestKit.NewResolver();
        state = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order, TargetUnitId = turret.EntityId }).State!;

        var legal = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders);

        Assert.Contains(legal, c => c is MoveUnitCommand m && m.UnitEntityId == turret.EntityId);
    }

    [Fact]
    public void Redeploy_grant_expires_at_end_of_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var turret = TestKit.Place(state, 0, "t_turret", new Cell(2, 1));
        int order = TestKit.GiveCard(state, 0, "t_redeploy");
        var resolver = TestKit.NewResolver();
        state = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = order, TargetUnitId = turret.EntityId }).State!;
        Assert.True(state.FindUnit(turret.EntityId)!.HasKeyword(Keyword.Mobilized));

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!;

        Assert.False(state.FindUnit(turret.EntityId)!.HasKeyword(Keyword.Mobilized)); // bolted down again next turn
    }
}

public class SecondBatchValidationTests
{
    [Fact]
    public void Self_moved_on_an_order_is_a_data_error()
    {
        var bad = new CardDefinition
        {
            Id = "bad_order", Name = "Bad", Type = CardType.Order, Cost = 1,
            Effects = [new EffectSpec { Trigger = "self_moved", Action = "draw", Amount = 1 }],
        };
        Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
    }

    [Fact]
    public void Self_moved_with_an_explicit_target_is_a_data_error()
    {
        var bad = new CardDefinition
        {
            Id = "bad_unit", Name = "Bad", Cost = 2, Atk = 1, Hp = 1,
            Effects = [new EffectSpec { Trigger = "self_moved", Action = "damage", Target = "target_unit", Amount = 1 }],
        };
        Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
    }

    [Fact]
    public void Self_moved_with_an_implicit_target_loads()
    {
        var ok = new CardDefinition
        {
            Id = "ok_unit", Name = "OK", Cost = 2, Atk = 1, Hp = 1,
            Effects = [new EffectSpec { Trigger = "self_moved", Action = "damage", Target = "adjacent_enemies", Amount = 1 }],
        };
        var db = new CardDatabase([ok]);
        Assert.True(db.TryGet("ok_unit", out _));
    }
}
