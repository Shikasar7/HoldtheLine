using System.Diagnostics;
using HoldTheLine.Net.Client;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;

// Networked console client (plan §8-3).
//   Handshake demo (N0):
//     dotnet run --project src/HoldTheLine.BotClient -- --create [--deck iron_wall] [--url ws://127.0.0.1:5210/ws] [--name Alice]
//     dotnet run --project src/HoldTheLine.BotClient -- --join ABC123 [--deck wildpack_hunt] [--url ...] [--name Bob]
//   Self-play soak (N1): two networked bots play N full matches against the server, no crash/hang.
//     dotnet run -c Release --project src/HoldTheLine.BotClient -- --selfplay 1000 [--url ...] [--deck-a iron_wall] [--deck-b wildpack_hunt]

string url = ArgValue("--url") ?? "ws://127.0.0.1:5210/ws";
if (HasFlag("--debug")) NetworkBotDriver.DebugLog = true;

if (ArgValue("--selfplay") is { } nStr && int.TryParse(nStr, out int games))
    return await SelfPlayAsync(new Uri(url), games,
        ArgValue("--deck-a") ?? "iron_wall", ArgValue("--deck-b") ?? "wildpack_hunt");

if (HasFlag("--play") && (HasFlag("--create") || ArgValue("--join") is not null))
    return await PlayAsync(new Uri(url), ArgValue("--join"), ArgValue("--deck") ?? "wildpack_hunt", ArgValue("--name") ?? "bot");

return await HandshakeDemoAsync(new Uri(url));

// Create or join a room and play it out with the random-legal policy (manual N2 testing vs a Godot
// client). Waits generously so a slow human opponent doesn't trip the timeout.
async Task<int> PlayAsync(Uri serverUri, string? joinCode, string deck, string name)
{
    await using var client = new GameServerClient(new WebSocketTransport());
    var host = new RemoteGameHost(client);

    if (joinCode is null)
        client.MessageReceived += m => { if (m is RoomCreated rc) Console.WriteLine($"[room] created — code: {rc.Code}"); };

    await client.ConnectAsync(serverUri, NewHello(name));
    if (joinCode is null)
        await client.SendAsync(new CreateRoom { DeckId = deck });
    else
        await client.SendAsync(new JoinRoom { Code = joinCode, DeckId = deck });

    int seat = await host.WaitForMatchAsync().WaitAsync(TimeSpan.FromMinutes(10));
    Console.WriteLine($"[match] started as seat {seat}");

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
    var driver = new NetworkBotDriver(host, NetworkBotDriver.RandomLegal(seed: 4242));
    try
    {
        int winner = await driver.RunAsync(cts.Token);
        Console.WriteLine($"[match] over — winner seat {winner}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[match] driver cancelled (timeout)");
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------

async Task<int> HandshakeDemoAsync(Uri serverUri)
{
    string name = ArgValue("--name") ?? "bot";
    string deck = ArgValue("--deck") ?? "iron_wall";
    bool create = HasFlag("--create");
    string? joinCode = ArgValue("--join");

    if (!create && joinCode is null)
    {
        Console.Error.WriteLine("Specify --create, --join <CODE>, or --selfplay <N>. See header for usage.");
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

    Console.WriteLine($"Connecting to {serverUri} as '{name}' ...");
    var ok = await client.ConnectAsync(serverUri, NewHello(name));
    Console.WriteLine($"[hello] ok — server time {ok.ServerTimeUnixMs}");

    if (create)
        await client.SendAsync(new CreateRoom { DeckId = deck });
    else
        await client.SendAsync(new JoinRoom { Code = joinCode!, DeckId = deck });

    try { await Task.Delay(Timeout.Infinite, done.Token); }
    catch (OperationCanceledException) { }
    Console.WriteLine("Done.");
    return 0;
}

async Task<int> SelfPlayAsync(Uri serverUri, int games, string deckA, string deckB)
{
    int[] seatWins = new int[2];
    int draws = 0, failures = 0;
    var sw = Stopwatch.StartNew();

    for (int g = 0; g < games; g++)
    {
        try
        {
            int winner = await PlayOneAsync(serverUri, deckA, deckB, seedA: g * 2, seedB: g * 2 + 1);
            if (winner < 0) draws++; else seatWins[winner]++;
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"[game {g}] FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        if ((g + 1) % 25 == 0)
            Console.WriteLine($"  {g + 1}/{games}  seat0={seatWins[0]} seat1={seatWins[1]} draws={draws} fails={failures}  ({sw.Elapsed.TotalSeconds:F0}s)");
    }

    Console.WriteLine($"\nself-play done: {games} games in {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"seat0 wins={seatWins[0]}  seat1 wins={seatWins[1]}  draws={draws}  failures={failures}");
    return failures == 0 ? 0 : 1;
}

async Task<int> PlayOneAsync(Uri serverUri, string deckA, string deckB, int seedA, int seedB)
{
    await using var clientA = new GameServerClient(new WebSocketTransport());
    await using var clientB = new GameServerClient(new WebSocketTransport());
    var hostA = new RemoteGameHost(clientA);
    var hostB = new RemoteGameHost(clientB);

    var roomCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    clientA.MessageReceived += m => { if (m is RoomCreated rc) roomCode.TrySetResult(rc.Code); };

    await clientA.ConnectAsync(serverUri, NewHello("selfA"));
    await clientB.ConnectAsync(serverUri, NewHello("selfB"));

    await clientA.SendAsync(new CreateRoom { DeckId = deckA });
    var code = await roomCode.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await clientB.SendAsync(new JoinRoom { Code = code, DeckId = deckB });

    int seatA = await hostA.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await hostB.WaitForMatchAsync().WaitAsync(TimeSpan.FromSeconds(10));

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var driverA = new NetworkBotDriver(hostA, NetworkBotDriver.RandomLegal(seedA));
    var driverB = new NetworkBotDriver(hostB, NetworkBotDriver.RandomLegal(seedB));
    await Task.WhenAll(driverA.RunAsync(cts.Token), driverB.RunAsync(cts.Token));

    return hostA.GetView(seatA).Result?.WinnerSeat ?? -1;
}

Hello NewHello(string name) => new()
{
    GuestId = "",
    Name = name,
    ProtocolVersion = ProtocolConstants.ProtocolVersion,
    RulesVersion = RulesInfo.Version,
};

string? ArgValue(string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool HasFlag(string flag) => Array.IndexOf(args, flag) >= 0;
