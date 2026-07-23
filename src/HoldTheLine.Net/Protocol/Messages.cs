using System.Text.Json.Serialization;
using HoldTheLine.Rules.Commands;
using HoldTheLine.Rules.Events;
using HoldTheLine.Rules.Hosting;

namespace HoldTheLine.Net.Protocol;

/// <summary>
/// The wire vocabulary (M2 plan §4). Two polymorphic families — client→server and server→client —
/// each discriminated by a <c>t</c> tag, exactly like <see cref="Command"/>/<see cref="GameEvent"/>
/// are by <c>$type</c>. The tag *is* the envelope: a frame is one JSON object carrying its type in
/// <c>t</c>, its request id in <c>seq</c>, and its payload as sibling fields (this flat shape
/// supersedes the illustrative {t,seq,p} nesting sketched in the plan — same information, no double
/// tagging, and it round-trips through the same <see cref="Rules.Serialization.RulesJson"/> contract).
///
/// FROZEN like the rules shapes: adding/removing a message or field is a protocol change → Fable
/// review (switch signal S2). Bump <see cref="ProtocolConstants.ProtocolVersion"/> when it happens.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(CreateRoom), "create_room")]
[JsonDerivedType(typeof(JoinRoom), "join_room")]
[JsonDerivedType(typeof(LeaveRoom), "leave_room")]
[JsonDerivedType(typeof(SubmitCommand), "submit_command")]
[JsonDerivedType(typeof(Resync), "resync")]
[JsonDerivedType(typeof(Ping), "ping")]
// --- protocol v2 (M3 plan §3.4): lobby / deck / queue / ladder. Frozen up front so later phases add
//     only handlers, not new message shapes.
[JsonDerivedType(typeof(SaveDeck), "save_deck")]
[JsonDerivedType(typeof(DeleteDeck), "delete_deck")]
[JsonDerivedType(typeof(GetProfile), "get_profile")]
[JsonDerivedType(typeof(JoinQueue), "join_queue")]
[JsonDerivedType(typeof(LeaveQueue), "leave_queue")]
[JsonDerivedType(typeof(GetLadder), "get_ladder")]
[JsonDerivedType(typeof(Rematch), "rematch")]
// --- protocol v5 (docs/12 B1): username+password accounts on top of the persistent guest identity.
[JsonDerivedType(typeof(Register), "register")]
[JsonDerivedType(typeof(Login), "login")]
// --- docs/16 (login flow): rename the display name in place, no reconnect. Additive like ClientVersion —
//     an old server just skips the unknown "set_name" tag, so it is NOT a protocol-version bump.
[JsonDerivedType(typeof(SetName), "set_name")]
// --- 开发者测试修改器 (dev-only). Additive/tolerant like set_name (an old server log-and-skips the unknown
//     tag) → NOT a ProtocolVersion bump. Server only acts on it when ServerOptions.DevCheatsEnabled; ranked
//     matches additionally require ServerOptions.DevCheatsAllowRanked.
[JsonDerivedType(typeof(DevCheat), "dev_cheat")]
public abstract record ClientMessage
{
    /// <summary>Client-assigned, monotonic per connection. Server echoes it in the matching reply
    /// (e.g. <see cref="CommandResultMsg.AckSeq"/>) so requests and responses can be paired.</summary>
    public int Seq { get; init; }
}

public sealed record Hello : ClientMessage
{
    public required string GuestId { get; init; }
    public required string Name { get; init; }
    public required int ProtocolVersion { get; init; }
    public required string RulesVersion { get; init; }
    /// <summary>Present only when re-attaching to an in-progress match after a drop.</summary>
    public string? ResumeToken { get; init; }
    /// <summary>Persistent-identity credential (M3 B0): the random secret paired with a stable GuestId.
    /// Null for an anonymous/ephemeral session — the server issues a throwaway guest id and skips
    /// persistence. When present, the server registers on first sight and verifies it thereafter.</summary>
    public string? Secret { get; init; }
    /// <summary>Content fingerprint of the client's card/leader/deck data (see <see cref="DataHash"/>).
    /// Null skips the check (e.g. bots); when present and unequal to the server's, the handshake is
    /// rejected with a "data_mismatch" update prompt.</summary>
    public string? DataHash { get; init; }
    /// <summary>Client app version (docs/15 §2), SemVer Major.Minor.Patch — distinct from
    /// <see cref="RulesVersion"/>. Optional (bots / legacy clients omit it → the server treats it as
    /// "0.0.0"). When the server has a configured min client version and this is below it, the handshake
    /// is rejected with "client_outdated" (until then the server only logs — see ServerOptions).</summary>
    public string? ClientVersion { get; init; }
}

