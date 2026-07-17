using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>
/// N0 acceptance (plan §11): two real clients complete hello → create → join over a live WebSocket
/// and each receives its correctly-seated match_started. Plus the version-mismatch gate that keeps a
/// stale client from silently diverging (plan §4.1).
/// </summary>
public class HandshakeTests
{
    private static Hello NewHello(string name) => new()
    {
        GuestId = "",
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    [Fact]
    public async Task Two_clients_handshake_to_match_started()
    {
        await using var server = await RunningServer.StartAsync();
        await using var a = new GameServerClient(new WebSocketTransport());
        await using var b = new GameServerClient(new WebSocketTransport());

        var roomCode = Tcs<string>();
        var aStarted = Tcs<MatchStarted>();
        var bStarted = Tcs<MatchStarted>();

        a.MessageReceived += m =>
        {
            switch (m)
            {
                case RoomCreated rc: roomCode.TrySetResult(rc.Code); break;
                case MatchStarted ms: aStarted.TrySetResult(ms); break;
                case ErrorMsg e: Fail(roomCode, aStarted, e); break;
            }
        };
        b.MessageReceived += m =>
        {
            switch (m)
            {
                case MatchStarted ms: bStarted.TrySetResult(ms); break;
                case ErrorMsg e: bStarted.TrySetException(new Xunit.Sdk.XunitException($"server error: {e.Code}")); break;
            }
        };

        await a.ConnectAsync(server.Ws, NewHello("alice"));
        await b.ConnectAsync(server.Ws, NewHello("bob"));

        await a.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await b.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        var aMs = await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bMs = await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, aMs.Seat);
        Assert.Equal(1, bMs.Seat);
        Assert.Equal("bob", aMs.OpponentName);
        Assert.Equal("alice", bMs.OpponentName);
        Assert.NotNull(aMs.View);
        Assert.NotNull(bMs.View);
        Assert.False(string.IsNullOrEmpty(aMs.ResumeToken));
        Assert.NotEqual(aMs.ResumeToken, bMs.ResumeToken);

        // Exactly one seat is on the move at the opening, and its view agrees.
        Assert.True((aMs.LegalCommands is not null) ^ (bMs.LegalCommands is not null));
        var mover = aMs.LegalCommands is not null ? aMs : bMs;
        Assert.Equal(mover.Seat, mover.View.ActiveSeat);
        Assert.NotEmpty(mover.LegalCommands!);
    }

    [Fact]
    public async Task Hello_with_wrong_protocol_version_is_rejected()
    {
        await using var server = await RunningServer.StartAsync();
        await using var t = new WebSocketTransport();
        await t.ConnectAsync(server.Ws, CancellationToken.None);

        var badHello = new Hello { GuestId = "", Name = "x", ProtocolVersion = 999, RulesVersion = RulesInfo.Version };
        await t.SendTextAsync(ProtocolJson.Encode(badHello), CancellationToken.None);

        var reply = await t.ReceiveTextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(reply);
        var err = Assert.IsType<ErrorMsg>(ProtocolJson.TryDecodeServer(reply!));
        Assert.Equal("version_mismatch", err.Code);
    }

    [Fact]
    public async Task Joining_a_missing_room_returns_error_not_crash()
    {
        await using var server = await RunningServer.StartAsync();
        await using var c = new GameServerClient(new WebSocketTransport());

        var error = Tcs<ErrorMsg>();
        c.MessageReceived += m => { if (m is ErrorMsg e) error.TrySetResult(e); };

        await c.ConnectAsync(server.Ws, NewHello("lonely"));
        await c.SendAsync(new JoinRoom { Code = "ZZZZZZ", DeckId = "iron_wall" });

        var err = await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("room_not_found", err.Code);
    }

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Fail<T1, T2>(TaskCompletionSource<T1> a, TaskCompletionSource<T2> b, ErrorMsg e)
    {
        var ex = new Xunit.Sdk.XunitException($"server error: {e.Code}");
        a.TrySetException(ex);
        b.TrySetException(ex);
    }
}
