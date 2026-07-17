using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;

// Networked console client (plan §8-3). N0 scope: prove the handshake + room lifecycle by hand —
// connect, hello, then create or join a room and print the match_started snapshot. N1 turns this
// into a full GreedyAi opponent that plays a match over the wire.
//
// Usage:
//   dotnet run --project src/HoldTheLine.BotClient -- --create [--deck iron_wall] [--url ws://127.0.0.1:5210/ws] [--name Alice]
//   dotnet run --project src/HoldTheLine.BotClient -- --join ABC123 [--deck wildpack_hunt] [--url ...] [--name Bob]

string url = ArgValue("--url") ?? "ws://127.0.0.1:5210/ws";
string name = ArgValue("--name") ?? "bot";
string deck = ArgValue("--deck") ?? "iron_wall";
bool create = HasFlag("--create");
string? joinCode = ArgValue("--join");

if (!create && joinCode is null)
{
    Console.Error.WriteLine("Specify --create or --join <CODE>. See header for usage.");
    return 1;
}

await using var client = new GameServerClient(new WebSocketTransport());
using var done = new CancellationTokenSource(TimeSpan.FromMinutes(2));

client.MessageReceived += msg =>
{
    switch (msg)
    {
        case RoomCreated rc:
            Console.WriteLine($"[room] created — code: {rc.Code}  (share this with the other client)");
            break;
        case MatchStarted ms:
            Console.WriteLine($"[match] started — you are seat {ms.Seat}, vs {ms.OpponentName}");
            Console.WriteLine($"        hand={ms.View.Self.Hand.Count} leaderHp={ms.View.Self.LeaderHp} activeSeat={ms.View.ActiveSeat} yourTurn={ms.LegalCommands is not null}");
            done.CancelAfter(TimeSpan.FromSeconds(1));
            break;
        case ErrorMsg em:
            Console.Error.WriteLine($"[error] {em.Code}: {em.Message}");
            done.Cancel();
            break;
    }
};

Console.WriteLine($"Connecting to {url} as '{name}' ...");
var hello = new Hello
{
    GuestId = "",
    Name = name,
    ProtocolVersion = ProtocolConstants.ProtocolVersion,
    RulesVersion = RulesInfo.Version,
};
var ok = await client.ConnectAsync(new Uri(url), hello);
Console.WriteLine($"[hello] ok — server time {ok.ServerTimeUnixMs}");

if (create)
    await client.SendAsync(new CreateRoom { DeckId = deck });
else
    await client.SendAsync(new JoinRoom { Code = joinCode!, DeckId = deck });

try { await Task.Delay(Timeout.Infinite, done.Token); }
catch (OperationCanceledException) { }

Console.WriteLine("Done.");
return 0;

string? ArgValue(string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool HasFlag(string flag) => Array.IndexOf(args, flag) >= 0;
