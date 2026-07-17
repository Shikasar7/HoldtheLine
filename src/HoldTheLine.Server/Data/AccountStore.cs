using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HoldTheLine.Server.Data;

public sealed record Account(string GuestId, string Name);

/// <summary>
/// Persistent guest identities (M3 plan B0). A client owns a stable <c>guest_id</c> and a random
/// <c>secret</c> (stored client-side in user://identity.json); the server registers the pair on first
/// sight and verifies the secret thereafter, so the identity survives across sessions and server
/// restarts. No passwords or email — "本机密钥 = 身份" (§1.2); lose the key, you're a new player.
/// </summary>
public sealed class AccountStore
{
    private readonly Db _db;

    public AccountStore(Db db)
    {
        _db = db;
        _db.Run(c => Exec(c, """
            CREATE TABLE IF NOT EXISTS accounts (
                guest_id    TEXT PRIMARY KEY,
                secret_hash TEXT NOT NULL,
                name        TEXT NOT NULL,
                created_at  INTEGER NOT NULL,
                last_seen   INTEGER NOT NULL
            );
            """));
    }

    public enum Outcome { Registered, Restored, BadSecret }

    /// <summary>Register a brand-new identity, or restore an existing one after verifying its secret.
    /// On success the display name and last-seen timestamp are refreshed.</summary>
    public (Outcome Outcome, Account Account) RegisterOrRestore(string guestId, string secret, string name)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string hash = HashSecret(secret);

        return _db.Run(c =>
        {
            string? storedHash = QueryScalar(c, "SELECT secret_hash FROM accounts WHERE guest_id=$g", ("$g", guestId));
            if (storedHash is null)
            {
                Exec(c, "INSERT INTO accounts(guest_id,secret_hash,name,created_at,last_seen) VALUES($g,$h,$n,$t,$t)",
                    ("$g", guestId), ("$h", hash), ("$n", name), ("$t", now));
                return (Outcome.Registered, new Account(guestId, name));
            }

            if (!FixedTimeEquals(storedHash, hash))
            {
                string existingName = QueryScalar(c, "SELECT name FROM accounts WHERE guest_id=$g", ("$g", guestId)) ?? guestId;
                return (Outcome.BadSecret, new Account(guestId, existingName));
            }

            Exec(c, "UPDATE accounts SET name=$n, last_seen=$t WHERE guest_id=$g",
                ("$g", guestId), ("$n", name), ("$t", now));
            return (Outcome.Restored, new Account(guestId, name));
        });
    }

    public Account? Find(string guestId) => _db.Run(c =>
    {
        string? name = QueryScalar(c, "SELECT name FROM accounts WHERE guest_id=$g", ("$g", guestId));
        return name is null ? null : new Account(guestId, name);
    });

    // ---- helpers ----

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    internal static void Exec(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    internal static string? QueryScalar(SqliteConnection c, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar() as string;
    }
}
