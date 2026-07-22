using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Serialization;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>
/// Guards <see cref="GameState.Clone"/> — the resolver's hand-written deep copy that replaced the JSON
/// round-trip on the Execute hot path — against its two failure modes:
/// 1. a field added to a state class but missed in its Clone (caught as a serialization diff), and
/// 2. aliasing — a mutation on the clone leaking into the original (caught by deep-mutating the clone
///    and re-serializing the source).
/// Runs over full random playouts with the REAL shipped card set (all four factions' precon decks), so
/// every mechanic's state — cell states, secrets, spell charge, temp grants, growth, mulligan RNG —
/// passes through the check at some seed.
/// </summary>
public class CloneParityTests
{
    private static string CardsDir => Path.Combine(RepoPaths.Root, "game", "data", "cards");
    private static string LeadersDir => Path.Combine(RepoPaths.Root, "game", "data", "leaders");
    private static string DecksDir => Path.Combine(RepoPaths.Root, "game", "data", "decks");

    [Theory]
    [InlineData(1UL)]
    [InlineData(77UL)]
    [InlineData(1234UL)]
    [InlineData(987654321UL)]
    public void Manual_clone_matches_json_clone_over_a_full_random_playout(ulong seed)
    {
        var db = CardDatabase.LoadFromDirectory(CardsDir);
        var leaders = LeaderDatabase.LoadFromDirectory(LeadersDir);
        var decks = DeckLibrary.LoadFromDirectory(DecksDir);

        var pick = new DeterministicRng(seed ^ 0xC10E_C10E);
        var deck0 = decks[(int)(pick.NextUInt64() % (ulong)decks.Count)];
        var deck1 = decks[(int)(pick.NextUInt64() % (ulong)decks.Count)];

        var (state, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = seed,
            FirstSeat = (int)(seed % 2),
            Deck0 = deck0.Expand(),
            Deck1 = deck1.Expand(),
            Leader0 = deck0.Leader,
            Leader1 = deck1.Leader,
            MulliganEnabled = true,
        }, db);

        var resolver = new Resolver(db, leaders);
        for (int step = 0; step < 200 && state.Result is null; step++)
        {
            AssertCloneParity(state);

            // In the mulligan phase enumerate for a seat that still owes one (both may act).
            int? forSeat = state.Mulligan is { } m ? (m.Done[0] ? 1 : 0) : null;
            var legal = CommandEnumerator.LegalCommands(state, db, leaders, forSeat);
            Assert.NotEmpty(legal);

            var cmd = legal[(int)(pick.NextUInt64() % (ulong)legal.Count)];
            var result = resolver.Execute(state, cmd);
            Assert.True(result.Success, result.Error?.Message);
            state = result.State!;
        }

        AssertCloneParity(state);
    }

    private static void AssertCloneParity(GameState state)
    {
        string before = RulesJson.Serialize(state);

        // 1. Completeness: the manual clone serializes byte-identically to the source.
        var clone = state.Clone();
        Assert.Equal(before, RulesJson.Serialize(clone));

        // 2. Aliasing: deep-mutate every mutable corner of the clone; the source must not move.
        clone.EventSequence++;
        clone.Rng.NextUInt64();
        if (clone.Units.Count > 0)
        {
            var u = clone.Units[0];
            u.CurrentHp -= 1;
            u.Keywords.Add(new KeywordSpec(Keyword.Taunt));
            u.TempGrants.Add(new TempKeywordGrant());
        }
        foreach (var p in clone.Players)
        {
            p.Mana++;
            p.Graveyard.Add("__mutated__");
            p.Secrets.Add(new Secret { CardId = "__mutated__", Kind = "counter_order" });
            if (p.Hand.Count > 0) p.Hand[0].CardId = "__mutated__";
            if (p.Deck.Count > 0) p.Deck[0].CardId = "__mutated__";
        }
        if (clone.CellStates.Count > 0)
            clone.CellStates[0].TurnsLeft += 5;
        if (clone.Mulligan is { } mull)
        {
            mull.Done[0] = !mull.Done[0];
            mull.RngState[0] ^= 0xFFUL;
        }

        Assert.Equal(before, RulesJson.Serialize(state));
    }
}