public sealed record CreateRoom : ClientMessage
{
    public required string DeckId { get; init; }
}

public sealed record JoinRoom : ClientMessage
{
    public required string Code { get; init; }
    public required string DeckId { get; init; }
}

public sealed record LeaveRoom : ClientMessage;

/// <summary>The only in-match input. Carries a polymorphic <see cref="Command"/> (nested $type).</summary>
public sealed record SubmitCommand : ClientMessage
{
    public required Command Command { get; init; }
}

public sealed record Resync : ClientMessage
{
    public required int SinceEventIndex { get; init; }
}

public sealed record Ping : ClientMessage;

// --- protocol v2 client messages (M3 §3.4) -------------------------------------------------------

/// <summary>Create or update a deck (server validates with DeckValidator). Null DeckId = create.</summary>
public sealed record SaveDeck : ClientMessage
{
    public string? DeckId { get; init; }
    public required string Name { get; init; }
    public required string Leader { get; init; }
    public required IReadOnlyList<string> CardIds { get; init; }
}

public sealed record DeleteDeck : ClientMessage
{
    public required string DeckId { get; init; }
}

/// <summary>Ask the server to (re)push the account <see cref="Profile"/>.</summary>
public sealed record GetProfile : ClientMessage;

public sealed record JoinQueue : ClientMessage
{
    public required string DeckId { get; init; }
}

public sealed record LeaveQueue : ClientMessage;

public sealed record GetLadder : ClientMessage
{
    public int? Season { get; init; }
}

/// <summary>After a match ends, both players sending this re-opens the same room for another game.</summary>
public sealed record Rematch : ClientMessage;

// --- protocol v5 client messages (docs/12 B1) ----------------------------------------------------

/// <summary>Upgrade the connection's already-handshaked persistent identity into a username+password
/// account. Win/loss, decks and rating stay in place — it just binds credentials to the same guest_id.</summary>
public sealed record Register : ClientMessage
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

/// <summary>Log in to a registered account: on success this connection's identity switches to that
/// account and the server rotates its secret (see AuthOk / docs/12 B1.3).</summary>
public sealed record Login : ClientMessage
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

/// <summary>docs/16: change the display name of the current connection (guest or account). Applies to this
/// connection immediately and persists to the identity's Profile; the server replies with a fresh
/// <see cref="Profile"/> (carrying this message's Seq) on success, or an <see cref="ErrorMsg"/> on a bad name.</summary>
public sealed record SetName : ClientMessage
{
    public required string Name { get; init; }
}

/// <summary>开发者测试修改器 (dev-only): one in-match request from the sending seat. <see cref="Kind"/> is
/// <c>refill_mana</c> (top this seat's 辉尘 to max), <c>tutor_card</c> (move <see cref="CardEntityId"/> from
/// this seat's deck to hand), or <c>list_deck</c> (server replies with a <see cref="DevDeckList"/> of this
/// seat's OWN deck — its own hidden info, so no leak). The server acts only when it has DevCheatsEnabled;
/// ranked matches additionally require DevCheatsAllowRanked. Otherwise it replies with an
/// <see cref="ErrorMsg"/> (code dev_*).</summary>
public sealed record DevCheat : ClientMessage
{
    public required string Kind { get; init; }
    /// <summary>Target card EntityId for <c>tutor_card</c>; ignored by the other kinds.</summary>
    public int? CardEntityId { get; init; }
}

