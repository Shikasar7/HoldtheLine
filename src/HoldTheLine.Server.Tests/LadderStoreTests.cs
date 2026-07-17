using HoldTheLine.Server.Data;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B2: ELO settlement, W/L tracking, ranking.</summary>
public class LadderStoreTests
{
    [Fact]
    public void New_player_is_base_and_unranked()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);
        Assert.Equal((LadderStore.BaseRating, 0, 0), ladder.Get("g1"));
        Assert.Equal(0, ladder.Rank("g1"));
    }

    [Fact]
    public void Equal_ratings_win_is_symmetric_with_new_player_K()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);

        var (s0, s1) = ladder.RecordResult("a", "b", winnerSeat: 0, "normal");
        // Both new (K=64), equal ratings (expected 0.5): winner +32, loser -32.
        Assert.Equal(1000, s0.Old);
        Assert.Equal(1032, s0.New);
        Assert.Equal(968, s1.New);

        Assert.Equal((1032, 1, 0), ladder.Get("a"));
        Assert.Equal((968, 0, 1), ladder.Get("b"));
    }

    [Fact]
    public void Draw_at_equal_ratings_moves_nobody()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);
        var (s0, s1) = ladder.RecordResult("a", "b", winnerSeat: -1, "normal");
        Assert.Equal(1000, s0.New);
        Assert.Equal(1000, s1.New);
        Assert.Equal((1000, 0, 0), ladder.Get("a")); // a draw isn't a win or a loss
    }

    [Fact]
    public void Rank_and_top_reflect_results()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);
        ladder.RecordResult("a", "b", winnerSeat: 0, "normal"); // a=1032, b=968

        Assert.Equal(1, ladder.Rank("a"));
        Assert.Equal(2, ladder.Rank("b"));
        var top = ladder.Top(10);
        Assert.Equal(new[] { "a", "b" }, top.Select(r => r.GuestId));
        Assert.Equal(1, top[0].Rank);
        Assert.Equal(1032, top[0].Rating);
    }
}
