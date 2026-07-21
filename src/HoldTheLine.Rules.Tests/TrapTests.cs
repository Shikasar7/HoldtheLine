using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.7 (Rules 0.9.0): 烬火陷阱 — a hidden trap that fires 薪炎 灼蚀 on entry (move or summon),
/// reveals, then re-ticks its occupant at each turn end for 2 turns. Hidden from the opponent's PlayerView.</summary>
public class TrapTests
{
    private static CellState Trap(GameState state, int seat, Cell cell, bool revealed = false)
    {
        var t = new CellState { Cell = cell, Kind = "trap", OwnerSeat = seat, Hidden = !revealed, Revealed = revealed, TurnsLeft = revealed ? ResolutionContext.TrapBurnTurns : 0 };
        state.CellStates.Add(t);
        return t;
    }

    // ---- placement ----

    [Fact]
    public void Placed_trap_is_hidden_from_the_opponent_only()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int card = TestKit.GiveCard(state, 0, "t_trap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 1) });

        Assert.True(r.Success, r.Error?.Message);
        var trap = r.State!.CellStates.Single(s => s.Kind == "trap");
        Assert.True(trap.Hidden);
        // Server authority: the caster sees its own trap; the opponent's view never carries it.
        Assert.Contains(PlayerView.From(r.State!, 0).CellStates, c => c.Kind == "trap");
        Assert.DoesNotContain(PlayerView.From(r.State!, 1).CellStates, c => c.Kind == "trap");
    }

    [Fact]
    public void Trap_cannot_be_placed_on_an_occupied_cell()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        int card = TestKit.GiveCard(state, 0, "t_trap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 1) });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.CellOccupied, r.Error!.Code);
    }

    [Fact]
    public void Trap_cannot_be_placed_on_the_enemy_backline()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int card = TestKit.GiveCard(state, 0, "t_trap");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 3) }); // seat 1's home row

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, r.Error!.Code);
    }

    // ---- triggering ----

    [Fact]
    public void Moving_onto_a_trap_sears_and_reveals_it()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        Trap(state, 0, new Cell(2, 1));
        var mover = TestKit.Place(state, 1, "t_big", new Cell(2, 2)); // 5/6

        var r = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 1, UnitEntityId = mover.EntityId, To = new Cell(2, 1) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(3, r.State!.FindUnit(mover.EntityId)!.CurrentHp); // 6 - 3 灼蚀
        var trap = r.State!.CellStates.Single(s => s.Kind == "trap");
        Assert.False(trap.Hidden);
        Assert.True(trap.Revealed);
        Assert.Contains(r.Events, e => e is TrapTriggeredEvent { Revealed: true });
        // Now that it is revealed, both seats see it.
        Assert.Contains(PlayerView.From(r.State!, 1).CellStates, c => c.Kind == "trap");
    }

    [Fact]
    public void Summoning_onto_a_trap_triggers_it()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        TestKit.Place(state, 0, "t_vanilla", new Cell(0, 0)); // fill home row so the summon lands on (2,0)
        TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        Trap(state, 0, new Cell(2, 0));
        int card = TestKit.GiveCard(state, 0, "t_summon1");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = card });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Contains(r.Events, e => e is TrapTriggeredEvent { Revealed: true });
        Assert.False(r.State!.CellStates.Single(s => s.Kind == "trap").Hidden);
        Assert.Null(r.State!.UnitAt(new Cell(2, 0))); // the 1/1 pup died to 3 灼蚀
    }

    [Fact]
    public void Revealed_trap_reticks_the_occupant_and_burns_out_after_two_turns()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        Trap(state, 0, new Cell(2, 1), revealed: true); // burning, TurnsLeft = 2
        var occupant = TestKit.Place(state, 1, "t_guardian", new Cell(2, 1)); // 2/8, sits on the fire
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new EndTurnCommand { Seat = 1 }).State!; // tick 1: 8 → 5, TurnsLeft → 1
        Assert.Equal(5, state.FindUnit(occupant.EntityId)!.CurrentHp);
        Assert.Single(state.CellStates);

        var last = resolver.Execute(state, new EndTurnCommand { Seat = 0 }); // tick 2: 5 → 2, TurnsLeft → 0, gone
        Assert.Equal(2, last.State!.FindUnit(occupant.EntityId)!.CurrentHp);
        Assert.Empty(last.State!.CellStates);
        Assert.Contains(last.Events, e => e is TrapExpiredEvent);
    }

    [Fact]
    public void A_trap_does_not_fire_on_a_unit_that_never_steps_on_it()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        Trap(state, 0, new Cell(2, 1));
        var mover = TestKit.Place(state, 1, "t_big", new Cell(0, 2));

        var r = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 1, UnitEntityId = mover.EntityId, To = new Cell(0, 1) }); // nowhere near the trap

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(6, r.State!.FindUnit(mover.EntityId)!.CurrentHp);
        Assert.True(r.State!.CellStates.Single(s => s.Kind == "trap").Hidden); // still armed & hidden
        Assert.DoesNotContain(r.Events, e => e is TrapTriggeredEvent);
    }
}
