using HoldTheLine.Server;
using HoldTheLine.Server.Data;
using HoldTheLine.Server.Rooms;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>
/// docs/20 §6-U6 迁移: the 掘世匠会 rework REMOVED cards (弩炮塔 / 迫击炮组 …). A player's saved deck that still
/// references one is rejected UP FRONT (at DeckSource.Resolve, the room-create/join/queue gate) with a fixable
/// "deck_needs_repair" message + the missing count — never crashed deeper in game creation. A clean deck resolves.
/// </summary>
public class DeckMigrationTests
{
    [Fact]
    public void Saved_deck_with_removed_card_is_rejected_with_repair_error()
    {
        using var db = new Db(null);
        var store = new DeckStore(db);
        var content = GameContent.Load();

        // 29 current cards + 1 removed id (弩炮塔 uv_bolt_turret was 砍 in the rework).
        var cards = Enumerable.Repeat("uv_scattergunner", 29).Append("uv_bolt_turret").ToArray();
        var id = store.Save("g1", null, "遗留卡组", "undervault", "leader_uv_brom", cards)!;

        var source = new DeckSource(store, content);
        var ex = Assert.Throws<ProtocolError>(() => source.Resolve("g1", id));
        Assert.Equal("deck_needs_repair", ex.Code);
    }

    [Fact]
    public void Saved_deck_of_current_cards_still_resolves()
    {
        using var db = new Db(null);
        var store = new DeckStore(db);
        var content = GameContent.Load();

        var cards = Enumerable.Repeat("uv_scattergunner", 30).ToArray();
        var id = store.Save("g1", null, "现役卡组", "undervault", "leader_uv_brom", cards)!;

        var source = new DeckSource(store, content);
        var resolved = source.Resolve("g1", id);
        Assert.Equal(30, resolved.CardIds.Count);
        Assert.Equal("leader_uv_brom", resolved.Leader);
    }
}
