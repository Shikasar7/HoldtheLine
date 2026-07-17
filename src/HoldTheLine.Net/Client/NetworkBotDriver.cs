using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Net.Client;

/// <summary>
/// Plays a match end-to-end through a <see cref="RemoteGameHost"/>, choosing from the server-provided
/// legal commands. A networked client only knows its own <see cref="PlayerView"/> — it has no
/// GameState — so this is deliberately a view-level policy (random-legal by default), which is exactly
/// what the N1 goal needs: exhaustive exercise of the command/event/redaction/replay pipeline (the
/// in-process GreedyAi self-play sim keeps living in HoldTheLine.Sim). A smarter view-aware policy can
/// drop in via the <c>policy</c> delegate later without touching the driver.
///
/// Turn handling uses a non-blocking "poke": <see cref="RemoteGameHost.ViewUpdated"/> just releases a
/// semaphore (never blocks the receive loop); the driver's own loop wakes, reads the fresh view, and
/// acts. After each submit it waits for the resulting event batch before deciding again.
/// </summary>
public sealed class NetworkBotDriver
{
    public delegate Command Policy(PlayerView view, IReadOnlyList<Command> legal);

    /// <summary>Temporary: set true to trace every submit to stderr (diagnostics).</summary>
    public static bool DebugLog;

    private readonly RemoteGameHost _host;
    private readonly Policy _policy;
    private readonly SemaphoreSlim _poke = new(0);
    private readonly int _seat;

    public NetworkBotDriver(RemoteGameHost host, Policy policy)
    {
        _host = host;
        _policy = policy;
        _seat = host.Seat;
        _host.ViewUpdated += _ => _poke.Release();
    }

    /// <summary>Runs until the match ends; returns the winning seat (-1 for a draw).</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        _poke.Release(); // in case it's already our turn at match start

        while (!ct.IsCancellationRequested)
        {
            // Read view + legal as one consistent pair. Legal is non-empty iff it's our turn (the
            // server only sends legal moves to the active seat), so that alone gates whether we act.
            var (view, legal) = _host.Snapshot(_seat);
            if (view.Result is { } result)
                return result.WinnerSeat;

            if (legal.Count == 0)
            {
                await WaitPokeAsync(ct).ConfigureAwait(false);
                continue;
            }

            var command = _policy(view, legal);
            if (DebugLog)
                Console.Error.WriteLine($"[bot{_seat}] t{view.TurnNumber} active={view.ActiveSeat} legal={legal.Count} -> {command.GetType().Name}(seat={command.Seat})");

            int gen = _host.EventIndex;
            bool accepted;
            try
            {
                var outcome = await _host.SubmitCommandAsync(_seat, command).ConfigureAwait(false);
                accepted = outcome.Accepted;
                if (!accepted && DebugLog)
                    Console.Error.WriteLine($"[bot{_seat}] rejected {command.GetType().Name}: {outcome.Error?.Code}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Transient — a drop mid-submit. Reconnect + resync will re-poke; re-evaluate from fresh state.
                if (DebugLog) Console.Error.WriteLine($"[bot{_seat}] submit failed ({ex.GetType().Name}); waiting for resync");
                accepted = false;
            }

            if (!accepted)
            {
                await WaitPokeAsync(ct).ConfigureAwait(false); // wait for the next update, then re-decide
                continue;
            }

            // Block until this action's result batch has actually been applied — otherwise the next
            // snapshot could still show the pre-action legal set and we'd act on stale state.
            while (!ct.IsCancellationRequested && _host.EventIndex == gen)
                await WaitPokeAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return -1;
    }

    private async Task WaitPokeAsync(CancellationToken ct)
    {
        await _poke.WaitAsync(ct).ConfigureAwait(false);
        while (_poke.Wait(0)) { } // collapse any backlog; we always re-read the latest view
    }

    /// <summary>Random legal move with an anti-stall bias: usually prefers a real action over ending
    /// the turn, so self-play games actually progress (mirrors HoldTheLine.Sim's random policy).</summary>
    public static Policy RandomLegal(int seed)
    {
        var rng = new Random(seed);
        return (view, legal) =>
        {
            var actions = legal.Where(c => c is not EndTurnCommand).ToList();
            if (actions.Count > 0 && rng.Next(100) < 85)
                return actions[rng.Next(actions.Count)];
            return legal.FirstOrDefault(c => c is EndTurnCommand) ?? legal[rng.Next(legal.Count)];
        };
    }
}
