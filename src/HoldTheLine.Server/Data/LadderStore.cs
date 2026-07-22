using Microsoft.Data.Sqlite;

namespace HoldTheLine.Server.Data;

/// <summary>Per-seat visible-points delta from a settled match (what rating_change shows the player).</summary>
public readonly record struct RatingDelta(int Old, int New);

public sealed record LadderRow(int Rank, string GuestId, int Rating, int Wins, int Losses);

/// <summary>
/// Ratings + match history (M3 plan B2), split into two layers since the visible-points rework:
///
/// <para><b>Hidden MMR</b> (column <c>rating</c>): symmetric-K ELO (32, or 64 while BOTH players are in
/// their first 10 games) used ONLY to pair the queue. Symmetric K keeps every match exactly zero-sum, so
/// no pair grinding each other can bleed the pool's combined skill estimate. Never shown to players.</para>
///
/// <para><b>Visible points</b> (column <c>points</c>): what the client sees in profile / ladder /
/// rating_change. A win always gains, a loss always costs (base ±20), nudged by how far the hidden MMR
/// sits from the visible points — an underrated player (MMR above points, e.g. someone who tanked their
/// early placement games) gains up to +40 and loses as little as −5 until the two converge, and vice
/// versa for an overrated one. Clamped to [5,40] per game and floored at 0 total, so "I won but got
/// nothing" and "stuck at the bottom forever" are both impossible by construction.</para>
///
/// A season column means a reset only appends rows, never deletes history. Only ranked matches (queue
/// matches) call <see cref="RecordResult"/> — friend rooms and practice bots don't touch the ladder.
/// </summary>
public sealed class LadderStore
{
    public const int BaseRating = 1000;
    public const int DefaultSeason = 1;

    /// <summary>Visible points swing per game: base, and the clamp range after the MMR nudge.</summary>
    public const int PointsBase = 20;
    public const int PointsMin = 5;
    public const int PointsMax = 40;

    private readonly Db _db;

    public LadderStore(Db db)
    {
        _db = db;
        _db.Run(c =>
        {
            AccountStore.Exec(c, """
                CREATE TABLE IF NOT EXISTS ratings (
                    guest_id TEXT NOT NULL,
                    season   INTEGER NOT NULL,
                    rating   INTEGER NOT NULL,
                    wins     INTEGER NOT NULL,
                    losses   INTEGER NOT NULL,
                    PRIMARY KEY (guest_id, season)
                );
                """);
            AccountStore.Exec(c, """
                CREATE TABLE IF NOT EXISTS match_history (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    season      INTEGER NOT NULL,
                    guest0      TEXT NOT NULL,
                    guest1      TEXT NOT NULL,
                    winner_seat INTEGER NOT NULL,
                    reason      TEXT NOT NULL,
                    ended_at    INTEGER NOT NULL
                );
                """);
            // Visible-points rework: additive migration (same pattern as AccountStore's username columns).
            // Existing rows seed points from their ELO rating so the ladder is continuous across the switch.
            try { AccountStore.Exec(c, "ALTER TABLE ratings ADD COLUMN points INTEGER"); }
            catch (SqliteException) { /* column already present */ }
            AccountStore.Exec(c, "UPDATE ratings SET points=rating WHERE points IS NULL");
        });
    }

    /// <summary>The client-facing standing: visible points + W/L. This is what profile and ladder show.</summary>
    public (int Rating, int Wins, int Losses) Get(string guestId, int season = DefaultSeason) => _db.Run(c =>
    {
        var (_, points, wins, losses) = Read(c, guestId, season);
        return (points, wins, losses);
    });

    /// <summary>The hidden matchmaking rating. Queue pairing only — never sent to a client.</summary>
    public int Mmr(string guestId, int season = DefaultSeason) => _db.Run(c => Read(c, guestId, season).Mmr);

