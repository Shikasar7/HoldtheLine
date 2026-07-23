using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Events;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>
/// 开发者测试修改器 (dev-only) over the wire: the server honours a <c>dev_cheat</c> in a friend room when
/// <see cref="ServerOptions.DevCheatsEnabled"/> is on, and in ranked only when the additional
/// <see cref="ServerOptions.DevCheatsAllowRanked"/> switch is on. Results use the ordinary event path.
/// Asserts deck privacy, tutor/refill behaviour, and both the master and ranked-specific gates.
/// </summary>
public class DevCheatTests
{
    private static Hello NewHello(string name) => new()
    {
        GuestId = "",
        Name = name,
        ProtocolVersion = ProtocolConstants.ProtocolVersion,
        RulesVersion = RulesInfo.Version,
    };

    private static async Task<(int SeatA, int SeatB)> StartFriendRoomAsync(
        RunningServer server, GameServerClient clientA, GameServerClient clientB, RemoteGameHost hostA, RemoteGameHost hostB)
    {
        var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

        await clientA.ConnectAsync(server.Ws, NewHello("alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("bob"));

        await clientA.SendAsync(new CreateRoom { DeckId = "iron_wall" });
        var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clientB.SendAsync(new JoinRoom { Code = code, DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return (seatA, seatB);
    }

    private static async Task<(int SeatA, int SeatB)> StartRankedAsync(
        RunningServer server, GameServerClient clientA, GameServerClient clientB, RemoteGameHost hostA, RemoteGameHost hostB)
    {
        await clientA.ConnectAsync(server.Ws, NewHello("ranked-alice"));
        await clientB.ConnectAsync(server.Ws, NewHello("ranked-bob"));

        await clientA.SendAsync(new JoinQueue { DeckId = "iron_wall" });
        await clientB.SendAsync(new JoinQueue { DeckId = "wildpack_hunt" });

        int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int seatB = await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return (seatA, seatB);
    }

    [Fact]
    public async Task Tutor_and_refill_work_in_a_friend_room_when_enabled()
    {
        await using var server = await RunningServer.StartAsync(devCheatsEnabled: true);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);
        var (seatA, _) = await StartFriendRoomAsync(server, clientA, clientB, hostA, hostB);

        // (1) list_deck → the requester's own deck, card ids visible (own hidden info).
        var deckTcs = new TaskCompletionSource<IReadOnlyList<(int EntityId, string CardId)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostA.DevDeckReceived += cards => deckTcs.TrySetResult(cards);
        await hostA.RequestDevDeckAsync();
        var deck = await deckTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(deck);
        Assert.All(deck, c => Assert.False(string.IsNullOrEmpty(c.CardId)));

        // (2) tutor the first deck card → CardDrawnEvent on seat A (id visible), redacted on seat B.
        int targetId = deck[0].EntityId;
        string targetCardId = deck[0].CardId;
        var drawnA = new TaskCompletionSource<CardDrawnEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostA.Subscribe(0, e => { if (e is CardDrawnEvent cd && cd.CardEntityId == targetId) drawnA.TrySetResult(cd); });
        var drawnB = new TaskCompletionSource<CardDrawnEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostB.Subscribe(0, e => { if (e is CardDrawnEvent cd && cd.Seat == seatA) drawnB.TrySetResult(cd); });

        var before = hostA.GetView(seatA).Self;
        int handBefore = before.Hand.Count;
        int deckBefore = before.DeckCount;

        await hostA.DevTutorCardAsync(targetId);
        var evA = await drawnA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(targetCardId, evA.CardId);

        var after = hostA.GetView(seatA).Self;
        Assert.Equal(handBefore + 1, after.Hand.Count);
        Assert.Equal(deckBefore - 1, after.DeckCount);
        Assert.Contains(after.Hand, h => h.EntityId == targetId && h.CardId == targetCardId);

        var evB = await drawnB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(evB.CardId); // opponent sees a draw happened, not which card

        // (3) refill → mana ends at max, no dev error.
        bool devError = false;
        hostA.DevCheatFailed += _ => devError = true;
        var viewUpdated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnView(HoldTheLine.Rules.Hosting.PlayerView _) => viewUpdated.TrySetResult(true);
        hostA.ViewUpdated += OnView;
        await hostA.DevRefillManaAsync();
        await viewUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hostA.ViewUpdated -= OnView;
        var refilled = hostA.GetView(seatA).Self;
        Assert.Equal(refilled.ManaMax, refilled.Mana);
        Assert.False(devError);
    }

    [Fact]
    public async Task Dev_cheat_is_refused_when_the_server_flag_is_off()
    {
        await using var server = await RunningServer.StartAsync(devCheatsEnabled: false);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);
        var (seatA, _) = await StartFriendRoomAsync(server, clientA, clientB, hostA, hostB);

        int deckBefore = hostA.GetView(seatA).Self.DeckCount;

        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostA.DevCheatFailed += msg => failed.TrySetResult(msg);
        bool gotDeck = false;
        hostA.DevDeckReceived += _ => gotDeck = true;

        await hostA.RequestDevDeckAsync(); // even list_deck is gated → dev_disabled
        var reason = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(gotDeck);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(deckBefore, hostA.GetView(seatA).Self.DeckCount); // nothing changed
    }

    [Fact]
    public async Task Ranked_dev_cheat_requires_the_separate_ranked_switch()
    {
        await using var server = await RunningServer.StartAsync(devCheatsEnabled: true);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);
        var (seatA, _) = await StartRankedAsync(server, clientA, clientB, hostA, hostB);

        int deckBefore = hostA.GetView(seatA).Self.DeckCount;
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostA.DevCheatFailed += msg => failed.TrySetResult(msg);

        await hostA.RequestDevDeckAsync();
        string reason = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("排位测试开关未开启", reason);
        Assert.Equal(deckBefore, hostA.GetView(seatA).Self.DeckCount);
    }

    [Fact]
    public async Task Ranked_dev_cheat_works_when_both_switches_are_enabled()
    {
        await using var server = await RunningServer.StartAsync(
            devCheatsEnabled: true,
            devCheatsAllowRanked: true);
        await using var clientA = new GameServerClient(new WebSocketTransport());
        await using var clientB = new GameServerClient(new WebSocketTransport());
        var hostA = new RemoteGameHost(clientA);
        var hostB = new RemoteGameHost(clientB);
        var (seatA, _) = await StartRankedAsync(server, clientA, clientB, hostA, hostB);

        var deckTcs = new TaskCompletionSource<IReadOnlyList<(int EntityId, string CardId)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostA.DevDeckReceived += cards => deckTcs.TrySetResult(cards);
        await hostA.RequestDevDeckAsync();
        var deck = await deckTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(deck);

        int handBefore = hostA.GetView(seatA).Self.Hand.Count;
        int deckBefore = hostA.GetView(seatA).Self.DeckCount;
        int targetId = deck[0].EntityId;
        var drawn = new TaskCompletionSource<CardDrawnEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostA.Subscribe(0, e =>
        {
            if (e is CardDrawnEvent cd && cd.CardEntityId == targetId)
                drawn.TrySetResult(cd);
        });

        await hostA.DevTutorCardAsync(targetId);
        await drawn.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var after = hostA.GetView(seatA).Self;
        Assert.Equal(handBefore + 1, after.Hand.Count);
        Assert.Equal(deckBefore - 1, after.DeckCount);
    }
}
