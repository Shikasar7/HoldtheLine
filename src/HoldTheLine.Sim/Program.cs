using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.State;

// Self-play smoke / balance simulator (plan §6; docs/07 X2.2). Bot policies:
//   random — random-legal-move bots; exercises the engine, but systematically favours defensive
//            decks, so NOT a balance signal.
//   greedy — one-ply heuristic bots (GreedyAi); crude but symmetric — the balance口径.
//
// Usage: dotnet run --project src/HoldTheLine.Sim -- [games] [seed] [mode] [deckA] [deckB]
//   mode = greedy (default) | random | roundrobin
//   roundrobin ignores deckA/deckB and runs all 4-faction pairings (greedy) into a matrix.

int games = args.Length > 0 ? int.Parse(args[0]) : 400;
ulong seed = args.Length > 1 ? ulong.Parse(args[1]) : 20260717UL;
string mode = args.Length > 2 ? args[2] : "greedy";
string deckAId = args.Length > 3 ? args[3] : "iron_wall";
string deckBId = args.Length > 4 ? args[4] : "wildpack_hunt";
const int TurnLimit = 300;

string root = FindDataRoot();
var db = CardDatabase.LoadFromDirectory(Path.Combine(root, "cards"));
var leaders = LeaderDatabase.LoadFromDirectory(Path.Combine(root, "leaders"));
var decks = DeckLibrary.LoadFromDirectory(Path.Combine(root, "decks"));
var resolver = new Resolver(db, leaders);
var rng = new DeterministicRng(seed);
var pool = db.All.Where(c => c.Rarity != Rarity.Token).Select(c => c.Id).ToList();

if (mode == "roundrobin")
{
    RunRoundRobin();
    return;
}

// ---- single-pairing modes (greedy / random) ----
DeckList deckA = decks.Single(d => d.Id == deckAId);
DeckList deckB = decks.Single(d => d.Id == deckBId);

int[] firstSeatWins = new int[2];
int aWins = 0, bWins = 0, draws = 0, timeouts = 0, tideKills = 0;
long totalTurns = 0, totalCommands = 0;

for (int g = 0; g < games; g++)
{
    bool aIsSeat0 = g % 2 == 0;
    var (d0, d1) = aIsSeat0 ? (deckA, deckB) : (deckB, deckA);

    IReadOnlyList<string> cards0 = mode == "random" ? RandomDeck() : d0.Expand();
    IReadOnlyList<string> cards1 = mode == "random" ? RandomDeck() : d1.Expand();
    string l0 = mode == "random" ? "" : d0.Leader;
    string l1 = mode == "random" ? "" : d1.Leader;

    var r = PlayMatch(cards0, cards1, l0, l1, rng.NextUInt64(), greedy: mode != "random");
    totalTurns += r.Turns;
    totalCommands += r.Commands;
    if (r.TideKill) tideKills++;

    if (r.Winner == -2) { timeouts++; continue; }
    if (r.Winner < 0) { draws++; continue; }

    firstSeatWins[r.Winner]++;
    bool aWon = (r.Winner == 0) == aIsSeat0;
    if (aWon) aWins++; else bWins++;
}

int decided = aWins + bWins;
Console.WriteLine($"mode={mode} games={games} seed={seed}");
Console.WriteLine($"[{deckA.Name} vs {deckB.Name}] {deckA.Name}={aWins} ({Pct(aWins, decided)})  {deckB.Name}={bWins} ({Pct(bWins, decided)})");
Console.WriteLine($"first-seat wins: seat0={firstSeatWins[0]} ({Pct(firstSeatWins[0], decided)})  seat1={firstSeatWins[1]} ({Pct(firstSeatWins[1], decided)})");
Console.WriteLine($"draws={draws}  timeouts(>{TurnLimit} turns)={timeouts}  tide-kills={tideKills} ({Pct(tideKills, games)} of games)");
Console.WriteLine($"avg turns/game={(double)totalTurns / games:F1}  avg commands/game={(double)totalCommands / games:F1}");
return;

