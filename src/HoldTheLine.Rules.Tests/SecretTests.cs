using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.Serialization;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §1.7 / §3.2 (Rules 0.9.0): the 秘密区 + 焰誓反制. A secret is set face-down (opponent sees
/// only "N暗牌"); it voids the next enemy order that selects your minion and burns a random enemy minion.</summary>
public class SecretTests
{
    private static void GiveCounter(GameState state, int seat) =>
        state.Player(seat).Secrets.Add(new Secret { CardId = "t_counter", Kind = "counter_order" });

    [Fact]
    public void Playing_a_secret_sets_it_face_down_not_to_graveyard()
    {
        var state = TestKit.NewGame();
        state.Player(0).Mana = 10;
        int card = TestKit.GiveCard(state, 0, "t_counter");

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 0, CardEntityId = card });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Single(r.State!.Player(0).Secrets);
        Assert.Empty(r.State!.Player(0).Graveyard);
        Assert.Contains(r.Events, e => e is SecretPlayedEvent { Seat: 0, SecretCount: 1 });
    }

    [Fact]
    public void Opponent_sees_only_the_secret_count_never_the_card()
    {
        var state = TestKit.NewGame();
        GiveCounter(state, 0);

        var ownerView = PlayerView.From(state, viewerSeat: 0);
        var opponentView = PlayerView.From(state, viewerSeat: 1);

        Assert.Contains("t_counter", ownerView.Self.Secrets);       // caster sees its own secret
        Assert.Equal(1, opponentView.Opponent.SecretCount);         // opponent sees the count
        Assert.Empty(opponentView.Self.Secrets);                    // and nothing that leaks the card id
    }

    [Fact]
    public void Secret_played_event_redacts_the_card_id_for_the_opponent()
    {
        var played = new SecretPlayedEvent { Seat = 0, CardEntityId = 5, CardId = "t_counter", ManaSpent = 3, SecretCount = 1 };

        Assert.Equal("t_counter", ((SecretPlayedEvent)played.RedactFor(0)).CardId);
        Assert.Null(((SecretPlayedEvent)played.RedactFor(1)).CardId);
    }

    [Fact]
    public void Counter_voids_the_enemy_order_and_burns_the_casters_side()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        GiveCounter(state, 0);
        var myMinion = TestKit.Place(state, 0, "t_big", new Cell(2, 1));    // the order's intended target, 5/6
        var enemyMinion = TestKit.Place(state, 1, "t_big", new Cell(2, 2)); // the only caster-side minion → gets punished
        int zap = TestKit.GiveCard(state, 1, "t_zap"); // deals 2 to a target unit

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 1, CardEntityId = zap, TargetUnitId = myMinion.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Equal(6, r.State!.FindUnit(myMinion.EntityId)!.CurrentHp);    // order voided — my minion untouched
        Assert.Equal(3, r.State!.FindUnit(enemyMinion.EntityId)!.CurrentHp); // 6 - 3 counter punishment
        Assert.Empty(r.State!.Player(0).Secrets);                            // secret consumed
        Assert.Contains("t_counter", r.State!.Player(0).Graveyard);
        Assert.Contains(r.Events, e => e is OrderCounteredEvent { OwnerSeat: 0, CasterSeat: 1 });
        Assert.Contains(r.State!.Player(1).Graveyard, id => id == "t_zap");  // the voided order is still spent
    }

    [Fact]
    public void Opponent_view_serialization_never_carries_a_secret_id_or_hidden_trap()
    {
        // §7 hidden-information assertion: the wire itself must not leak. Seat 0 holds a secret and a hidden trap.
        var state = TestKit.NewGame();
        state.Player(0).Secrets.Add(new Secret { CardId = "t_counter", Kind = "counter_order" });
        state.CellStates.Add(new CellState { Cell = new Cell(2, 1), Kind = "trap", OwnerSeat = 0, Hidden = true });

        var json = RulesJson.Serialize(PlayerView.From(state, viewerSeat: 1));

        Assert.DoesNotContain("t_counter", json); // no secret card id
        Assert.DoesNotContain("trap", json);      // the hidden trap's cell/kind is stripped entirely
    }

    [Fact]
    public void Counter_ignores_an_order_that_does_not_select_your_minion()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        GiveCounter(state, 0);
        int draw = TestKit.GiveCard(state, 1, "t_draw2"); // no target — does not "select" a minion

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand { Seat = 1, CardEntityId = draw });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Single(r.State!.Player(0).Secrets); // still armed
    }

    [Fact]
    public void A_stray_target_on_a_non_targeting_order_is_rejected_and_cannot_bait_the_secret()
    {
        // Server authority: a crafted command must not attach a bogus enemy TargetUnitId to a cheap
        // non-targeting order purely to pop the opponent's 焰誓反制 on the cheap.
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        GiveCounter(state, 0);
        var bait = TestKit.Place(state, 0, "t_vanilla", new Cell(2, 1));
        int draw = TestKit.GiveCard(state, 1, "t_draw2"); // no unit-target play effect

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 1, CardEntityId = draw, TargetUnitId = bait.EntityId });

        Assert.False(r.Success);
        Assert.Equal(RuleErrorCode.InvalidCommand, r.Error!.Code);
        Assert.Single(state.Player(0).Secrets); // still armed — the bait never reached the secret
    }

    [Fact]
    public void Counter_ignores_an_order_targeting_the_casters_own_minion()
    {
        var state = TestKit.NewGame();
        state.ActiveSeat = 1;
        state.Player(1).Mana = 10;
        GiveCounter(state, 0);
        var ownMinion = TestKit.Place(state, 1, "t_vanilla", new Cell(2, 2));
        int shield = TestKit.GiveCard(state, 1, "t_grant_shield"); // buffs a target unit (targets own here)

        var r = TestKit.NewResolver().Execute(state, new PlayCardCommand
        { Seat = 1, CardEntityId = shield, TargetUnitId = ownMinion.EntityId });

        Assert.True(r.Success, r.Error?.Message);
        Assert.Single(r.State!.Player(0).Secrets);                              // not the secret's business
        Assert.True(r.State!.FindUnit(ownMinion.EntityId)!.ShieldActive);       // the buff went through
    }
}
