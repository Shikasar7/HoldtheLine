using HoldTheLine.Rules.Ai;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.Serialization;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/12 C2: the three vs-AI tiers wired through <see cref="AiProfile"/> and <see cref="LocalGameHost"/>.</summary>
public class AiProfileTests
{
    private static readonly string Root = Path.Combine(RepoPaths.Root, "game", "data");
    private static readonly CardDatabase Db = CardDatabase.LoadFromDirectory(Path.Combine(Root, "cards"));
    private static readonly LeaderDatabase Leaders = LeaderDatabase.LoadFromDirectory(Path.Combine(Root, "leaders"));
    private static readonly IReadOnlyList<DeckList> Decks = DeckLibrary.LoadFromDirectory(Path.Combine(Root, "decks"));

    private static MatchConfig Config(ulong seed, bool mulligan)
    {
        var a = Decks.Single(d => d.Id == "iron_wall");
        var b = Decks.Single(d => d.Id == "wildpack_hunt");
        return new MatchConfig
        {
            Seed = seed,
            FirstSeat = 0,
            Deck0 = a.Expand(),
            Deck1 = b.Expand(),
            Leader0 = a.Leader,
            Leader1 = b.Leader,
            MulliganEnabled = mulligan,
        };
    }

    // 1 — Easy keeps its whole opening hand; Hard's mulligan is exactly MulliganAi's heuristic swap.
    [Fact]
    public void Easy_keeps_hand_and_Hard_mirrors_MulliganAi()
    {
        var config = Config(seed: 100, mulligan: true);
        var (state, _) = GameFactory.CreateGame(config, Db, Leaders);
        var expected = MulliganAi.Pick(state, Db, 0);

        var easy = new LocalGameHost(Db, Leaders, config, aiProfile: AiProfile.Easy);
        Assert.Empty(Assert.IsType<MulliganCommand>(easy.SuggestCommand(0)).ReplacedEntityIds);

        var hard = new LocalGameHost(Db, Leaders, config, aiProfile: AiProfile.Hard);
        Assert.Equal(expected.ReplacedEntityIds, Assert.IsType<MulliganCommand>(hard.SuggestCommand(0)).ReplacedEntityIds);
    }

    // 2 — two Easy hosts on the same config produce an identical 20-suggestion sequence (ε rolls are seeded off the match seed).
    [Fact]
    public void Easy_suggestions_are_deterministic_across_hosts()
    {
        var config = Config(seed: 202, mulligan: false);
        var a = new LocalGameHost(Db, Leaders, config, aiProfile: AiProfile.Easy);
        var b = new LocalGameHost(Db, Leaders, config, aiProfile: AiProfile.Easy);

        var seqA = new List<string>();
        var seqB = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            seqA.Add(RulesJson.Serialize<Command>(a.SuggestCommand(0)!));
            seqB.Add(RulesJson.Serialize<Command>(b.SuggestCommand(0)!));
        }
        Assert.Equal(seqA, seqB);
    }

    // 3 — every tier drives a real game (fixed seed, two preconstructed decks) and the engine accepts every command.
    [Theory]
    [InlineData(AiLevel.Easy)]
    [InlineData(AiLevel.Normal)]
    [InlineData(AiLevel.Hard)]
    public async Task Each_tier_plays_a_legal_game(AiLevel level)
    {
        var host = new LocalGameHost(Db, Leaders, Config(seed: 303, mulligan: true), aiProfile: AiProfile.For(level));
        int commands = 0;
        while (host.GetView(0).Result is null && commands < 50)
        {
            var cmd = host.SuggestCommand(0) ?? host.SuggestCommand(1);
            if (cmd is null) break;
            var res = await host.SubmitCommandAsync(cmd.Seat, cmd);
            Assert.True(res.Accepted, $"{level} produced an illegal command: {cmd.GetType().Name}");
            commands++;
        }
        Assert.True(commands > 0, "the tier never produced a command");
    }

    // 4 — a LocalGameHost with no profile behaves exactly like an explicit Hard host (first 10 suggestions).
    [Fact]
    public async Task Default_profile_equals_Hard()
    {
        var config = Config(seed: 404, mulligan: false);
        var seqDefault = await Drive(new LocalGameHost(Db, Leaders, config), 10);
        var seqHard = await Drive(new LocalGameHost(Db, Leaders, config, aiProfile: AiProfile.Hard), 10);
        Assert.Equal(seqDefault, seqHard);
    }

    private static async Task<List<string>> Drive(LocalGameHost host, int steps)
    {
        var seq = new List<string>();
        for (int i = 0; i < steps && host.GetView(0).Result is null; i++)
        {
            var cmd = host.SuggestCommand(0) ?? host.SuggestCommand(1);
            if (cmd is null) break;
            seq.Add(RulesJson.Serialize<Command>(cmd));
            Assert.True((await host.SubmitCommandAsync(cmd.Seat, cmd)).Accepted);
        }
        return seq;
    }
}
