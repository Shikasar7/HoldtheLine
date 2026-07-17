using Microsoft.Data.Sqlite;

namespace HoldTheLine.Server.Data;

/// <summary>
/// The server's SQLite handle (M3 plan §3.1). One long-lived connection, and every access serialized
/// through a lock — SQLite is a single-writer store and Beta scale (≤ tens of players, turn-based) is
/// nowhere near needing more. The repository seam (this + the per-table stores) is deliberately thin so
/// a later Postgres swap doesn't touch business code.
///
/// <para>A null / empty path opens a private in-memory database that lives exactly as long as this
/// connection — isolated per <see cref="Db"/> instance, which is what tests want. A real path opens a
/// file (creating its directory), in WAL mode for durability.</para>
/// </summary>
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public Db(string? path)
    {
        bool inMemory = string.IsNullOrWhiteSpace(path) || path == ":memory:";
        if (!inMemory)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path!));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        // Pooling=False: we hold a single long-lived connection, so pooling buys nothing and would keep
        // the file handle open past Dispose (blocking file deletion / clean shutdown).
        _conn = new SqliteConnection(inMemory ? "Data Source=:memory:" : $"Data Source={path};Pooling=False");
        _conn.Open();
        Exec("PRAGMA foreign_keys=ON;");
        if (!inMemory)
            Exec("PRAGMA journal_mode=WAL;");
    }

    /// <summary>Run a unit of work against the connection under the single-writer lock.</summary>
    public T Run<T>(Func<SqliteConnection, T> op)
    {
        lock (_gate)
            return op(_conn);
    }

    public void Run(Action<SqliteConnection> op)
    {
        lock (_gate)
            op(_conn);
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
