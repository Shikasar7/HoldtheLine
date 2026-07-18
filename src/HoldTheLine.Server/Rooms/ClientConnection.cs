using System.Net.WebSockets;
using HoldTheLine.Net;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Server.Data;
using Microsoft.Extensions.Logging;

namespace HoldTheLine.Server.Rooms;

/// <summary>
/// One connected client: owns its WebSocket, runs the receive/dispatch loop, and is the only place
/// that writes to the socket (all sends funnel through <see cref="SendAsync"/> under a gate, since
/// the opponent's thread also pushes match-start/events here). N0 handles hello + room lifecycle;
/// in-match routing (submit_command / resync) is stubbed for N1.
/// </summary>
public sealed class ClientConnection(WebSocket socket, ILogger<ClientConnection> logger)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // Server-wide singletons, captured at the top of RunAsync so the handshake/dispatch/profile helpers
    // can reach them without threading every dependency through each signature.
    private RoomManager _rooms = null!;
    private GameContent _content = null!;
    private ServerOptions _opts = null!;
    private AccountStore _accounts = null!;
    private DeckStore _decks = null!;
    private CollectionStore _collection = null!;
    private LadderStore _ladder = null!;
    private QueueManager _queue = null!;

    public string GuestId { get; private set; } = "";
    public string Name { get; private set; } = "";
    public Room? Room { get; set; }
    public int Seat { get; set; }

    public async Task RunAsync(RoomManager rooms, GameContent content, ServerOptions opts, AccountStore accounts,
        DeckStore decks, CollectionStore collection, LadderStore ladder, QueueManager queue, CancellationToken ct)
    {
        _rooms = rooms;
        _content = content;
        _opts = opts;
        _accounts = accounts;
        _decks = decks;
        _collection = collection;
        _ladder = ladder;
        _queue = queue;
        try
        {
            if (!await HandshakeAsync(ct).ConfigureAwait(false))
                return;

            while (!ct.IsCancellationRequested)
            {
                var msg = await ReceiveAsync(ct).ConfigureAwait(false);
                if (msg is null)
                    break; // socket closed

                try
                {
                    await DispatchAsync(msg).ConfigureAwait(false);
                }
                catch (ProtocolError pe)
                {
                    await SendAsync(new ErrorMsg { Code = pe.Code, Message = pe.Message, Seq = msg.Seq }, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* server shutting down */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Connection {Guest} faulted", GuestId);
        }
        finally
        {
            _queue.Leave(this);
            _rooms.OnDisconnect(this);
        }
    }

    /// <summary>First frame must be a version-matching hello; otherwise send an error and close. A hello
    /// carrying a resume token re-attaches to an in-progress match instead of waiting for create/join.
    /// M3 B0: also gates on the data hash and establishes / restores the persistent identity, then pushes
    /// the account <see cref="Profile"/>.</summary>
    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        if (await ReceiveAsync(ct).ConfigureAwait(false) is not Hello hello)
        {
            await SendAsync(new ErrorMsg { Code = "expected_hello", Message = "First message must be hello." }, ct)
                .ConfigureAwait(false);
            return false;
        }

        if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion || hello.RulesVersion != RulesInfo.Version)
        {
            await SendAsync(new ErrorMsg
            {
                Code = "version_mismatch",
                Message = $"Server speaks protocol {ProtocolConstants.ProtocolVersion} / rules {RulesInfo.Version}.",
                Seq = hello.Seq,
            }, ct).ConfigureAwait(false);
            return false;
        }

        // Data gate (B0): only when the client sends a hash — closes the "version gate guards code, not
        // card data" gap. Bots and legacy callers omit it and are unaffected.
        if (!string.IsNullOrEmpty(hello.DataHash) && hello.DataHash != _content.DataHash)
        {
            await SendAsync(new ErrorMsg
            {
                Code = "data_mismatch",
                Message = "Your card data differs from the server's — please update the game.",
                Seq = hello.Seq,
            }, ct).ConfigureAwait(false);
            return false;
        }

        GuestId = string.IsNullOrWhiteSpace(hello.GuestId) ? SessionAuth.NewGuestId() : hello.GuestId;
        Name = string.IsNullOrWhiteSpace(hello.Name) ? GuestId : hello.Name;

        // Persistent identity (B0): register on first sight, verify the secret thereafter. A hello without
        // a secret is an anonymous/ephemeral session (bots, tests) — no db row, throwaway guest id.
        if (!string.IsNullOrEmpty(hello.Secret) && !string.IsNullOrWhiteSpace(hello.GuestId))
        {
            var (outcome, account) = _accounts.RegisterOrRestore(hello.GuestId, hello.Secret, Name);
            if (outcome == AccountStore.Outcome.BadSecret)
            {
                await SendAsync(new ErrorMsg
                {
                    Code = "bad_identity",
                    Message = "That guest id is taken by another device.",
                    Seq = hello.Seq,
                }, ct).ConfigureAwait(false);
                return false;
            }
            Name = account.Name;
        }

        await SendAsync(new HelloOk
        {
            ServerTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Motd = _opts.Motd,
            Seq = hello.Seq,
        }, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(hello.ResumeToken))
        {
            if (_rooms.TryReconnect(hello.ResumeToken, this))
                return true; // Reattach (via the match pump) resyncs this connection

            await SendAsync(new ErrorMsg { Code = "bad_resume", Message = "Unknown or expired resume token." }, ct)
                .ConfigureAwait(false);
            return false;
        }

        await SendAsync(BuildProfile(), ct).ConfigureAwait(false); // B0: name + Beta defaults; ratings/decks filled in B1/B2
        return true;
    }

    /// <summary>The account snapshot pushed after hello_ok and on get_profile. Name + decks are persisted;
    /// rating / W-L stay at Beta defaults until LadderStore lands (B2).</summary>
    private Profile BuildProfile()
    {
        var (rating, wins, losses) = _ladder.Get(GuestId);
        return new Profile
        {
            Name = Name,
            Rating = rating,
            Wins = wins,
            Losses = losses,
            Decks = _decks.ListFor(GuestId).Select(d => new DeckSummary
            {
                Id = d.Id, Name = d.Name, Faction = d.Faction, Leader = d.Leader, CardIds = d.CardIds,
            }).ToList(),
            CollectionMode = _collection.Mode,
        };
    }

    private async Task DispatchAsync(ClientMessage msg)
    {
        switch (msg)
        {
            case Ping:
                await SendAsync(new Pong { Seq = msg.Seq }).ConfigureAwait(false);
                break;

            case CreateRoom cr:
                var room = _rooms.CreateRoom(this, cr.DeckId);
                await SendAsync(new RoomCreated { Code = room.Code, Seq = msg.Seq }).ConfigureAwait(false);
                break;

            case JoinRoom jr:
                await _rooms.JoinAsync(jr.Code, this, jr.DeckId).ConfigureAwait(false);
                break;

            case LeaveRoom:
                _rooms.OnDisconnect(this);
                break;

            case SubmitCommand or Resync:
                var session = Room?.Session ?? throw new ProtocolError("not_in_match", "No active match on this connection.");
                session.Enqueue(Seat, msg);
                break;

            case GetProfile:
                await SendAsync(BuildProfile()).ConfigureAwait(false);
                break;

            case SaveDeck sd:
                await HandleSaveDeckAsync(sd).ConfigureAwait(false);
                break;

            case DeleteDeck dd:
                _decks.Delete(GuestId, dd.DeckId);
                await SendAsync(BuildProfile()).ConfigureAwait(false);
                break;

            case JoinQueue jq:
                _queue.Join(this, jq.DeckId); // validates the deck; throws ProtocolError on a bad one
                await SendAsync(new QueueStatus { Position = 1, WaitedSeconds = 0, BotFallbackIn = QueueManager.BotFallbackSeconds, Seq = msg.Seq }).ConfigureAwait(false);
                break;

            case LeaveQueue:
                _queue.Leave(this);
                break;

            case GetLadder gl:
                await SendAsync(BuildLadder(gl.Season ?? LadderStore.DefaultSeason, gl.Seq)).ConfigureAwait(false);
                break;

            case Hello:
                throw new ProtocolError("already_hello", "Duplicate hello on an established connection.");
        }
    }

    /// <summary>Validate and persist a custom deck (B1). Server-authoritative: it derives the faction from
    /// the cards, checks size/rarity/faction purity via <see cref="DeckValidator"/>, and verifies the
    /// leader exists and matches the faction — the client can't smuggle in an illegal deck.</summary>
    private async Task HandleSaveDeckAsync(SaveDeck sd)
    {
        async Task Reject(string code, string message) =>
            await SendAsync(new DeckError { Code = code, Message = message, Seq = sd.Seq }).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(sd.Name))
        {
            await Reject("invalid_name", "A deck needs a name."); return;
        }
        if (DeckValidator.Validate(sd.CardIds, _content.Cards) is { } deckErr)
        {
            await Reject("invalid_deck", deckErr.Message); return;
        }
        if (!_content.Leaders.TryGet(sd.Leader, out var leader))
        {
            await Reject("unknown_leader", $"No leader '{sd.Leader}'."); return;
        }

        string faction = DeriveFaction(sd.CardIds);
        if (faction != DeckValidator.NeutralFaction && leader.Faction != DeckValidator.NeutralFaction && leader.Faction != faction)
        {
            await Reject("leader_faction", $"Leader '{leader.Name}' ({leader.Faction}) doesn't match a {faction} deck."); return;
        }

        if (sd.DeckId is null && _decks.CountFor(GuestId) >= DeckStore.MaxDecksPerAccount)
        {
            await Reject("too_many_decks", $"You already have {DeckStore.MaxDecksPerAccount} decks."); return;
        }

        string? id = _decks.Save(GuestId, sd.DeckId, sd.Name.Trim(), faction, sd.Leader, sd.CardIds);
        if (id is null)
        {
            await Reject("unknown_deck", "That deck doesn't exist."); return;
        }

        await SendAsync(new DeckSaved { DeckId = id, Seq = sd.Seq }).ConfigureAwait(false);
        await SendAsync(BuildProfile()).ConfigureAwait(false);
    }

    /// <summary>Top-50 ladder + this player's own rank (M3 B4), names resolved from the account store.</summary>
    private Ladder BuildLadder(int season, int seq) => new()
    {
        Entries = _ladder.Top(50, season).Select(r => new LadderEntry
        {
            Rank = r.Rank,
            Name = _accounts.Find(r.GuestId)?.Name ?? r.GuestId,
            Rating = r.Rating,
            Wins = r.Wins,
            Losses = r.Losses,
        }).ToList(),
        MyRank = _ladder.Rank(GuestId, season),
        Seq = seq,
    };

    /// <summary>The deck's faction is its single non-neutral faction (DeckValidator guarantees at most one),
    /// or neutral if it's an all-neutral pile.</summary>
    private string DeriveFaction(IReadOnlyList<string> cardIds)
    {
        foreach (var id in cardIds)
            if (_content.Cards.TryGet(id, out var def) && def.Faction != DeckValidator.NeutralFaction)
                return def.Faction;
        return DeckValidator.NeutralFaction;
    }

    /// <summary>Next decoded client frame; null when the socket closes. Unknown tags are logged and skipped.</summary>
    private async Task<ClientMessage?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var json = await WebSocketText.ReceiveAsync(socket, ct).ConfigureAwait(false);
            if (json is null)
                return null;

            var msg = ProtocolJson.TryDecodeClient(json);
            if (msg is null)
            {
                logger.LogDebug("Skipping unknown frame from {Guest}", GuestId);
                continue;
            }
            return msg;
        }
    }

    public async Task SendAsync(ServerMessage msg, CancellationToken ct = default)
    {
        var json = ProtocolJson.Encode(msg);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
                await WebSocketText.SendAsync(socket, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
