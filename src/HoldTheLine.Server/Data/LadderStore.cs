namespace HoldTheLine.Server.Data;

/// <summary>Per-seat rating delta from a settled match.</summary>
public readonly record struct RatingDelta(int Old, int New);

public sealed record LadderRow(int Rank, string GuestId, int Rating, int Wins, int Losses);

/// <summary>
/// Ratings + match history (M3 plan B2). ELO with K=32 (K=64 for a player's first 10 games so new
/// accounts converge fast); a season column so a reset only appends rows, never deletes history.
/// Only ranked matches (queue matches) call <see cref="RecordResult"/> — friend rooms and practice bots
/// don't touch the ladder.
/// </summary>
public sealed class LadderStore
{
    public const int BaseRating = 1000;
    public const int DefaultSeason = 1;

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
        });
    }

    public (int Rating, int Wins, int Losses) Get(string guestId, int season = DefaultSeason) => _db.Run(c =>
        Read(c, guestId, season));

    /// <summary>Settle a finished ranked match: update both ratings by ELO, bump W/L, append history.
    /// winnerSeat is 0 or 1, or -1 for a draw. Returns each seat's (old,new) rating.</summary>
    public (RatingDelta Seat0, RatingDelta Seat1) RecordResult(string guest0, string guest1, int winnerSeat, string reason, int season = DefaultSeason)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _db.Run(c =>
        {
            var (r0, w0, l0) = Read(c, guest0, season);
            var (r1, w1, l1) = Read(c, guest1, season);

            double score0 = winnerSeat switch { 0 => 1.0, 1 => 0.0, _ => 0.5 };
            int k0 = (w0 + l0) < 10 ? 64 : 32;
            int k1 = (w1 + l1) < 10 ? 64 : 32;
            int n0 = r0 + (int)Math.Round(k0 * (score0 - Expected(r0, r1)));
            int n1 = r1 + (int)Math.Round(k1 * ((1.0 - score0) - Expected(r1, r0)));

            Upsert(c, guest0, season, n0, w0 + (winnerSeat == 0 ? 1 : 0), l0 + (winnerSeat == 1 ? 1 : 0));
            Upsert(c, guest1, season, n1, w1 + (winnerSeat == 1 ? 1 : 0), l1 + (winnerSeat == 0 ? 1 : 0));

            AccountStore.Exec(c, "INSERT INTO match_history(season,guest0,guest1,winner_seat,reason,ended_at) VALUES($s,$a,$b,$w,$r,$t)",
                ("$s", season), ("$a", guest0), ("$b", guest1), ("$w", winnerSeat), ("$r", reason), ("$t", now));

            return (new RatingDelta(r0, n0), new RatingDelta(r1, n1));
        });
    }

    /// <summary>1-based rank among players with a rating this season; 0 if the guest has none.</summary>
    public int Rank(string guestId, int season = DefaultSeason) => _db.Run(c =>
    {
        using var mineCmd = c.CreateCommand();
        mineCmd.CommandText = "SELECT rating FROM ratings WHERE guest_id=$g AND season=$s";
        mineCmd.Parameters.AddWithValue("$g", guestId);
        mineCmd.Parameters.AddWithValue("$s", season);
        if (mineCmd.ExecuteScalar() is not { } mine)
            return 0; // no rated games this season
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*)+1 FROM ratings WHERE season=$s AND rating>$r";
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$r", Convert.ToInt32(mine));
        return Convert.ToInt32(cmd.ExecuteScalar());
    });

    public IReadOnlyList<LadderRow> Top(int n, int season = DefaultSeason) => _db.Run(c =>
    {
        var list = new List<LadderRow>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT guest_id,rating,wins,losses FROM ratings WHERE season=$s ORDER BY rating DESC, wins DESC LIMIT $n";
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

    private static (int Rating, int Wins, int Losses) Read(Microsoft.Data.Sqlite.SqliteConnection c, string guestId, int season)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rating,wins,losses FROM ratings WHERE guest_id=$g AND season=$s";
        cmd.Parameters.AddWithValue("$g", guestId);
        cmd.Parameters.AddWithValue("$s", season);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : (BaseRating, 0, 0);
    }

    private static void Upsert(Microsoft.Data.Sqlite.SqliteConnection c, string guestId, int season, int rating, int wins, int losses) =>
        AccountStore.Exec(c, """
            INSERT INTO ratings(guest_id,season,rating,wins,losses) VALUES($g,$s,$r,$w,$l)
            ON CONFLICT(guest_id,season) DO UPDATE SET rating=$r,wins=$w,losses=$l;
            """,
            ("$g", guestId), ("$s", season), ("$r", rating), ("$w", wins), ("$l", losses));
}
