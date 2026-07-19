using System.Threading.Channels;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// One live match. The authoritative state IS a <see cref="LocalGameHost"/> reused verbatim from the
/// prototype; this class bridges its per-seat views/events to two client connections (plan §3, §5.1).
///
/// Everything that mutates the match — client commands, a drop, a reconnect, a grace-window forfeit,
/// a turn-clock expiry — is funneled through a single-reader channel and applied on one pump thread,
/// so the host, the connection slots, and the turn timer are only ever touched serially. Per-seat
/// event redaction is the host's job (both seats are subscribed; their buffers fill with their
/// already-redacted view of a command's events).
/// </summary>
public sealed class MatchSession
{
    public LocalGameHost Host { get; }

    private readonly ClientConnection[] _conns;             // indexed by seat; swapped on reconnect
    private readonly string[] _resumeTokens;
    private readonly List<GameEvent>[] _buffers;
    private readonly IDisposable[] _subscriptions;
    private readonly int[] _eventIndex = new int[2];
    private readonly bool[] _connected = { true, true };
    private readonly CancellationTokenSource?[] _graceCts = new CancellationTokenSource?[2];
    private readonly int _graceSeconds;

    // Turn clock (anti-AFK, plan §5.2): the active seat has _turnSeconds; on expiry the server plays
    // EndTurn for them, and a seat that lets the clock run out twice in a row forfeits.
    private readonly int _turnSeconds;
    private readonly int[] _timeoutStreak = new int[2];
    private int _activeSeat = -1;
    private int _turnGeneration;
    private CancellationTokenSource? _turnTimerCts;

    // 起手重抽 clock (docs/11 §5): a single shared countdown; on expiry the server plays keep-all for any
    // seat that hasn't chosen. Runs from match start until both seats submit — the turn clock waits for it.
    private readonly int _mulliganSeconds;
    private CancellationTokenSource? _mulliganCts;
    private long _mulliganStartTick;

    private readonly Channel<Envelope> _inbox;
    private readonly Task _pump;
    private bool _over;
    private string? _endReasonOverride;
    private readonly string? _logPath;   // per-match JSONL command log (null = disabled)
    // Ranked settlement hook (winnerSeat, reason) → the per-seat follow-up to deliver (rating_change).
    // Returning the messages (rather than sending them itself) lets the session route them to the LIVE
    // _conns[seat] — a player who reconnected mid-match has a swapped-in connection, and the closure that
    // built this hook captured the *old* one. Null for friend rooms (unranked).
    private readonly Func<int, string, (ServerMessage? Seat0, ServerMessage? Seat1)>? _onEnded;

    private enum Signal { Client, Dropped, Reattach, Forfeit, TurnBegin, TurnTimeout, MulliganTimeout }
    private readonly record struct Envelope(Signal Kind, int Seat, ClientMessage? Message, ClientConnection? Conn, string? Reason, int Gen);

    private MatchSession(LocalGameHost host, ClientConnection[] conns, string[] resumeTokens, int graceSeconds, int turnSeconds, int mulliganSeconds, string? logDir, Func<int, string, (ServerMessage?, ServerMessage?)>? onEnded)
    {
        Host = host;
        _conns = conns;
        _resumeTokens = resumeTokens;
        _graceSeconds = graceSeconds;
        _turnSeconds = turnSeconds;
        _mulliganSeconds = mulliganSeconds;
        _onEnded = onEnded;
        _logPath = OpenLog(logDir, host.Config);
        _buffers = [new List<GameEvent>(), new List<GameEvent>()];

        _subscriptions =
        [
            Host.Subscribe(0, e => _buffers[0].Add(e)),
            Host.Subscribe(1, e => _buffers[1].Add(e)),
        ];

        _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _pump = Task.Run(PumpAsync);
    }

    public string ResumeTokenFor(int seat) => _resumeTokens[seat];
    public bool IsOver => _over;

    public static MatchSession Create(
        GameContent content, ServerOptions opts,
        ClientConnection seat0, Data.ResolvedDeck deck0,
        ClientConnection seat1, Data.ResolvedDeck deck1,
        Func<int, string, (ServerMessage?, ServerMessage?)>? onEnded = null)
    {
        var config = new MatchConfig
        {
            Seed = SessionAuth.NewMatchSeed(),
            FirstSeat = SessionAuth.NewFirstSeat(),
            Deck0 = deck0.CardIds,
            Deck1 = deck1.CardIds,
            Leader0 = deck0.Leader,
            Leader1 = deck1.Leader,
            MulliganEnabled = opts.MulliganEnabled, // 起手重抽 (docs/11); rematch/queue/friend rooms all route through here
        };

        var host = new LocalGameHost(content.Cards, content.Leaders, config);
        var conns = new[] { seat0, seat1 };
        var tokens = new[] { SessionAuth.NewResumeToken(), SessionAuth.NewResumeToken() };
        return new MatchSession(host, conns, tokens, opts.DisconnectGraceSeconds, opts.TurnSeconds, opts.MulliganSeconds, opts.CommandLogDir, onEnded);
    }

