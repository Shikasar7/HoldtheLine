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

        // docs/12 B1: username accounts layered onto the guest identity. Additive migration so an existing
        // B0 database upgrades in place — each ALTER is skipped if the column already exists.
        foreach (var column in new[] { "username TEXT", "username_lower TEXT", "password_hash TEXT" })
        {
            try { _db.Run(c => Exec(c, $"ALTER TABLE accounts ADD COLUMN {column}")); }
            catch (SqliteException) { /* column already present */ }
        }
        _db.Run(c => Exec(c, """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_username
            ON accounts(username_lower) WHERE username_lower IS NOT NULL;
            """));
    }

    public enum Outcome { Registered, Restored, BadSecret }

    public enum AuthOutcome { Ok, NameTaken, BadCredentials, NotIdentified, AlreadyBound }

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

    /// <summary>register (docs/12 B1): bind a username+password to an already-identified guest. The
    /// username_lower unique index rejects a collision as <see cref="AuthOutcome.NameTaken"/>; a guest that
    /// already carries a username is <see cref="AuthOutcome.AlreadyBound"/>; a missing row (anonymous
    /// connection) is <see cref="AuthOutcome.NotIdentified"/>.</summary>
    public AuthOutcome BindCredentials(string guestId, string username, string password) => _db.Run(c =>
    {
        string? row = QueryScalar(c, "SELECT guest_id FROM accounts WHERE guest_id=$g", ("$g", guestId));
        if (row is null)
            return AuthOutcome.NotIdentified;
        string? existing = QueryScalar(c, "SELECT username FROM accounts WHERE guest_id=$g AND username IS NOT NULL", ("$g", guestId));
        if (existing is not null)
            return AuthOutcome.AlreadyBound;

        try
        {
            Exec(c, "UPDATE accounts SET username=$u, username_lower=$ul, password_hash=$ph WHERE guest_id=$g",
                ("$u", username), ("$ul", username.ToLowerInvariant()), ("$ph", PasswordHash.Hash(password)), ("$g", guestId));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT: username_lower already taken
        {
            return AuthOutcome.NameTaken;
        }
        return AuthOutcome.Ok;
    });

    /// <summary>login (docs/12 B1): verify username+password, then rotate the account's secret (single active
    /// device — the old identity.json stops verifying). Returns the account's guest_id and the NEW secret on
    /// success; a missing user or wrong password is <see cref="AuthOutcome.BadCredentials"/> (indistinguishable
    /// on purpose, to defeat username enumeration).</summary>
    public (AuthOutcome Outcome, string? GuestId, string? NewSecret) Login(string username, string password) => _db.Run(c =>
    {
        string? guestId = null, pwHash = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT guest_id, password_hash FROM accounts WHERE username_lower=$ul";
            cmd.Parameters.AddWithValue("$ul", username.ToLowerInvariant());
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                guestId = reader.GetString(0);
                pwHash = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        if (guestId is null || pwHash is null || !PasswordHash.Verify(password, pwHash))
            return (AuthOutcome.BadCredentials, (string?)null, (string?)null);

        string newSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Exec(c, "UPDATE accounts SET secret_hash=$sh, last_seen=$t WHERE guest_id=$g",
            ("$sh", HashSecret(newSecret)), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$g", guestId));
        return (AuthOutcome.Ok, guestId, newSecret);
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
