using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HoldTheLine.Server.Data;

/// <summary>
/// Daily online backup of the SQLite database (docs/12 B2). Uses SQLite's live backup API so it never
/// stops the server or locks writers. Writes <c>{BackupDir}/holdtheline-{yyyyMMdd}.db</c> and keeps the
/// newest <see cref="KeepCount"/>. Only registered when a real (file) db and a BackupDir are configured.
/// </summary>
public sealed class BackupService(Db db, ServerOptions opts, ILogger<BackupService> logger) : BackgroundService
{
    private const int KeepCount = 14;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string dir = opts.BackupDir!;
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string path = Backup(db, dir);
                logger.LogInformation("Wrote database backup {Path}", path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Database backup failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Back up the live db to <c>{dir}/holdtheline-{yyyyMMdd}.db</c> (online, no downtime), then
    /// prune to the newest <see cref="KeepCount"/>. Returns the backup path. Internal so tests drive it directly.</summary>
    internal static string Backup(Db db, string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"holdtheline-{DateTimeOffset.UtcNow:yyyyMMdd}.db");
        db.Run(source =>
        {
            using var dest = new SqliteConnection($"Data Source={path};Pooling=False");
            dest.Open();
            source.BackupDatabase(dest); // SQLite Online Backup API — snapshots a consistent copy while live
        });
        Prune(dir, KeepCount);
        return path;
    }

    /// <summary>Keep the newest <paramref name="keep"/> daily backups (filenames sort lexically = by date),
    /// delete the rest. Internal so the retention rule is unit-testable without waiting a day.</summary>
    internal static void Prune(string dir, int keep = KeepCount)
    {
        if (!Directory.Exists(dir))
            return;
        var byNewest = Directory.GetFiles(dir, "holdtheline-*.db")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();
        foreach (var stale in byNewest.Skip(keep))
            try { File.Delete(stale); } catch (IOException) { /* best-effort; retried next run */ }
    }
}
