using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.Serialization;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>Hard constraint #1 (plan §3.1): everything that will ever cross the wire must round-trip.</summary>
public class SerializationTests
{
    public static IEnumerable<object[]> AllCommands() =>
    [
        [new PlayCardCommand { Seat = 0, CardEntityId = 7, TargetCell = new Cell(2, 0), TargetUnitId = 9 }],
        [new MoveUnitCommand { Seat = 1, UnitEntityId = 3, To = new Cell(1, 2) }],
        [new AttackCommand { Seat = 0, AttackerEntityId = 4, TargetUnitId = 5, OccupyCellOnKill = true }],
        [new AttackCommand { Seat = 0, AttackerEntityId = 4, TargetLeader = true }],
        [new UseLeaderSkillCommand { Seat = 1, TargetCell = new Cell(0, 3) }],
        [new EndTurnCommand { Seat = 0 }],
        [new ConcedeCommand { Seat = 1 }],
    ];

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Every_command_type_round_trips_via_base_type(Command original)
    {
        var json = RulesJson.Serialize(original);
        var back = RulesJson.Deserialize<Command>(json);
        Assert.Equal(original, back);
    }

    [Fact]
    public void Command_json_uses_type_discriminator_and_snake_case()
    {
        var json = RulesJson.Serialize<Command>(new PlayCardCommand { Seat = 0, CardEntityId = 7 });
        Assert.Contains("\"$type\":\"play_card\"", json);
        Assert.Contains("\"card_entity_id\":7", json);
    }

    [Fact]
    public void Events_round_trip_via_base_type()
    {
        GameEvent original = new UnitDeployedEvent
        {
            Seat = 1, UnitEntityId = 12, CardId = "t_vanilla",
            Cell = new Cell(3, 3), Atk = 2, Hp = 3,
        };
        var back = RulesJson.Deserialize<GameEvent>(RulesJson.Serialize(original));
        Assert.Equal(original, back);
    }

    [Fact]
    public void CardDrawn_redacts_card_id_for_the_opponent_only()
    {
        var drawn = new CardDrawnEvent { Seat = 0, CardEntityId = 5, CardId = "t_vanilla" };
        Assert.Same(drawn, drawn.RedactFor(0));
        var redacted = (CardDrawnEvent)drawn.RedactFor(1);
        Assert.Null(redacted.CardId);
        Assert.Equal(5, redacted.CardEntityId);
    }

    [Fact]
    public void Redacted_event_survives_serialization()
    {
        var drawn = new CardDrawnEvent { Seat = 0, CardEntityId = 5, CardId = "t_vanilla" };
        var wire = RulesJson.Deserialize<GameEvent>(RulesJson.Serialize(drawn.RedactFor(1)));
        Assert.Null(((CardDrawnEvent)wire).CardId);
    }

    [Fact]
    public void GameState_clone_is_deep_and_independent()
    {
        var state = TestKit.NewGame();
        TestKit.Place(state, 0, TestKit.Vanilla.Id, new Cell(2, 1));

        var clone = RulesJson.Clone(state);
        Assert.Equal(state.TurnNumber, clone.TurnNumber);
        Assert.Equal(state.Rng.State, clone.Rng.State);
        Assert.Equal(state.Player(0).Hand.Select(c => c.CardId), clone.Player(0).Hand.Select(c => c.CardId));
        Assert.Equal(state.Units.Count, clone.Units.Count);

        clone.Units[0].CurrentHp = -99;
        clone.Player(0).Hand.Clear();
        Assert.True(state.Units[0].CurrentHp > 0);
        Assert.NotEmpty(state.Player(0).Hand);
    }

    [Fact]
    public void Enums_serialize_as_snake_case()
    {
        var json = RulesJson.Serialize(new KeywordSpec(Keyword.CheapShot));
        Assert.Contains("\"cheap_shot\"", json);
    }

    [Fact]
    public void New_keywords_serialize_as_snake_case()
    {
        Assert.Contains("\"emplacement\"", RulesJson.Serialize(new KeywordSpec(Keyword.Emplacement)));
        Assert.Contains("\"pierce\"", RulesJson.Serialize(new KeywordSpec(Keyword.Pierce)));
    }

    [Fact]
    public void New_faction_vocabulary_round_trips()
    {
        // Keywords 架设/贯穿 and effect triggers/actions/targets added for the new factions must survive the wire.
        CardDefinition[] cards =
        [
            TestKit.Turret, TestKit.Piercer, TestKit.SacrificeOrder,
            TestKit.RowBlastOrder, TestKit.CrossBlastOrder, TestKit.ColumnAllyBuffOrder,
            TestKit.OnCastGrower, TestKit.OnCastPinger,
        ];
        foreach (var card in cards)
        {
            var back = RulesJson.Deserialize<CardDefinition>(RulesJson.Serialize(card));
            Assert.Equal(card.Keywords.Select(k => (k.Keyword, k.Value)), back.Keywords.Select(k => (k.Keyword, k.Value)));
            Assert.Equal(card.Effects.Select(e => (e.Trigger, e.Action, e.Target)),
                         back.Effects.Select(e => (e.Trigger, e.Action, e.Target)));
        }
    }
}
