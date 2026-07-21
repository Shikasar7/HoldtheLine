using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Per-card, presentation-only art direction. Zoom 1 is a cover fit; the editor supports 0.5–3x.
/// Offsets are normalized against half the target window, deliberately allowing uncovered space.
/// Keeping this separate from rules data prevents a visual adjustment from changing the authoritative
/// card-data hash or the network protocol.
/// </summary>
public sealed record CardArtFrame
{
    public float Zoom { get; init; } = 1f;
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
}

public static class CardArtFraming
{
    public const string ResourcePath = "res://data/card_art_framing.json";
    public const float MinZoom = 0.5f;
    public const float MaxZoom = 3f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static Dictionary<string, CardArtFrame>? _frames;

    public static CardArtFrame Get(string cardId)
    {
        EnsureLoaded();
        return _frames!.TryGetValue(cardId, out var frame) ? frame : new CardArtFrame();
    }

    public static void Set(string cardId, CardArtFrame frame)
    {
        EnsureLoaded();
        var normalized = new CardArtFrame
        {
            Zoom = Mathf.Clamp(frame.Zoom, MinZoom, MaxZoom),
            OffsetX = Mathf.Clamp(frame.OffsetX, -1f, 1f),
            OffsetY = Mathf.Clamp(frame.OffsetY, -1f, 1f),
        };
        if (IsDefault(normalized)) _frames!.Remove(cardId);
        else _frames![cardId] = normalized;
    }

    public static void Reset(string cardId)
    {
        EnsureLoaded();
        _frames!.Remove(cardId);
    }

    /// <summary>Writes the visual-only override file. Intended for the editor/dev build, not players.</summary>
    public static bool Save(out string error)
    {
        EnsureLoaded();
        try
        {
            string path = ProjectSettings.GlobalizePath(ResourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_frames, JsonOptions) + System.Environment.NewLine);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_frames != null) return;
        _frames = new Dictionary<string, CardArtFrame>(StringComparer.Ordinal);
        try
        {
            // Godot FileAccess can read from both the editor filesystem and an exported PCK.
            if (!Godot.FileAccess.FileExists(ResourcePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, CardArtFrame>>(
                Godot.FileAccess.GetFileAsString(ResourcePath), JsonOptions);
            if (loaded != null)
                _frames = new Dictionary<string, CardArtFrame>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"CardArtFraming: could not load {ResourcePath}: {ex.Message}");
        }
    }

    private static bool IsDefault(CardArtFrame frame) =>
        Mathf.IsEqualApprox(frame.Zoom, 1f)
        && Mathf.IsZeroApprox(frame.OffsetX)
        && Mathf.IsZeroApprox(frame.OffsetY);
}
