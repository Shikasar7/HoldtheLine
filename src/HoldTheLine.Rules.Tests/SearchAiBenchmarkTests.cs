using System.Diagnostics;
using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.State;
using Xunit;
using Xunit.Abstractions;

namespace HoldTheLine.Rules.Tests;

/// <summary>M3 B3 acceptance (§3.5): SearchAi must beat the one-ply GreedyAi it is built on by a clear
/// margin, within its step budget.</summary>
public class SearchAiBenchmarkTests
{
    private readonly ITestOutputHelper _out;
    public SearchAiBenchmarkTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SearchAi_beats_GreedyAi()
    {
        string root = Path.Combine(RepoPaths.Root, "game", "data");
        var db = CardDatabase.LoadFromDirectory(Path.Combine(root, "cards"));
        var leaders = LeaderDatabase.LoadFromDirectory(Path.Combine(root, "leaders"));
        var decks = DeckLibrary.LoadFromDirectory(Path.Combine(root, "decks"));
        var resolver = new Resolver(db, leaders);
        var search = new SearchAi(db, leaders);

        var pairings = new[] { ("iron_wall", "wildpack_hunt"), ("duskweaver_vesper", "undervault_sunline") };
        const int gamesPerConfig = 4;
        int searchWins = 0, decided = 0;
        long stepMicrosMax = 0;
        var rng = new DeterministicRng(20260718UL);

        var sw = Stopwatch.StartNew();
        foreach (var (x, y) in pairings)
            for (int g = 0; g < gamesPerConfig; g++)
            {
                bool searchIsSeat0 = g % 2 == 0;                 // alternate seats for fairness
                var dx = decks.Single(d => d.Id == x);
                var dy = decks.Single(d => d.Id == y);
                var (d0, d1) = searchIsSeat0 ? (dx, dy) : (dy, dx);

                var (state, _) = GameFactory.CreateGame(new MatchConfig
                {
                    Seed = rng.NextUInt64(), FirstSeat = 0,
                    Deck0 = d0.Expand(), Deck1 = d1.Expand(), Leader0 = d0.Leader, Leader1 = d1.Leader,
                }, db, leaders);

                int searchSeat = searchIsSeat0 ? 0 : 1;
                int turns = 0;
                while (state.Result is null && turns++ < 400)
                {
                    var legal = CommandEnumerator.LegalCommands(state, db, leaders);
                    Command pick;
                    if (state.ActiveSeat == searchSeat)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        pick = search.Pick(state, legal);
                        long micros = (long)((Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency);
                        stepMicrosMax = Math.Max(stepMicrosMax, micros);
                    }
                    else
                    {
                        pick = GreedyAi.Pick(state, db, leaders, legal);
                    }
                    state = resolver.Execute(state, pick).State!;
                }

                if (state.Result is { WinnerSeat: >= 0 } r)
                {
                    decided++;
                    if (r.WinnerSeat == searchSeat) searchWins++;
                }
            }
        sw.Stop();

        double rate = decided == 0 ? 0 : (double)searchWins / decided;
        _out.WriteLine($"SearchAi vs GreedyAi: {searchWins}/{decided} = {rate:P1}  |  worst step {stepMicrosMax / 1000.0:F1} ms  |  {sw.ElapsedMilliseconds} ms total");

        Assert.True(rate >= 0.65, $"SearchAi win rate {rate:P1} < 65% (§3.5 benchmark)");
        Assert.True(stepMicrosMax < 500_000, $"worst step {stepMicrosMax / 1000.0:F1} ms exceeded the 500 ms local budget");
    }
}
