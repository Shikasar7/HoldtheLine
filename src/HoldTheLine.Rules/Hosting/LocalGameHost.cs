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
    private readonly Resolver _resolver;
    private readonly List<(int Seat, Action<GameEvent> Handler)> _subscribers = new();
    private readonly List<Command> _commandLog = new();
    private readonly List<GameEvent> _eventLog = new();
    private GameState _state;

    public MatchConfig Config { get; }
    public bool LoopbackSerialization { get; }

    public LocalGameHost(CardDatabase db, MatchConfig config, bool loopbackSerialization = true)
    {
        Config = config;
        LoopbackSerialization = loopbackSerialization;
        _resolver = new Resolver(db);
        var (state, events) = GameFactory.CreateGame(config, db);
        _state = state;
        _eventLog.AddRange(events);
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
            return PlayerView.From(_state, seat);
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
