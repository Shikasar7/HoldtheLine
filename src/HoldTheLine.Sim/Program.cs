using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.State;

// Self-play smoke simulator (plan §6). Random-legal-move bots — NOT an AI. Exercises the engine
// end-to-end (now incl. leader skills, summons, spatial orders) and prints a balance dashboard.
//
// Usage: dotnet run --project src/HoldTheLine.Sim -- [games] [seed] [mode]
//   mode = precon (default: iron_wall vs wildpack_hunt) | random

int games = args.Length > 0 ? int.Parse(args[0]) : 400;
ulong seed = args.Length > 1 ? ulong.Parse(args[1]) : 20260717UL;
string mode = args.Length > 2 ? args[2] : "precon";
const int TurnLimit = 300;

string root = FindDataRoot();
var db = CardDatabase.LoadFromDirectory(Path.Combine(root, "cards"));
var leaders = LeaderDatabase.LoadFromDirectory(Path.Combine(root, "leaders"));
var decks = DeckLibrary.LoadFromDirectory(Path.Combine(root, "decks"));
var resolver = new Resolver(db, leaders);
var rng = new DeterministicRng(seed);

var pool = db.All.Where(c => c.Rarity != Rarity.Token).Select(c => c.Id).ToList();
DeckList deckA = decks.Single(d => d.Id == "iron_wall");
DeckList deckB = decks.Single(d => d.Id == "wildpack_hunt");

// Per-deck-name and per-first-seat tallies.
int[] firstSeatWins = new int[2];
int aWins = 0, bWins = 0, draws = 0, timeouts = 0;
long totalTurns = 0, totalCommands = 0;

for (int g = 0; g < games; g++)
{
    // Alternate which deck sits in seat 0 to factor out first-player advantage.
    bool aIsSeat0 = g % 2 == 0;
    var (d0, d1) = aIsSeat0 ? (deckA, deckB) : (deckB, deckA);

    IReadOnlyList<string> cards0 = mode == "random" ? RandomDeck() : d0.Expand();
    IReadOnlyList<string> cards1 = mode == "random" ? RandomDeck() : d1.Expand();

    var (state, _) = GameFactory.CreateGame(new MatchConfig
    {
        Seed = rng.NextUInt64(),
        FirstSeat = 0,
        Deck0 = cards0, Deck1 = cards1,
        Leader0 = mode == "random" ? "" : d0.Leader,
        Leader1 = mode == "random" ? "" : d1.Leader,
    }, db, leaders);

    while (state.Result is null && state.TurnNumber <= TurnLimit)
    {
        var legal = CommandEnumerator.LegalCommands(state, db, leaders);
        var nonEnd = legal.Where(c => c is not EndTurnCommand).ToList();
        Command pick = nonEnd.Count > 0 && rng.NextInt(100) < 85
            ? nonEnd[rng.NextInt(nonEnd.Count)]
            : new EndTurnCommand { Seat = state.ActiveSeat };

        var result = resolver.Execute(state, pick);
        if (!result.Success)
            throw new InvalidOperationException($"Enumerated command rejected: {result.Error!.Code} {result.Error.Message}");
        state = result.State!;
        totalCommands++;
    }

    totalTurns += Math.Min(state.TurnNumber, TurnLimit);

    if (state.Result is null) { timeouts++; continue; }
    if (state.Result.WinnerSeat < 0) { draws++; continue; }

    int winnerSeat = state.Result.WinnerSeat;
    firstSeatWins[winnerSeat]++;
    bool aWon = (winnerSeat == 0) == aIsSeat0;
    if (aWon) aWins++; else bWins++;
}

int decided = aWins + bWins;
Console.WriteLine($"mode={mode} games={games} seed={seed}");
Console.WriteLine($"[{deckA.Name} vs {deckB.Name}] {deckA.Name}={aWins} ({Pct(aWins, decided)})  {deckB.Name}={bWins} ({Pct(bWins, decided)})");
Console.WriteLine($"first-seat wins: seat0={firstSeatWins[0]} ({Pct(firstSeatWins[0], decided)})  seat1={firstSeatWins[1]} ({Pct(firstSeatWins[1], decided)})");
Console.WriteLine($"draws={draws}  timeouts(>{TurnLimit} turns)={timeouts}");
Console.WriteLine($"avg turns/game={(double)totalTurns / games:F1}  avg commands/game={(double)totalCommands / games:F1}");

string Pct(int n, int d) => d == 0 ? "0.0%" : $"{100.0 * n / d:F1}%";

List<string> RandomDeck()
{
    var deck = new List<string>(30);
    for (int i = 0; i < 30; i++)
        deck.Add(pool[rng.NextInt(pool.Count)]);
    return deck;
}

static string FindDataRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "game", "data");
        if (Directory.Exists(Path.Combine(candidate, "cards")))
            return candidate;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("game/data not found above " + AppContext.BaseDirectory);
}
