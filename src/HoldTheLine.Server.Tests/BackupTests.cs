using HoldTheLine.Server.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HoldTheLine.Server.Tests;

/// <summary>docs/12 B2.1: the daily online backup produces a queryable copy, and retention keeps 14.</summary>
public class BackupTests
{
    [Fact]
    public void Backup_writes_a_copy_that_can_be_queried()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"htl-bak-{Guid.NewGuid():N}");
        string dbPath = Path.Combine(dir, "src.db");
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new Db(dbPath);
            new AccountStore(db).RegisterOrRestore("gA", "secretA", "Alice"); // a row to look for in the backup

            string backupPath = BackupService.Backup(db, dir);
            Assert.True(File.Exists(backupPath));

            using var conn = new SqliteConnection($"Data Source={backupPath};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM accounts WHERE guest_id='gA'";
            Assert.Equal("Alice", cmd.ExecuteScalar() as string);
        }
        finally { TryDeleteDir(dir); }
    }

    [Fact]
    public void Prune_keeps_the_newest_fourteen()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"htl-bak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            for (int day = 1; day <= 16; day++)
                File.WriteAllText(Path.Combine(dir, $"holdtheline-202601{day:D2}.db"), "x");

            BackupService.Prune(dir);

            var remaining = Directory.GetFiles(dir, "holdtheline-*.db").Select(Path.GetFileName).ToList();
            Assert.Equal(14, remaining.Count);
            Assert.DoesNotContain("holdtheline-20260101.db", remaining); // two oldest pruned
            Assert.DoesNotContain("holdtheline-20260102.db", remaining);
            Assert.Contains("holdtheline-20260116.db", remaining);       // newest kept
        }
        finally { TryDeleteDir(dir); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
