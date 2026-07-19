using System.Text.Json;
using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// The device's persistent guest identity (M3 B0). A stable <c>guest_id</c> plus a random <c>secret</c>
/// are generated on first launch and stored in <c>user://identity.json</c>; the hello carries both so
/// the server recognizes this device across sessions. "本机密钥 = 身份" — lose the file, you're a new
/// player (acceptable for a friends Beta; real accounts are M4).
/// </summary>
public static class Identity
{
    private const string Path = "user://identity.json";
    private static (string GuestId, string Secret)? _cached;

    private sealed record Stored(string GuestId, string Secret);

    /// <summary>Whether a stored identity file exists and is non-empty — WITHOUT creating one (unlike
    /// <see cref="Get"/>, which mints and persists a guest identity on demand). docs/16 uses this only as a
    /// hint; the authoritative "has the player entered yet" gate is <see cref="Prefs.Entered"/>.</summary>
    public static bool Exists()
    {
        if (_cached is not null) return true;
        if (!Godot.FileAccess.FileExists(Path)) return false;
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Read);
        return f != null && !string.IsNullOrWhiteSpace(f.GetAsText());
    }

    /// <summary>Wipe the stored identity and cache (docs/16 logout). The next <see cref="Get"/> mints a
    /// brand-new guest — the old guest secret is gone for good (no server-side recovery), so callers warn first.</summary>
    public static void Clear()
    {
        _cached = null;
        using var dir = Godot.DirAccess.Open("user://");
        if (dir != null && dir.FileExists("identity.json"))
            dir.Remove("identity.json");
    }

    public static (string GuestId, string Secret) Get()
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

        if (stored is null || string.IsNullOrEmpty(stored.GuestId) || string.IsNullOrEmpty(stored.Secret))
        {
            stored = new Stored("guest-" + Token(6), Token(16));
            Save(stored);
        }

        _cached = (stored.GuestId, stored.Secret);
        return _cached.Value;
    }

    /// <summary>Adopt a new identity, persisting it and refreshing the cache (docs/12 B1). Called after a
    /// successful login: the server rotated this account's secret, so the device stores the new pair — the
    /// old identity.json is now stale everywhere else (single active device).</summary>
    public static void Replace(string guestId, string secret)
    {
        var stored = new Stored(guestId, secret);
        Save(stored);
        _cached = (guestId, secret);
    }

    private static void Save(Stored stored)
    {
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
        if (f != null)
            f.StoreString(JsonSerializer.Serialize(stored));
        else
            GD.PushError($"Identity: cannot write {Path}");
    }

    private static string Token(int bytes)
    {
        var buf = new byte[bytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return System.Convert.ToHexString(buf).ToLowerInvariant();
    }
}
