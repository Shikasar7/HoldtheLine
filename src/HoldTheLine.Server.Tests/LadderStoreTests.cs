using HoldTheLine.Server.Data;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>M3 B2 + visible-points rework: hidden-MMR ELO settlement, visible points, W/L, ranking.</summary>
public class LadderStoreTests
{
    [Fact]
    public void New_player_is_base_and_unranked()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);
        Assert.Equal((LadderStore.BaseRating, 0, 0), ladder.Get("g1"));
        Assert.Equal(LadderStore.BaseRating, ladder.Mmr("g1"));
        Assert.Equal(0, ladder.Rank("g1"));
    }

    [Fact]
    public void Equal_fresh_players_swing_symmetrically()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);

        var (s0, s1) = ladder.RecordResult("a", "b", winnerSeat: 0, "normal");
        // Hidden MMR: both new (K=64), equal ratings → 1032 / 968. Visible: base 20 nudged by the fresh
        // MMR gap (±3.2) → winner +23, loser −23.
        Assert.Equal(1000, s0.Old);
        Assert.Equal(1023, s0.New);
        Assert.Equal(977, s1.New);

        Assert.Equal((1023, 1, 0), ladder.Get("a"));
        Assert.Equal((977, 0, 1), ladder.Get("b"));
        Assert.Equal(1032, ladder.Mmr("a"));
        Assert.Equal(968, ladder.Mmr("b"));
    }

    [Fact]
    public void A_win_is_always_worth_at_least_the_minimum_gain()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);

        // Grind an extreme mismatch. However far ahead the winner gets (visible points run above MMR,
        // where the nudge is maximally negative), the clamp keeps every win worth at least +PointsMin —
        // "I won and got nothing" is impossible. The loser is floored at 0, never negative.
        for (int i = 0; i < 500; i++)
            ladder.RecordResult("strong", "weak", winnerSeat: 0, "normal");

        var (win, lose) = ladder.RecordResult("strong", "weak", winnerSeat: 0, "normal");
        Assert.True(win.New - win.Old >= LadderStore.PointsMin, $"win worth {win.New - win.Old}, floor is {LadderStore.PointsMin}");
        Assert.True(lose.New >= 0, "visible points never go negative");
        Assert.Equal(0, lose.New); // 500 losses in: pinned to the floor, not drifting below it
    }

    [Fact]
    public void Underrated_player_climbs_faster_than_they_fall()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);

        // Recreate the real stuck-player shape: repeatedly losing to a far stronger opponent. ELO barely
        // docks the MMR for an expected loss, but the visible points take near-full cost every time — so
        // the points sink well below the MMR ("underrated"). Then check the catch-up asymmetry: at 50%
        // winrate against peers they gain net points instead of being stuck at the bottom.
        for (int i = 0; i < 20; i++)
            ladder.RecordResult("boss", "bag" + i, winnerSeat: 0, "normal"); // build a strong veteran
        for (int i = 0; i < 6; i++)
            ladder.RecordResult("victim", "boss", winnerSeat: 1, "normal"); // victim feeds the boss

        int before = ladder.Get("victim").Rating;
        var (win, _) = ladder.RecordResult("victim", "sparA", winnerSeat: 0, "normal");
        var (loss, _) = ladder.RecordResult("victim", "sparB", winnerSeat: 1, "normal");
        Assert.True(win.New - win.Old > loss.Old - loss.New,
            $"underrated: win (+{win.New - win.Old}) should outweigh loss (−{loss.Old - loss.New})");
        Assert.True(ladder.Get("victim").Rating > before, "a 1-1 split should still climb");
    }

    [Fact]
    public void Veteran_vs_newcomer_conserves_their_combined_mmr()
    {
        using var db = new Db(null);
        var ladder = new LadderStore(db);

        // Make "vet" a veteran (>=10 games) on a separate punching bag; "new" stays fresh (0 games).
        for (int i = 0; i < 12; i++)
            ladder.RecordResult("vet", "bag", winnerSeat: i % 2, "normal");

        int before = ladder.Mmr("vet") + ladder.Mmr("new");
        // vet (K=32) vs a brand-new player: symmetric K makes every match zero-sum in MMR, so their
        // combined hidden rating is untouched no matter who wins. On the old asymmetric-K code the
        // veteran's wins would destroy 16 points each — the "the more they play, the lower the total" bug.
        ladder.RecordResult("vet", "new", winnerSeat: 0, "normal");
        ladder.RecordResult("vet", "new", winnerSeat: 1, "normal");
        ladder.RecordResult("vet", "new", winnerSeat: 0, "normal");

        int after = ladder.Mmr("vet") + ladder.Mmr("new");
        Assert.Equal(before, after);
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
        ladder.RecordResult("a", "b", winnerSeat: 0, "normal"); // visible: a=1023, b=977

        Assert.Equal(1, ladder.Rank("a"));
        Assert.Equal(2, ladder.Rank("b"));
        var top = ladder.Top(10);
        Assert.Equal(new[] { "a", "b" }, top.Select(r => r.GuestId));
        Assert.Equal(1, top[0].Rank);
        Assert.Equal(1023, top[0].Rating);
    }

    [Fact]
    public void Existing_rows_migrate_points_from_their_elo_rating()
    {
        using var db = new Db(null);
        // Simulate a pre-rework database: ratings table without the points column, one seeded row.
        db.Run(c => AccountStore.Exec(c, """
            CREATE TABLE ratings (
                guest_id TEXT NOT NULL,
                season   INTEGER NOT NULL,
                rating   INTEGER NOT NULL,
                wins     INTEGER NOT NULL,
                losses   INTEGER NOT NULL,
                PRIMARY KEY (guest_id, season)
            );
            INSERT INTO ratings(guest_id,season,rating,wins,losses) VALUES('old',1,1234,20,10);
            """));

        var ladder = new LadderStore(db); // constructor runs the additive migration
        Assert.Equal((1234, 20, 10), ladder.Get("old")); // visible points seeded from the old ELO
        Assert.Equal(1234, ladder.Mmr("old"));           // MMR continues from the same value
    }
}
