using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Cards;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B1 acceptance (§4-3): illegal decks are rejected server-side, and a saved custom deck can
/// actually be taken into a match.</summary>
public class DeckFlowTests
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
    public async Task Illegal_decks_are_rejected()
    {
        var content = GameContent.Load();
        var iron = content.FindDeck("iron_wall")!;
        var legal = iron.Expand().ToList();
        var wildCard = content.Cards.All.First(c => c.Faction == "wildpack" && DeckValidator.MaxCopies(c.Rarity) >= 1).Id;

        var tooMany = legal.Append(legal[0]).ToList();                 // 31 cards
        var overCap = Enumerable.Repeat(legal[0], 30).ToList();        // 30 copies of one card
        var mixed = legal.ToList(); mixed[0] = wildCard;               // iron + one wildpack card → 2 factions

        await using var server = await RunningServer.StartAsync();
        await using var client = new GameServerClient(new WebSocketTransport());
        var pending = new TaskCompletionSource<ServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += m => { if (m is DeckSaved or DeckError) pending.TrySetResult(m); };

        await client.ConnectAsync(server.Ws, Hello("gA", "Alice"));

        async Task<DeckError> ExpectError(IReadOnlyList<string> cards)
        {
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.SendAsync(new SaveDeck { Name = "Test", Leader = iron.Leader, CardIds = cards });
            return Assert.IsType<DeckError>(await pending.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal("invalid_deck", (await ExpectError(tooMany)).Code);
        Assert.Equal("invalid_deck", (await ExpectError(overCap)).Code);
        Assert.Equal("invalid_deck", (await ExpectError(mixed)).Code);
    }

    [Fact]
    public async Task Save_a_custom_deck_then_take_it_into_a_match()
    {
        var content = GameContent.Load();
        var iron = content.FindDeck("iron_wall")!;

        await using var server = await RunningServer.StartAsync();
        await using var a = new GameServerClient(new WebSocketTransport());
        await using var b = new GameServerClient(new WebSocketTransport());

        var saved = Tcs<DeckSaved>();
        var code = Tcs<string>();
        var aStarted = Tcs<MatchStarted>();
        var bStarted = Tcs<MatchStarted>();
        a.MessageReceived += m =>
        {
            switch (m)
            {
                case DeckSaved ds: saved.TrySetResult(ds); break;
                case RoomCreated rc: code.TrySetResult(rc.Code); break;
                case MatchStarted ms: aStarted.TrySetResult(ms); break;
                case DeckError de: saved.TrySetException(new Xunit.Sdk.XunitException($"deck_error: {de.Code}")); break;
            }
        };
        b.MessageReceived += m => { if (m is MatchStarted ms) bStarted.TrySetResult(ms); };

        await a.ConnectAsync(server.Ws, Hello("gA", "Alice"));
        await b.ConnectAsync(server.Ws, Hello("gB", "Bob"));

        // Save Alice's own copy of the iron deck, then create a room using it.
        await a.SendAsync(new SaveDeck { Name = "Alice's Wall", Leader = iron.Leader, CardIds = iron.Expand().ToList() });
        var deckId = (await saved.Task.WaitAsync(TimeSpan.FromSeconds(5))).DeckId;

        await a.SendAsync(new CreateRoom { DeckId = deckId });
        var room = await code.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await b.SendAsync(new JoinRoom { Code = room, DeckId = "wildpack_hunt" });

        var aMs = await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, aMs.Seat);
        Assert.Equal(iron.Leader, aMs.View.Self.LeaderId); // Alice is playing her saved deck's leader
    }

    private static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
