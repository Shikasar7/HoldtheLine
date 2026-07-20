using HoldTheLine.Rules.Cards;
using Xunit;

namespace HoldTheLine.Rules.Tests;

public class CardDataTests
{
    [Fact]
    public void Parses_snake_case_card_json()
    {
        const string json = """
        [{
          "id": "iv_test", "name": "测试盾卫", "faction": "iron_vow", "type": "unit",
          "rarity": "rare", "cost": 3, "atk": 2, "hp": 5,
          "keywords": [{"keyword": "taunt"}, {"keyword": "hold_fast"}],
          "effects": [{"trigger": "battlecry", "action": "buff", "target": "adjacent_allies", "hp": 1}],
          "art_prompt": "a dwarven shield bearer"
        }]
        """;
        var defs = CardDatabase.ParseJson(json);
        var card = Assert.Single(defs);
        Assert.Equal(CardType.Unit, card.Type);
        Assert.Equal(Rarity.Rare, card.Rarity);
        Assert.True(card.HasKeyword(Keyword.Taunt));
        Assert.True(card.HasKeyword(Keyword.HoldFast));
        Assert.Equal("adjacent_allies", card.Effects[0].Target);
        Assert.Equal("a dwarven shield bearer", card.ArtPrompt);
    }

    [Fact]
    public void Duplicate_ids_fail_loudly() =>
        Assert.Throws<InvalidDataException>(() => new CardDatabase([TestKit.Vanilla, TestKit.Vanilla]));

    [Fact]
    public void Unknown_effect_action_is_a_data_error()
    {
        var bad = TestKit.Vanilla with
        {
            Id = "t_bad",
            Effects = [new EffectSpec { Trigger = "battlecry", Action = "explode_everything" }],
        };
        Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
    }

    [Fact]
    public void Order_with_source_relative_target_is_a_data_error()
    {
        var bad = TestKit.ZapOrder with
        {
            Id = "t_bad_order",
            Effects = [new EffectSpec { Trigger = "play", Action = "damage", Target = "adjacent_enemies", Amount = 1 }],
        };
        Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
    }

    [Fact]
    public void Hidden_keyword_is_rejected_until_P2()
    {
        var bad = TestKit.Vanilla with { Id = "t_hidden", Keywords = [new KeywordSpec(Keyword.Hidden)] };
        Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
    }

    [Fact]
    public void Swift_and_Range_require_a_value()
    {
        var bad = TestKit.Vanilla with { Id = "t_bad_swift", Keywords = [new KeywordSpec(Keyword.Swift)] };
        Assert.Throws<InvalidDataException>(() => new CardDatabase([bad]));
    }

    [Fact]
    public void Shipped_card_data_directory_loads_and_validates()
    {
        var dir = Path.Combine(RepoPaths.Root, "game", "data", "cards");
        var db = CardDatabase.LoadFromDirectory(dir);
        Assert.True(db.All.Count >= 10, $"Expected the starter set, found {db.All.Count} cards.");
        Assert.True(db.TryGet("neutral_coin", out _));
    }
}
