using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Geometry;
using HoldTheLine.Rules.State;

// Self-play smoke simulator (plan §6). Two bot policies:
//   random — random-legal-move bots; exercises the engine, but systematically favours defensive
//            decks (random walkers rarely execute a coherent rush), so NOT a balance signal.
//   greedy — one-ply heuristic bots (kill > trade up > push toward the enemy home row); crude but
//            symmetric, and good enough to expose faction-level imbalance until the P4 AI lands.
//
// Usage: dotnet run --project src/HoldTheLine.Sim -- [games] [seed] [mode]
//   mode = greedy (default) | precon (random policy, precon decks) | random (random policy+decks)

int games = args.Length > 0 ? int.Parse(args[0]) : 400;
ulong seed = args.Length > 1 ? ulong.Parse(args[1]) : 20260717UL;
string mode = args.Length > 2 ? args[2] : "greedy";
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
        Command pick = mode == "greedy" ? GreedyPick(state, legal) : RandomPick(state, legal);

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

Command RandomPick(GameState state, List<Command> legal)
{
    var nonEnd = legal.Where(c => c is not EndTurnCommand).ToList();
    return nonEnd.Count > 0 && rng.NextInt(100) < 85
        ? nonEnd[rng.NextInt(nonEnd.Count)]
        : new EndTurnCommand { Seat = state.ActiveSeat };
}

// ---- greedy policy: score every legal command one ply deep, take the best ----

Command GreedyPick(GameState state, List<Command> legal)
{
    Command best = legal[^1]; // EndTurn is always last
    double bestScore = double.NegativeInfinity;
    foreach (var c in legal)
    {
        double score = ScoreCommand(state, c) + rng.NextInt(100) * 0.001; // jitter breaks ties
        if (score > bestScore) { bestScore = score; best = c; }
    }
    return best;
}

double ScoreCommand(GameState s, Command c)
{
    switch (c)
    {
        case EndTurnCommand:
            return 0.05;

        case AttackCommand a:
        {
            var attacker = s.FindUnit(a.AttackerEntityId)!;
            if (a.TargetLeader)
                return 1000 + attacker.Atk * 10;

            var target = s.FindUnit(a.TargetUnitId!.Value)!;
            bool melee = !attacker.HasKeyword(Keyword.Range) || attacker.KeywordValue(Keyword.Range) == 0;

            int myDmg = EstimateDamage(s, attacker, target, melee);
            bool kills = myDmg >= target.CurrentHp;
            int retDmg = melee && !attacker.HasKeyword(Keyword.CheapShot) && target.Atk > 0
                ? EstimateDamage(s, target, attacker, melee: true)
                : 0;
            bool iDie = retDmg >= attacker.CurrentHp;

            return myDmg * 2
                + (kills ? UnitValue(target) * 3 : 0)
                - retDmg * 1.5
                - (iDie ? UnitValue(attacker) * 3 : 0);
        }

        case MoveUnitCommand m:
        {
            var unit = s.FindUnit(m.UnitEntityId)!;
            int enemyHome = BoardGeometry.EnemyHomeRow(m.Seat);
            int progress = Math.Abs(unit.Cell.Row - enemyHome) - Math.Abs(m.To.Row - enemyHome);
            double score = progress * 1.5 + 0.2;
            if (unit.HasKeyword(Keyword.Garrison) && unit.Cell.Row == BoardGeometry.HomeRow(m.Seat) && m.To.Row != unit.Cell.Row)
                score -= 4; // leaving the home row drops the garrison bonus
            return score;
        }

        case UseLeaderSkillCommand ls:
        {
            var target = ls.TargetUnitId is { } id ? s.FindUnit(id) : null;
            if (target != null && target.OwnerSeat != ls.Seat)
                return -100; // never aim a friendly skill at the enemy
            return 0.8;
        }

        case PlayCardCommand p:
        {
            var def = db.Get(s.Player(p.Seat).Hand.First(h => h.EntityId == p.CardEntityId).CardId);
            return def.Type == CardType.Unit ? ScoreDeploy(s, p, def) : ScoreOrder(s, p, def);
        }

        default:
            return -1000; // Concede — never
    }
}

double ScoreDeploy(GameState s, PlayCardCommand p, CardDefinition def)
{
    double score = 4 + def.Cost * 2;
    // Battlecries that take a unit target (buffs) must aim at a friendly unit.
    if (p.TargetUnitId is { } id && s.FindUnit(id) is { } t && t.OwnerSeat != p.Seat)
        return -100;
    return score;
}

double ScoreOrder(GameState s, PlayCardCommand p, CardDefinition def)
{
    double score = 0;
    foreach (var e in def.Effects.Where(e => e.Trigger == "play"))
    {
        var target = p.TargetUnitId is { } id ? s.FindUnit(id) : null;
        bool targetIsEnemy = target != null && target.OwnerSeat != p.Seat;

        switch (e.Action)
        {
            case "damage" when e.Target is "target_unit" or "target_unit_own_half":
                if (!targetIsEnemy) return -100;
                score += e.Amount * 2 + (e.Amount >= target!.CurrentHp ? UnitValue(target) * 3 : 0);
                break;
            case "damage" when e.Target == "column_enemies":
            {
                var hit = s.Units.Where(u => u.OwnerSeat != p.Seat && p.TargetCell is { } cell && u.Cell.Col == cell.Col).ToList();
                score += hit.Sum(u => e.Amount * 2 + (e.Amount >= u.CurrentHp ? UnitValue(u) * 3 : 0));
                break;
            }
            case "buff" or "heal" or "grant_keyword" or "move_bonus" when e.Target is "target_unit":
                if (targetIsEnemy) return -100;
                score += 2 + def.Cost;
                break;
            case "buff" when e.Target is "allies_home_row" or "all_allies":
            {
                int count = e.Target == "all_allies"
                    ? s.Units.Count(u => u.OwnerSeat == p.Seat)
                    : s.Units.Count(u => u.OwnerSeat == p.Seat && u.Cell.Row == BoardGeometry.HomeRow(p.Seat));
                score += count * (e.Atk + e.Hp) * 1.5;
                break;
            }
            case "summon":
                score += 3 + def.Cost;
                break;
            case "draw":
                score += e.Amount * 2;
                break;
            case "gain_mana":
                score += 0.5;
                break;
            default:
                score += 1;
                break;
        }
    }
    return score;
}

// Mirrors the resolver's damage pipeline closely enough for one-ply scoring:
// shield eats the whole instance, hold-fast -1 while stationary, pack-tactics +1 on flanked prey.
int EstimateDamage(GameState s, UnitInstance attacker, UnitInstance target, bool melee)
{
    if (target.ShieldActive)
        return 0;
    int dmg = attacker.Atk;
    if (melee && attacker.HasKeyword(Keyword.PackTactics)
        && BoardGeometry.AdjacentCells(target.Cell).Select(s.UnitAt)
            .Any(u => u != null && u.OwnerSeat == attacker.OwnerSeat && u.EntityId != attacker.EntityId))
        dmg += 1;
    if (target.HasKeyword(Keyword.HoldFast) && !target.MovedThisRound)
        dmg -= 1;
    return Math.Max(0, dmg);
}

double UnitValue(UnitInstance u) => u.Atk * 1.5 + u.CurrentHp;

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
