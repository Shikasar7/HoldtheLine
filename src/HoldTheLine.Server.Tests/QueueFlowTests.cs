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

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
