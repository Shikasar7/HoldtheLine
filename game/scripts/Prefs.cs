using System.Text.Json;
using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Small persistent UI preferences (<c>user://prefs.json</c>) — currently the deck last taken into a
/// match per surface, so the pickers can preselect it next launch. Mirrors <see cref="Identity"/>'s
/// user-file pattern; loaded lazily, written on every set.
/// </summary>
public static class Prefs
{
    private const string Path = "user://prefs.json";

    private sealed record Stored
    {
        public string LastLobbyDeck { get; init; } = "";   // server deck id or built-in id
        public string LastVsAiDeck { get; init; } = "";    // built-in id or "local:<StoredDeck.Id>"
    }

    private static Stored? _cached;

    private static Stored Load()
    {
        if (_cached is { } c)
            return c;
        Stored? stored = null;
        if (Godot.FileAccess.FileExists(Path))
        {
            using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Read);
            if (f != null)
            {
                try { stored = JsonSerializer.Deserialize<Stored>(f.GetAsText()); }
                catch { stored = null; }
            }
        }
        _cached = stored ?? new Stored();
        return _cached;
    }

    private static void Save(Stored stored)
    {
        _cached = stored;
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
        if (f != null)
            f.StoreString(JsonSerializer.Serialize(stored));
        else
            GD.PushError($"Prefs: cannot write {Path}");
    }

    public static string LastLobbyDeck
    {
        get => Load().LastLobbyDeck;
        set => Save(Load() with { LastLobbyDeck = value });
    }

    public static string LastVsAiDeck
    {
        get => Load().LastVsAiDeck;
        set => Save(Load() with { LastVsAiDeck = value });
    }
}
