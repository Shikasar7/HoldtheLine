using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class KeywordTests
{
    // ---- 嘲讽 Taunt (formerly named 守护/Guard) ----

    [Fact]
    public void Adjacent_guard_forces_target_choice()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var guard = TestKit.Place(state, 1, "t_guard", new Cell(1, 1));   // adjacent to attacker
        var squishy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // also adjacent
        var resolver = TestKit.NewResolver();

        var hitSquishy = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = squishy.EntityId });
        Assert.Equal(RuleErrorCode.GuardEnforced, hitSquishy.Error!.Code);

        var hitGuard = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = guard.EntityId });
        Assert.True(hitGuard.Success, hitGuard.Error?.Message);
    }

    [Fact]
    public void Guard_only_binds_adjacent_attackers()
    {
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_archer", new Cell(2, 0)); // Range 2, NOT adjacent to the guard
        TestKit.Place(state, 1, "t_guard", new Cell(3, 1));
        var target = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = target.EntityId });
        Assert.True(result.Success, result.Error?.Message);
    }

    [Fact]
    public void Guard_also_blocks_leader_attacks()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 3));
        TestKit.Place(state, 1, "t_guard", new Cell(1, 3));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetLeader = true });
        Assert.Equal(RuleErrorCode.GuardEnforced, result.Error!.Code);
    }

    // ---- 福泽 Blessing (0.8.0) ----

    [Fact]
    public void Blessing_reduces_adjacent_ally_damage()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2 atk
        var ally = TestKit.Place(state, 1, "t_big", new Cell(2, 2));         // 5/6, adjacent to the attacker
        TestKit.Place(state, 1, "t_bless", new Cell(1, 2));                  // 福泽, adjacent to the ally

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = ally.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(5, result.State!.FindUnit(ally.EntityId)!.CurrentHp); // 2 dmg − 1 福泽 = 1 → 6-1=5
    }

    [Fact]
    public void Blessing_does_not_reduce_its_own_incoming_damage()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2 atk
        var blesser = TestKit.Place(state, 1, "t_bless", new Cell(2, 2));    // 福泽, no other blesser adjacent

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = blesser.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(4, result.State!.FindUnit(blesser.EntityId)!.CurrentHp); // full 2 dmg → 6-2=4 (aura never helps self)
    }

    // ---- 守护 Guardian (0.8.0) ----

    [Fact]
    public void Guardian_soaks_adjacent_ally_damage()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_big", new Cell(2, 1));      // 5 atk
        var ally = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));      // 2/3, adjacent to the attacker
        var guardian = TestKit.Place(state, 1, "t_guardian", new Cell(1, 2)); // 守护 2/8, adjacent to the ally

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = ally.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(ally.EntityId)!.CurrentHp);    // spared — took 0
        Assert.Equal(3, result.State.FindUnit(guardian.EntityId)!.CurrentHp); // soaked 5 → 8-5=3
        Assert.Contains(result.Events, e => e is UnitDamagedEvent { GuardRedirect: true, Amount: 0 } d && d.UnitEntityId == ally.EntityId);
        Assert.Contains(result.Events, e => e is UnitDamagedEvent { GuardRedirect: true, Amount: 5 } d && d.UnitEntityId == guardian.EntityId);
    }

    [Fact]
    public void Guardian_resolves_redirect_through_its_own_holdfast()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_big", new Cell(2, 1));           // 5 atk
        var ally = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        var guardian = TestKit.Place(state, 1, "t_guardian_hold", new Cell(1, 2)); // 守护+坚守 2/8, stationary

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = ally.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(ally.EntityId)!.CurrentHp);         // spared
        Assert.Equal(4, result.State.FindUnit(guardian.EntityId)!.CurrentHp);      // 5 − 1 坚守 = 4 → 8-4=4
    }

    [Fact]
    public void Guardian_hit_directly_takes_it_itself_no_loop()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_big", new Cell(2, 1));      // 5 atk
        var guardian = TestKit.Place(state, 1, "t_guardian", new Cell(2, 2)); // 守护, no other guardian adjacent

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = guardian.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(3, result.State!.FindUnit(guardian.EntityId)!.CurrentHp); // 8-5=3, no self-redirect
    }

    // ---- 坚守 HoldFast ----

    [Fact]
    public void HoldFast_reduces_damage_while_stationary()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2 atk
        var holder = TestKit.Place(state, 1, "t_holder", new Cell(2, 2));    // 2/4 HoldFast

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = holder.EntityId });

        Assert.True(result.Success);
        Assert.Equal(3, result.State!.FindUnit(holder.EntityId)!.CurrentHp); // 4 - (2-1)
    }

    [Fact]
    public void HoldFast_is_lost_after_moving()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var holder = TestKit.Place(state, 1, "t_holder", new Cell(2, 2));
        state.FindUnit(holder.EntityId)!.MovedThisRound = true;

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = holder.EntityId });

        Assert.Equal(2, result.State!.FindUnit(holder.EntityId)!.CurrentHp); // full 2 damage
    }

    // ---- 偷袭 CheapShot ----

    [Fact]
    public void CheapShot_melee_takes_no_retaliation()
    {
        var state = TestKit.NewGame();
        var sneak = TestKit.Place(state, 0, "t_sneak", new Cell(2, 1));    // 3/2
        var defender = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // 2/3

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = sneak.EntityId, TargetUnitId = defender.EntityId });

        Assert.True(result.Success);
        Assert.Equal(2, result.State!.FindUnit(sneak.EntityId)!.CurrentHp); // untouched
        Assert.Null(result.State.FindUnit(defender.EntityId)); // 3 damage killed it
    }

    // ---- 持盾 Shield ----

    [Fact]
    public void Shield_absorbs_exactly_one_hit()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        var shielded = TestKit.Place(state, 1, "t_shield", new Cell(2, 2));
        var resolver = TestKit.NewResolver();

        var first = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = shielded.EntityId });
        var afterFirst = first.State!.FindUnit(shielded.EntityId)!;
        Assert.Equal(2, afterFirst.CurrentHp);
        Assert.False(afterFirst.ShieldActive);
        Assert.Contains(first.Events, e => e is UnitDamagedEvent { ShieldAbsorbed: true });

        // Second hit (reset the attack for the test) connects normally.
        first.State!.FindUnit(attacker.EntityId)!.AttacksUsed = 0;
        var second = resolver.Execute(first.State, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = shielded.EntityId });
        Assert.Equal(0, second.State!.FindUnit(shielded.EntityId)?.CurrentHp ?? 0);
    }

    // ---- 践踏 Trample (0.6.0: melee splash around the target) ----

    [Fact]
    public void Trample_splashes_all_units_adjacent_to_the_target()
    {
        var state = TestKit.NewGame();
        var trampler = TestKit.Place(state, 0, "t_trampler", new Cell(2, 1)); // 4/3
        var victim = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));    // 2/3 — dies to the main hit
        var bystander = TestKit.Place(state, 1, "t_holder", new Cell(3, 2));  // 2/4 坚守 — splash 4-1=3
        var friendly = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 2));  // 2/3 friendly — splash hits it too

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = trampler.EntityId, TargetUnitId = victim.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(victim.EntityId));                     // 4 dmg main hit
        Assert.Equal(1, result.State.FindUnit(bystander.EntityId)!.CurrentHp);    // 4 - (4-1 hold_fast)
        Assert.Null(result.State.FindUnit(friendly.EntityId));                    // friend or foe — 4 splash kills it
        // The attacker never moves into the vacated cell any more, and only eats the target's retaliation.
        Assert.Equal(new Cell(2, 1), result.State.FindUnit(trampler.EntityId)!.Cell);
        Assert.Equal(1, result.State.FindUnit(trampler.EntityId)!.CurrentHp);     // 3 - 2 retaliation only
    }

    [Fact]
    public void Trample_splash_excludes_the_attacker_itself()
    {
        var state = TestKit.NewGame();
        var trampler = TestKit.Place(state, 0, "t_trampler", new Cell(2, 1)); // adjacent to its own target
        var victim = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));    // 2/3

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = trampler.EntityId, TargetUnitId = victim.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(trampler.EntityId)!.CurrentHp); // retaliation 2 only — no self-splash
    }

    // ---- 围猎 PackTactics ----

    [Fact]
    public void PackTactics_adds_two_damage_when_target_is_flanked()
    {
        var state = TestKit.NewGame();
        var hunter = TestKit.Place(state, 0, "t_pack", new Cell(2, 1));   // 2/2 围猎
        TestKit.Place(state, 0, "t_vanilla", new Cell(3, 2));             // friendly flanker beside the prey
        var prey = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));  // 2/3

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = hunter.EntityId, TargetUnitId = prey.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(result.State!.FindUnit(prey.EntityId)); // 2 + 2 flank = 4 → dead
    }

    [Fact]
    public void PackTactics_deals_normal_damage_when_alone()
    {
        var state = TestKit.NewGame();
        var hunter = TestKit.Place(state, 0, "t_pack", new Cell(2, 1));
        var prey = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2)); // 2/3

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = hunter.EntityId, TargetUnitId = prey.EntityId });

        Assert.Equal(1, result.State!.FindUnit(prey.EntityId)!.CurrentHp); // just 2 damage
    }

    [Fact]
    public void PackTactics_ignores_the_attacker_itself_as_flanker()
    {
        var state = TestKit.NewGame();
        var hunter = TestKit.Place(state, 0, "t_pack", new Cell(2, 1)); // adjacent to prey, but it's the attacker
        var prey = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = hunter.EntityId, TargetUnitId = prey.EntityId });

        Assert.Equal(1, result.State!.FindUnit(prey.EntityId)!.CurrentHp);
    }

    [Fact]
    public void PackTactics_does_not_boost_ranged_attacks()
    {
        var state = TestKit.NewGame();
        var archer = TestKit.Place(state, 0, "t_pack_archer", new Cell(2, 0)); // Range 2 + 围猎
        TestKit.Place(state, 0, "t_vanilla", new Cell(3, 2));                  // friendly beside the prey
        var prey = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));       // 2/3

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = archer.EntityId, TargetUnitId = prey.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.State!.FindUnit(prey.EntityId)!.CurrentHp); // 2 damage — no flank bonus at range
    }

    [Fact]
    public void PackTactics_bonus_is_reduced_by_HoldFast()
    {
        var state = TestKit.NewGame();
        var hunter = TestKit.Place(state, 0, "t_pack", new Cell(2, 1));  // 2 atk + 2 flank
        TestKit.Place(state, 0, "t_vanilla", new Cell(3, 2));
        var holder = TestKit.Place(state, 1, "t_holder", new Cell(2, 2)); // 2/4 坚守 (stationary)

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = hunter.EntityId, TargetUnitId = holder.EntityId });

        Assert.Equal(1, result.State!.FindUnit(holder.EntityId)!.CurrentHp); // 4 - (2+2-1)
    }

    // ---- 战吼 / 亡语 / 指令 ----

    [Fact]
    public void Battlecry_buffs_adjacent_allies_on_deploy()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var ally = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0)); // will be adjacent to (2,0)
        int card = TestKit.GiveCard(state, 0, "t_buffer");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success);
        var buffed = result.State!.FindUnit(ally.EntityId)!;
        Assert.Equal(4, buffed.CurrentHp); // 3 + 1
        Assert.Contains(result.Events, e => e is UnitBuffedEvent { HpDelta: 1 });
    }

    [Fact]
    public void Deathrattle_can_cascade_kills()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2/3
        var bomber = TestKit.Place(state, 1, "t_bomber", new Cell(2, 2));    // 1/1, deathrattle: 2 dmg to adjacent enemies

        var result = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = bomber.EntityId });

        Assert.True(result.Success);
        Assert.Null(result.State!.FindUnit(bomber.EntityId));
        // Attacker: 3 hp - 1 retaliation - 2 deathrattle = 0 → dead.
        Assert.Null(result.State.FindUnit(attacker.EntityId));
    }

    [Fact]
    public void Coin_grants_one_temporary_mana()
    {
        var state = TestKit.NewGame();
        var resolver = TestKit.NewResolver();
        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // seat 1's turn, 1 mana

        var coin = state.Player(1).Hand.First(c => c.CardId == "neutral_coin");
        var result = resolver.Execute(state, new PlayCardCommand { Seat = 1, CardEntityId = coin.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.State!.Player(1).Mana);
        Assert.Equal(1, result.State.Player(1).ManaMax); // temporary, not a ramp
    }

    [Fact]
    public void Targeted_order_damages_the_chosen_unit_and_requires_a_target()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var enemy = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        int zap = TestKit.GiveCard(state, 0, "t_zap");
        var resolver = TestKit.NewResolver();

        var noTarget = resolver.Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = zap });
        Assert.Equal(RuleErrorCode.InvalidTarget, noTarget.Error!.Code);

        var result = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = zap, TargetUnitId = enemy.EntityId });
        Assert.True(result.Success);
        Assert.Equal(1, result.State!.FindUnit(enemy.EntityId)!.CurrentHp);
    }

    [Fact]
    public void Draw_order_draws_two()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_draw2");
        int handBefore = state.Player(0).Hand.Count;
        int deckBefore = state.Player(0).Deck.Count;

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = card });

        Assert.True(result.Success);
        Assert.Equal(handBefore - 1 + 2, result.State!.Player(0).Hand.Count);
        Assert.Equal(deckBefore - 2, result.State.Player(0).Deck.Count);
    }
}
