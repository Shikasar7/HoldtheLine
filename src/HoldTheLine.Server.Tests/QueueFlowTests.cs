using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Server.Data;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B2 acceptance: the queue pairs two players into a ranked match, and finishing it settles
/// ELO consistently on both sides and records it on the ladder.</summary>
public class QueueFlowTests
{
    private static Hello Hello(string guest, string name) => new()
    {
        GuestId = guest,
        Secret = "secret-" + guest,
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    [Fact]
    public async Task Queue_pairs_players_and_settles_elo()
    {
        await using var server = await RunningServer.StartAsync();
        await using var a = new GameServerClient(new WebSocketTransport());
        await using var b = new GameServerClient(new WebSocketTransport());

        var aStarted = Tcs<MatchStarted>();
        var bStarted = Tcs<MatchStarted>();
        var aRating = Tcs<RatingChange>();
        var bRating = Tcs<RatingChange>();
        a.MessageReceived += m => { if (m is MatchStarted ms) aStarted.TrySetResult(ms); else if (m is RatingChange rc) aRating.TrySetResult(rc); };
        b.MessageReceived += m => { if (m is MatchStarted ms) bStarted.TrySetResult(ms); else if (m is RatingChange rc) bRating.TrySetResult(rc); };

        await a.ConnectAsync(server.Ws, Hello("gA", "Alice"));
        await b.ConnectAsync(server.Ws, Hello("gB", "Bob"));

        await a.SendAsync(new JoinQueue { DeckId = "iron_wall" });
        await b.SendAsync(new JoinQueue { DeckId = "wildpack_hunt" });

        var aMs = await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Alice concedes → Bob wins.
        await a.SendAsync(new SubmitCommand { Command = new ConcedeCommand { Seat = aMs.Seat } });

        var aRc = await aRating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bRc = await bRating.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1000, aRc.Old);
        Assert.True(aRc.New < 1000, $"loser should drop, got {aRc.New}");
        Assert.True(bRc.New > 1000, $"winner should climb, got {bRc.New}");
        Assert.Equal(aRc.New - 1000, 1000 - bRc.New); // symmetric at equal starting ratings

        var ladder = server.Service<LadderStore>();
        Assert.Equal((bRc.New, 1, 0), ladder.Get("gB"));
        Assert.Equal((aRc.New, 0, 1), ladder.Get("gA"));
        Assert.Equal("gB", ladder.Top(10)[0].GuestId);
    }

    /// <summary>The "quit to menu → sudden loss next game" bug: a client that left mid-match without
    /// conceding and re-queued on the same lobby socket used to leave the old match running — its turn
    /// clock forfeited it minutes later, pushing the loss into the middle of the player's NEXT match.
    /// Now join_queue settles a still-live match as an immediate concede.</summary>
    [Fact]
    public async Task Requeue_during_live_match_concedes_it_immediately()
    {
        await using var server = await RunningServer.StartAsync();
        await using var a = new GameServerClient(new WebSocketTransport());
        await using var b = new GameServerClient(new WebSocketTransport());
        await using var c = new GameServerClient(new WebSocketTransport());

        var aStarted = Tcs<MatchStarted>();
        var aSecondStarted = Tcs<MatchStarted>();
        var bStarted = Tcs<MatchStarted>();
        var bEnded = Tcs<MatchEnded>();
        var cStarted = Tcs<MatchStarted>();
        int aStarts = 0; // MessageReceived fires on a single receive-loop thread per client
        a.MessageReceived += m => { if (m is MatchStarted ms) { if (++aStarts == 1) aStarted.TrySetResult(ms); else aSecondStarted.TrySetResult(ms); } };
        b.MessageReceived += m => { if (m is MatchStarted ms) bStarted.TrySetResult(ms); else if (m is MatchEnded me) bEnded.TrySetResult(me); };
        c.MessageReceived += m => { if (m is MatchStarted ms) cStarted.TrySetResult(ms); };

        await a.ConnectAsync(server.Ws, Hello("gA", "Alice"));
        await b.ConnectAsync(server.Ws, Hello("gB", "Bob"));
        await c.ConnectAsync(server.Ws, Hello("gC", "Cara"));

        await a.SendAsync(new JoinQueue { DeckId = "iron_wall" });
        await b.SendAsync(new JoinQueue { DeckId = "wildpack_hunt" });
        var aMs = await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bMs = await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Alice quits to the menu WITHOUT conceding (old-client behaviour) and queues again on the same socket.
        await a.SendAsync(new JoinQueue { DeckId = "iron_wall" });

        // The live match settles NOW as Alice's concede — Bob wins here, not mid-next-game.
        var ended = await bEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("concede", ended.Reason);
        Assert.Equal(bMs.Seat, ended.WinnerSeat);

        // Alice's requeue stayed intact: Cara joins and a FRESH match starts for the pair.
        await c.SendAsync(new JoinQueue { DeckId = "wildpack_hunt" });
        var aSecond = await aSecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(aMs.ResumeToken, aSecond.ResumeToken);

        var ladder = server.Service<LadderStore>();
        Assert.True(ladder.Get("gB").Rating > 1000, "Bob should have banked the win from the abandoned match");
        Assert.True(ladder.Get("gA").Rating < 1000, "Alice should have taken the loss for quitting");
    }

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