// ---------------------------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloOk), "hello_ok")]
[JsonDerivedType(typeof(RoomCreated), "room_created")]
[JsonDerivedType(typeof(MatchStarted), "match_started")]
[JsonDerivedType(typeof(CommandResultMsg), "command_result")]
[JsonDerivedType(typeof(EventsMsg), "events")]
[JsonDerivedType(typeof(ResyncOk), "resync_ok")]
[JsonDerivedType(typeof(OpponentStatus), "opponent_status")]
[JsonDerivedType(typeof(TurnTimer), "turn_timer")]
[JsonDerivedType(typeof(MatchEnded), "match_ended")]
[JsonDerivedType(typeof(ErrorMsg), "error")]
[JsonDerivedType(typeof(Pong), "pong")]
// --- protocol v2 (M3 §3.4) ---
[JsonDerivedType(typeof(Profile), "profile")]
[JsonDerivedType(typeof(DeckSaved), "deck_saved")]
[JsonDerivedType(typeof(DeckError), "deck_error")]
[JsonDerivedType(typeof(QueueStatus), "queue_status")]
[JsonDerivedType(typeof(Ladder), "ladder")]
[JsonDerivedType(typeof(RematchStatus), "rematch_status")]
[JsonDerivedType(typeof(RatingChange), "rating_change")]
// --- protocol v5 (docs/12 B1) ---
[JsonDerivedType(typeof(AuthOk), "auth_ok")]
// --- 开发者测试修改器 (dev-only). Additive/tolerant → NOT a ProtocolVersion bump. Reply to DevCheat{list_deck}.
[JsonDerivedType(typeof(DevDeckList), "dev_deck_list")]
public abstract record ServerMessage
{
    /// <summary>Echoes the client <see cref="ClientMessage.Seq"/> this is a direct reply to; 0 for
    /// unsolicited pushes (events, timers, opponent status).</summary>
    public int Seq { get; init; }
}

public sealed record HelloOk : ServerMessage
{
    public required long ServerTimeUnixMs { get; init; }
    public string? Motd { get; init; }
}

public sealed record RoomCreated : ServerMessage
{
    public required string Code { get; init; }
}

public sealed record MatchStarted : ServerMessage
{
    public required int Seat { get; init; }
    /// <summary>Opaque session credential; the only way to re-attach after a drop within the grace window.</summary>
    public required string ResumeToken { get; init; }
    public required PlayerView View { get; init; }
    public required string OpponentName { get; init; }
    /// <summary>Legal commands for the recipient — non-empty only if it is their turn.</summary>
    public IReadOnlyList<Command>? LegalCommands { get; init; }
    /// <summary>起手重抽 (docs/11): seconds left on the shared mulligan clock when the match opens with a
    /// mulligan phase; null when there is none (or it has already passed). Whether THIS seat still owes a
    /// mulligan is in <see cref="View"/>.MulliganPending.</summary>
    public int? MulliganSecondsLeft { get; init; }
}

