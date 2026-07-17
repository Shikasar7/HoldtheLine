using System.Collections.Concurrent;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Server.Rooms;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>
/// The N1 moat (plan §8-2): two networked bots play a full match over a live server, then we assert
/// the three properties the whole M2 architecture rests on —
///   (1) hidden information stays hidden (each seat's event stream never carries the opponent's card ids),
///   (2) both clients and the server agree on the outcome, and
///   (3) determinism survives the network: replaying MatchConfig + the server's command log through a
///       fresh LocalGameHost reproduces the exact result.
/// </summary>
public class NetworkedMatchTests
{
    private static Hello NewHello(string name) => new()
    {
        GuestId = "",
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    [Fact]
    public async Task Two_bots_play_a_full_match_redacted_and_replay_deterministic()
    {
        await using var server = await RunningServer.StartAsync();
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        // Capture every event each seat is dispatched, to check redaction after the game.
        var eventsA = new ConcurrentQueue<GameEvent>();
        var eventsB = new ConcurrentQueue<GameEvent>();
        hostA.Subscribe(0, eventsA.Enqueue);
        hostB.Subscribe(0, eventsB.Enqueue);

        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));

        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, seatA);
        Assert.Equal(1, seatB);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var driverA = new NetworkBotDriver(hostA, NetworkBotDriver.RandomLegal(seed: 101));
        var driverB = new NetworkBotDriver(hostB, NetworkBotDriver.RandomLegal(seed: 202));
        var winners = await Task.WhenAll(driverA.RunAsync(cts.Token), driverB.RunAsync(cts.Token));

        // (2) Both clients agree, and the server agrees.
        Assert.Equal(winners[0], winners[1]);
        var room = server.Service<RoomManager>().FindRoom(code);
        Assert.NotNull(room);
        var session = room!.Session!;
        var serverResult = session.Host.GetView(0).Result;
        Assert.NotNull(serverResult);
        Assert.Equal(serverResult!.WinnerSeat, winners[0]);

        // (1) Redaction: seat A never saw seat B's drawn card ids, and vice versa.
        AssertNoOpponentCardIds(eventsA, ownerSeat: seatA);
        AssertNoOpponentCardIds(eventsB, ownerSeat: seatB);

        // (3) Determinism across the wire: replay config + command log → identical winner.
        var content = server.Service<GameContent>();
        var replay = new LocalGameHost(content.Cards, content.Leaders, session.Host.Config, loopbackSerialization: false);
        foreach (var command in session.Host.CommandLog)
        {
            var r = await replay.SubmitCommandAsync(command.Seat, command);
            Assert.True(r.Accepted, $"replay rejected a logged command: {r.Error?.Code}");
        }
        Assert.Equal(serverResult.WinnerSeat, replay.GetView(0).Result?.WinnerSeat);
    }

    private static void AssertNoOpponentCardIds(IEnumerable<GameEvent> events, int ownerSeat)
    {
        foreach (var drawn in events.OfType<CardDrawnEvent>())
            if (drawn.Seat != ownerSeat)
                Assert.True(drawn.CardId is null,
                    $"seat {ownerSeat} was leaked opponent card id '{drawn.CardId}' via a redacted draw event");
    }

    [Fact]
    public async Task Rejected_when_submitting_a_command_out_of_turn()
    {
        await using var server = await RunningServer.StartAsync();
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // The non-active seat tries to end the turn — the server must reject, not apply.
        var idleSeat = hostA.GetView(seatA).ActiveSeat == seatA ? (host: hostB, seat: seatB) : (host: hostA, seat: seatA);
        var result = await idleSeat.host.SubmitCommandAsync(idleSeat.seat, new EndTurnCommand { Seat = idleSeat.seat });

        Assert.False(result.Accepted);
        Assert.Equal(RuleErrorCode.NotYourTurn, result.Error!.Code);
    }
}
