using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Net.Client;

/// <summary>
/// The client-side <see cref="IGameHost"/>: same shape the battle UI already talks to, but backed by
/// the server instead of an in-process resolver (plan §6.2). Writes are async (submit → await the
/// server's command_result); reads are synchronous over a cache the server keeps fresh by shipping a
/// PlayerView + legal-command set with every event batch — so the UI never blocks and never touches
/// GameState.
///
/// Construct it on an already-connected <see cref="GameServerClient"/> *before* creating/joining a
/// room, so no early event batch is missed, then await <see cref="WaitForMatchAsync"/>.
///
/// Threading: server messages are applied on the client's receive-loop thread; subscriber callbacks
/// and <see cref="ViewUpdated"/> fire there. Handlers must not block (see the driver's poke pattern).
/// </summary>
public sealed class RemoteGameHost : IGameHost
{
    private static readonly IReadOnlyList<Command> NoCommands = [];

    private readonly GameServerClient _client;
    private readonly object _gate = new();
    private readonly List<(int Seat, Action<GameEvent> Handler)> _subscribers = [];
    private readonly List<GameEvent> _eventLog = [];
    private readonly Dictionary<int, TaskCompletionSource<CommandResult>> _pending = [];
    private readonly HashSet<int> _matchRequestSeqs = [];
    private readonly TaskCompletionSource<int> _matchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _seat = -1;
    private PlayerView? _view;
    private IReadOnlyList<Command> _legal = NoCommands;
    private int _eventIndex;
    private int? _mulliganSecondsLeft;

    public string ResumeToken { get; private set; } = "";
    public int Seat => _seat;

    /// <summary>起手重抽 (docs/11): seconds left on the mulligan clock from the last match_started / resync,
    /// or null when not in a mulligan phase. Whether this seat still owes a mulligan is on GetView().MulliganPending.</summary>
    public int? MulliganSecondsLeft
    {
        get { lock (_gate) return _mulliganSecondsLeft; }
    }

    /// <summary>Monotonic count of events applied for this seat. Advances by ≥1 after every accepted
    /// command, so a caller can tell "my action's result has landed" from "some unrelated poke" —
    /// capture it before submitting, then wait until it changes.</summary>
    public int EventIndex
    {
        get { lock (_gate) return _eventIndex; }
    }

    /// <summary>Fires after each applied update (match start / events / resync) with the latest view.
    /// Use it only to signal — do not block the receive loop inside the handler.</summary>
    public event Action<PlayerView>? ViewUpdated;

    /// <summary>Opponent connectivity changes (N3 grace UI). connected, graceSeconds.</summary>
    public event Action<bool, int?>? OpponentStatusChanged;

    /// <summary>Server-announced turn clock at each handover (activeSeat, secondsLeft) — for a countdown UI.</summary>
    public event Action<int, int>? TurnTimerReceived;

    /// <summary>起手重抽 clock (docs/11): fires with the seconds left whenever a match_started / resync carries
    /// a mulligan countdown. There is no separate mulligan-timer message — it rides the snapshot.</summary>
    public event Action<int>? MulliganTimerReceived;

    public RemoteGameHost(GameServerClient client)
    {
        _client = client;
        _client.MessageReceived += OnMessage;
        _client.StateChanged += OnClientState;
    }

    /// <summary>Stop consuming the client's message stream. Call when retiring a finished match's host so a
    /// long-lived client (M3 lobby: one socket across many matches) doesn't accumulate dead subscribers.</summary>
    public void Detach()
    {
        _client.MessageReceived -= OnMessage;
        _client.StateChanged -= OnClientState;
    }

    /// <summary>Register the Seq of an outgoing match-establishing request (join_queue / create_room /
    /// join_room) BEFORE it is sent, so an ErrorMsg reply to that request — and only that request —
    /// faults <see cref="WaitForMatchAsync"/>. Unrelated errors on the shared lobby socket (a concurrent
    /// set_name rejection, a ladder failure) must NOT abort matchmaking: pre-fix they did, leaving the
    /// player queued server-side while the UI showed "匹配失败" — the eventual match then timed them out.</summary>
    public void TagMatchRequest(int seq)
    {
        lock (_gate) _matchRequestSeqs.Add(seq);
    }

