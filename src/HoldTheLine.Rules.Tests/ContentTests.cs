using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Engine;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>Validates the actual shipped game data (game/data): the 48-card set, leaders, precon decks.</summary>
public class ContentTests
{
    private static string CardsDir => Path.Combine(RepoPaths.Root, "game", "data", "cards");
    private static string LeadersDir => Path.Combine(RepoPaths.Root, "game", "data", "leaders");
    private static string DecksDir => Path.Combine(RepoPaths.Root, "game", "data", "decks");

    private static CardDatabase Cards() => CardDatabase.LoadFromDirectory(CardsDir);
    private static LeaderDatabase Leaders() => LeaderDatabase.LoadFromDirectory(LeadersDir);

    [Fact]
    public void All_shipped_cards_load_and_validate()
    {
        var db = Cards();
        // 16 neutral + coin + 16 iron_vow + 16 wildpack.
        Assert.Equal(49, db.All.Count);
        Assert.Equal(16, db.All.Count(c => c.Faction == "iron_vow"));
        Assert.Equal(16, db.All.Count(c => c.Faction == "wildpack"));
    }

    [Fact]
    public void Leaders_load_and_validate()
    {
        var leaders = Leaders();
        Assert.Equal(2, leaders.All.Count);
        Assert.True(leaders.TryGet("leader_iv_valen", out var valen));
        Assert.True(valen.SkillNeedsUnitTarget);
    }

    [Theory]
    [InlineData("iron_wall")]
    [InlineData("wildpack_hunt")]
    public void Precon_decks_are_legal_and_playable(string deckId)
    {
        var db = Cards();
        var leaders = Leaders();
        var decks = DeckLibrary.LoadFromDirectory(DecksDir);
        var deck = decks.Single(d => d.Id == deckId);

        var expanded = deck.Expand();
        Assert.Equal(30, expanded.Count);
        Assert.Null(DeckValidator.Validate(expanded, db));
        Assert.True(leaders.TryGet(deck.Leader, out _));

        // A game can be created from the precon (validates every id + leader against the engine).
        var (_, events) = GameFactory.CreateGame(new MatchConfig
        {
            Seed = 1, Deck0 = expanded, Deck1 = expanded,
            Leader0 = deck.Leader, Leader1 = deck.Leader, ValidateDecks = true,
        }, db, leaders);
        Assert.NotEmpty(events);
    }

    [Fact]
    public void Every_card_has_art_prompt_for_the_generation_pipeline()
    {
        // Tokens aside, every real card needs an art_prompt so the AI-art pipeline (plan §9.4) can run.
        var missing = Cards().All
            .Where(c => c.Rarity != Rarity.Token && string.IsNullOrWhiteSpace(c.ArtPrompt))
            .Select(c => c.Id)
            .ToList();
        Assert.True(missing.Count == 0, "Cards missing art_prompt: " + string.Join(", ", missing));
    }
}
