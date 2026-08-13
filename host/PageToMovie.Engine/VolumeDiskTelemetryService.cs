using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Telemetry service tracking Railway persistent volume (/data) disk capacity, free space, and daily storage usage trend.
/// </summary>
public sealed class VolumeDiskTelemetryService
{
    private readonly string _dataDir;
    private readonly string _dbPath;

    public VolumeDiskTelemetryService(IOptions<PageToMovieOptions> opts)
    {
        var root = opts.Value.WorkspaceRoot;
        _dataDir = UserDatabaseService.ResolveDataDirectory(root);
        Directory.CreateDirectory(_dataDir);
        _dbPath = Path.Combine(_dataDir, "pagetomovie.db");
        EnsureTableInitialized();
    }

    private void EnsureTableInitialized()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS volume_disk_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    snapshot_date TEXT NOT NULL UNIQUE,
                    recorded_at TEXT NOT NULL,
                    volume_path TEXT NOT NULL,
                    total_bytes INTEGER NOT NULL,
                    free_bytes INTEGER NOT NULL,
                    used_bytes INTEGER NOT NULL,
                    used_percent REAL NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_volume_disk_date ON volume_disk_snapshots(snapshot_date);
            ";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VolumeDiskTelemetry] Table init failed: {ex.Message}");
        }
    }

    public VolumeDiskStatusDto GetDiskStatus()
    {
        try
        {
            var drive = GetDriveInfoForPath(_dataDir);
            if (drive is not null && drive.IsReady && drive.TotalSize > 0)
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = total - free;
                var pct = (double)used / total * 100.0;

                return new VolumeDiskStatusDto
                {
                    VolumePath = drive.Name,
                    TotalBytes = total,
                    FreeBytes = free,
                    UsedBytes = used,
                    UsedPercent = Math.Round(pct, 1),
                    FormattedTotal = FormatBytes(total),
                    FormattedFree = FormatBytes(free),
                    FormattedUsed = FormatBytes(used),
                    IsAvailable = true
                };
            }
        }
        catch (Exception ex)
        {
            return new VolumeDiskStatusDto
            {
                VolumePath = _dataDir,
                Error = ex.Message,
                IsAvailable = false
            };
        }

        return new VolumeDiskStatusDto
        {
            VolumePath = _dataDir,
            IsAvailable = false
        };
    }

    public void RecordDailySnapshotIfNeeded()
    {
        var status = GetDiskStatus();
        if (!status.IsAvailable) return;

        try
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO volume_disk_snapshots
                (snapshot_date, recorded_at, volume_path, total_bytes, free_bytes, used_bytes, used_percent)
                VALUES (@date, @at, @path, @total, @free, @used, @pct)
                ON CONFLICT(snapshot_date) DO UPDATE SET
                    recorded_at = excluded.recorded_at,
                    volume_path = excluded.volume_path,
                    total_bytes = excluded.total_bytes,
                    free_bytes = excluded.free_bytes,
                    used_bytes = excluded.used_bytes,
                    used_percent = excluded.used_percent;
            ";
            cmd.Parameters.AddWithValue("@date", today);
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@path", status.VolumePath ?? _dataDir);
            cmd.Parameters.AddWithValue("@total", status.TotalBytes);
            cmd.Parameters.AddWithValue("@free", status.FreeBytes);
            cmd.Parameters.AddWithValue("@used", status.UsedBytes);
            cmd.Parameters.AddWithValue("@pct", status.UsedPercent);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VolumeDiskTelemetry] Snapshot record failed: {ex.Message}");
        }
    }

    public List<VolumeDiskSnapshotDto> GetDiskHistory(int days = 30)
    {
        var list = new List<VolumeDiskSnapshotDto>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT snapshot_date, recorded_at, volume_path, total_bytes, free_bytes, used_bytes, used_percent
                FROM volume_disk_snapshots
                ORDER BY snapshot_date ASC
                LIMIT @days;
            ";
            cmd.Parameters.AddWithValue("@days", days);

            using var r = cmd.ExecuteReader();
            long? prevUsed = null;

            while (r.Read())
            {
                var sDate = r.GetString(0);
                var recAtStr = r.GetString(1);
                var vPath = r.GetString(2);
                var total = r.GetInt64(3);
                var free = r.GetInt64(4);
                var used = r.GetInt64(5);
                var pct = r.GetDouble(6);

                DateTimeOffset.TryParse(recAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var recAt);

                long? delta = null;
                string? formattedDelta = null;

                if (prevUsed.HasValue)
                {
                    delta = used - prevUsed.Value;
                    var sign = delta.Value >= 0 ? "+" : "";
                    formattedDelta = $"{sign}{FormatBytes(delta.Value)}";
                }
                prevUsed = used;

                list.Add(new VolumeDiskSnapshotDto
                {
                    SnapshotDate = sDate,
                    RecordedAt = recAt,
                    VolumePath = vPath,
                    TotalBytes = total,
                    FreeBytes = free,
                    UsedBytes = used,
                    UsedPercent = Math.Round(pct, 1),
                    FormattedUsed = FormatBytes(used),
                    FormattedFree = FormatBytes(free),
                    FormattedTotal = FormatBytes(total),
                    DailyChangeBytes = delta,
                    FormattedDailyChange = formattedDelta
                });
            }

            list.Reverse();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VolumeDiskTelemetry] History load failed: {ex.Message}");
        }

        return list;
    }

    private static DriveInfo? GetDriveInfoForPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drives = DriveInfo.GetDrives();

            DriveInfo? bestMatch = null;
            int bestLength = -1;

            foreach (var d in drives)
            {
                try
                {
                    if (d.IsReady && full.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase) && d.Name.Length > bestLength)
                    {
                        bestMatch = d;
                        bestLength = d.Name.Length;
                    }
                }
                catch { /* skip */ }
            }

            return bestMatch ?? new DriveInfo(Path.GetPathRoot(full) ?? full);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        var abs = Math.Abs(bytes);
        if (abs >= 1024L * 1024L * 1024L)
            return $"{(double)bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (abs >= 1024L * 1024L)
            return $"{(double)bytes / (1024.0 * 1024.0):F1} MB";
        return $"{(double)bytes / 1024.0:F0} KB";
    }
}
