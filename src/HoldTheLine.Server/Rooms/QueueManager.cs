using HoldTheLine.Net.Protocol;
using HoldTheLine.Server.Data;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// The single ranked matchmaking queue (M3 plan §3.3, B2). A joiner is paired with the closest-rated
/// waiter; when two are matched a ranked <see cref="MatchSession"/> starts (reusing the whole room/
/// reconnect/timer stack), and its result settles ELO and pushes rating_change to both. A background
/// loop refreshes queue_status (waited time, bot-fallback countdown) for anyone still waiting.
/// </summary>
public sealed class QueueManager : IDisposable
{
    /// <summary>Practice-bot fallback threshold (D-M3-4). The bot itself lands in B3; B2 surfaces the countdown.</summary>
    public const int BotFallbackSeconds = 45;

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
            if (_waiting.Count >= 2)
            {
                var sorted = _waiting.OrderBy(w => w.Rating).ToList();
                int bestGap = int.MaxValue, bestI = 0;
                for (int i = 0; i + 1 < sorted.Count; i++)
                {
                    int gap = sorted[i + 1].Rating - sorted[i].Rating;
                    if (gap < bestGap) { bestGap = gap; bestI = i; }
                }
                a = sorted[bestI];
                b = sorted[bestI + 1];
                _waiting.Remove(a);
                _waiting.Remove(b);
            }
        }

        if (a is not null && b is not null)
            _ = StartAsync(a, b);
    }

    private async Task StartAsync(Waiter a, Waiter b)
    {
        try
        {
            string g0 = a.Conn.GuestId, g1 = b.Conn.GuestId;
            ClientConnection c0 = a.Conn, c1 = b.Conn;
            await _rooms.StartRankedMatchAsync(a.Conn, a.DeckId, b.Conn, b.DeckId, async (winnerSeat, reason) =>
            {
                var (d0, d1) = _ladder.RecordResult(g0, g1, winnerSeat, reason);
                await c0.SendAsync(new RatingChange { Old = d0.Old, New = d0.New, Season = LadderStore.DefaultSeason }).ConfigureAwait(false);
                await c1.SendAsync(new RatingChange { Old = d1.Old, New = d1.New, Season = LadderStore.DefaultSeason }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch
        {
            // Pairing failed to start (e.g. a peer dropped between dequeue and start): put the survivors
            // back so they can be rematched rather than stranded.
            foreach (var w in new[] { a, b })
                if (w.Conn.Room is null)
                    lock (_gate) _waiting.Add(w with { Since = DateTimeOffset.UtcNow });
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
                        BotFallbackIn = Math.Max(0, BotFallbackSeconds - waited),
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
