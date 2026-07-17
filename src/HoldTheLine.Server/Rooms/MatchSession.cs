using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules.Engine;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// One live match. The authoritative state IS a <see cref="LocalGameHost"/> reused verbatim from the
/// prototype — this class only bridges its per-seat views/events to two client connections
/// (plan §3, §5.1). N0 wires the opening: build the host and push each seat its
/// <c>match_started</c> snapshot. In-match command routing and event fan-out land in N1.
/// </summary>
public sealed class MatchSession
{
    public LocalGameHost Host { get; }
    private readonly ClientConnection[] _conns;    // indexed by seat
    private readonly string[] _resumeTokens;       // indexed by seat

    private MatchSession(LocalGameHost host, ClientConnection[] conns, string[] resumeTokens)
    {
        Host = host;
        _conns = conns;
        _resumeTokens = resumeTokens;
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
}
