using System.Text.Json;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Net.Protocol;

public static class ProtocolConstants
{
    /// <summary>Wire-format version. Bump on any change to <see cref="ClientMessage"/>/<see cref="ServerMessage"/> shapes.
    /// v2 (M3): persistent identity fields on hello + the §3.4 lobby/deck/queue/ladder message families.</summary>
    public const int ProtocolVersion = 2;
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
