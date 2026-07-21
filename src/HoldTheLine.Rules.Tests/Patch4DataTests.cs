using HoldTheLine.Rules.Cards;
using Xunit;

namespace HoldTheLine.Rules.Tests;

/// <summary>docs/21 §2 + §3.1 (Rules 0.9.0) — asserts the shipped data edits parsed correctly: neutral
/// retargets/anchors, and the cult 引导/锚/薪炎 tags. Behaviour of 锚·N / 引导·N itself lives in
/// <see cref="AnchorChannelTests"/>; this pins the data so a future retune can't silently drop a field.</summary>
public class Patch4DataTests
{
    private static readonly CardDatabase Db =
        CardDatabase.LoadFromDirectory(Path.Combine(RepoPaths.Root, "game", "data", "cards"));

    private static EffectSpec Effect(string cardId, string action) =>
        Db.Get(cardId).Effects.Single(e => e.Action == action);

    // ---- neutral (§2) — stays physical ----

    [Fact]
    public void Volley_only_hits_your_own_half()
    {
        Assert.Equal("target_unit_own_half", Effect("nl_volley", "damage").Target);
    }

    [Fact]
    public void Torchbearer_is_now_a_deathrattle_pinger()
    {
        var def = Db.Get("nl_torchbearer");
        Assert.DoesNotContain(def.Effects, e => e.Trigger == "battlecry");
        var dr = def.Effects.Single();
        Assert.Equal("deathrattle", dr.Trigger);
        Assert.Equal("adjacent_enemies", dr.Target);
        Assert.Equal(1, dr.Amount);
    }

    [Fact]
    public void Sapper_battlecry_is_a_self_anchor_but_stays_physical()
    {
        var e = Effect("nl_sapper", "damage");
        Assert.True(e.IsSelfAnchor);
        Assert.Equal(2, e.AnchorRange);
        Assert.Equal("physical", e.School); // 中立不打薪炎 (§1.1)
    }

    // ---- cult (§3.1) — 引导/锚 + spell.kindle ----

    [Fact]
    public void Spark_is_channel_2_kindle()
    {
        var e = Effect("dw_spark", "damage");
        Assert.True(e.IsChannel);
        Assert.Equal(2, e.AnchorRange);
        Assert.Equal("spell.kindle", e.School);
    }

    [Fact]
    public void Immolate_now_costs_one()
    {
        Assert.Equal(1, Db.Get("dw_immolate").Cost);
    }

    [Fact]
    public void Searing_brand_is_channel_2_kindle_sear()
    {
        var e = Effect("dw_searing_brand", "sear");
        Assert.True(e.IsChannel);
        Assert.Equal(2, e.AnchorRange);
        Assert.Equal("spell.kindle", e.School);
        Assert.Equal(3, e.AmountMax); // 2-3 unchanged
    }

    [Theory]
    [InlineData("dw_vesper_blast", "damage", "cell_cross_all")]
    [InlineData("dw_fire_curtain", "damage", "row_enemies")]
    [InlineData("dw_cinder_storm", "sear", "column_enemies")]
    public void Area_orders_gain_channel_3_kindle(string cardId, string action, string target)
    {
        var e = Effect(cardId, action);
        Assert.Equal(target, e.Target);
        Assert.True(e.IsChannel);
        Assert.Equal(3, e.AnchorRange);
        Assert.Equal("spell.kindle", e.School);
    }

    [Fact]
    public void Cinder_moth_is_now_a_one_cost_zero_two_with_three_kindle_deathrattle()
    {
        var def = Db.Get("dw_cinder_moth");
        Assert.Equal(1, def.Cost);
        Assert.Equal(0, def.Atk);
        Assert.Equal(2, def.Hp);
        var dr = def.Effects.Single();
        Assert.Equal("deathrattle", dr.Trigger);
        Assert.Equal(3, dr.Amount);
        Assert.Equal("spell.kindle", dr.School);
    }

    [Fact]
    public void Flame_caster_battlecry_is_a_self_anchor_kindle()
    {
        var e = Effect("dw_flame_caster", "damage");
        Assert.True(e.IsSelfAnchor);
        Assert.Equal(2, e.AnchorRange);
        Assert.Equal("spell.kindle", e.School);
    }

    [Fact]
    public void Pyre_channeler_ping_is_tagged_kindle()
    {
        Assert.Equal("spell.kindle", Effect("dw_pyre_channeler", "damage").School);
    }

    // ---- §1.3 引导者差异化 + 蓄能 reworks ----

    [Fact]
    public void Flare_dancer_battlecry_now_charges_two_and_keeps_assault()
    {
        var def = Db.Get("dw_flare_dancer");
        Assert.True(def.HasKeyword(Keyword.Assault));
        var e = def.Effects.Single();
        Assert.Equal("battlecry", e.Trigger);
        Assert.Equal("amplify_next", e.Action);
        Assert.Equal(2, e.Amount);
    }

    [Theory]
    [InlineData("dw_flame_adept", "deepen", 1)]  // 焰术学徒 → 引导加深 1
    [InlineData("dw_pyroclast", "deepen", 2)]    // 熔岩巨灵 → 引导伤害 +2
    [InlineData("dw_vesper_cantor", "discount", 1)] // 晚祷领唱 → 引导减费 1
    public void Channeler_units_carry_their_channel_marker(string cardId, string action, int amount)
    {
        var def = Db.Get(cardId);
        var e = def.Effects.Single();
        Assert.Equal("channel", e.Trigger);
        Assert.Equal(action, e.Action);
        Assert.Equal(amount, e.Amount);
    }
}
