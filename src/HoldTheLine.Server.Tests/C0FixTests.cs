using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Server.Rooms;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>Regressions for the three C0 correctness fixes from the Fable review.</summary>
public class C0FixTests
{
    private static Hello Hello(string guest, string name) => new()
    {
        GuestId = guest,
        Secret = "secret-" + guest,
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // C0-1: rating_change must reach the LIVE connection — a player who reconnected mid-match still sees ±分.
    [Fact]
    public async Task Rating_change_reaches_a_reconnected_player()
    {
        await using var server = await RunningServer.StartAsync();
        var helloA = Hello("gA", "Alice");
        var helloB = Hello("gB", "Bob");
        await using var a = new GameServerClient(() => new WebSocketTransport());
        await using var b = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(a);
        var hostB = new RemoteGameHost(b);

        bool dropped = false;
        var resynced = Tcs<bool>();
        var aRating = Tcs<RatingChange>();
        a.MessageReceived += m =>
        {
            if (m is ResyncOk && dropped) resynced.TrySetResult(true);
            if (m is RatingChange rc) aRating.TrySetResult(rc);
        };
        a.StateChanged += s => { if (s == ConnectionState.Reconnecting) dropped = true; };

        await a.ConnectAsync(server.Ws, helloA);
        await b.ConnectAsync(server.Ws, helloB);
        await a.SendAsync(new JoinQueue { DeckId = "iron_wall" });
        await b.SendAsync(new JoinQueue { DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        hostA.EnableReconnect(helloA);

        // Drop A and wait until the SERVER has re-attached it (ResyncOk means _conns[seatA] is swapped).
        await a.SimulateDropAsync();
        await resynced.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Bob concedes → Alice (the reconnected seat) wins and must receive her rating_change.
        await b.SendAsync(new SubmitCommand { Command = new ConcedeCommand { Seat = seatB } });

        var rc = await aRating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000, rc.Old);
        Assert.True(rc.New > 1000, $"winner's rating should climb, got {rc.New}");
        _ = seatA;
    }

    // C0-2: two windows sharing one identity.json (same guest_id) must never self-match.
    [Fact]
    public async Task Same_guest_id_is_never_self_paired()
    {
        await using var server = await RunningServer.StartAsync();
        await using var c1 = new GameServerClient(new WebSocketTransport());
        await using var c2 = new GameServerClient(new WebSocketTransport());
        await using var c3 = new GameServerClient(new WebSocketTransport());

        var s1 = Tcs<MatchStarted>();
        var s2 = Tcs<MatchStarted>();
        var s3 = Tcs<MatchStarted>();
        c1.MessageReceived += m => { if (m is MatchStarted ms) s1.TrySetResult(ms); };
        c2.MessageReceived += m => { if (m is MatchStarted ms) s2.TrySetResult(ms); };
        c3.MessageReceived += m => { if (m is MatchStarted ms) s3.TrySetResult(ms); };

        // c1 and c2 are the SAME identity (shared file); c3 is a distinct player.
        await c1.ConnectAsync(server.Ws, Hello("dup", "P"));
        await c2.ConnectAsync(server.Ws, Hello("dup", "P"));
        await c1.SendAsync(new JoinQueue { DeckId = "iron_wall" });
        await c2.SendAsync(new JoinQueue { DeckId = "wildpack_hunt" });

        await Task.Delay(400); // TryPair ran on each join; the self-pair must have been refused
        Assert.False(s1.Task.IsCompleted);
        Assert.False(s2.Task.IsCompleted);

        // A real third player pairs with one of them.
        await c3.ConnectAsync(server.Ws, Hello("third", "Q"));
        await c3.SendAsync(new JoinQueue { DeckId = "iron_wall" });

        await s3.Task.WaitAsync(TimeSpan.FromSeconds(5));                       // the third player got a match
        await Task.WhenAny(s1.Task, s2.Task).WaitAsync(TimeSpan.FromSeconds(5)); // wait for its duplicate partner too
        Assert.True(s1.Task.IsCompleted ^ s2.Task.IsCompleted, "exactly one of the duplicates should match the third player");
    }

    // C0-3: a host deck deleted after create_room must not leave the room stuck "started, no session".
    [Fact]
    public async Task Deleted_host_deck_does_not_zombify_the_room()
    {
        var content = GameContent.Load();
        var iron = content.FindDeck("iron_wall")!;

        await using var server = await RunningServer.StartAsync();
        await using var a = new GameServerClient(new WebSocketTransport());
        await using var b = new GameServerClient(new WebSocketTransport());

        var saved = Tcs<DeckSaved>();
        var code = Tcs<string>();
        var bError = Tcs<ErrorMsg>();
        a.MessageReceived += m => { if (m is DeckSaved ds) saved.TrySetResult(ds); else if (m is RoomCreated rc) code.TrySetResult(rc.Code); };
        b.MessageReceived += m => { if (m is ErrorMsg e) bError.TrySetResult(e); };

        await a.ConnectAsync(server.Ws, Hello("gA", "Alice"));
        await b.ConnectAsync(server.Ws, Hello("gB", "Bob"));

        await a.SendAsync(new SaveDeck { Name = "Doomed", Leader = iron.Leader, CardIds = iron.Expand().ToList() });
        var deckId = (await saved.Task.WaitAsync(TimeSpan.FromSeconds(5))).DeckId;

        await a.SendAsync(new CreateRoom { DeckId = deckId });
        var room = await code.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Alice deletes the deck the room is built on, THEN Bob tries to join.
        await a.SendAsync(new DeleteDeck { DeckId = deckId });
        await Task.Delay(100); // let the delete land
        await b.SendAsync(new JoinRoom { Code = room, DeckId = "wildpack_hunt" });

        var err = await bError.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("unknown_deck", err.Code);

        var serverRoom = server.Service<RoomManager>().FindRoom(room);
        Assert.NotNull(serverRoom);
        Assert.False(serverRoom!.Started); // not the "started, no session" zombie
        Assert.Null(serverRoom.Session);
    }
}
