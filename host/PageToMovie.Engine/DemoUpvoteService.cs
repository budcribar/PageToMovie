using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Per-user demo upvotes (stars) in pagetomovie.db — one row per (demo_id, user_id).
/// Gallery ranks by count; no downvotes / 1–5 scale.
/// </summary>
public sealed class DemoUpvoteService
{
    private readonly string _dbPath;
    private readonly ILogger<DemoUpvoteService> _log;
    private readonly object _initLock = new();
    private bool _ready;

    public DemoUpvoteService(
        IOptions<PageToMovieOptions> options,
        ILogger<DemoUpvoteService> log)
    {
        _log = log;
        var dataDir = UserDatabaseService.ResolveDataDirectory(options.Value.WorkspaceRoot);
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "pagetomovie.db");
        EnsureReady();
    }

    private string ConnectionString => $"Data Source={_dbPath};Cache=Shared;";

    private void EnsureReady()
    {
        if (_ready) return;
        lock (_initLock)
        {
            if (_ready) return;
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS demo_upvotes (
                    demo_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (demo_id, user_id)
                );
                CREATE INDEX IF NOT EXISTS idx_demo_upvotes_demo ON demo_upvotes(demo_id);
            ";
            cmd.ExecuteNonQuery();
            _ready = true;
            _log.LogDebug("demo_upvotes table ready at {Path}", _dbPath);
        }
    }

    private const string InsertUpvoteSql = @"
            INSERT OR IGNORE INTO demo_upvotes (demo_id, user_id, created_at)
            VALUES ($d, $u, $t);";

    /// <summary>Trim, drop blanks, and de-duplicate a batch of demo ids (case-insensitive).</summary>
    private static List<string> NormalizeDistinctIds(IEnumerable<string> demoIds) =>
        demoIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Bind the id slice as a JSON array parameter for <c>json_each($ids)</c>.</summary>
    private static void BindJsonIdList(SqliteCommand cmd, List<string> slice) =>
        cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(slice));

    /// <summary>Idempotent add. Returns true if a new row was inserted.</summary>
    public async Task<bool> TryAddAsync(string demoId, string userId, CancellationToken ct = default)
    {
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        userId = (userId ?? "").Trim();
        if (demoId.Length == 0 || userId.Length == 0)
            return false;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = InsertUpvoteSql;
        cmd.Parameters.AddWithValue("$d", demoId);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public bool TryAdd(string demoId, string userId)
    {
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        userId = (userId ?? "").Trim();
        if (demoId.Length == 0 || userId.Length == 0)
            return false;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = InsertUpvoteSql;
        cmd.Parameters.AddWithValue("$d", demoId);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Remove upvote. Returns true if a row was deleted.</summary>
    public async Task<bool> TryRemoveAsync(string demoId, string userId, CancellationToken ct = default)
    {
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        userId = (userId ?? "").Trim();
        if (demoId.Length == 0 || userId.Length == 0)
            return false;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM demo_upvotes WHERE demo_id = $d AND user_id = $u;";
        cmd.Parameters.AddWithValue("$d", demoId);
        cmd.Parameters.AddWithValue("$u", userId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public bool TryRemove(string demoId, string userId)
    {
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        userId = (userId ?? "").Trim();
        if (demoId.Length == 0 || userId.Length == 0)
            return false;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM demo_upvotes WHERE demo_id = $d AND user_id = $u;";
        cmd.Parameters.AddWithValue("$d", demoId);
        cmd.Parameters.AddWithValue("$u", userId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public async Task<int> GetCountAsync(string demoId, CancellationToken ct = default)
    {
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        if (demoId.Length == 0) return 0;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM demo_upvotes WHERE demo_id = $d;";
        cmd.Parameters.AddWithValue("$d", demoId);
        var o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return o is long l ? (int)l : Convert.ToInt32(o ?? 0);
    }

    public int GetCount(string demoId)
    {
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        if (demoId.Length == 0) return 0;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM demo_upvotes WHERE demo_id = $d;";
        cmd.Parameters.AddWithValue("$d", demoId);
        var o = cmd.ExecuteScalar();
        return o is long l ? (int)l : Convert.ToInt32(o ?? 0);
    }

    public async Task<bool> HasUpvotedAsync(string demoId, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        userId = userId.Trim();
        if (demoId.Length == 0) return false;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM demo_upvotes WHERE demo_id = $d AND user_id = $u LIMIT 1;";
        cmd.Parameters.AddWithValue("$d", demoId);
        cmd.Parameters.AddWithValue("$u", userId);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    public bool HasUpvoted(string demoId, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        EnsureReady();
        demoId = (demoId ?? "").Trim();
        userId = userId.Trim();
        if (demoId.Length == 0) return false;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM demo_upvotes WHERE demo_id = $d AND user_id = $u LIMIT 1;";
        cmd.Parameters.AddWithValue("$d", demoId);
        cmd.Parameters.AddWithValue("$u", userId);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Counts for many demos (missing ids → 0).</summary>
    public async Task<Dictionary<string, int>> GetCountsAsync(IEnumerable<string> demoIds, CancellationToken ct = default)
    {
        EnsureReady();
        var ids = NormalizeDistinctIds(demoIds);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
            map[id] = 0;
        if (ids.Count == 0) return map;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        const int chunk = 80;
        for (var i = 0; i < ids.Count; i += chunk)
        {
            var slice = ids.Skip(i).Take(chunk).ToList();
            using var cmd = conn.CreateCommand();
            BindJsonIdList(cmd, slice);
            cmd.CommandText =
                "SELECT demo_id, COUNT(*) FROM demo_upvotes WHERE demo_id IN (SELECT value FROM json_each($ids)) GROUP BY demo_id;";
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = r.GetString(0);
                var n = r.GetInt32(1);
                map[id] = n;
            }
        }
        return map;
    }

    public Dictionary<string, int> GetCounts(IEnumerable<string> demoIds)
    {
        EnsureReady();
        var ids = NormalizeDistinctIds(demoIds);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
            map[id] = 0;
        if (ids.Count == 0) return map;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        const int chunk = 80;
        for (var i = 0; i < ids.Count; i += chunk)
        {
            var slice = ids.Skip(i).Take(chunk).ToList();
            using var cmd = conn.CreateCommand();
            BindJsonIdList(cmd, slice);
            cmd.CommandText =
                "SELECT demo_id, COUNT(*) FROM demo_upvotes WHERE demo_id IN (SELECT value FROM json_each($ids)) GROUP BY demo_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var n = r.GetInt32(1);
                map[id] = n;
            }
        }
        return map;
    }

    /// <summary>Demo ids the user has upvoted (among the given set).</summary>
    public async Task<HashSet<string>> GetUpvotedSetAsync(string? userId, IEnumerable<string> demoIds, CancellationToken ct = default)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(userId)) return set;
        EnsureReady();
        var ids = NormalizeDistinctIds(demoIds);
        if (ids.Count == 0) return set;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        const int chunk = 80;
        for (var i = 0; i < ids.Count; i += chunk)
        {
            var slice = ids.Skip(i).Take(chunk).ToList();
            using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("$u", userId.Trim());
            BindJsonIdList(cmd, slice);
            cmd.CommandText =
                "SELECT demo_id FROM demo_upvotes WHERE user_id = $u AND demo_id IN (SELECT value FROM json_each($ids));";
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                set.Add(r.GetString(0));
        }
        return set;
    }

    public HashSet<string> GetUpvotedSet(string? userId, IEnumerable<string> demoIds)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(userId)) return set;
        EnsureReady();
        var ids = NormalizeDistinctIds(demoIds);
        if (ids.Count == 0) return set;

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        const int chunk = 80;
        for (var i = 0; i < ids.Count; i += chunk)
        {
            var slice = ids.Skip(i).Take(chunk).ToList();
            using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("$u", userId.Trim());
            BindJsonIdList(cmd, slice);
            cmd.CommandText =
                "SELECT demo_id FROM demo_upvotes WHERE user_id = $u AND demo_id IN (SELECT value FROM json_each($ids));";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                set.Add(r.GetString(0));
        }
        return set;
    }
}
