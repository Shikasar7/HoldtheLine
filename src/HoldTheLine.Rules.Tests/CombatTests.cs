using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class CombatTests
{
    [Fact]
    public void Melee_is_a_simultaneous_exchange()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2/3
        var defender = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // 2/3

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = defender.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(attacker.EntityId)!.CurrentHp);
        Assert.Equal(1, result.State.FindUnit(defender.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Lethal_exchange_kills_both_and_fires_died_events()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_sneak", new Cell(2, 1));   // 3/2 (CheapShot irrelevant here: give it a real fight)
        var defender = TestKit.Place(state, 1, "t_trampler", new Cell(2, 2)); // 4/3

        // Remove CheapShot so the exchange is mutual.
        state.FindUnit(attacker.EntityId)!.Keywords.Clear();

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = defender.EntityId });

        Assert.True(result.Success);
        Assert.Null(result.State!.FindUnit(attacker.EntityId)); // took 4, had 2
        Assert.Null(result.State.FindUnit(defender.EntityId));  // took 3, had 3 — simultaneous strike
        Assert.Equal(2, result.Events.Count(e => e is UnitDiedEvent));
    }

    [Fact]
    public void Melee_requires_adjacency()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var far = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 3));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = far.EntityId });
        Assert.Equal(RuleErrorCode.NotAdjacent, result.Error!.Code);
    }

    [Fact]
    public void Ranged_attack_from_safe_distance_takes_no_retaliation()
    {
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_archer", new Cell(2, 0)); // 2/2, Range 2
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // 2/3 melee, 2 cells away → can't reach back

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(archer.EntityId)!.CurrentHp); // untouched
        Assert.Equal(1, result.State.FindUnit(target.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Ranged_attacker_is_retaliated_when_inside_the_targets_reach()
    {
        // A ranged unit shelling an adjacent melee enemy is itself in that enemy's reach → takes the counter.
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_archer", new Cell(2, 1)); // 2/2, Range 2
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // 2/3 melee, adjacent

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(archer.EntityId));               // 2 hp - 2 retaliation → dead
        Assert.Equal(1, result.State.FindUnit(target.EntityId)!.CurrentHp); // 3 - 2
    }

    [Fact]
    public void Two_ranged_units_in_range_of_each_other_both_take_damage()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_archer", new Cell(2, 0)); // Range 2
        var target = TestKit.Place(state, 1, "t_archer", new Cell(2, 2));   // Range 2, 2 steps away — reaches back

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(attacker.EntityId)); // 2/2 - 2 retaliation → dead
        Assert.Null(result.State.FindUnit(target.EntityId));    // 2/2 - 2 → dead (simultaneous)
    }

    [Fact]
    public void CheapShot_ranged_attacker_still_avoids_retaliation_in_range()
    {
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_archer", new Cell(2, 1)); // Range 2, adjacent to target
        state.FindUnit(archer.EntityId)!.Keywords.Add(new HoldTheLine.Rules.Cards.KeywordSpec(HoldTheLine.Rules.Cards.Keyword.CheapShot));
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // adjacent melee

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.FindUnit(archer.EntityId)!.CurrentHp); // 偷袭 → no counter even point-blank
    }

    [Fact]
    public void Ranged_fire_passes_over_bodies()
    {
        // GDD §2.5 (2026-07-17): ranged shots ignore line blocking — friend or foe.
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_archer", new Cell(2, 0));
        TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // friendly body in the lane
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = target.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(target.EntityId)!.CurrentHp); // 3 - 2
    }

    [Fact]
    public void Ranged_attack_reaches_diagonals_within_step_range()
    {
        // 射程 is Manhattan distance: a diagonal-1 cell is 2 steps away, so Range 2 reaches it.
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_archer", new Cell(2, 0)); // Range 2
        var diagonal = TestKit.Place(state, 1, "t_vanilla", new Cell(3, 1)); // |1|+|1| = 2 steps
        var resolver = TestKit.NewResolver();

        var hit = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = diagonal.EntityId });
        Assert.True(hit.Success, hit.Error?.Message);
        Assert.Equal(1, hit.State!.FindUnit(diagonal.EntityId)!.CurrentHp); // 3 - 2
    }

    [Fact]
    public void Ranged_attack_respects_max_range()
    {
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_archer", new Cell(2, 0)); // Range 2
        var tooFar = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 3)); // 3 steps
        var farDiagonal = TestKit.Place(state, 1, "t_vanilla", new Cell(4, 1)); // |2|+|1| = 3 steps
        var resolver = TestKit.NewResolver();

        Assert.Equal(RuleErrorCode.OutOfRange, resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = tooFar.EntityId }).Error!.Code);

        Assert.Equal(RuleErrorCode.OutOfRange, resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = farDiagonal.EntityId }).Error!.Code);
    }

    [Fact]
    public void One_attack_per_turn()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_big", new Cell(2, 1)); // 5/6
        var e1 = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        var e2 = TestKit.Place(state, 1, "t_vanilla", new Cell(1, 1));
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = e1.EntityId }).State!;

        var second = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = e2.EntityId });
        Assert.Equal(RuleErrorCode.NoAttacksLeft, second.Error!.Code);
    }

    [Fact]
    public void Friendly_fire_is_rejected()
    {
        var state = TestKit.NewGame();
        var a = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var b = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = a.EntityId, TargetUnitId = b.EntityId });
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Leader_can_only_be_attacked_from_their_home_row()
    {
        var state = TestKit.NewGame();
        var tooEarly = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));
        var arrived = TestKit.Place(state, 0, "t_vanilla", new Cell(3, 3));
        var resolver = TestKit.NewResolver();

        Assert.Equal(RuleErrorCode.NotOnEnemyHomeRow, resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = tooEarly.EntityId, TargetLeader = true }).Error!.Code);

        var hit = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = arrived.EntityId, TargetLeader = true });
        Assert.True(hit.Success, hit.Error?.Message);
        Assert.Equal(23, hit.State!.Player(1).LeaderHp);
        Assert.Contains(hit.Events, e => e is LeaderDamagedEvent { Seat: 1, Amount: 2 });
    }

    [Fact]
    public void Reducing_a_leader_to_zero_ends_the_game()
    {
        var state = TestKit.NewGame();
        state.Player(1).LeaderHp = 2;
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(3, 3));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetLeader = true });

        Assert.True(result.Success);
        Assert.Equal(0, result.State!.Result!.WinnerSeat);
        Assert.Equal("leader_defeated", result.State.Result.Reason);
        Assert.Contains(result.Events, e => e is GameEndedEvent { WinnerSeat: 0 });
    }
}
