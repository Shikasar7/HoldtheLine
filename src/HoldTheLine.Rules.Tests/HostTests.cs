using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>LocalGameHost behaves like the future network host: async boundary, redaction, replayability.</summary>
public class HostTests
{
    private static MatchConfig Config(ulong seed = 42) => new()
    {
        Seed = seed,
        Deck0 = Enumerable.Repeat("t_vanilla", 12).ToList(),
        Deck1 = Enumerable.Repeat("t_vanilla", 12).ToList(),
    };

    [Fact]
    public async Task Events_are_redacted_per_subscriber_seat()
    {
        var host = new LocalGameHost(TestKit.Db, Config());
        var seenBySeat0 = new List<GameEvent>();
        var seenBySeat1 = new List<GameEvent>();
        host.Subscribe(0, seenBySeat0.Add);
        host.Subscribe(1, seenBySeat1.Add);

        // Ending seat 0's turn makes seat 1 draw a card.
        var result = await host.SubmitCommandAsync(0, new EndTurnCommand { Seat = 0 });
        Assert.True(result.Accepted);

        var drawnSeenByOpponent = seenBySeat0.OfType<CardDrawnEvent>().Single(e => e.Seat == 1);
        var drawnSeenByOwner = seenBySeat1.OfType<CardDrawnEvent>().Single(e => e.Seat == 1);
        Assert.Null(drawnSeenByOpponent.CardId);
        Assert.NotNull(drawnSeenByOwner.CardId);
    }

    [Fact]
    public async Task Rejected_commands_change_nothing_and_emit_nothing()
    {
        var host = new LocalGameHost(TestKit.Db, Config());
        var seen = new List<GameEvent>();
        host.Subscribe(0, seen.Add);

        var result = await host.SubmitCommandAsync(1, new EndTurnCommand { Seat = 1 });
        Assert.False(result.Accepted);
        Assert.Equal(RuleErrorCode.NotYourTurn, result.Error!.Code);
        Assert.Empty(seen);
        Assert.Empty(host.CommandLog);
    }

    [Fact]
    public async Task Seat_spoofing_is_rejected()
    {
        var host = new LocalGameHost(TestKit.Db, Config());
        var result = await host.SubmitCommandAsync(1, new EndTurnCommand { Seat = 0 });
        Assert.False(result.Accepted);
        Assert.Equal(RuleErrorCode.InvalidCommand, result.Error!.Code);
    }

    [Fact]
    public void Views_never_contain_opponent_card_ids()
    {
        var host = new LocalGameHost(TestKit.Db, Config());
        var view = host.GetView(0);
        Assert.Equal(7, view.Opponent.HandCount); // 6 + coin — count only, no list exists on the type
        Assert.All(view.Self.Hand, c => Assert.False(string.IsNullOrEmpty(c.CardId)));
    }

    [Fact]
    public async Task Config_plus_command_log_replays_to_the_same_outcome()
    {
        var host = new LocalGameHost(TestKit.Db, Config(seed: 777));
        await host.SubmitCommandAsync(0, new EndTurnCommand { Seat = 0 });
        await host.SubmitCommandAsync(1, new EndTurnCommand { Seat = 1 });
        await host.SubmitCommandAsync(0, new EndTurnCommand { Seat = 0 });

        var replay = new LocalGameHost(TestKit.Db, host.Config);
        foreach (var cmd in host.CommandLog)
        {
            var result = await replay.SubmitCommandAsync(cmd.Seat, cmd);
            Assert.True(result.Accepted, $"Replay diverged on {cmd.GetType().Name}: {result.Error?.Message}");
        }

        var original = host.GetView(0);
        var replayed = replay.GetView(0);
        Assert.Equal(original.TurnNumber, replayed.TurnNumber);
        Assert.Equal(original.Self.Hand.Select(c => c.CardId), replayed.Self.Hand.Select(c => c.CardId));
        Assert.Equal(original.Self.LeaderHp, replayed.Self.LeaderHp);
        Assert.Equal(original.Opponent.LeaderHp, replayed.Opponent.LeaderHp);
    }

    [Fact]
    public async Task Late_subscribers_can_catch_up_from_the_event_log()
    {
        var host = new LocalGameHost(TestKit.Db, Config());
        await host.SubmitCommandAsync(0, new EndTurnCommand { Seat = 0 });

        var log = host.GetEventLog(0);
        Assert.Contains(log, e => e is GameStartedEvent);
        Assert.Contains(log, e => e is TurnStartedEvent { Seat: 1 });
        // Redaction applies to the log too.
        Assert.All(log.OfType<CardDrawnEvent>().Where(e => e.Seat == 1), e => Assert.Null(e.CardId));
    }
}
