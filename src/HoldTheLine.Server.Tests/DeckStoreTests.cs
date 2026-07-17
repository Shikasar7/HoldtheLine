using HoldTheLine.Server.Data;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B1: per-account deck persistence with ownership enforced on every access.</summary>
public class DeckStoreTests
{
    private static readonly string[] Thirty = Enumerable.Repeat("c", 30).ToArray();

    [Fact]
    public void Create_list_get_round_trip()
    {
        using var db = new Db(null);
        var store = new DeckStore(db);

        var id = store.Save("g1", null, "My Wall", "iron_vow", "valen", Thirty);
        Assert.NotNull(id);
        Assert.Equal(1, store.CountFor("g1"));

        var got = store.Get("g1", id!)!;
        Assert.Equal("My Wall", got.Name);
        Assert.Equal("iron_vow", got.Faction);
        Assert.Equal(30, got.CardIds.Count);
        Assert.Single(store.ListFor("g1"));
    }

    [Fact]
    public void Update_only_touches_your_own_deck()
    {
        using var db = new Db(null);
        var store = new DeckStore(db);
        var id = store.Save("g1", null, "A", "iron_vow", "valen", Thirty)!;

        Assert.Equal(id, store.Save("g1", id, "A2", "iron_vow", "valen", Thirty)); // owner updates
        Assert.Equal("A2", store.Get("g1", id)!.Name);

        Assert.Null(store.Save("g2", id, "hijack", "wildpack", "thane", Thirty)); // stranger can't
        Assert.Equal("A2", store.Get("g1", id)!.Name);                            // unchanged
    }

    [Fact]
    public void Delete_is_ownership_scoped()
    {
        using var db = new Db(null);
        var store = new DeckStore(db);
        var id = store.Save("g1", null, "A", "iron_vow", "valen", Thirty)!;

        Assert.False(store.Delete("g2", id)); // not yours
        Assert.True(store.Delete("g1", id));
        Assert.Null(store.Get("g1", id));
        Assert.Equal(0, store.CountFor("g1"));
    }
}
