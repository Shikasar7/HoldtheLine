using System.Threading.Channels;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// One live match. The authoritative state IS a <see cref="LocalGameHost"/> reused verbatim from the
/// prototype; this class bridges its per-seat views/events to two client connections (plan §3, §5.1).
///
/// Everything that mutates the match — client commands, a drop, a reconnect, a grace-window forfeit —
/// is funneled through a single-reader channel and applied on one pump thread, so the host and the
/// connection slots are only ever touched serially. Per-seat event redaction is the host's job (both
/// seats are subscribed and their buffers fill with their already-redacted view of a command's events).
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
    private readonly Channel<Envelope> _inbox;
    private readonly Task _pump;
    private bool _over;
    private string? _endReasonOverride;

    private enum Signal { Client, Dropped, Reattach, Forfeit }
    private readonly record struct Envelope(Signal Kind, int Seat, ClientMessage? Message, ClientConnection? Conn, string? Reason);

    private MatchSession(LocalGameHost host, ClientConnection[] conns, string[] resumeTokens, int graceSeconds)
    {
        Host = host;
        _conns = conns;
        _resumeTokens = resumeTokens;
        _graceSeconds = graceSeconds;
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
        ClientConnection seat0, string deck0Id,
        ClientConnection seat1, string deck1Id)
    {
        var d0 = content.FindDeck(deck0Id) ?? throw new ProtocolError("unknown_deck", $"No deck '{deck0Id}'.");
        var d1 = content.FindDeck(deck1Id) ?? throw new ProtocolError("unknown_deck", $"No deck '{deck1Id}'.");

        var config = new MatchConfig
        {
            Seed = SessionAuth.NewMatchSeed(),
            FirstSeat = SessionAuth.NewFirstSeat(),
            Deck0 = d0.Expand(),
            Deck1 = d1.Expand(),
            Leader0 = d0.Leader,
            Leader1 = d1.Leader,
        };

        var host = new LocalGameHost(content.Cards, content.Leaders, config);
        var conns = new[] { seat0, seat1 };
        var tokens = new[] { SessionAuth.NewResumeToken(), SessionAuth.NewResumeToken() };
        return new MatchSession(host, conns, tokens, opts.DisconnectGraceSeconds);
    }

    public async Task SendMatchStartedAsync()
    {
        for (int seat = 0; seat < 2; seat++)
        {
            var view = Host.GetView(seat);
            var legal = view.ActiveSeat == seat ? Host.LegalCommands(seat) : null;
            await _conns[seat].SendAsync(new MatchStarted
            {
                Seat = seat,
                ResumeToken = _resumeTokens[seat],
                View = view,
                OpponentName = _conns[1 - seat].Name,
                LegalCommands = legal,
            });
        }
    }

    // ---- inbox producers (any thread) ---------------------------------------------------------

    public void Enqueue(int seat, ClientMessage message) =>
        _inbox.Writer.TryWrite(new Envelope(Signal.Client, seat, message, null, null));

    /// <summary>A connection's socket closed. Starts the grace window unless the match is already over.</summary>
    public void OnConnectionDropped(ClientConnection conn) =>
        _inbox.Writer.TryWrite(new Envelope(Signal.Dropped, -1, null, conn, null));

    /// <summary>A client re-attached with a valid resume token. Swaps in the new connection and resyncs it.</summary>
    public void Reattach(int seat, ClientConnection conn) =>
        _inbox.Writer.TryWrite(new Envelope(Signal.Reattach, seat, null, conn, null));

    public void Stop()
    {
        _inbox.Writer.TryComplete();
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

        await FanOutAsync();
        await CheckMatchEndAsync();
    }

    private async Task FanOutAsync()
    {
        for (int seat = 0; seat < 2; seat++)
        {
            var batch = _buffers[seat].ToList();
            _eventIndex[seat] += batch.Count;
            var view = Host.GetView(seat);
            var legal = view.Result is null && view.ActiveSeat == seat ? Host.LegalCommands(seat) : null;

            await _conns[seat].SendAsync(new EventsMsg
            {
                Batch = batch,
                View = view,
                EventIndex = _eventIndex[seat],
                LegalCommands = legal,
            });
        }
    }

    private async Task CheckMatchEndAsync()
    {
        if (_over || Host.GetView(0).Result is not { } outcome)
            return;
        _over = true;
        var reason = _endReasonOverride
            ?? _buffers[0].OfType<GameEndedEvent>().FirstOrDefault()?.Reason
            ?? "normal";
        for (int s = 0; s < 2; s++)
            await _conns[s].SendAsync(new MatchEnded { WinnerSeat = outcome.WinnerSeat, Reason = reason });
    }

    private async Task HandleResyncAsync(int seat)
    {
        var view = Host.GetView(seat);
        await _conns[seat].SendAsync(new ResyncOk
        {
            View = view,
            EventsSince = [],
            EventIndex = _eventIndex[seat],
            LegalCommands = view.Result is null && view.ActiveSeat == seat ? Host.LegalCommands(seat) : null,
        });
    }

    private async Task HandleDroppedAsync(ClientConnection conn)
    {
        int seat = Array.IndexOf(_conns, conn);
        if (seat < 0 || _over || !_connected[seat])
            return; // stale (already reattached/replaced) or match over

        _connected[seat] = false;
        await _conns[1 - seat].SendAsync(new OpponentStatus { Connected = false, GraceSeconds = _graceSeconds });

        var cts = new CancellationTokenSource();
        _graceCts[seat] = cts;
        _ = RunGraceAsync(seat, cts.Token);
    }

    private async Task RunGraceAsync(int seat, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(_graceSeconds), ct); }
        catch (OperationCanceledException) { return; } // reattached in time
        _inbox.Writer.TryWrite(new Envelope(Signal.Forfeit, seat, null, null, "abandon"));
    }

    private async Task HandleReattachAsync(int seat, ClientConnection conn)
    {
        _conns[seat] = conn;
        _connected[seat] = true;
        _graceCts[seat]?.Cancel();

        var view = Host.GetView(seat);
        await conn.SendAsync(new ResyncOk
        {
            View = view,
            EventsSince = [],
            EventIndex = _eventIndex[seat],
            LegalCommands = view.Result is null && view.ActiveSeat == seat ? Host.LegalCommands(seat) : null,
        });
        await _conns[1 - seat].SendAsync(new OpponentStatus { Connected = true });
    }

    private async Task HandleForfeitAsync(int loserSeat, string reason)
    {
        if (_over || _connected[loserSeat])
            return; // reattached before the window closed, or already over

        _buffers[0].Clear();
        _buffers[1].Clear();
        _endReasonOverride = reason;
        await Host.SubmitCommandAsync(loserSeat, new ConcedeCommand { Seat = loserSeat });
        await FanOutAsync();
        await CheckMatchEndAsync();
    }
}