    /// <summary>Start a JSONL log for this match (config header, then one accepted command per line).
    /// Returns null when logging is disabled or the directory can't be created.</summary>
    private static string? OpenLog(string? logDir, MatchConfig config)
    {
        if (string.IsNullOrWhiteSpace(logDir))
            return null;
        try
        {
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"match-{SessionAuth.NewResumeToken()}.jsonl");
            File.WriteAllText(path, RulesJson.Serialize(config) + "\n");
            return path;
        }
        catch
        {
            return null; // logging is best-effort, never fatal to a match
        }
    }

    private void LogCommand(int seat, Command command)
    {
        if (_logPath is null)
            return;
        try { File.AppendAllText(_logPath, RulesJson.Serialize(command) + "\n"); }
        catch { /* best-effort */ }
    }

    /// <summary>True while the match is in the 起手重抽 phase (either seat still owes a mulligan).</summary>
    private bool InMulligan
    {
        get { var v = Host.GetView(0); return v.MulliganPending || v.OpponentMulliganPending; }
    }

    /// <summary>Legal commands to ship a seat: its keep-all mulligan while it still owes one, its turn moves
    /// while it is on the move, else null. The host returns [] for a seat with nothing to do.</summary>
    private IReadOnlyList<Command>? LegalFor(int seat)
    {
        var view = Host.GetView(seat);
        if (view.Result is not null)
            return null;
        if (view.MulliganPending || view.OpponentMulliganPending) // 起手重抽: both seats may act, gated per-seat
            return view.MulliganPending ? Host.LegalCommands(seat) : null;
        return view.ActiveSeat == seat ? Host.LegalCommands(seat) : null;
    }

    /// <summary>Seconds left on the mulligan clock for a resync/reattach, or null when not in the phase.</summary>
    private int? MulliganSecondsLeftNow()
    {
        if (!InMulligan)
            return null;
        long elapsed = (Environment.TickCount64 - _mulliganStartTick) / 1000;
        return (int)Math.Max(0, _mulliganSeconds - elapsed);
    }

    public async Task SendMatchStartedAsync()
    {
        bool mulligan = InMulligan;
        for (int seat = 0; seat < 2; seat++)
        {
            var view = Host.GetView(seat);
            await _conns[seat].SendAsync(new MatchStarted
            {
                Seat = seat,
                ResumeToken = _resumeTokens[seat],
                View = view,
                OpponentName = _conns[1 - seat].Name,
                LegalCommands = LegalFor(seat),
                MulliganSecondsLeft = mulligan ? _mulliganSeconds : null,
            });
        }
    }

    // ---- inbox producers (any thread) ---------------------------------------------------------

    /// <summary>Kick off the first turn clock (after match_started has been sent).</summary>
    public void Begin() => _inbox.Writer.TryWrite(new Envelope(Signal.TurnBegin, -1, null, null, null, 0));

    public void Enqueue(int seat, ClientMessage message) =>
        _inbox.Writer.TryWrite(new Envelope(Signal.Client, seat, message, null, null, 0));

    public void OnConnectionDropped(ClientConnection conn) =>
        _inbox.Writer.TryWrite(new Envelope(Signal.Dropped, -1, null, conn, null, 0));

    public void Reattach(int seat, ClientConnection conn) =>
        _inbox.Writer.TryWrite(new Envelope(Signal.Reattach, seat, null, conn, null, 0));

    /// <summary>Forfeit a seat from outside the pump. Used when a player starts a NEW match activity
    /// (queue / room) while this match is still live: the loss must settle HERE and NOW — left running,
    /// this session's eventual timeout/abandon forfeit would be pushed down the same lobby socket into
    /// the middle of the player's next match.</summary>
    public void Forfeit(int seat, string reason) =>
        _inbox.Writer.TryWrite(new Envelope(Signal.Forfeit, seat, null, null, reason, 0));

    public void Stop()
    {
        _inbox.Writer.TryComplete();
        _turnTimerCts?.Cancel();
        _mulliganCts?.Cancel();
        foreach (var cts in _graceCts) cts?.Cancel();
        foreach (var sub in _subscriptions) sub.Dispose();
    }

    // ---- the single pump ----------------------------------------------------------------------

    private async Task PumpAsync()
    {
        await foreach (var env in _inbox.Reader.ReadAllAsync())
        {
            try
            {
                switch (env.Kind)
                {
                    case Signal.Client when env.Message is SubmitCommand sc: await HandleSubmitAsync(env.Seat, sc); break;
                    case Signal.Client when env.Message is Resync: await HandleResyncAsync(env.Seat); break;
                    case Signal.Dropped: await HandleDroppedAsync(env.Conn!); break;
                    case Signal.Reattach: await HandleReattachAsync(env.Seat, env.Conn!); break;
                    case Signal.Forfeit: await HandleForfeitAsync(env.Seat, env.Reason!); break;
                    case Signal.TurnBegin: await BeginAsync(); break;
                    case Signal.TurnTimeout: await HandleTimeoutAsync(env.Seat, env.Gen); break;
                    case Signal.MulliganTimeout: await HandleMulliganTimeoutAsync(); break;
                }
            }
            catch (Exception ex)
            {
                if (env.Seat is >= 0 and < 2)
                    await _conns[env.Seat].SendAsync(new ErrorMsg { Code = "internal", Message = ex.Message });
            }
        }
    }

    private async Task HandleSubmitAsync(int seat, SubmitCommand sc)
    {
        _buffers[0].Clear();
        _buffers[1].Clear();

        var result = await Host.SubmitCommandAsync(seat, sc.Command);
        await _conns[seat].SendAsync(new CommandResultMsg
        {
            AckSeq = sc.Seq,
            Accepted = result.Accepted,
            ErrorCode = result.Error?.Code.ToString(),
            ErrorMessage = result.Error?.Message,
        });
        if (!result.Accepted)
            return;

        LogCommand(seat, sc.Command);
        _timeoutStreak[seat] = 0; // the player acted — they're present
        await FanOutAsync();
        await CheckMatchEndAsync();
        await SyncTurnTimerAsync();
    }

    private async Task FanOutAsync()
    {
        for (int seat = 0; seat < 2; seat++)
        {
            var batch = _buffers[seat].ToList();
            _eventIndex[seat] += batch.Count;
            var view = Host.GetView(seat);

            await _conns[seat].SendAsync(new EventsMsg
            {
                Batch = batch,
                View = view,
                EventIndex = _eventIndex[seat],
                LegalCommands = LegalFor(seat), // includes the keep-all mulligan for a still-pending seat
            });
        }
    }

    private async Task CheckMatchEndAsync()
    {
        if (_over || Host.GetView(0).Result is not { } outcome)
            return;
        _over = true;
        _turnTimerCts?.Cancel();
        _mulliganCts?.Cancel();
        var reason = _endReasonOverride
            ?? _buffers[0].OfType<GameEndedEvent>().FirstOrDefault()?.Reason
            ?? "normal";
        for (int s = 0; s < 2; s++)
            await _conns[s].SendAsync(new MatchEnded { WinnerSeat = outcome.WinnerSeat, Reason = reason });

        if (_onEnded is not null)
        {
            try
            {
                var (m0, m1) = _onEnded(outcome.WinnerSeat, reason); // settle ELO, get each seat's rating_change
                if (m0 is not null) await _conns[0].SendAsync(m0);   // deliver to the LIVE connection (post-reconnect)
                if (m1 is not null) await _conns[1].SendAsync(m1);
            }
            catch { /* ranked settlement is best-effort; a match must never fail to end because of it */ }
        }
    }

    private async Task HandleResyncAsync(int seat)
    {
        await _conns[seat].SendAsync(new ResyncOk
        {
            View = Host.GetView(seat),
            EventsSince = [],
            EventIndex = _eventIndex[seat],
            LegalCommands = LegalFor(seat),
            MulliganSecondsLeft = MulliganSecondsLeftNow(),
        });
    }

    // ---- 起手重抽 clock (docs/11 §5) ----------------------------------------------------------

    /// <summary>Called once after match_started is sent: start the mulligan clock if the match opened into a
    /// mulligan phase, otherwise the turn clock. When both seats finish mulliganing, the completing submit's
    /// tail (SyncTurnTimerAsync) opens the first turn clock.</summary>
    private async Task BeginAsync()
    {
        if (InMulligan)
            StartMulliganTimer();
        else
            await SyncTurnTimerAsync();
    }

    private void StartMulliganTimer()
    {
        _mulliganCts?.Cancel();
        _mulliganStartTick = Environment.TickCount64;
        var cts = new CancellationTokenSource();
        _mulliganCts = cts;
        _ = RunMulliganTimerAsync(cts.Token);
    }

    private async Task RunMulliganTimerAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _mulliganSeconds)), ct); }
        catch (OperationCanceledException) { return; }
        _inbox.Writer.TryWrite(new Envelope(Signal.MulliganTimeout, -1, null, null, null, 0));
    }

    /// <summary>Mulligan clock expiry: keep-all on behalf of every seat that hasn't chosen. This is benign —
    /// no forfeit, no timeout streak (docs/11 D9) — then the completing submit opens the first turn clock.</summary>
    private async Task HandleMulliganTimeoutAsync()
    {
        if (_over || !InMulligan)
            return;

        for (int seat = 0; seat < 2 && !_over; seat++)
        {
            if (!Host.GetView(seat).MulliganPending)
                continue;
            _buffers[0].Clear();
            _buffers[1].Clear();
            var auto = new MulliganCommand { Seat = seat, ReplacedEntityIds = [] };
            var result = await Host.SubmitCommandAsync(seat, auto);
            if (!result.Accepted)
                continue;
            LogCommand(seat, auto);
            await FanOutAsync();
            await CheckMatchEndAsync();
        }
        await SyncTurnTimerAsync(); // phase closed → open the first turn clock
    }

    // ---- turn clock ---------------------------------------------------------------------------

    /// <summary>Restart the clock when the turn hands over (a new active seat); leave it running within
    /// a turn so the whole turn has one budget, not one-per-action.</summary>
    private async Task SyncTurnTimerAsync()
    {
        if (_over)
        {
            _turnTimerCts?.Cancel();
            _mulliganCts?.Cancel();
            return;
        }
        if (InMulligan) // 起手重抽 not finished — the turn clock must not start yet (docs/11 R2: gate by phase, not ActiveSeat)
            return;
        _mulliganCts?.Cancel(); // phase is over; retire its clock so a late expiry can't fire

        int active = Host.GetView(0).ActiveSeat;
        if (active == _activeSeat)
            return;

        _activeSeat = active;
        _turnTimerCts?.Cancel();
        _turnGeneration++;
        int gen = _turnGeneration;
        var cts = new CancellationTokenSource();
        _turnTimerCts = cts;

        for (int s = 0; s < 2; s++)
            await _conns[s].SendAsync(new TurnTimer { Seat = active, SecondsLeft = _turnSeconds });

        _ = RunTurnTimerAsync(active, gen, cts.Token);
    }

    private async Task RunTurnTimerAsync(int seat, int gen, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(_turnSeconds), ct); }
        catch (OperationCanceledException) { return; }
        _inbox.Writer.TryWrite(new Envelope(Signal.TurnTimeout, seat, null, null, null, gen));
    }

    private async Task HandleTimeoutAsync(int seat, int gen)
    {
        if (_over || gen != _turnGeneration)
            return; // stale: the turn already moved on

        _timeoutStreak[seat]++;
        if (_timeoutStreak[seat] >= 2)
        {
            await HandleForfeitAsync(seat, "timeout");
            return;
        }

        // Auto-end the turn on their behalf, then the handover restarts the clock.
        _buffers[0].Clear();
        _buffers[1].Clear();
        var autoEnd = new EndTurnCommand { Seat = seat };
        var result = await Host.SubmitCommandAsync(seat, autoEnd);
        if (!result.Accepted)
            return;
        LogCommand(seat, autoEnd);
        await FanOutAsync();
        await CheckMatchEndAsync();
        await SyncTurnTimerAsync();
    }

    // ---- drop / reconnect / forfeit -----------------------------------------------------------

    private async Task HandleDroppedAsync(ClientConnection conn)
    {
        int seat = Array.IndexOf(_conns, conn);
        if (seat < 0 || _over || !_connected[seat])
            return;

        _connected[seat] = false;
        await _conns[1 - seat].SendAsync(new OpponentStatus { Connected = false, GraceSeconds = _graceSeconds });

        var cts = new CancellationTokenSource();
        _graceCts[seat] = cts;
        _ = RunGraceAsync(seat, cts.Token);
    }

    private async Task RunGraceAsync(int seat, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(_graceSeconds), ct); }
        catch (OperationCanceledException) { return; }
        _inbox.Writer.TryWrite(new Envelope(Signal.Forfeit, seat, null, null, "abandon", 0));
    }

    private async Task HandleReattachAsync(int seat, ClientConnection conn)
    {
        _conns[seat] = conn;
        _connected[seat] = true;
        _graceCts[seat]?.Cancel();

        await conn.SendAsync(new ResyncOk
        {
            View = Host.GetView(seat),
            EventsSince = [],
            EventIndex = _eventIndex[seat],
            LegalCommands = LegalFor(seat),
            MulliganSecondsLeft = MulliganSecondsLeftNow(), // rebuild the mulligan UI on mid-phase reconnect
        });
        await _conns[1 - seat].SendAsync(new OpponentStatus { Connected = true });
    }

    private async Task HandleForfeitAsync(int loserSeat, string reason)
    {
        if (_over || (reason == "abandon" && _connected[loserSeat]))
            return; // reattached before the window closed, or already over

        _buffers[0].Clear();
        _buffers[1].Clear();
        _endReasonOverride = reason;
        var concede = new ConcedeCommand { Seat = loserSeat };
        await Host.SubmitCommandAsync(loserSeat, concede);
        LogCommand(loserSeat, concede);
        await FanOutAsync();
        await CheckMatchEndAsync();
    }
}
