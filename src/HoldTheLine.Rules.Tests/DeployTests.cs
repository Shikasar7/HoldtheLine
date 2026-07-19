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

    // ---- 先上随从再判战吼: a target-needing battlecry never blocks the deploy on an empty board ----

    [Fact]
    public void Ally_buff_battlecry_deploys_with_no_target_when_no_ally_exists()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_ally_buffer"); // battlecry: +1/+1 to another ally

        // No other unit on the board → no legal target. The unit must still deploy; the battlecry fizzles.
        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.True(result.Success, result.Error?.Message);
        var unit = result.State!.UnitAt(new Cell(2, 0));
        Assert.NotNull(unit);
        Assert.Equal(2, unit!.Atk);          // unbuffed — the battlecry found no target and fizzled
        Assert.Equal(2, unit.CurrentHp);
    }

    [Fact]
    public void Ally_buff_battlecry_still_requires_a_target_when_an_ally_is_present()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0)); // a legal ally target exists
        int card = TestKit.GiveCard(state, 0, "t_ally_buffer");

        // A legal target exists, so a targetless deploy is rejected — the player must pick the ally.
        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0) });

        Assert.False(result.Success);
        Assert.Equal(RuleErrorCode.InvalidTarget, result.Error!.Code);
    }

    [Fact]
    public void Ally_buff_battlecry_applies_when_a_target_is_supplied()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        var ally = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0)); // 2/3
        int card = TestKit.GiveCard(state, 0, "t_ally_buffer");

        var result = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 0, CardEntityId = card, TargetCell = new Cell(2, 0), TargetUnitId = ally.EntityId });

        Assert.True(result.Success, result.Error?.Message);
        var buffed = result.State!.FindUnit(ally.EntityId)!;
        Assert.Equal(3, buffed.Atk);       // 2 + 1
        Assert.Equal(4, buffed.CurrentHp); // 3 + 1
    }

    [Fact]
    public void Enumerator_offers_the_fizzle_deploy_only_when_no_target_exists()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 5;
        int card = TestKit.GiveCard(state, 0, "t_ally_buffer");

        // Empty board: the only legal plays for this card are bare (no-target) deploys.
        var empty = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders)
            .OfType<PlayCardCommand>().Where(c => c.CardEntityId == card).ToList();
        Assert.NotEmpty(empty);
        Assert.All(empty, c => Assert.Null(c.TargetUnitId));

        // With an ally present the fizzle candidate is pruned — every legal play now carries a target.
        var ally = TestKit.Place(state, 0, "t_vanilla", new Cell(1, 0));
        var withAlly = CommandEnumerator.LegalCommands(state, TestKit.Db, TestKit.Leaders)
            .OfType<PlayCardCommand>().Where(c => c.CardEntityId == card).ToList();
        Assert.NotEmpty(withAlly);
        Assert.All(withAlly, c => Assert.Equal(ally.EntityId, c.TargetUnitId));
    }
}
