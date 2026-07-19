namespace HoldTheLine.Net;

/// <summary>
/// A tiny, dependency-free SemVer-ish comparator shared by the client (version.json "latest" check,
/// docs/15 §3) and the server (the min-client-version gate, docs/15 §2). It compares the numeric
/// Major.Minor.Patch triple only — pre-release / build metadata are ignored (split off at the first
/// '-' or '+'), which is all the update flow needs. Unparseable / missing input compares as 0.0.0, so
/// a client that omits its version is treated as maximally out of date.
/// </summary>
public static class SemVer
{
    /// <summary>&lt;0 if <paramref name="a"/> is older than <paramref name="b"/>, 0 if equal, &gt;0 if newer.</summary>
    public static int Compare(string? a, string? b)
    {
        var (a0, a1, a2) = Parse(a);
        var (b0, b1, b2) = Parse(b);
        if (a0 != b0) return a0.CompareTo(b0);
        if (a1 != b1) return a1.CompareTo(b1);
        return a2.CompareTo(b2);
    }

    /// <summary>True when <paramref name="version"/> is strictly older than <paramref name="minimum"/>.</summary>
    public static bool IsOlder(string? version, string? minimum) => Compare(version, minimum) < 0;

    private static (int Major, int Minor, int Patch) Parse(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return (0, 0, 0);

        // Drop any pre-release / build suffix, then take up to three dotted numeric components.
        int cut = v.IndexOfAny(['-', '+']);
        string core = cut >= 0 ? v[..cut] : v;
        var parts = core.Split('.');
        return (Part(parts, 0), Part(parts, 1), Part(parts, 2));
    }

    private static int Part(string[] parts, int i) =>
        i < parts.Length && int.TryParse(parts[i].Trim(), out var n) && n >= 0 ? n : 0;
}
