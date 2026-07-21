using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.6 (Rules 0.9.0): 烟幕区 — a 5-cell cross where units cannot attack and do not retaliate.
/// The effect is positional (walking off lifts it) and the zone lapses at the caster's next turn.</summary>
public class SmokeTests
{
    private static void Smoke(GameState state, int seat, Cell cell) =>
        state.CellStates.Add(new CellState { Cell = cell, Kind = "smoke", OwnerSeat = seat });

    [Fact]
    public void Place_smoke_covers_the_target_cell_and_its_cross()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int card = TestKit.GiveCard(state, 0, "t_smoke");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 2) });

        Assert.True(r.Success, r.Error?.Message);
        var smoked = r.State!.CellStates.Where(s => s.Kind == "smoke").Select(s => s.Cell).ToHashSet();
        Assert.Equal(5, smoked.Count);
        foreach (var c in new[] { new Cell(2, 2), new Cell(1, 2), new Cell(3, 2), new Cell(2, 1), new Cell(2, 3) })
            Assert.Contains(c, smoked);
        Assert.Contains(r.Events, e => e is SmokeAppliedEvent { Seat: 0 });
    }

    [Fact]
    public void Smoked_unit_cannot_attack()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));
        TestKit.Place(state, 1, "t_big", new Cell(2, 1));
        Smoke(state, 0, new Cell(2, 2)); // attacker stands in smoke

        var r = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = state.Units[1].EntityId });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.Smoked, r.Error!.Code);
    }

    [Fact]
    public void Smoked_defender_does_not_retaliate()
    {
        var state = TestKit.NewGame();
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2)); // 2/3
        var defender = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 1)); // 2/3, adjacent
        Smoke(state, 0, new Cell(2, 1)); // only the defender is smoked

        var r = TestKit.NewResolver().Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = defender.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(1, r.State!.FindUnit(defender.EntityId)!.CurrentHp); // took 2
        Assert.Equal(3, r.State!.FindUnit(attacker.EntityId)!.CurrentHp); // no retaliation
    }

    [Fact]
    public void Walking_off_smoke_restores_the_ability_to_attack()
    {
        var state = TestKit.NewGame();
        var unit = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 2));
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(1, 1));
        Smoke(state, 0, new Cell(2, 2)); // single-cell smoke
        var resolver = TestKit.NewResolver();

        // Step off the smoke to (1,2) — no longer smoked.
        state = resolver.Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(1, 2) }).State!;
        var r = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = unit.EntityId, TargetUnitId = enemy.EntityId }); // (1,2)→(1,1) adjacent

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(4, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - 2
    }

    [Fact]
    public void Smoke_lapses_at_the_casters_next_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int card = TestKit.GiveCard(state, 0, "t_smoke");
        var resolver = TestKit.NewResolver();
        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 2) }).State!;
        Assert.NotEmpty(state.CellStates);

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // → seat 1
        var back = resolver.Execute(state, new EndTurnCommand { Seat = 1 });     // → seat 0, smoke clears

        Assert.Empty(back.State!.CellStates);
        Assert.Contains(back.Events, e => e is SmokeExpiredEvent { Seat: 0 });
    }

    [Fact]
    public void Opponent_view_shows_smoke_as_public()
    {
        var state = TestKit.NewGame();
        Smoke(state, 0, new Cell(2, 2));

        var opponentView = PlayerView.From(state, viewerSeat: 1);

        Assert.Contains(opponentView.CellStates, c => c.Kind == "smoke" && c.Cell == new Cell(2, 2));
    }
}
