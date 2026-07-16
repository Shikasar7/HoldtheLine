using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoldTheLine.Rules.Serialization;

/// <summary>
/// The single JSON contract for everything the rules layer serializes: card data files,
/// commands/events on the (future) wire, state snapshots, and replay logs. snake_case throughout.
/// </summary>
public static class RulesJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException($"JSON deserialized to null for {typeof(T).Name}.");

    /// <summary>
    /// Deep clone via a JSON round-trip. Doubles as a continuous serializability check: anything
    /// that can't survive this cannot exist in GameState (hard constraint #2, plan §3.1).
    /// </summary>
    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
}
