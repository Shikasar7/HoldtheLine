using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class DeployTests
{
    [Fact]
    public void Deploying_on_own_home_row_works_and_pays_mana()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_vanilla");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        var unit = result.State!.UnitAt(new Cell(2, 0));
        Assert.NotNull(unit);
        Assert.Equal(2, unit!.Atk);
        Assert.Equal(3, result.State.Player(0).Mana); // 5 - cost 2
        Assert.DoesNotContain(result.State.Player(0).Hand, c => c.EntityId == card);
    }

    [Theory]
    [InlineData(2, 1, RuleErrorCode.NotHomeRow)]     // not the home row
    [InlineData(2, 3, RuleErrorCode.NotHomeRow)]     // enemy home row
    [InlineData(9, 0, RuleErrorCode.CellOutsideBoard)]
    public void Deploying_elsewhere_is_rejected(int col, int row, RuleErrorCode expected)
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_vanilla");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(col, row) });

        Assert.False(result.Success);
        Assert.Equal(expected, result.Error!.Code);
    }

    [Fact]
    public void Occupied_cell_is_rejected()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        TestKit.Place(state, 0, "t_vanilla", new Cell(2, 0));
        int card = TestKit.GiveCard(state, 0, "t_vanilla");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.Equal(RuleErrorCode.CellOccupied, result.Error!.Code);
    }

    [Fact]
    public void Insufficient_mana_is_rejected()
    {
        var state = TestKit.NewGame(); // 1 mana on turn 1
        int card = TestKit.GiveCard(state, 0, "t_big"); // costs 5

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.Equal(RuleErrorCode.NotEnoughMana, result.Error!.Code);
    }

    [Fact]
    public void Fresh_units_are_summoning_sick()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_vanilla");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) }).State!;
        var unit = state.UnitAt(new Cell(2, 0))!;

        var move = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = unit.EntityId, To = new Cell(2, 1) });
        Assert.Equal(RuleErrorCode.SummoningSickness, move.Error!.Code);
    }

    [Fact]
    public void Charge_can_move_and_attack_on_deploy_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        int card = TestKit.GiveCard(state, 0, "t_charger");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) }).State!;
        var charger = state.UnitAt(new Cell(2, 0))!;

        state = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = charger.EntityId, To = new Cell(2, 1) }).State!;
        var enemy = state.UnitAt(new Cell(2, 2))!;
        var attack = resolver.Execute(state, new AttackCommand { Seat = 0, AttackerEntityId = charger.EntityId, TargetUnitId = enemy.EntityId });
        Assert.True(attack.Success, attack.Error?.Message);
    }

    [Fact]
    public void Assault_can_attack_but_not_move_on_deploy_turn()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        TestKit.Place(state, 1, "t_vanilla", new Cell(2, 1)); // adjacent to home row deploy
        int card = TestKit.GiveCard(state, 0, "t_assault");
        var resolver = TestKit.NewResolver();

        state = resolver.Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) }).State!;
        var assaulter = state.UnitAt(new Cell(2, 0))!;

        var move = resolver.Execute(state, new MoveUnitCommand { Seat = 0, UnitEntityId = assaulter.EntityId, To = new Cell(1, 0) });
        Assert.Equal(RuleErrorCode.SummoningSickness, move.Error!.Code);

        var enemy = state.UnitAt(new Cell(2, 1))!;
        var attack = resolver.Execute(state, new AttackCommand { Seat = 0, AttackerEntityId = assaulter.EntityId, TargetUnitId = enemy.EntityId });
        Assert.True(attack.Success, attack.Error?.Message);
    }
}
