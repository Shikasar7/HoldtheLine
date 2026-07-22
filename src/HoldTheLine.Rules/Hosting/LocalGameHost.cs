using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Serialization;
using HoldTheLine.Rules.State;

namespace HoldTheLine.Rules.Hosting;

/// <summary>
/// In-process authoritative host: the prototype's "server". With loopback serialization enabled
/// (the default), every command and every dispatched event crosses a JSON boundary exactly like
/// it will over the wire — single-player builds cannot quietly rely on shared references.
/// Also owns the replay log: MatchConfig + accepted commands = the whole game.
/// </summary>
public sealed class LocalGameHost : IGameHost
{
    private readonly object _gate = new();
    private readonly CardDatabase _db;
    private readonly LeaderDatabase _leaders;
    private readonly Resolver _resolver;
    private readonly Ai.AiProfile _profile;     // docs/12 C2: which difficulty tier this host's AI plays at
    private readonly Ai.SearchAi? _ai;          // built only when the profile uses lookahead; Greedy tiers leave it null
    private readonly DeterministicRng _aiRng;   // ε-misplay rolls; seeded off the match seed so a tier is reproducible
    private readonly List<(int Seat, Action<GameEvent> Handler)> _subscribers = new();
    private readonly List<Command> _commandLog = new();
    private readonly List<GameEvent> _eventLog = new();
    private GameState _state;

    // Per-seat legal-command cache. _state is REPLACED (never mutated) on each accepted command, so a
    // reference compare is a complete freshness check. Kills the "every render/click/AI think re-runs
    // the full enumerate-and-dry-run pass over an unchanged state" hot spot.
    private readonly IReadOnlyList<Command>?[] _legalCache = new IReadOnlyList<Command>?[2];
    private GameState? _legalCacheState;

    public MatchConfig Config { get; }
    public bool LoopbackSerialization { get; }

    public LocalGameHost(CardDatabase db, MatchConfig config, bool loopbackSerialization = true, Ai.AiProfile? aiProfile = null)
        : this(db, LeaderDatabase.Empty, config, loopbackSerialization, aiProfile) { }

    public LocalGameHost(CardDatabase db, LeaderDatabase leaders, MatchConfig config, bool loopbackSerialization = true, Ai.AiProfile? aiProfile = null)
    {
        Config = config;
        LoopbackSerialization = loopbackSerialization;
        _db = db;
        _leaders = leaders;
        _resolver = new Resolver(db, leaders);
        _profile = aiProfile ?? Ai.AiProfile.Hard; // null = Hard: server MatchSession / BotClient / existing tests unchanged
        _ai = _profile.UseSearch ? new Ai.SearchAi(db, leaders, _profile.SearchTopK, _profile.SearchRollout) : null;
        _aiRng = new DeterministicRng(config.Seed ^ 0xA5A5A5A5UL);
        var (state, events) = GameFactory.CreateGame(config, db, leaders);
        _state = state;
        _eventLog.AddRange(events);
    }

    /// <summary>
    /// Legal commands for a seat (empty unless it's that seat's turn). Keeps move enumeration —
    /// which needs authoritative state — inside the host so the UI/AI never touch GameState.
    /// </summary>
    public IReadOnlyList<Command> LegalCommands(int seat)
    {
        lock (_gate)
        {
            if (seat is not (0 or 1))
                return [];
            if (_legalCacheState != _state)
            {
                _legalCache[0] = _legalCache[1] = null;
                _legalCacheState = _state;
            }
            return _legalCache[seat] ??= ComputeLegalCommands(seat);
        }
    }

    // Callers hold _gate. Legality still has exactly one definition (the enumerator's resolver dry-run) —
    // the cache above only removes repeat computation over an unchanged state.
    private IReadOnlyList<Command> ComputeLegalCommands(int seat)
    {
        if (_state.Result != null)
            return [];
        if (_state.Mulligan is not null) // 起手重抽: either seat may act, gated per-seat by the enumerator
            return CommandEnumerator.LegalCommands(_state, _db, _leaders, forSeat: seat);
        if (seat != _state.ActiveSeat)
            return [];
        return CommandEnumerator.LegalCommands(_state, _db, _leaders);
    }

