using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.Serialization;
using HoldTheLine.Rules.State;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>起手重抽 (mulligan, docs/11) rules-layer coverage: T1–T13 from the plan §8.</summary>
public class MulliganTests
{
    private static MatchConfig Config(ulong seed = 42, bool coin = true) => new()
    {
        Seed = seed,
        Deck0 = Enumerable.Repeat("t_vanilla", 12).ToList(),
        Deck1 = Enumerable.Repeat("t_vanilla", 12).ToList(),
        CoinCardId = coin ? "neutral_coin" : "",
        MulliganEnabled = true,
    };

    private static GameState NewGame(ulong seed = 42, bool coin = true)
    {
        var (state, _) = GameFactory.CreateGame(Config(seed, coin), TestKit.Db);
        return state;
    }

    private static GameState Apply(Resolver r, GameState state, Command cmd)
    {
        var res = r.Execute(state, cmd);
        Assert.True(res.Success, res.Error?.Message);
        return res.State!;
    }

    private static int[] HandIds(GameState s, int seat) => s.Player(seat).Hand.Select(c => c.EntityId).ToArray();
    private static int[] DeckIds(GameState s, int seat) => s.Player(seat).Deck.Select(c => c.EntityId).ToArray();

    // T1 — replacing k cards: hand size unchanged, replaced cards leave, new cards come from the deck top,
    // and the deck's multiset = original − new draws + replaced.
    [Fact]
    public void Replacing_k_cards_draws_from_the_top_and_preserves_the_deck_multiset()
    {
        var s = NewGame(seed: 100);
        var r = TestKit.NewResolver();
        var handBefore = HandIds(s, 0);
        var deckBefore = DeckIds(s, 0);
        var replaced = new[] { handBefore[0], handBefore[1] };
        var expectedNew = new[] { deckBefore[^1], deckBefore[^2] }; // draw order = top, then next

        s = Apply(r, s, new MulliganCommand { Seat = 0, ReplacedEntityIds = replaced });
        var handAfter = HandIds(s, 0);

        Assert.Equal(4, handAfter.Length);
        Assert.DoesNotContain(replaced[0], handAfter);
        Assert.DoesNotContain(replaced[1], handAfter);
        Assert.Contains(expectedNew[0], handAfter);
        Assert.Contains(expectedNew[1], handAfter);

        var expectedDeck = deckBefore.Except(expectedNew).Concat(replaced).OrderBy(x => x);
        Assert.Equal(expectedDeck, DeckIds(s, 0).OrderBy(x => x));
    }

    // T2 — a swapped card can never be redrawn this same mulligan (draw-first, then shuffle back).
    [Fact]
    public void Swapped_cards_are_never_redrawn()
    {
        var s = NewGame(seed: 101);
        var r = TestKit.NewResolver();
        var handBefore = HandIds(s, 0);
        var replaced = new[] { handBefore[0], handBefore[1], handBefore[2] };

        s = Apply(r, s, new MulliganCommand { Seat = 0, ReplacedEntityIds = replaced });

        var newCards = HandIds(s, 0).Except(handBefore); // whatever is now in hand that wasn't before
        Assert.Empty(newCards.Intersect(replaced));
    }

    // T3 — seat isolation: seat 0's result is identical no matter what seat 1 chooses.
    [Fact]
    public void One_seats_mulligan_is_independent_of_the_others_choice()
    {
        GameState Run(int seat1Replace)
        {
            var s = NewGame(seed: 7);
            var r = TestKit.NewResolver();
            s = Apply(r, s, new MulliganCommand { Seat = 0, ReplacedEntityIds = HandIds(s, 0).Take(2).ToArray() });
            s = Apply(r, s, new MulliganCommand { Seat = 1, ReplacedEntityIds = HandIds(s, 1).Take(seat1Replace).ToArray() });
            return s;
        }

        var a = Run(0);
        var b = Run(3);
        Assert.Equal(HandIds(a, 0), HandIds(b, 0));
        Assert.Equal(DeckIds(a, 0), DeckIds(b, 0));
    }

