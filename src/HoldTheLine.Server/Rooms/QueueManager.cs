using HoldTheLine.Net.Protocol;
using HoldTheLine.Server.Data;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// The single ranked matchmaking queue (M3 plan §3.3, B2). A joiner is paired with the closest-rated
/// waiter; when two are matched a ranked <see cref="MatchSession"/> starts (reusing the whole room/
/// reconnect/timer stack), and its result settles ELO and pushes rating_change to both. A background
/// loop refreshes queue_status (waited time) for anyone still waiting. Ranked matching is human-vs-human
/// only — there is no practice-bot fallback; a waiter either finds an opponent or cancels.
/// </summary>
public sealed class QueueManager : IDisposable
{
    private readonly RoomManager _rooms;
    private readonly LadderStore _ladder;
    private readonly DeckSource _deckSource;
    private readonly object _gate = new();
    private readonly List<Waiter> _waiting = [];
    private readonly CancellationTokenSource _cts = new();

    private sealed record Waiter(ClientConnection Conn, string DeckId, int Rating, DateTimeOffset Since);

    public QueueManager(RoomManager rooms, LadderStore ladder, DeckSource deckSource)
    {
        _rooms = rooms;
        _ladder = ladder;
        _deckSource = deckSource;
        _ = Task.Run(RefreshLoopAsync);
    }

    /// <summary>Enter the queue with a (validated) deck. Idempotent per connection.</summary>
    public void Join(ClientConnection conn, string deckId)
    {
        _deckSource.Resolve(conn.GuestId, deckId); // reject an unknown/illegal deck before queuing
        int rating = _ladder.Get(conn.GuestId).Rating;
        lock (_gate)
        {
            _waiting.RemoveAll(w => w.Conn == conn);
            _waiting.Add(new Waiter(conn, deckId, rating, DateTimeOffset.UtcNow));
        }
        TryPair();
    }

    public void Leave(ClientConnection conn)
    {
        lock (_gate)
            _waiting.RemoveAll(w => w.Conn == conn);
    }

    public bool IsQueued(ClientConnection conn)
    {
        lock (_gate)
            return _waiting.Any(w => w.Conn == conn);
    }

    /// <summary>Number of players currently waiting (for /healthz).</summary>
    public int Count
    {
        get { lock (_gate) return _waiting.Count; }
    }

    /// <summary>Pull the closest-rated matchable pair, if any, and start a ranked match for them.</summary>
    private void TryPair()
    {
        Waiter? a = null, b = null;
        lock (_gate)
        {
            // Closest-rated pair among DIFFERENT guests. Two windows sharing one identity.json (local
            // double-window testing) must never self-match — it would record a player beating themselves
            // and corrupt ELO (C0-2). Full scan is fine at Beta queue sizes.
            var sorted = _waiting.OrderBy(w => w.Rating).ToList();
            int bestGap = int.MaxValue;
            for (int i = 0; i < sorted.Count; i++)
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (string.Equals(sorted[i].Conn.GuestId, sorted[j].Conn.GuestId, StringComparison.Ordinal))
                        continue;
                    int gap = sorted[j].Rating - sorted[i].Rating; // ascending → non-negative
                    if (gap < bestGap) { bestGap = gap; a = sorted[i]; b = sorted[j]; }
                }
            if (a is not null && b is not null)
            {
                _waiting.Remove(a);
                _waiting.Remove(b);
            }
        }

        if (a is not null && b is not null)
            _ = StartAsync(a, b);
    }

    private async Task StartAsync(Waiter a, Waiter b)
    {
        string g0 = a.Conn.GuestId, g1 = b.Conn.GuestId;
        try
        {
            // The settlement RETURNS the per-seat rating_change; MatchSession delivers each to the LIVE
            // seat connection, so a player who reconnected mid-match still sees their ±rating (C0-1).
            await _rooms.StartRankedMatchAsync(a.Conn, a.DeckId, b.Conn, b.DeckId, (winnerSeat, reason) =>
            {
                var (d0, d1) = _ladder.RecordResult(g0, g1, winnerSeat, reason);
                return (
                    new RatingChange { Old = d0.Old, New = d0.New, Season = LadderStore.DefaultSeason },
                    new RatingChange { Old = d1.Old, New = d1.New, Season = LadderStore.DefaultSeason });
            }).ConfigureAwait(false);
        }
        catch
        {
            // Start failed (a peer dropped, or a deck vanished between dequeue and start). Re-seat each
            // survivor whose connection is free AND whose deck still resolves — re-validating avoids a
            // pair→fail→requeue loop when a deck is the cause; otherwise drop them with a notice.
            foreach (var w in new[] { a, b })
            {
                if (w.Conn.Room is not null)
                    continue;
                try { _deckSource.Resolve(w.Conn.GuestId, w.DeckId); }
                catch
                {
                    _ = w.Conn.SendAsync(new ErrorMsg { Code = "queue_deck_gone", Message = "Your queued deck is no longer available." });
                    continue;
                }
                lock (_gate) _waiting.Add(w with { Since = DateTimeOffset.UtcNow });
            }
        }
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token).ConfigureAwait(false);
                Waiter[] snapshot;
                lock (_gate)
                    snapshot = _waiting.ToArray();

                foreach (var w in snapshot)
                {
                    int waited = (int)(DateTimeOffset.UtcNow - w.Since).TotalSeconds;
                    await w.Conn.SendAsync(new QueueStatus
                    {
                        Position = 1,
                        WaitedSeconds = waited,
                    }).ConfigureAwait(false);
                }
                TryPair();
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