    /// <summary>The heuristic AI's chosen command for a seat (null unless it's that seat's turn). GameState stays inside the host.</summary>
    public Command? SuggestCommand(int seat)
    {
        lock (_gate)
        {
            if (_state.Result != null)
                return null;
            if (_state.Mulligan is { } mull) // 起手重抽: Easy keeps everything; Normal/Hard swap high-cost cards (docs/11 §7)
            {
                if (seat is not (0 or 1) || mull.Done[seat])
                    return null;
                return _profile.MulliganKeepAll
                    ? new MulliganCommand { Seat = seat, ReplacedEntityIds = [] }
                    : Ai.MulliganAi.Pick(_state, _db, seat);
            }
            if (seat != _state.ActiveSeat)
                return null;

            var legal = LegalCommands(seat); // Monitor is reentrant — shares the per-state cache with the UI's calls
            // ε-random misplay (Easy tier): pick a real but suboptimal legal move — never Concede/EndTurn, so
            // the AI blunders a play rather than silently forfeiting the turn. Empty pool → fall through to the normal pick.
            if (_profile.Epsilon > 0 && _aiRng.NextInt(1000) < (int)(_profile.Epsilon * 1000))
            {
                var pool = legal.Where(c => c is not ConcedeCommand and not EndTurnCommand).ToList();
                if (pool.Count > 0)
                    return pool[_aiRng.NextInt(pool.Count)];
            }
            return _profile.UseSearch ? _ai!.Pick(_state, legal) : Ai.GreedyAi.Pick(_state, _db, _leaders, legal);
        }
    }

    public IReadOnlyList<Command> CommandLog
    {
        get { lock (_gate) return _commandLog.ToList(); }
    }

    public Task<CommandResult> SubmitCommandAsync(int seat, Command command)
    {
        lock (_gate)
        {
            if (command.Seat != seat)
                return Task.FromResult(CommandResult.Rejected(
                    new RuleError(RuleErrorCode.InvalidCommand, "Command seat does not match the submitting seat.")));

            if (LoopbackSerialization)
                command = RulesJson.Clone(command); // polymorphic round-trip via the base type

            var result = _resolver.Execute(_state, command);
            if (!result.Success)
                return Task.FromResult(CommandResult.Rejected(result.Error!));

            _state = result.State!;
            _commandLog.Add(command);
            _eventLog.AddRange(result.Events);
            Dispatch(result.Events);
            return Task.FromResult(CommandResult.Ok);
        }
    }

    public IDisposable Subscribe(int seat, Action<GameEvent> onEvent)
    {
        var entry = (seat, onEvent);
        lock (_gate)
            _subscribers.Add(entry);
        return new Subscription(this, entry);
    }

    public PlayerView GetView(int seat)
    {
        lock (_gate)
            return PlayerView.From(_state, seat, _db);
    }

    public IReadOnlyList<GameEvent> GetEventLog(int seat)
    {
        lock (_gate)
            return _eventLog.Select(e => Redact(e, seat)).ToList();
    }

    private void Dispatch(IReadOnlyList<GameEvent> events)
    {
        foreach (var (seat, handler) in _subscribers.ToList())
            foreach (var e in events)
                handler(Redact(e, seat));
    }

    private GameEvent Redact(GameEvent e, int seat)
    {
        var redacted = e.RedactFor(seat);
        return LoopbackSerialization ? RulesJson.Clone(redacted) : redacted;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly LocalGameHost _host;
        private readonly (int, Action<GameEvent>) _entry;

        public Subscription(LocalGameHost host, (int, Action<GameEvent>) entry)
        {
            _host = host;
            _entry = entry;
        }

        public void Dispose()
        {
            lock (_host._gate)
                _host._subscribers.Remove(_entry);
        }
    }
}
