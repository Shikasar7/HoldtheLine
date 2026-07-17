using System.Threading.Channels;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// One live match. The authoritative state IS a <see cref="LocalGameHost"/> reused verbatim from the
/// prototype; this class bridges its per-seat views/events to two client connections (plan §3, §5.1).
///
/// Concurrency (plan §5.1): every in-match input is funneled through a single-reader channel and
/// applied on one pump thread, so the host is only ever touched serially — no lock contention with
/// its own internal gate. Per-seat event redaction is the host's job: we subscribe both seats and let
/// each seat's buffer fill with its already-redacted view of a command's events, then fan those out.
/// </summary>
public sealed class MatchSession
{
    public LocalGameHost Host { get; }

    private readonly ClientConnection[] _conns;              // indexed by seat
    private readonly string[] _resumeTokens;                 // indexed by seat
    private readonly List<GameEvent>[] _buffers;             // per-seat redacted events for the in-flight command
    private readonly IDisposable[] _subscriptions;
    private readonly int[] _eventIndex = new int[2];         // running count already fanned out per seat
    private readonly Channel<Envelope> _inbox;
    private readonly Task _pump;
    private bool _over;

    private readonly record struct Envelope(int Seat, ClientMessage Message);

    private MatchSession(LocalGameHost host, ClientConnection[] conns, string[] resumeTokens)
    {
        Host = host;
        _conns = conns;
        _resumeTokens = resumeTokens;
        _buffers = [new List<GameEvent>(), new List<GameEvent>()];

        // The buffers fill synchronously inside SubmitCommandAsync (on the pump thread), already
        // seat-redacted by the host — so seat 0 physically cannot see seat 1's hidden card ids.
        _subscriptions =
        [
            Host.Subscribe(0, e => _buffers[0].Add(e)),
            Host.Subscribe(1, e => _buffers[1].Add(e)),
        ];

        _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _pump = Task.Run(PumpAsync);
    }

    public string ResumeTokenFor(int seat) => _resumeTokens[seat];

    public static MatchSession Create(
        GameContent content,
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
        return new MatchSession(host, conns, tokens);
    }

    /// <summary>Send each seat its opening snapshot, plus legal moves to whichever seat acts first.</summary>
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

    /// <summary>Queue an in-match message (submit_command / resync) for serial processing.</summary>
    public void Enqueue(int seat, ClientMessage message) => _inbox.Writer.TryWrite(new Envelope(seat, message));

    /// <summary>Stop the pump and unsubscribe. N1 tears the whole session down when a player drops;
    /// N3 replaces this with a grace window + resume-token reconnect.</summary>
    public void Stop()
    {
        _inbox.Writer.TryComplete();
        foreach (var sub in _subscriptions)
            sub.Dispose();
    }

    private async Task PumpAsync()
    {
        await foreach (var env in _inbox.Reader.ReadAllAsync())
        {
            try
            {
                switch (env.Message)
                {
                    case SubmitCommand sc: await HandleSubmitAsync(env.Seat, sc); break;
                    case Resync r: await HandleResyncAsync(env.Seat, r); break;
                }
            }
            catch (Exception ex)
            {
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
            return; // rejected: no state change, no events

        await FanOutAsync();

        if (!_over && Host.GetView(0).Result is { } outcome)
        {
            _over = true;
            var reason = _buffers[0].OfType<GameEndedEvent>().FirstOrDefault()?.Reason ?? "normal";
            for (int s = 0; s < 2; s++)
                await _conns[s].SendAsync(new MatchEnded { WinnerSeat = outcome.WinnerSeat, Reason = reason });
        }
    }

    /// <summary>Push the just-produced batch to both seats — each its own redacted copy — with the
    /// post-batch snapshot, and legal moves to whoever is now on the move.</summary>
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

    /// <summary>N1 resync = snapshot only: hand the seat the authoritative view and reset its index
    /// (the client snaps to state without replaying animation). N3 adds event-delta catch-up.</summary>
    private async Task HandleResyncAsync(int seat, Resync r)
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
}