    // A connection that is finally given up can never deliver match_started — fault the wait so the
    // menu's `await WaitForMatchAsync()` surfaces an error instead of hanging on a dead socket forever.
    private void OnClientState(ConnectionState state)
    {
        if (state is ConnectionState.Failed or ConnectionState.Disconnected)
            _matchStarted.TrySetException(new InvalidOperationException("连接已断开"));
    }

    /// <summary>Completes with the local seat once the server's match_started snapshot has been applied.</summary>
    public Task<int> WaitForMatchAsync() => _matchStarted.Task;

    /// <summary>Connection lifecycle (Connected / Reconnecting / Failed) for a reconnect UI. Passthrough
    /// to the underlying client.</summary>
    public event Action<ConnectionState> ConnectionStateChanged
    {
        add => _client.StateChanged += value;
        remove => _client.StateChanged -= value;
    }

    public ConnectionState ConnectionState => _client.State;

    /// <summary>Turn on transparent reconnect: a dropped socket is retried with backoff, re-sending
    /// <paramref name="baseHello"/> stamped with this match's resume token. Call after the match starts.</summary>
    public void EnableReconnect(Hello baseHello)
    {
        _client.AutoReconnect = true;
        _client.ReconnectHelloProvider = () => baseHello with { ResumeToken = ResumeToken };
    }

    // ---- IGameHost (writes) -------------------------------------------------------------------

    public async Task<CommandResult> SubmitCommandAsync(int seat, Command command)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seq = _client.NextSeq();
        lock (_gate)
            _pending[seq] = tcs; // registered before the frame goes out — no reply-before-register race

        await _client.SendWithSeqAsync(new SubmitCommand { Command = command }, seq).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    /// <summary>Ask the server to resend the authoritative snapshot (used on a detected event gap).</summary>
    public Task RequestResyncAsync()
    {
        int since;
        lock (_gate) since = _eventIndex;
        return _client.SendAsync(new Resync { SinceEventIndex = since });
    }

    // ---- IGameHost (reads over the server-fed cache) ------------------------------------------

    public PlayerView GetView(int seat)
    {
        lock (_gate)
            return _view ?? throw new InvalidOperationException("No match snapshot yet — await WaitForMatchAsync first.");
    }

    public IReadOnlyList<GameEvent> GetEventLog(int seat)
    {
        lock (_gate) return _eventLog.ToList();
    }

    public IReadOnlyList<Command> LegalCommands(int seat)
    {
        lock (_gate) return seat == _seat ? _legal : NoCommands;
    }

    /// <summary>The view and this-seat legal moves as a single consistent pair. Callers that decide a
    /// move MUST read both together — reading them via two calls can straddle an incoming batch and mix
    /// a stale view with fresh legal moves (or vice versa), producing commands the server then rejects.</summary>
    public (PlayerView View, IReadOnlyList<Command> Legal) Snapshot(int seat)
    {
        lock (_gate)
        {
            var view = _view ?? throw new InvalidOperationException("No match snapshot yet — await WaitForMatchAsync first.");
            return (view, seat == _seat ? _legal : NoCommands);
        }
    }

    public IDisposable Subscribe(int seat, Action<GameEvent> onEvent)
    {
        var entry = (seat, onEvent);
        lock (_gate) _subscribers.Add(entry);
        return new Subscription(this, entry);
    }

    // ---- server message handling --------------------------------------------------------------

