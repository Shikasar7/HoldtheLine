using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>加农校准 (boost_range): +1 range this turn, ADDITIVE onto the unit's current range
/// (docs/00 §3 — restores the GDD "射程加法叠加" so it isn't dead on units that already have range).</summary>
public class BoostRangeTests
{
    private static readonly LeaderDefinition Brom = new()
    {
        Id = "test_brom", Name = "Brom", SkillCost = 2,
        SkillEffects =
        [
            new EffectSpec { Trigger = "leader_skill", Action = "boost_range", Target = "target_unit_ally", Amount = 1, Duration = "end_of_turn" },
        ],
    };

    [Theory]
    [InlineData("t_archer", 2, 3)]   // a range-2 unit stacks up to 3 (the old grant_keyword was a no-op here)
    [InlineData("t_vanilla", 0, 1)]  // a melee unit gains range 1
    public void Calibration_adds_one_range_additively(string cardId, int before, int after)
    {
        var leaders = new LeaderDatabase([Brom]);
        var resolver = new Resolver(TestKit.Db, leaders);
        var deck = Enumerable.Repeat(TestKit.Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, FirstSeat = 0, Deck0 = deck, Deck1 = deck, Leader0 = Brom.Id, Leader1 = Brom.Id,
        }, TestKit.Db, leaders);

        var unit = TestKit.Place(state, 0, cardId, new Cell(2, BoardGeometry.HomeRow(0)));
        Assert.Equal(before, unit.KeywordValue(Keyword.Range));
        state.Player(0).Mana = 5; // afford the 2-cost skill on turn 1

        var r = resolver.Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = unit.EntityId });
        Assert.True(r.Success, r.Error?.Message);

        var boosted = r.State!.FindUnit(unit.EntityId)!;
        Assert.Equal(after, boosted.KeywordValue(Keyword.Range));
    }

    [Fact]
    public void The_boost_expires_at_end_of_turn()
    {
        var leaders = new LeaderDatabase([Brom]);
        var resolver = new Resolver(TestKit.Db, leaders);
        var deck = Enumerable.Repeat(TestKit.Vanilla.Id, 12).ToList();
        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, FirstSeat = 0, Deck0 = deck, Deck1 = deck, Leader0 = Brom.Id, Leader1 = Brom.Id,
        }, TestKit.Db, leaders);

        var unit = TestKit.Place(state, 0, "t_archer", new Cell(2, BoardGeometry.HomeRow(0)));
        state.Player(0).Mana = 5;

        state = resolver.Execute(state, new UseLeaderSkillCommand { Seat = 0, TargetUnitId = unit.EntityId }).State!;
        Assert.Equal(3, state.FindUnit(unit.EntityId)!.KeywordValue(Keyword.Range));

        state = resolver.Execute(state, new EndTurnCommand { Seat = 0 }).State!; // end_of_turn grant lapses
        Assert.Equal(2, state.FindUnit(unit.EntityId)!.KeywordValue(Keyword.Range));
    }
}