// ---- round robin (docs/07 X2.2) ----
void RunRoundRobin()
{
    string[] ids = ["iron_wall", "wildpack_hunt", "duskweaver_vesper", "undervault_sunline"];
    var deckById = ids.ToDictionary(id => id, id => decks.Single(d => d.Id == id));
    string[] names = ids.Select(id => deckById[id].Name).ToArray();
    int n = ids.Length;

    // wins[i,j] = games deck i beat deck j (across both first-seat assignments).
    var wins = new int[n, n];
    var played = new int[n, n];
    int[] firstSeat = new int[2];
    long turnsSum = 0; int decidedTotal = 0, tideTotal = 0, timeoutTotal = 0;

    for (int i = 0; i < n; i++)
    for (int j = i + 1; j < n; j++)
    {
        for (int g = 0; g < games; g++)
        {
            bool iSeat0 = g % 2 == 0;
            var (id0, id1) = iSeat0 ? (ids[i], ids[j]) : (ids[j], ids[i]);
            var da = deckById[id0]; var dbk = deckById[id1];

            var r = PlayMatch(da.Expand(), dbk.Expand(), da.Leader, dbk.Leader, rng.NextUInt64(), greedy: true);
            turnsSum += r.Turns;
            if (r.TideKill) tideTotal++;
            if (r.Winner == -2) { timeoutTotal++; continue; }
            if (r.Winner < 0) continue; // draw

            firstSeat[r.Winner]++;
            decidedTotal++;
            // Map seat winner back to deck index.
            int winnerDeck = (r.Winner == 0) == iSeat0 ? i : j;
            int loserDeck = winnerDeck == i ? j : i;
            wins[winnerDeck, loserDeck]++;
            played[winnerDeck, loserDeck]++;
            played[loserDeck, winnerDeck]++;
        }
    }

    Console.WriteLine($"mode=roundrobin games/pairing={games} seed={seed}  (greedy)");
    Console.WriteLine();
    Console.WriteLine("row win% vs column:");
    Console.Write("               ");
    foreach (var nm in names) Console.Write($"{Short(nm),10}");
    Console.WriteLine();
    for (int i = 0; i < n; i++)
    {
        Console.Write($"{Short(names[i]),-15}");
        for (int j = 0; j < n; j++)
        {
            if (i == j) { Console.Write($"{"—",10}"); continue; }
            int w = wins[i, j], p = played[i, j];
            Console.Write($"{Pct(w, p),10}");
        }
        Console.WriteLine();
    }
    Console.WriteLine();
    Console.WriteLine($"first-seat: seat0={Pct(firstSeat[0], decidedTotal)}  seat1={Pct(firstSeat[1], decidedTotal)}");
    Console.WriteLine($"avg turns/game={(double)turnsSum / (games * 6):F1}  tide-kills={tideTotal} ({Pct(tideTotal, games * 6)})  timeouts={timeoutTotal}");
}

MatchResult PlayMatch(IReadOnlyList<string> d0, IReadOnlyList<string> d1, string l0, string l1, ulong matchSeed, bool greedy)
{
    var (state, _) = GameFactory.CreateGame(new MatchConfig
    {
        Seed = matchSeed, FirstSeat = 0,
        Deck0 = d0, Deck1 = d1, Leader0 = l0, Leader1 = l1,
    }, db, leaders);

    long commands = 0;
    bool tideKill = false;
    while (state.Result is null && state.TurnNumber <= TurnLimit)
    {
        var legal = CommandEnumerator.LegalCommands(state, db, leaders);
        Command pick = greedy ? GreedyAi.Pick(state, db, leaders, legal) : RandomPick(state, legal);
        var res = resolver.Execute(state, pick);
        if (!res.Success)
            throw new InvalidOperationException($"Enumerated command rejected: {res.Error!.Code} {res.Error.Message}");
        state = res.State!;
        commands++;
        if (state.Result != null)
            tideKill = res.Events.Any(e => e is PressureTideEvent) && res.Events.Any(e => e is GameEndedEvent);
    }

    int winner = state.Result is null ? -2 : state.Result.WinnerSeat; // -2 timeout, -1 draw, else seat
    return new MatchResult(winner, Math.Min(state.TurnNumber, TurnLimit), commands, tideKill);
}

string Pct(int nn, int dd) => dd == 0 ? "0.0%" : $"{100.0 * nn / dd:F1}%";
static string Short(string s) => s.Length <= 6 ? s : s[..6];

Command RandomPick(GameState st, List<Command> legal)
{
    var nonEnd = legal.Where(c => c is not EndTurnCommand).ToList();
    return nonEnd.Count > 0 && rng.NextInt(100) < 85
        ? nonEnd[rng.NextInt(nonEnd.Count)]
        : new EndTurnCommand { Seat = st.ActiveSeat };
}

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

readonly record struct MatchResult(int Winner, int Turns, long Commands, bool TideKill);