    private void OnMessage(ServerMessage message)
    {
        // Until OUR match_started has been applied, drop match-scoped traffic. On the shared lobby
        // socket a freshly-armed host can still receive stragglers from the PREVIOUS match (its
        // concede/timeout event batch, final view, turn timer) — applying those here would poison the
        // next match's cache and flash its game-over into a game that hasn't even begun.
        bool started = _matchStarted.Task.IsCompletedSuccessfully;
        switch (message)
        {
            case MatchStarted ms: ApplyMatchStarted(ms); break;
            case EventsMsg ev when started: ApplyEvents(ev); break;
            case ResyncOk rs when started: ApplySnapshot(rs.View, rs.EventIndex, rs.LegalCommands, rs.MulliganSecondsLeft); break;
            case CommandResultMsg cr: CompletePending(cr); break;
            case OpponentStatus os when started: OpponentStatusChanged?.Invoke(os.Connected, os.GraceSeconds); break;
            case TurnTimer tt when started: TurnTimerReceived?.Invoke(tt.Seat, tt.SecondsLeft); break;
            case ErrorMsg err when !_matchStarted.Task.IsCompleted && IsMatchRequestReply(err.Seq):
                _matchStarted.TrySetException(new InvalidOperationException($"server error: {err.Code}: {err.Message}"));
                break;
        }
    }

    private bool IsMatchRequestReply(int seq)
    {
        lock (_gate) return _matchRequestSeqs.Contains(seq);
    }

    private void ApplyMatchStarted(MatchStarted ms)
    {
        lock (_gate)
        {
            _seat = ms.Seat;
            _view = ms.View;
            _legal = ms.LegalCommands ?? NoCommands;
            _eventIndex = 0;
            _mulliganSecondsLeft = ms.MulliganSecondsLeft;
            ResumeToken = ms.ResumeToken;
        }
        _matchStarted.TrySetResult(ms.Seat);
        ViewUpdated?.Invoke(ms.View);
        if (ms.MulliganSecondsLeft is { } secs)
            MulliganTimerReceived?.Invoke(secs);
    }

    private void ApplyEvents(EventsMsg ev)
    {
        List<GameEvent> dispatch;
        bool gap;
        lock (_gate)
        {
            gap = ev.EventIndex - ev.Batch.Count != _eventIndex;
            _eventLog.AddRange(ev.Batch);
            _eventIndex = ev.EventIndex;
            _view = ev.View;
            _legal = ev.LegalCommands ?? NoCommands;
            dispatch = ev.Batch.ToList();
        }

        foreach (var e in dispatch)
            NotifySubscribers(e);

        ViewUpdated?.Invoke(ev.View);

        if (gap)
            _ = RequestResyncAsync(); // self-heal: a batch was missed (shouldn't happen over TCP loopback)
    }

    private void ApplySnapshot(PlayerView view, int eventIndex, IReadOnlyList<Command>? legal, int? mulliganSecondsLeft = null)
    {
        lock (_gate)
        {
            _view = view;
            _eventIndex = eventIndex;
            _legal = legal ?? NoCommands;
            _mulliganSecondsLeft = mulliganSecondsLeft;
        }
        ViewUpdated?.Invoke(view);
        if (mulliganSecondsLeft is { } secs)
            MulliganTimerReceived?.Invoke(secs);
    }

    private void CompletePending(CommandResultMsg cr)
    {
        TaskCompletionSource<CommandResult>? tcs;
        lock (_gate)
        {
            _pending.Remove(cr.AckSeq, out tcs);
        }
        if (tcs is null)
            return;

        if (cr.Accepted)
        {
            tcs.TrySetResult(CommandResult.Ok);
        }
        else
        {
            var code = Enum.TryParse<RuleErrorCode>(cr.ErrorCode, out var c) ? c : RuleErrorCode.InvalidCommand;
            tcs.TrySetResult(CommandResult.Rejected(new RuleError(code, cr.ErrorMessage ?? "rejected")));
        }
    }

    private void NotifySubscribers(GameEvent e)
    {
        (int Seat, Action<GameEvent> Handler)[] snapshot;
        lock (_gate) snapshot = _subscribers.ToArray();
        foreach (var (_, handler) in snapshot)
            handler(e);
    }

    private sealed class Subscription(RemoteGameHost host, (int, Action<GameEvent>) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (host._gate) host._subscribers.Remove(entry);
        }
    }
}
