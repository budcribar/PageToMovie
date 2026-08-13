using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

public sealed record TimingTelemetryRecord(
    string Id,
    string ProjectId,
    int SceneNumber,
    string VideoModelId,
    string VideoModelVersion,
    string EvaluatorModelId,
    string EvaluatorModelVersion,
    string CameraCategory,
    string ActionCategory,
    int WordCount,
    double EstimatedDurationSec,
    double ClipDurationSec,
    double MeasuredCamOverheadSec,
    double MeasuredActionOverheadSec,
    bool DialogueTruncated,
    string CreatedAt);

public sealed record TimingCacheStats(
    int TotalHits,
    int TotalMisses,
    double HitRatePercent,
    double MeanAbsoluteErrorSec);

public sealed record TimingTrendPoint(
    string Timestamp,
    int Hits,
    int Misses,
    double HitRatePercent,
    double MeanAbsoluteErrorSec);

/// <summary>
/// Persistent SQLite Repository for Action Timing Telemetry, Cache Hits/Misses, and Trend Snapshots.
/// Database location: /data/pagetomovie.db
/// </summary>
public sealed class ClipTimingTelemetryRepository
{
    private const string CreatedAtParam = "$created_at";
    private readonly string _dbPath;
    private readonly ILogger<ClipTimingTelemetryRepository>? _log;

