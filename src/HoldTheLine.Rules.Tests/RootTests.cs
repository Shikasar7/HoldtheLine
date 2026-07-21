using System.Linq;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.5 (Rules 0.9.0): 定身 — a rooted unit cannot move (Leap/move_bonus are moot) but can
/// still attack and retaliate. Granted with a duration, so it lapses at the caster's next turn.</summary>
public class RootTests
{
    private static (GameState state, int rootedId) RootAUnit(string rootedCardId, Cell cell)
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var unit = TestKit.Place(state, 0, rootedCardId, cell);
        int card = TestKit.GiveCard(state, 0, "t_root");
        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = unit.EntityId });
        Assert.True(r.Success, r.Error?.Message);
        return (r.State!, unit.EntityId);
    }

    [Fact]
    public void Rooted_unit_cannot_move()
    {
        var (state, id) = RootAUnit("t_vanilla", new Cell(2, 1));

        var r = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = id, To = new Cell(2, 2) });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.Rooted, r.Error!.Code);
    }

    [Fact]
    public void Rooted_leaper_cannot_leap()
    {
        var (state, id) = RootAUnit("t_leaper", new Cell(2, 1));

        var r = TestKit.NewResolver().Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = id, To = new Cell(2, 3) }); // a distance-2 leap

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.Rooted, r.Error!.Code);
    }

    [Fact]
    public void Rooted_unit_can_still_attack()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        var attacker = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1)); // 2/3
        var enemy = TestKit.Place(state, 1, "t_big", new Cell(2, 2));        // 5/6, adjacent
        int card = TestKit.GiveCard(state, 0, "t_root");
        var resolver = TestKit.NewResolver();
        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetUnitId = attacker.EntityId }).State!;

        var r = resolver.Execute(state, new AttackCommand
        { Seat = 0, AttackerEntityId = attacker.EntityId, TargetUnitId = enemy.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(4, r.State!.FindUnit(enemy.EntityId)!.CurrentHp); // 6 - 2 attack
    }

    [Fact]
    public void Rooted_unit_surfaces_the_keyword_in_the_client_view()
    {
        // Regression: 定身 is a TEMP grant (TempGrants, not the permanent Keywords list). The client view must
        // still surface it, or the debuff badge + detail-panel description never appear (they read view keywords).
        var (state, id) = RootAUnit("t_vanilla", new Cell(2, 1));

        var uv = PlayerView.From(state, viewerSeat: 0).Units.First(x => x.EntityId == id);

        Assert.Contains(uv.Keywords, k => k.Keyword == Keyword.Rooted);
    }

    [Fact]
    public void Root_lapses_at_the_casters_next_turn()
    {
        var (state, id) = RootAUnit("t_vanilla", new Cell(2, 1));
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // → seat 1
        state = resolver.Execute(state, new EndTurnCommand { Seat = 1 }).State!; // → seat 0 again, root expires

        var r = resolver.Execute(state, new MoveUnitCommand
        { Seat = 0, UnitEntityId = id, To = new Cell(2, 2) });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(new Cell(2, 2), r.State!.FindUnit(id)!.Cell);
    }
}
