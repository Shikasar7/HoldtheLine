using System.Text.Json;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Net.Protocol;

public static class ProtocolConstants
{
    /// <summary>Wire-format version. Bump on any change to <see cref="ClientMessage"/>/<see cref="ServerMessage"/> shapes.
    /// v2 (M3): persistent identity fields on hello + the §3.4 lobby/deck/queue/ladder message families.
    /// v3 (M3 收尾): <see cref="DeckSummary"/> carries leader + card_ids so saved decks are editable client-side.
    /// v4 (docs/11): 起手重抽 — MulliganCommand rides SubmitCommand (no new C→S message); match_started /
    /// resync_ok carry MulliganSecondsLeft.
    /// v5 (docs/12 B1): 注册/登录 — Register / Login (C→S) + AuthOk (S→C); auth failures reuse ErrorMsg.
    /// docs/15 §2 (2026-07-19): Hello gained the optional <c>ClientVersion</c> field. Deliberately NOT a
    /// version bump — it is null-skipped and both directions tolerate its absence (old server ignores the
    /// unknown property, new server treats a missing value as "0.0.0"), so the soft update-gate can roll
    /// out without hard-rejecting the currently deployed v5 clients.
    /// docs/16 / queue cleanup (2026-07-19): added the <c>set_name</c> C→S message, the optional
    /// Profile.<c>Username</c> field (S→C, tells the client it is a registered account after a silent
    /// reconnect), and dropped QueueStatus.<c>bot_fallback_in</c>. All NOT a bump, same reasoning — an old
    /// server skips the unknown set_name tag / sends a null username, and an old client skips the extra /
    /// now-absent properties (System.Text.Json ignores unmapped members), so mixed v5 builds stay compatible
    /// while both ends are re-shipped.</summary>
    public const int ProtocolVersion = 5;
}

/// <summary>
/// The protocol reuses the rules layer's single JSON contract (<see cref="RulesJson.Options"/>):
/// snake_case, enum-as-string, null-skipping — so a <c>Command</c> or <c>GameEvent</c> nested inside
/// a message serializes byte-identically to how it would on its own. Decoding is deliberately
/// tolerant: an unrecognized <c>t</c> (a newer peer's message type) yields null rather than throwing,
/// so a receive loop can log-and-skip instead of dropping the connection (plan §8-1).
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = RulesJson.Options;

    public static string Encode(ClientMessage message) => JsonSerializer.Serialize(message, Options);
    public static string Encode(ServerMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Decode a client→server frame, or null if the tag is unknown / the JSON is malformed.</summary>
    public static ClientMessage? TryDecodeClient(string json) => TryDecode<ClientMessage>(json);

    /// <summary>Decode a server→client frame, or null if the tag is unknown / the JSON is malformed.</summary>
    public static ServerMessage? TryDecodeServer(string json) => TryDecode<ServerMessage>(json);

    private static T? TryDecode<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null; // unknown discriminator or malformed JSON — caller logs and ignores
        }
        catch (NotSupportedException)
        {
            return null; // no discriminator at all (abstract base can't be built) — same treatment
        }
    }
}