    // T4 — order independence: submitting 0→1 vs 1→0 yields identical final hands and decks for both seats.
    [Fact]
    public void Submit_order_does_not_change_the_outcome()
    {
        GameState Run(bool seat0First)
        {
            var s = NewGame(seed: 55);
            var r = TestKit.NewResolver();
            var h0 = HandIds(s, 0).Take(2).ToArray();
            var h1 = HandIds(s, 1).Take(3).ToArray();
            var m0 = new MulliganCommand { Seat = 0, ReplacedEntityIds = h0 };
            var m1 = new MulliganCommand { Seat = 1, ReplacedEntityIds = h1 };
            s = seat0First ? Apply(r, Apply(r, s, m0), m1) : Apply(r, Apply(r, s, m1), m0);
            return s;
        }

        var ab = Run(true);
        var ba = Run(false);
        Assert.Equal(HandIds(ab, 0), HandIds(ba, 0));
        Assert.Equal(DeckIds(ab, 0), DeckIds(ba, 0));
        Assert.Equal(HandIds(ab, 1), HandIds(ba, 1));
        Assert.Equal(DeckIds(ab, 1), DeckIds(ba, 1));
    }

    // T5 — keep-all consumes no RNG, does not reshuffle, and emits only a count-0 resolved event.
    [Fact]
    public void Keeping_everything_touches_nothing()
    {
        var s = NewGame(seed: 9);
        var r = TestKit.NewResolver();
        ulong rngBefore = s.Mulligan!.RngState[0];
        var deckBefore = DeckIds(s, 0);

        var res = r.Execute(s, new MulliganCommand { Seat = 0, ReplacedEntityIds = [] });
        Assert.True(res.Success);
        var s2 = res.State!;

        Assert.Equal(rngBefore, s2.Mulligan!.RngState[0]); // seat 1 still pending → phase alive
        Assert.Equal(deckBefore, DeckIds(s2, 0));
        Assert.Equal(0, res.Events.OfType<MulliganResolvedEvent>().Single().ReplacedCount);
        Assert.Empty(res.Events.OfType<CardDrawnEvent>());
    }

    // T6 — validation: bad ids, double submit, wrong phase, and duplicate-id dedup.
    [Fact]
    public void Validation_rejects_illegal_mulligans()
    {
        var r = TestKit.NewResolver();

        // non-hand entity id
        var s = NewGame(seed: 12);
        Assert.Equal(RuleErrorCode.UnknownEntity,
            r.Execute(s, new MulliganCommand { Seat = 0, ReplacedEntityIds = [99999] }).Error!.Code);

        // a non-mulligan command during the phase → MulliganPending
        Assert.Equal(RuleErrorCode.MulliganPending,
            r.Execute(s, new EndTurnCommand { Seat = 0 }).Error!.Code);

        // second submission by the same seat
        var afterFirst = Apply(r, s, new MulliganCommand { Seat = 0, ReplacedEntityIds = [] });
        Assert.Equal(RuleErrorCode.InvalidCommand,
            r.Execute(afterFirst, new MulliganCommand { Seat = 0, ReplacedEntityIds = [] }).Error!.Code);

        // MulliganCommand once play has begun (fresh non-mulligan game)
        var plain = TestKit.NewGame(seed: 12);
        Assert.Equal(RuleErrorCode.InvalidCommand,
            r.Execute(plain, new MulliganCommand { Seat = 0, ReplacedEntityIds = [] }).Error!.Code);

        // duplicate ids are deduped, not doubled — replacing [id, id] swaps exactly one card
        var dup = NewGame(seed: 12);
        int id = HandIds(dup, 0)[0];
        dup = Apply(r, dup, new MulliganCommand { Seat = 0, ReplacedEntityIds = [id, id] });
        Assert.Equal(4, dup.Player(0).Hand.Count);
        Assert.DoesNotContain(id, HandIds(dup, 0));
    }

    // T7 — the coin is withheld during mulligan and handed out on completion; 4/6 opening counts hold.
    [Fact]
    public void Coin_is_deferred_until_both_seats_finish()
    {
        var s = NewGame(seed: 3);
        Assert.Equal(4, s.Player(0).Hand.Count);
        Assert.Equal(6, s.Player(1).Hand.Count);
        Assert.DoesNotContain(s.Player(1).Hand, c => c.CardId == "neutral_coin");

        var r = TestKit.NewResolver();
        s = Apply(r, s, new MulliganCommand { Seat = 0, ReplacedEntityIds = [] });
        var res = r.Execute(s, new MulliganCommand { Seat = 1, ReplacedEntityIds = [] });
        s = res.State!;

        Assert.Null(s.Mulligan);
        Assert.Contains(s.Player(1).Hand, c => c.CardId == "neutral_coin");
        Assert.Equal(5, s.Player(0).Hand.Count); // 4 kept + turn-start draw
        Assert.Equal(7, s.Player(1).Hand.Count); // 6 kept + coin
        Assert.Contains(res.Events, e => e is MulliganCompletedEvent);
        Assert.Contains(res.Events, e => e is TurnStartedEvent { Seat: 0, TurnNumber: 1 });
    }