    /// <summary>Settle a finished ranked match: update hidden MMR by symmetric-K ELO, move visible points
    /// (win always gains, loss always costs, nudged toward MMR), bump W/L, append history.
    /// winnerSeat is 0 or 1, or -1 for a draw. Returns each seat's (old,new) VISIBLE points.</summary>
    public (RatingDelta Seat0, RatingDelta Seat1) RecordResult(string guest0, string guest1, int winnerSeat, string reason, int season = DefaultSeason)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _db.Run(c =>
        {
            var (m0, p0, w0, l0) = Read(c, guest0, season);
            var (m1, p1, w1, l1) = Read(c, guest1, season);

            // Hidden MMR: symmetric per-match K so every match is exactly zero-sum. K=64 only while BOTH
            // are in their first 10 games; the moment one is a veteran, both settle at 32. (An asymmetric
            // K — veteran 32 vs newcomer 64 — destroys 16 MMR every veteran win.)
            double score0 = winnerSeat switch { 0 => 1.0, 1 => 0.0, _ => 0.5 };
            int k = Math.Min((w0 + l0) < 10 ? 64 : 32, (w1 + l1) < 10 ? 64 : 32);
            int nm0 = m0 + (int)Math.Round(k * (score0 - Expected(m0, m1)));
            int nm1 = m1 + (int)Math.Round(k * ((1.0 - score0) - Expected(m1, m0)));

            // Visible points: winner +[5,40], loser -[5,40] (draw moves nobody), floored at 0.
            int np0 = p0, np1 = p1;
            if (winnerSeat == 0) { np0 = p0 + PointsGain(nm0, p0); np1 = Math.Max(0, p1 - PointsCost(nm1, p1)); }
            else if (winnerSeat == 1) { np1 = p1 + PointsGain(nm1, p1); np0 = Math.Max(0, p0 - PointsCost(nm0, p0)); }

            Upsert(c, guest0, season, nm0, np0, w0 + (winnerSeat == 0 ? 1 : 0), l0 + (winnerSeat == 1 ? 1 : 0));
            Upsert(c, guest1, season, nm1, np1, w1 + (winnerSeat == 1 ? 1 : 0), l1 + (winnerSeat == 0 ? 1 : 0));

            AccountStore.Exec(c, "INSERT INTO match_history(season,guest0,guest1,winner_seat,reason,ended_at) VALUES($s,$a,$b,$w,$r,$t)",
                ("$s", season), ("$a", guest0), ("$b", guest1), ("$w", winnerSeat), ("$r", reason), ("$t", now));

            return (new RatingDelta(p0, np0), new RatingDelta(p1, np1));
        });
    }

    /// <summary>1-based rank among players with a rating this season; 0 if the guest has none.</summary>
    public int Rank(string guestId, int season = DefaultSeason) => _db.Run(c =>
    {
        using var mineCmd = c.CreateCommand();
        mineCmd.CommandText = "SELECT points FROM ratings WHERE guest_id=$g AND season=$s";
        mineCmd.Parameters.AddWithValue("$g", guestId);
        mineCmd.Parameters.AddWithValue("$s", season);
        if (mineCmd.ExecuteScalar() is not { } mine)
            return 0; // no rated games this season
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*)+1 FROM ratings WHERE season=$s AND points>$r";
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$r", Convert.ToInt32(mine));
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    public IReadOnlyList<LadderRow> Top(int n, int season = DefaultSeason) => _db.Run(c =>
    {
        var list = new List<LadderRow>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT guest_id,points,wins,losses FROM ratings WHERE season=$s ORDER BY points DESC, wins DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$n", n);
        using var r = cmd.ExecuteReader();
        int rank = 0;
        while (r.Read())
            list.Add(new LadderRow(++rank, r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return (IReadOnlyList<LadderRow>)list;
    });

    /// <summary>Count of ranked matches recorded at or after a unix time (for /healthz "today").</summary>
    public int MatchesSince(long unixSeconds) => _db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM match_history WHERE ended_at >= $t";
        cmd.Parameters.AddWithValue("$t", unixSeconds);
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    // ---- helpers ----

    private static double Expected(int a, int b) => 1.0 / (1.0 + Math.Pow(10, (b - a) / 400.0));

    /// <summary>Winner's visible gain: base +20, nudged up when MMR says the player is underrated
    /// (mmr above points) and down when overrated. Always at least +5 — a win is never worth nothing.</summary>
    private static int PointsGain(int mmr, int points) =>
        Math.Clamp((int)Math.Round(PointsBase + (mmr - points) / 10.0), PointsMin, PointsMax);

    /// <summary>Loser's visible cost: mirror of <see cref="PointsGain"/> — an underrated player loses
    /// less, an overrated one loses more. Always at least −5.</summary>
    private static int PointsCost(int mmr, int points) =>
        Math.Clamp((int)Math.Round(PointsBase - (mmr - points) / 10.0), PointsMin, PointsMax);

    private static (int Mmr, int Points, int Wins, int Losses) Read(SqliteConnection c, string guestId, int season)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rating,COALESCE(points,rating),wins,losses FROM ratings WHERE guest_id=$g AND season=$s";
        cmd.Parameters.AddWithValue("$g", guestId);
        cmd.Parameters.AddWithValue("$s", season);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)) : (BaseRating, BaseRating, 0, 0);
    }

    private static void Upsert(SqliteConnection c, string guestId, int season, int mmr, int points, int wins, int losses) =>
        AccountStore.Exec(c, """
            INSERT INTO ratings(guest_id,season,rating,points,wins,losses) VALUES($g,$s,$m,$p,$w,$l)
            ON CONFLICT(guest_id,season) DO UPDATE SET rating=$m,points=$p,wins=$w,losses=$l;
            """,
            ("$g", guestId), ("$s", season), ("$m", mmr), ("$p", points), ("$w", wins), ("$l", losses));
}
