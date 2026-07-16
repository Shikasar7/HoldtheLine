using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.State;

// Self-play smoke simulator (plan §6): random-legal-move bots. This is NOT an AI — it exists to
// exercise the engine end-to-end and to print the first balance dashboard (win rates, game length).
// Usage: dotnet run --project src/HoldTheLine.Sim -- [games] [seed]

int games = args.Length > 0 ? int.Parse(args[0]) : 200;
ulong seed = args.Length > 1 ? ulong.Parse(args[1]) : 20260716UL;
const int TurnLimit = 200;

var db = CardDatabase.LoadFromDirectory(FindCardsDir());
var resolver = new Resolver(db);
var pool = db.All.Where(c => c.Rarity != Rarity.Token).Select(c => c.Id).ToList();
var rng = new DeterministicRng(seed);

int[] wins = new int[2];
int draws = 0, timeouts = 0;
long totalTurns = 0, totalCommands = 0;

for (int g = 0; g < games; g++)
{
    var (state, _) = GameFactory.CreateGame(new MatchConfig
    {
        Seed = rng.NextUInt64(),
        Deck0 = RandomDeck(),
        Deck1 = RandomDeck(),
    }, db);

    while (state.Result is null && state.TurnNumber <= TurnLimit)
    {
        var legal = CommandEnumerator.LegalCommands(state, db);
        var nonEnd = legal.Where(c => c is not EndTurnCommand).ToList();
        // 85% act / 15% pass keeps games moving without pure thrashing.
        Command pick = nonEnd.Count > 0 && rng.NextInt(100) < 85
            ? nonEnd[rng.NextInt(nonEnd.Count)]
            : new EndTurnCommand { Seat = state.ActiveSeat };

        var result = resolver.Execute(state, pick);
        if (!result.Success)
            throw new InvalidOperationException($"Enumerated command rejected: {result.Error!.Code} {result.Error.Message}");
        state = result.State!;
        totalCommands++;
    }

    if (state.Result is null) { timeouts++; }
    else if (state.Result.WinnerSeat < 0) { draws++; }
    else { wins[state.Result.WinnerSeat]++; }
    totalTurns += Math.Min(state.TurnNumber, TurnLimit);
}

Console.WriteLine($"games={games} seed={seed}");
Console.WriteLine($"seat0 wins={wins[0]} ({100.0 * wins[0] / games:F1}%)  seat1 wins={wins[1]} ({100.0 * wins[1] / games:F1}%)");
Console.WriteLine($"draws={draws} timeouts(>{TurnLimit} turns)={timeouts}");
Console.WriteLine($"avg turns/game={(double)totalTurns / games:F1}  avg commands/game={(double)totalCommands / games:F1}");

List<string> RandomDeck()
{
    var deck = new List<string>(30);
    for (int i = 0; i < 30; i++)
        deck.Add(pool[rng.NextInt(pool.Count)]);
    return deck;
}

static string FindCardsDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "game", "data", "cards");
        if (Directory.Exists(candidate))
            return candidate;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("game/data/cards not found above " + AppContext.BaseDirectory);
}