    // T8 — a both-keep-all mulligan game equals the same-seed non-mulligan game once the phase closes.
    [Fact]
    public void Keep_all_by_both_matches_a_non_mulligan_game()
    {
        var deck = Enumerable.Repeat("t_vanilla", 12).ToList();
        var r = TestKit.NewResolver();
        var mull = NewGame(seed: 321);
        mull = Apply(r, mull, new MulliganCommand { Seat = 0, ReplacedEntityIds = [] });
        mull = Apply(r, mull, new MulliganCommand { Seat = 1, ReplacedEntityIds = [] });

        var (plain, _) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 321, Deck0 = deck, Deck1 = deck, CoinCardId = "neutral_coin", MulliganEnabled = false,
        }, TestKit.Db);

        Assert.Null(mull.Mulligan);
        Assert.Equal(plain.TurnNumber, mull.TurnNumber);
        Assert.Equal(plain.ActiveSeat, mull.ActiveSeat);
        Assert.Equal(plain.Player(0).Mana, mull.Player(0).Mana);
        Assert.Equal(plain.Player(0).Hand.Select(c => c.CardId), mull.Player(0).Hand.Select(c => c.CardId));
        Assert.Equal(plain.Player(1).Hand.Select(c => c.CardId), mull.Player(1).Hand.Select(c => c.CardId));
        Assert.Equal(plain.Player(0).Deck.Select(c => c.CardId), mull.Player(0).Deck.Select(c => c.CardId));
    }

    // T9 — determinism: config + command log (incl. mulligans) replays to the same view; old config → false.
    [Fact]
    public async Task Config_plus_mulligan_log_replays_and_old_config_defaults_off()
    {
        var host = new LocalGameHost(TestKit.Db, Config(seed: 999));
        var h0 = host.GetView(0).Self.Hand.Take(2).Select(c => c.EntityId).ToList();
        Assert.True((await host.SubmitCommandAsync(0, new MulliganCommand { Seat = 0, ReplacedEntityIds = h0 })).Accepted);
        Assert.True((await host.SubmitCommandAsync(1, new MulliganCommand { Seat = 1, ReplacedEntityIds = [] })).Accepted);
        await host.SubmitCommandAsync(0, new EndTurnCommand { Seat = 0 });
        await host.SubmitCommandAsync(1, new EndTurnCommand { Seat = 1 });

        var replay = new LocalGameHost(TestKit.Db, host.Config);
        foreach (var cmd in host.CommandLog)
            Assert.True((await replay.SubmitCommandAsync(cmd.Seat, cmd)).Accepted, $"replay diverged on {cmd.GetType().Name}");

        Assert.Equal(host.GetView(0).Self.Hand.Select(c => c.CardId), replay.GetView(0).Self.Hand.Select(c => c.CardId));
        Assert.Equal(host.GetView(0).Self.LeaderHp, replay.GetView(0).Self.LeaderHp);

        // An old command log's config JSON has no mulligan flag → deserializes to false (no phase).
        var oldConfig = RulesJson.Deserialize<MatchConfig>("""{"seed":1,"deck0":[],"deck1":[]}""");
        Assert.False(oldConfig.MulliganEnabled);
    }

    // T10 — redaction: the opponent sees only the count; the owner sees the swapped ids and drawn card ids.
    [Fact]
    public async Task Opponent_sees_only_the_mulligan_count()
    {
        var host = new LocalGameHost(TestKit.Db, Config(seed: 11));
        var ownerSaw = new List<GameEvent>();
        var oppSaw = new List<GameEvent>();
        host.Subscribe(0, ownerSaw.Add);
        host.Subscribe(1, oppSaw.Add);

        var h0 = host.GetView(0).Self.Hand.Take(2).Select(c => c.EntityId).ToList();
        await host.SubmitCommandAsync(0, new MulliganCommand { Seat = 0, ReplacedEntityIds = h0 });

        var oppResolved = oppSaw.OfType<MulliganResolvedEvent>().Single(e => e.Seat == 0);
        Assert.Null(oppResolved.ReplacedEntityIds);
        Assert.Equal(2, oppResolved.ReplacedCount);
        Assert.All(oppSaw.OfType<CardDrawnEvent>().Where(e => e.Seat == 0), e => Assert.Null(e.CardId));

        var ownerResolved = ownerSaw.OfType<MulliganResolvedEvent>().Single(e => e.Seat == 0);
        Assert.NotNull(ownerResolved.ReplacedEntityIds);
        Assert.Equal(h0, ownerResolved.ReplacedEntityIds!);
        Assert.All(ownerSaw.OfType<CardDrawnEvent>().Where(e => e.Seat == 0), e => Assert.NotNull(e.CardId));
    }

    // T11 — serialization round-trip of the new command, events, and the phase state.
    [Fact]
    public void New_shapes_survive_a_json_round_trip()
    {
        var cmd = Assert.IsType<MulliganCommand>(RulesJson.Clone<Command>(new MulliganCommand { Seat = 1, ReplacedEntityIds = [4, 5, 7] }));
        Assert.Equal(1, cmd.Seat);
        Assert.Equal(new[] { 4, 5, 7 }, cmd.ReplacedEntityIds);

        var ev = Assert.IsType<MulliganResolvedEvent>(RulesJson.Clone<GameEvent>(
            new MulliganResolvedEvent { Seat = 0, ReplacedEntityIds = [1, 2], ReplacedCount = 2 }));
        Assert.Equal(0, ev.Seat);
        Assert.Equal(2, ev.ReplacedCount);
        Assert.Equal(new[] { 1, 2 }, ev.ReplacedEntityIds!);

        Assert.IsType<MulliganCompletedEvent>(RulesJson.Clone<GameEvent>(new MulliganCompletedEvent()));

        var s = RulesJson.Clone(NewGame(seed: 77));
        Assert.NotNull(s.Mulligan);
        Assert.Equal(s.Mulligan!.Done, RulesJson.Clone(s).Mulligan!.Done);
        Assert.Equal(s.Mulligan.RngState, RulesJson.Clone(s).Mulligan!.RngState);
        Assert.Equal(s.Mulligan.CoinCardId, RulesJson.Clone(s).Mulligan!.CoinCardId);
    }

    // T12 — host gates: pending seat gets exactly the keep-all option; done seat gets none; AI suggests keep-all.
    [Fact]
    public async Task Host_offers_the_keep_all_command_to_pending_seats_only()
    {
        var host = new LocalGameHost(TestKit.Db, Config(seed: 22));
        Assert.IsType<MulliganCommand>(Assert.Single(host.LegalCommands(0)));
        Assert.IsType<MulliganCommand>(Assert.Single(host.LegalCommands(1)));

        await host.SubmitCommandAsync(0, new MulliganCommand { Seat = 0, ReplacedEntityIds = [] });
        Assert.Empty(host.LegalCommands(0));
        Assert.Single(host.LegalCommands(1));
        Assert.IsType<MulliganCommand>(host.SuggestCommand(1));
    }

    // T13 — conceding is legal during the mulligan phase and ends the match.
    [Fact]
    public async Task Concede_is_legal_during_mulligan()
    {
        var host = new LocalGameHost(TestKit.Db, Config(seed: 4));
        Assert.True((await host.SubmitCommandAsync(0, new ConcedeCommand { Seat = 0 })).Accepted);
        Assert.NotNull(host.GetView(0).Result);
        Assert.Equal(1, host.GetView(0).Result!.WinnerSeat);
    }

    // M-5 — the AI heuristic swaps out cards costing ≥5 and keeps the cheap ones.
    [Fact]
    public void Ai_mulligan_swaps_expensive_cards()
    {
        var s = TestKit.NewGame(seed: 5);
        s.Player(0).Hand.Clear();
        int cheap = TestKit.GiveCard(s, 0, "t_vanilla"); // cost 2
        int pricey = TestKit.GiveCard(s, 0, "t_big");    // cost 5

        var pick = MulliganAi.Pick(s, TestKit.Db, 0);
        Assert.Contains(pricey, pick.ReplacedEntityIds);
        Assert.DoesNotContain(cheap, pick.ReplacedEntityIds);
    }
}
