using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoldTheLine.Rules.Cards;
using HoldTheLine.Rules.Serialization;

namespace HoldTheLine.Net;

/// <summary>
/// A content fingerprint of the loaded card / leader / deck data (M3 plan B0). Computed from the
/// *normalized* <see cref="RulesJson"/> serialization of the parsed data — never raw file bytes — so
/// it is identical whether the data came from a Godot <c>res://</c> pack or the server filesystem,
/// regardless of source line endings, file ordering, or whitespace.
///
/// <para>hello carries the client's hash; the server compares it with its own. A mismatch means client
/// and server ship *different* card data (someone tuned a value and redeployed only one side), which the
/// M2 version gate could not catch — it guarded code, not data — and which would otherwise diverge
/// silently mid-match. The handshake rejects it with a clear "update" error.</para>
/// </summary>
public static class DataHash
{
    public static string Compute(CardDatabase cards, LeaderDatabase leaders, IReadOnlyList<DeckList> decks)
    {
        var sb = new StringBuilder();
        sb.Append("cards\n");
        foreach (var c in cards.All.OrderBy(c => c.Id, StringComparer.Ordinal))
            sb.Append(JsonSerializer.Serialize(c, RulesJson.Options)).Append('\n');
        sb.Append("leaders\n");
        foreach (var l in leaders.All.OrderBy(l => l.Id, StringComparer.Ordinal))
            sb.Append(JsonSerializer.Serialize(l, RulesJson.Options)).Append('\n');
        sb.Append("decks\n");
        foreach (var d in decks.OrderBy(d => d.Id, StringComparer.Ordinal))
            sb.Append(JsonSerializer.Serialize(d, RulesJson.Options)).Append('\n');

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