public sealed record CommandResultMsg : ServerMessage
{
    public required int AckSeq { get; init; }
    public required bool Accepted { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>A batch of already seat-redacted resolution events, in resolver order.</summary>
public sealed record EventsMsg : ServerMessage
{
    public required IReadOnlyList<GameEvent> Batch { get; init; }
    /// <summary>The authoritative post-batch snapshot for this seat. Ships with every batch so
    /// RemoteGameHost.GetView stays current without reconstructing state from events (plan §6.2) —
    /// events drive animation, the view drives layout.</summary>
    public required PlayerView View { get; init; }
    /// <summary>Running count of events dispatched to this seat *after* this batch — used to detect gaps.</summary>
    public required int EventIndex { get; init; }
    /// <summary>Present when the recipient is the one now on the move; their fresh legal moves.</summary>
    public IReadOnlyList<Command>? LegalCommands { get; init; }
}

public sealed record ResyncOk : ServerMessage
{
    public required PlayerView View { get; init; }
    public required IReadOnlyList<GameEvent> EventsSince { get; init; }
    public required int EventIndex { get; init; }
    public IReadOnlyList<Command>? LegalCommands { get; init; }
    /// <summary>起手重抽 (docs/11): seconds left on the mulligan clock when reconnecting mid-phase; null
    /// otherwise. Pairs with <see cref="View"/>.MulliganPending to rebuild the mulligan UI on resume.</summary>
    public int? MulliganSecondsLeft { get; init; }
}

public sealed record OpponentStatus : ServerMessage
{
    public required bool Connected { get; init; }
    /// <summary>Seconds left before the disconnected opponent forfeits; null when reconnected.</summary>
    public int? GraceSeconds { get; init; }
}

public sealed record TurnTimer : ServerMessage
{
    public required int Seat { get; init; }
    public required int SecondsLeft { get; init; }
}

public sealed record MatchEnded : ServerMessage
{
    /// <summary>-1 for a draw.</summary>
    public required int WinnerSeat { get; init; }
    /// <summary>normal | concede | timeout | abandon.</summary>
    public required string Reason { get; init; }
}

public sealed record ErrorMsg : ServerMessage
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record Pong : ServerMessage;

/// <summary>Register / login succeeded. On login (only) it carries the account's guest_id and a freshly
/// rotated secret; the client persists both into identity.json (docs/12 B1.3). On register both are null
/// (the guest_id is unchanged). Failures use <see cref="ErrorMsg"/>.</summary>
public sealed record AuthOk : ServerMessage
{
    public required string Username { get; init; }
    public string? GuestId { get; init; }
    public string? Secret { get; init; }
}

// --- protocol v2 server messages (M3 §3.4) -------------------------------------------------------

/// <summary>Account snapshot, pushed right after hello_ok and on demand (get_profile).</summary>
public sealed record Profile : ServerMessage
{
    public required string Name { get; init; }
    /// <summary>The bound account username, or null for a plain guest (docs/16). Lets the client know it is
    /// a registered account after a silent reconnect, where no AuthOk is sent — additive/optional, so it is
    /// null-skipped and NOT a protocol bump (old clients ignore it, an old server sends null).</summary>
    public string? Username { get; init; }
    public required int Rating { get; init; }
    public required int Wins { get; init; }
    public required int Losses { get; init; }
    public required IReadOnlyList<DeckSummary> Decks { get; init; }
    /// <summary>"unlock_all" during Beta (D-M3-2) — every card is available.</summary>
    public required string CollectionMode { get; init; }
}

/// <summary>Complete deck snapshot inside <see cref="Profile"/>. Carries the full card list so the
/// client can edit a saved deck without a separate get_deck round-trip — the profile IS the account
/// snapshot (protocol v3; Fable ruling 2026-07-18: cheaper than a new message pair at Beta scale,
/// ~30 ids × ≤20 decks per push).</summary>
public sealed record DeckSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Faction { get; init; }
    public required string Leader { get; init; }
    public required IReadOnlyList<string> CardIds { get; init; }
}

public sealed record DeckSaved : ServerMessage
{
    public required string DeckId { get; init; }
}

public sealed record DeckError : ServerMessage
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record QueueStatus : ServerMessage
{
    /// <summary>1-based place in line; null while it's being computed / not queued.</summary>
    public int? Position { get; init; }
    public required int WaitedSeconds { get; init; }
}

public sealed record Ladder : ServerMessage
{
    public required IReadOnlyList<LadderEntry> Entries { get; init; }
    /// <summary>The requester's own rank (0 if unranked).</summary>
    public required int MyRank { get; init; }
}

public sealed record LadderEntry
{
    public required int Rank { get; init; }
    public required string Name { get; init; }
    public required int Rating { get; init; }
    public required int Wins { get; init; }
    public required int Losses { get; init; }
}

public sealed record RematchStatus : ServerMessage
{
    public required bool YouReady { get; init; }
    public required bool OpponentReady { get; init; }
}

public sealed record RatingChange : ServerMessage
{
    public required int Old { get; init; }
    public required int New { get; init; }
    public required int Season { get; init; }
}

/// <summary>开发者测试修改器 (dev-only): the reply to a <see cref="DevCheat"/> with kind <c>list_deck</c> —
/// the requesting seat's OWN deck contents, so its client's tutor picker can list them (the client otherwise
/// only knows its DeckCount). Sent to the requesting connection only.</summary>
public sealed record DevDeckList : ServerMessage
{
    public required IReadOnlyList<DevDeckCard> Cards { get; init; }
}

public sealed record DevDeckCard
{
    public required int EntityId { get; init; }
    public required string CardId { get; init; }
}
