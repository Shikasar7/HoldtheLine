using System.Collections.Concurrent;

namespace HoldTheLine.Server;

/// <summary>
/// Brute-force guard for register/login (docs/12 B1). Counts recent failures per username and per source
/// IP; five failures on either key locks that key for 60 seconds. No timer — expiry is lazy (checked on
/// access), and a success clears the counters. DI singleton.
/// </summary>
public sealed class AuthThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockWindow = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Entry> _byUser = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _byIp = new(StringComparer.Ordinal);

    private sealed record Entry(int Count, DateTimeOffset Until);

    /// <summary>True if neither the username nor the IP is currently locked out.</summary>
    public bool TryAcquire(string usernameLower, string remoteIp) =>
        !IsLocked(_byUser, usernameLower) && !IsLocked(_byIp, remoteIp);

    public void RecordFailure(string usernameLower, string remoteIp)
    {
        Bump(_byUser, usernameLower);
        Bump(_byIp, remoteIp);
    }

    public void RecordSuccess(string usernameLower, string remoteIp)
    {
        _byUser.TryRemove(usernameLower, out _);
        _byIp.TryRemove(remoteIp, out _);
    }

    private static bool IsLocked(ConcurrentDictionary<string, Entry> map, string key)
    {
        if (!map.TryGetValue(key, out var e))
            return false;
        if (e.Until <= DateTimeOffset.UtcNow)
        {
            map.TryRemove(key, out _); // window elapsed → forget it
            return false;
        }
        return e.Count >= MaxFailures;
    }

    private static void Bump(ConcurrentDictionary<string, Entry> map, string key)
    {
        var now = DateTimeOffset.UtcNow;
        map.AddOrUpdate(
            key,
            _ => new Entry(1, now + LockWindow),
            (_, e) => e.Until <= now
                ? new Entry(1, now + LockWindow)          // previous window expired → start fresh
                : new Entry(e.Count + 1, now + LockWindow)); // still within window → count up, slide the lock
    }
}
