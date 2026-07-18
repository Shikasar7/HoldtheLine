using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoldTheLine.Rules.Cards;

public enum DeckCodeError { None, BadFormat, UnsupportedVersion, DataMismatch }

/// <summary>
/// A shareable deck string: leader + card ids + the environment fingerprint (rules version and the
/// first 8 hex of the <c>DataHash</c>) they were built against. The code carries NO deck name — the
/// importer names it — and NO legality guarantee: the caller runs <see cref="DeckValidator.Validate"/>
/// after decoding. Structure and environment checks are split so the UI can distinguish "garbled code"
/// from "code from a different card table".
/// </summary>
public sealed record DeckCodePayload(
    string Rules,                     // RulesInfo.Version at encode time
    string Hash,                      // first 8 hex of DataHash at encode time
    string Leader,
    IReadOnlyList<string> Cards);

public static class DeckCode
{
    public const string Prefix = "HTL1-";

    private const int CurrentVersion = 1;

    // The wire DTO. Property names are frozen (a v1 code lives forever); compact, no indentation.
    private sealed record Dto
    {
        [JsonPropertyName("v")] public int V { get; init; }
        [JsonPropertyName("rules")] public string Rules { get; init; } = "";
        [JsonPropertyName("hash")] public string Hash { get; init; } = "";
        [JsonPropertyName("leader")] public string Leader { get; init; } = "";
        [JsonPropertyName("cards")] public IReadOnlyList<string> Cards { get; init; } = [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static string HashPrefix(string dataHash) =>
        dataHash.Length >= 8 ? dataHash[..8] : dataHash;

    /// <summary>Encode a deck. <paramref name="dataHash"/> is the full 64-hex DataHash; only its first 8 are stored.</summary>
    public static string Encode(string leader, IReadOnlyList<string> cards,
                                string rulesVersion, string dataHash)
    {
        var dto = new Dto
        {
            V = CurrentVersion,
            Rules = rulesVersion,
            Hash = HashPrefix(dataHash),
            Leader = leader,
            Cards = cards,
        };
        return Pack(JsonSerializer.Serialize(dto, JsonOpts));
    }

    /// <summary>Structural decode only. A bad prefix / Base64Url / Deflate / json → BadFormat; v != 1 → UnsupportedVersion.</summary>
    public static (DeckCodeError Error, DeckCodePayload? Payload) Decode(string code)
    {
        if (!TryUnpack(code, out var json))
            return (DeckCodeError.BadFormat, null);

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return (DeckCodeError.BadFormat, null);
        }
        if (dto is null || dto.Cards is null || dto.Leader is null || dto.Rules is null || dto.Hash is null)
            return (DeckCodeError.BadFormat, null);
        if (dto.V != CurrentVersion)
            return (DeckCodeError.UnsupportedVersion, null);

        return (DeckCodeError.None, new DeckCodePayload(dto.Rules, dto.Hash, dto.Leader, dto.Cards));
    }

    /// <summary>Environment check: rules version or hash-prefix mismatch → DataMismatch.</summary>
    public static DeckCodeError Check(DeckCodePayload p, string rulesVersion, string dataHash)
    {
        if (!string.Equals(p.Rules, rulesVersion, StringComparison.Ordinal))
            return DeckCodeError.DataMismatch;
        if (!string.Equals(p.Hash, HashPrefix(dataHash), StringComparison.Ordinal))
            return DeckCodeError.DataMismatch;
        return DeckCodeError.None;
    }

    // ---- pipeline: json <-> HTL1-<base64url(deflate(utf8))> ----------------------------------------

    // internal so DeckCodeTests can hand-craft an out-of-version ("v":2) payload without duplicating the pipeline.
    internal static string Pack(string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        var b64 = Convert.ToBase64String(ms.ToArray());
        return Prefix + b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    internal static bool TryUnpack(string code, out string json)
    {
        json = "";
        if (string.IsNullOrEmpty(code) || !code.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var body = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        switch (body.Length % 4)
        {
            case 2: body += "=="; break;
            case 3: body += "="; break;
            case 1: return false; // never a valid base64 length
        }

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(body);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var ms = new MemoryStream(compressed);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            json = reader.ReadToEnd();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