    public ClipTimingTelemetryRepository(string dbPath, ILogger<ClipTimingTelemetryRepository>? log = null)
    {
        _dbPath = dbPath;
        _log = log;
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS clip_timing_telemetry (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    scene_number INTEGER NOT NULL,
                    video_model_id TEXT NOT NULL,
                    video_model_version TEXT NOT NULL,
                    evaluator_model_id TEXT NOT NULL,
                    evaluator_model_version TEXT NOT NULL,
                    camera_category TEXT,
                    action_category TEXT,
                    word_count INTEGER NOT NULL,
                    estimated_duration_sec REAL NOT NULL DEFAULT 0.0,
                    clip_duration_sec REAL NOT NULL,
                    measured_cam_overhead_sec REAL NOT NULL,
                    measured_action_overhead_sec REAL NOT NULL,
                    dialogue_truncated INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS timing_cache_metrics (
                    id TEXT PRIMARY KEY,
                    is_hit INTEGER NOT NULL,
                    lookup_key TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS timing_telemetry_snapshots (
                    id TEXT PRIMARY KEY,
                    snapshot_timestamp TEXT NOT NULL,
                    hits INTEGER NOT NULL,
                    misses INTEGER NOT NULL,
                    hit_rate_percent REAL NOT NULL,
                    mean_absolute_error_sec REAL NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();

            // Migration: Add estimated_duration_sec column if missing on existing databases
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE clip_timing_telemetry ADD COLUMN estimated_duration_sec REAL NOT NULL DEFAULT 0.0;";
            try { alterCmd.ExecuteNonQuery(); } catch { /* column already exists */ }
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to initialize clip_timing_telemetry SQLite schema at {DbPath}", _dbPath);
        }
    }

    /// <summary>
    /// Share of spoken clips marked dialogue-truncated (proxy for QA fail / regen need).
    /// Returns null when sample size is below <paramref name="minSamples"/>.
    /// </summary>
    public async Task<(double FailRate, int Samples)?> GetDialogueFailRateAsync(
        int minSamples = 5,
        string? projectId = null,
        CancellationToken ct = default)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(dialogue_truncated), 0)
                FROM clip_timing_telemetry
                WHERE word_count > 0
                  AND (@projectId = '' OR project_id = @projectId)
                """;
            cmd.Parameters.AddWithValue("@projectId",
                string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim());
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return null;
            var n = r.GetInt32(0);
            if (n < minSamples) return null;
            var fails = r.GetInt32(1);
            return (fails / (double)n, n);
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "GetDialogueFailRateAsync failed");
            return null;
        }
    }

    public async Task RecordTelemetryAsync(TimingTelemetryRecord record)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clip_timing_telemetry (
                    id, project_id, scene_number, video_model_id, video_model_version,
                    evaluator_model_id, evaluator_model_version, camera_category, action_category,
                    word_count, estimated_duration_sec, clip_duration_sec, measured_cam_overhead_sec, measured_action_overhead_sec,
                    dialogue_truncated, created_at
                ) VALUES (
                    $id, $project_id, $scene_number, $video_model_id, $video_model_version,
                    $evaluator_model_id, $evaluator_model_version, $camera_category, $action_category,
                    $word_count, $estimated_duration_sec, $clip_duration_sec, $measured_cam_overhead_sec, $measured_action_overhead_sec,
                    $dialogue_truncated, $created_at
                );
                """;

            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$project_id", record.ProjectId);
            cmd.Parameters.AddWithValue("$scene_number", record.SceneNumber);
            cmd.Parameters.AddWithValue("$video_model_id", record.VideoModelId);
            cmd.Parameters.AddWithValue("$video_model_version", record.VideoModelVersion);
            cmd.Parameters.AddWithValue("$evaluator_model_id", record.EvaluatorModelId);
            cmd.Parameters.AddWithValue("$evaluator_model_version", record.EvaluatorModelVersion);
            cmd.Parameters.AddWithValue("$camera_category", (object?)record.CameraCategory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$action_category", (object?)record.ActionCategory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$word_count", record.WordCount);
            cmd.Parameters.AddWithValue("$estimated_duration_sec", record.EstimatedDurationSec);
            cmd.Parameters.AddWithValue("$clip_duration_sec", record.ClipDurationSec);
            cmd.Parameters.AddWithValue("$measured_cam_overhead_sec", record.MeasuredCamOverheadSec);
            cmd.Parameters.AddWithValue("$measured_action_overhead_sec", record.MeasuredActionOverheadSec);
            cmd.Parameters.AddWithValue("$dialogue_truncated", record.DialogueTruncated ? 1 : 0);
            cmd.Parameters.AddWithValue(CreatedAtParam, record.CreatedAt);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to insert clip_timing_telemetry record {Id}", record.Id);
        }
    }

    public async Task RecordCacheLookupAsync(bool isHit, string lookupKey)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO timing_cache_metrics (id, is_hit, lookup_key, created_at)
                VALUES ($id, $is_hit, $lookup_key, $created_at);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$is_hit", isHit ? 1 : 0);
            cmd.Parameters.AddWithValue("$lookup_key", lookupKey);
            cmd.Parameters.AddWithValue(CreatedAtParam, DateTime.UtcNow.ToString("o"));

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to log cache lookup for key {Key}", lookupKey);
        }
    }

    public async Task<TimingCacheStats> GetCacheTelemetryStatsAsync()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync().ConfigureAwait(false);

            int hits = 0;
            int misses = 0;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT is_hit, COUNT(*) FROM timing_cache_metrics GROUP BY is_hit;";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    int isHit = reader.GetInt32(0);
                    int count = reader.GetInt32(1);
                    if (isHit == 1) hits = count;
                    else misses = count;
                }
            }

            double maeSec = 0.0;
            using (var maeCmd = conn.CreateCommand())
            {
                maeCmd.CommandText = """
                    SELECT AVG(ABS(
                        CASE 
                            WHEN estimated_duration_sec > 0 THEN estimated_duration_sec 
                            ELSE (measured_cam_overhead_sec + measured_action_overhead_sec + (word_count / 2.6)) 
                        END - clip_duration_sec
                    )) FROM clip_timing_telemetry;
                    """;
                var result = await maeCmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (result != DBNull.Value && result != null && double.TryParse(result.ToString(), out var parsedMae))
                {
                    maeSec = Math.Round(parsedMae, 2);
                }
            }

            int total = hits + misses;
            double hitRate = total > 0 ? Math.Round((double)hits / total * 100.0, 1) : 0.0;

            return new TimingCacheStats(
                TotalHits: hits,
                TotalMisses: misses,
                HitRatePercent: hitRate,
                MeanAbsoluteErrorSec: maeSec);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to query cache telemetry stats from {DbPath}", _dbPath);
            return new TimingCacheStats(0, 0, 0.0, 0.0);
        }
    }

    public async Task<List<TimingTrendPoint>> GetTrendHistoryAsync(int maxPoints = 30)
    {
        var list = new List<TimingTrendPoint>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT snapshot_timestamp, hits, misses, hit_rate_percent, mean_absolute_error_sec
                FROM timing_telemetry_snapshots
                ORDER BY snapshot_timestamp DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", maxPoints);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new TimingTrendPoint(
                    Timestamp: reader.GetString(0),
                    Hits: reader.GetInt32(1),
                    Misses: reader.GetInt32(2),
                    HitRatePercent: reader.GetDouble(3),
                    MeanAbsoluteErrorSec: reader.GetDouble(4)));
            }

            list.Reverse();

            // Fallback: If no explicit snapshots exist yet, aggregate live clip_timing_telemetry records by day
            if (list.Count == 0)
            {
                using var groupCmd = conn.CreateCommand();
                groupCmd.CommandText = """
                    SELECT 
                        SUBSTR(created_at, 1, 10) as day,
                        COUNT(*) as total_count,
                        AVG(ABS(
                            CASE 
                                WHEN estimated_duration_sec > 0 THEN estimated_duration_sec 
                                ELSE (measured_cam_overhead_sec + measured_action_overhead_sec + (word_count / 2.6)) 
                            END - clip_duration_sec
                        )) as avg_mae
                    FROM clip_timing_telemetry
                    GROUP BY SUBSTR(created_at, 1, 10)
                    ORDER BY day ASC
                    LIMIT $limit;
                    """;
                groupCmd.Parameters.AddWithValue("$limit", maxPoints);

                using var gReader = await groupCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await gReader.ReadAsync().ConfigureAwait(false))
                {
                    var dayStr = gReader.GetString(0);
                    var cnt = gReader.GetInt32(1);
                    var mae = gReader.IsDBNull(2) ? 0.0 : Math.Round(gReader.GetDouble(2), 2);

                    list.Add(new TimingTrendPoint(
                        Timestamp: dayStr,
                        Hits: cnt,
                        Misses: 0,
                        HitRatePercent: 100.0,
                        MeanAbsoluteErrorSec: mae));
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to query trend history from {DbPath}", _dbPath);
        }

        return list;
    }

    public async Task<int> SeedInitialBenchmarksAsync(IEnumerable<(string Id, string Category, string Prompt, double EstimatedSec, string Mode, double Gamma)> entries)
    {
        int count = 0;
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync().ConfigureAwait(false);

            foreach (var e in entries)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO clip_timing_telemetry (
                        id, project_id, scene_number, video_model_id, video_model_version,
                        evaluator_model_id, evaluator_model_version, camera_category, action_category,
                        word_count, clip_duration_sec, measured_cam_overhead_sec, measured_action_overhead_sec,
                        dialogue_truncated, created_at
                    ) VALUES (
                        $id, 'benchmark_seed', 0, 'fal-ai/hunyuan-video', 'v1.0',
                        'google/gemini-2.5-flash', 'v1.0', $camera, $action,
                        12, $duration, 1.6, $action_overhead, 0, $created_at
                    );
                    """;
                cmd.Parameters.AddWithValue("$id", e.Id);
                cmd.Parameters.AddWithValue("$camera", e.Category.Contains("Camera") ? e.Id : "cam_push_in");
                cmd.Parameters.AddWithValue("$action", e.Id);
                cmd.Parameters.AddWithValue("$duration", e.EstimatedSec);
                cmd.Parameters.AddWithValue("$action_overhead", e.EstimatedSec);
                cmd.Parameters.AddWithValue(CreatedAtParam, DateTime.UtcNow.ToString("o"));

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Record cache hit entry
                using var hitCmd = conn.CreateCommand();
                hitCmd.CommandText = """
                    INSERT INTO timing_cache_metrics (id, is_hit, lookup_key, created_at)
                    VALUES ($id, 1, $key, $created_at);
                    """;
                hitCmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                hitCmd.Parameters.AddWithValue("$key", $"{e.Category}:{e.Id}:{e.Mode}");
                hitCmd.Parameters.AddWithValue(CreatedAtParam, DateTime.UtcNow.ToString("o"));
                await hitCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                count++;
            }

            _log?.LogInformation("[ClipTimingTelemetry] Seeded {Count} initial empirical benchmark records into SQLite database", count);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to seed benchmark records into SQLite database at {DbPath}", _dbPath);
        }

        return count;
    }
}
