using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Crash-safe persistence for small <c>user://</c> JSON files (decks, prefs). A save writes a sibling
/// <c>.tmp</c> first and renames it over the target only once fully written, keeping the previous
/// version as <c>.bak</c> — so a kill/power-cut/full-disk mid-save can no longer truncate the real
/// file. A file that fails to parse is quarantined (renamed <c>.corrupt-*</c>) with an error log
/// instead of being silently treated as empty: pre-fix, a truncated decks.json read as "no decks" and
/// the very next save overwrote the player's whole library for good.
/// </summary>
public static class UserFile
{
    /// <summary>Atomically replace <paramref name="path"/> with <paramref name="content"/>.
    /// Sequence: write .tmp → drop old .bak → current file becomes .bak → .tmp becomes current.
    /// Any interruption leaves either the old file or its .bak intact for <see cref="ReadBestText"/>.</summary>
    public static bool WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        string bak = path + ".bak";

        using (var f = Godot.FileAccess.Open(tmp, Godot.FileAccess.ModeFlags.Write))
        {
            if (f == null)
            {
                GD.PushError($"UserFile: cannot write {tmp}: {Godot.FileAccess.GetOpenError()}");
                return false;
            }
            f.StoreString(content);
        }

        if (Godot.FileAccess.FileExists(bak))
            DirAccess.RemoveAbsolute(bak);
        if (Godot.FileAccess.FileExists(path))
            DirAccess.RenameAbsolute(path, bak);
        var err = DirAccess.RenameAbsolute(tmp, path);
        if (err != Error.Ok)
        {
            GD.PushError($"UserFile: rename {tmp} -> {path} failed: {err}");
            return false;
        }
        return true;
    }

    /// <summary>The file's text, or null when it doesn't exist / can't be opened. No fallback.</summary>
    public static string? ReadText(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
            return null;
        using var f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        return f?.GetAsText();
    }

    /// <summary>Text of the newest intact copy: the file itself, else its <c>.bak</c> (covers the
    /// crash window where the main file was renamed away but the fresh one not yet moved in).</summary>
    public static string? ReadBestText(string path)
    {
        return ReadText(path) ?? ReadText(path + ".bak");
    }

    /// <summary>Move a file that failed to parse out of the way (<c>.corrupt-&lt;unix&gt;</c>) so it is
    /// preserved for recovery and never silently overwritten by the next save.</summary>
    public static void Quarantine(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
            return;
        string dest = $"{path}.corrupt-{System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        DirAccess.RenameAbsolute(path, dest);
        GD.PushError($"UserFile: {path} failed to parse — quarantined as {dest}");
    }
}
