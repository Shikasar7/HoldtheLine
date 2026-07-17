using System.Net.WebSockets;
using HoldTheLine.Net;
using HoldTheLine.Net.Protocol;
using HoldTheLine.Rules;
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

    public string GuestId { get; private set; } = "";
    public string Name { get; private set; } = "";
    public Room? Room { get; set; }
    public int Seat { get; set; }

    public async Task RunAsync(RoomManager rooms, GameContent content, ServerOptions opts, AccountStore accounts, CancellationToken ct)
    {
        try
        {
            if (!await HandshakeAsync(rooms, content, opts, accounts, ct).ConfigureAwait(false))
                return;

            while (!ct.IsCancellationRequested)
            {
                var msg = await ReceiveAsync(ct).ConfigureAwait(false);
                if (msg is null)
                    break; // socket closed

                try
                {
                    await DispatchAsync(msg, rooms, content).ConfigureAwait(false);
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
            rooms.OnDisconnect(this);
        }
    }

    /// <summary>First frame must be a version-matching hello; otherwise send an error and close. A hello
    /// carrying a resume token re-attaches to an in-progress match instead of waiting for create/join.
    /// M3 B0: also gates on the data hash and establishes / restores the persistent identity, then pushes
    /// the account <see cref="Profile"/>.</summary>
    private async Task<bool> HandshakeAsync(RoomManager rooms, GameContent content, ServerOptions opts, AccountStore accounts, CancellationToken ct)
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
        if (!string.IsNullOrEmpty(hello.DataHash) && hello.DataHash != content.DataHash)
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
            var (outcome, account) = accounts.RegisterOrRestore(hello.GuestId, hello.Secret, Name);
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
            Motd = opts.Motd,
            Seq = hello.Seq,
        }, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(hello.ResumeToken))
        {
            if (rooms.TryReconnect(hello.ResumeToken, this))
                return true; // Reattach (via the match pump) resyncs this connection

            await SendAsync(new ErrorMsg { Code = "bad_resume", Message = "Unknown or expired resume token." }, ct)
                .ConfigureAwait(false);
            return false;
        }

        await SendAsync(BuildProfile(), ct).ConfigureAwait(false); // B0: name + Beta defaults; ratings/decks filled in B1/B2
        return true;
    }

    /// <summary>The account snapshot pushed after hello_ok. B0 has only the name persisted; rating, W/L
    /// and decks are Beta defaults here and get real values once LadderStore/DeckStore land (B1/B2).</summary>
    private Profile BuildProfile() => new()
    {
        Name = Name,
        Rating = 1000,
        Wins = 0,
        Losses = 0,
        Decks = [],
        CollectionMode = "unlock_all",
    };

    private async Task DispatchAsync(ClientMessage msg, RoomManager rooms, GameContent content)
    {
        switch (msg)
        {
            case Ping:
                await SendAsync(new Pong { Seq = msg.Seq }).ConfigureAwait(false);
                break;

            case CreateRoom cr:
                var room = rooms.CreateRoom(this, cr.DeckId, content);
                await SendAsync(new RoomCreated { Code = room.Code, Seq = msg.Seq }).ConfigureAwait(false);
                break;

            case JoinRoom jr:
                await rooms.JoinAsync(jr.Code, this, jr.DeckId, content).ConfigureAwait(false);
                break;

            case LeaveRoom:
                rooms.OnDisconnect(this);
                break;

            case SubmitCommand or Resync:
                var session = Room?.Session ?? throw new ProtocolError("not_in_match", "No active match on this connection.");
                session.Enqueue(Seat, msg);
                break;

            case GetProfile:
                await SendAsync(BuildProfile()).ConfigureAwait(false);
                break;

            case Hello:
                throw new ProtocolError("already_hello", "Duplicate hello on an established connection.");
        }
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
