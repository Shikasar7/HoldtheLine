using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.Serialization;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>起手重抽 (mulligan, docs/11 §8) server-flow coverage over a live WebSocket: S1–S4 + S6.
/// The server is started with mulligan ON (production default is on; the test harness defaults it off,
/// so these opt in).</summary>
public class MulliganFlowTests
{
    private static Hello NewHello(string name) => new()
    {
        GuestId = "", Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion, RulesVersion = RulesInfo.Version,
    };

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static MulliganCommand KeepAll(int seat) => new() { Seat = seat, ReplacedEntityIds = [] };

    // S1 — the turn clock does not start until BOTH seats finish mulliganing; then it opens for FirstSeat.
    [Fact]
    public async Task Turn_clock_waits_for_both_mulligans()
    {
        await using var server = await RunningServer.StartAsync(mulliganEnabled: true);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var aMs = Tcs<MatchStarted>();
        var bMs = Tcs<MatchStarted>();
        var turnTimer = Tcs<TurnTimer>();
        var roomCode = Tcs<string>();
        clientA.MessageReceived += m =>
        {
            if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code);
            if (m is MatchStarted ms) aMs.TrySetResult(ms);
            if (m is TurnTimer tt) turnTimer.TrySetResult(tt);
        };
        clientB.MessageReceived += m => { if (m is MatchStarted ms) bMs.TrySetResult(ms); };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, seatA); // creator seats first
        var startedA = await aMs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var startedB = await bMs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Both openings carry the mulligan clock and put both seats into the mulligan.
        Assert.NotNull(startedA.MulliganSecondsLeft);
        Assert.NotNull(startedB.MulliganSecondsLeft);
        Assert.True(startedA.View.MulliganPending);
        Assert.True(startedB.View.MulliganPending);
        int firstSeat = startedA.View.ActiveSeat;

        // Seat 0 finishes first; the turn clock must still be silent.
        Assert.True((await hostA.SubmitCommandAsync(0, KeepAll(0))).Accepted);
        Assert.False(turnTimer.Task.IsCompleted);

        // Seat 1 finishes → the first turn clock opens, for FirstSeat.
        Assert.True((await hostB.SubmitCommandAsync(1, KeepAll(1))).Accepted);
        var tt = await turnTimer.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstSeat, tt.Seat);
    }

    // S2 — the mulligan clock expiring auto-keeps for anyone who didn't choose, and the match proceeds to
    // the first turn with NO forfeit (a mulligan timeout is benign, docs/11 D9).
    [Fact]
    public async Task Mulligan_timeout_auto_keeps_and_does_not_forfeit()
    {
        await using var server = await RunningServer.StartAsync(mulliganEnabled: true, mulliganSeconds: 1);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var roomCode = Tcs<string>();
        var turnTimer = Tcs<TurnTimer>();
        var ended = Tcs<MatchEnded>();
        clientA.MessageReceived += m =>
        {
            if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code);
            if (m is TurnTimer tt) turnTimer.TrySetResult(tt);
            if (m is MatchEnded me) ended.TrySetResult(me);
        };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });
        await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Nobody submits — after ~1s the server auto-keeps both and opens the first turn clock.
        var tt = await turnTimer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(tt.SecondsLeft > 0);
        Assert.False(ended.Task.IsCompleted); // benign: no forfeit
    }

    // S3 — reconnecting mid-mulligan restores the phase (ResyncOk carries the clock + MulliganPending), and
    // the returning player can still submit its mulligan.
    [Fact]
    public async Task Reconnecting_mid_mulligan_restores_the_phase()
    {
        await using var server = await RunningServer.StartAsync(mulliganEnabled: true);
        var helloA = NewHello("alice");
        var helloB = NewHello("bob");
        await using var clientA = new GameServerClient(() => new WebSocketTransport());
        await using var clientB = new GameServerClient(() => new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);

        var roomCode = Tcs<string>();
        var resync = Tcs<ResyncOk>();
        clientA.MessageReceived += m =>
        {
            if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code);
            if (m is ResyncOk rs) resync.TrySetResult(rs);
        };

        await clientA.ConnectAsync(server.Ws, helloA);
        await clientB.ConnectAsync(server.Ws, helloB);
        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });
        await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

        hostA.EnableReconnect(helloA);
        var reconnected = Tcs<bool>();
        bool dropped = false;
        hostA.ConnectionStateChanged += s =>
        {
            if (s == ConnectionState.Reconnecting) dropped = true;
            if (s == ConnectionState.Connected && dropped) reconnected.TrySetResult(true);
        };

        // Drop A before it has mulliganed; require a transparent reconnect back into the phase.
        await clientA.SimulateDropAsync();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var rs = await resync.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(rs.MulliganSecondsLeft);
        Assert.True(rs.View.MulliganPending);

        // The mulligan is still available after the round-trip.
        Assert.True((await hostA.SubmitCommandAsync(hostA.Seat, KeepAll(hostA.Seat))).Accepted);
    }

    // S4 — the on-disk command log has MulliganEnabled=true + the seats' mulligan commands, and replaying
    // config + log through a fresh host reproduces the winner.
    [Fact]
    public async Task Command_log_with_mulligan_replays_to_the_same_result()
    {
        var logDir = Path.Combine(Path.GetTempPath(), "htl-mull-" + Guid.NewGuid().ToString("N"));
        try
        {
            int serverWinner;
            await using (var server = await RunningServer.StartAsync(mulliganEnabled: true, commandLogDir: logDir))
            {
                await using var clientA = new GameServerClient(new WebSocketTransport());
                await using var clientB = new GameServerClient(new WebSocketTransport());
                var hostA = new RemoteGameHost(clientA);
                var hostB = new RemoteGameHost(clientB);

                var roomCode = Tcs<string>();
                clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

                await clientA.ConnectAsync(server.Ws, NewHello("alice"));
                await clientB.ConnectAsync(server.Ws, NewHello("bob"));
                await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
                var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });
                await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var winners = await Task.WhenAll(
                    new NetworkBotDriver(hostA, NetworkBotDriver.RandomLegal(31)).RunAsync(cts.Token),
                    new NetworkBotDriver(hostB, NetworkBotDriver.RandomLegal(32)).RunAsync(cts.Token));
                serverWinner = winners[0];
            }

            var file = Directory.EnumerateFiles(logDir, "match-*.jsonl").Single();
            var lines = File.ReadAllLines(file);
            var config = RulesJson.Deserialize<MatchConfig>(lines[0]);
            Assert.True(config.MulliganEnabled); // the phase really ran
            Assert.True(lines.Skip(1).Count(l => l.Contains("\"$type\":\"mulligan\"")) >= 2); // both seats logged a mulligan

            var content = GameContent.Load();
            var replay = new LocalGameHost(content.Cards, content.Leaders, config, loopbackSerialization: false);
            foreach (var line in lines.Skip(1))
            {
                var cmd = RulesJson.Deserialize<Command>(line);
                Assert.True((await replay.SubmitCommandAsync(cmd.Seat, cmd)).Accepted, $"logged command rejected on replay");
            }
            Assert.Equal(serverWinner, replay.GetView(0).Result?.WinnerSeat);
        }
        finally
        {
            if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
        }
    }

    // S6 — the version gate rejects an old protocol OR old rules version (docs/11 D10).
    [Fact]
    public async Task Old_protocol_or_rules_version_is_rejected()
    {
        await using var server = await RunningServer.StartAsync();

        await using (var t = new WebSocketTransport())
        {
            await t.ConnectAsync(server.Ws, CancellationToken.None);
            await t.SendTextAsync(ProtocolJson.Encode(
                new Hello { GuestId = "", Name = "x", ProtocolVersion = 3, RulesVersion = RulesInfo.Version }), CancellationToken.None);
            var reply = await t.ReceiveTextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("version_mismatch", Assert.IsType<ErrorMsg>(ProtocolJson.TryDecodeServer(reply!)).Code);
        }

        await using (var t = new WebSocketTransport())
        {
            await t.ConnectAsync(server.Ws, CancellationToken.None);
            await t.SendTextAsync(ProtocolJson.Encode(
                new Hello { GuestId = "", Name = "x", ProtocolVersion = ProtocolConstants.ProtocolVersion, RulesVersion = "0.3.0" }), CancellationToken.None);
            var reply = await t.ReceiveTextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("version_mismatch", Assert.IsType<ErrorMsg>(ProtocolJson.TryDecodeServer(reply!)).Code);
        }
    }
}
