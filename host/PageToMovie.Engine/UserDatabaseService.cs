using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// SQLite user database service for PageToMovie (pagetomovie.db).
/// Manages user authentication, account settings, WAL mode concurrency pragmas,
/// and AES-256 encryption at rest for per-user provider API keys (xAI, Gemini, Anthropic).
/// </summary>
public class UserDatabaseService
{
    private const string ContainerDataDir = "/data";
    private const string AppContainerDataDir = "/app/data";
    private const string PragmaUserVersion = "PRAGMA user_version;";
    private const string DefaultOperatorUserId = "budcribar";
    private const string ProviderGemini = "gemini";
    private const string ProviderAnthropic = "anthropic";

    /// <summary>Repeated SQL table / parameter / type literals (S1192).</summary>
    private static class SqlLit
    {
        public const string Users = "users";
        public const string UserApiCalls = "user_api_calls";
        public const string Projects = "projects";
        public const string RealNotNullDefault0 = "REAL NOT NULL DEFAULT 0";
        public const string ParamCreated = "@created";
        public const string ParamName = "@name";
        public const string ParamOwner = "@owner";
        public const string ParamProject = "@project";
        public const string ParamEmail = "@email";
        public const string ParamAlias = "@alias";
        public const string ParamPrimary = "@primary";
        public const string ParamTake = "@take";
        public const string ParamUserId = "@userId";
        public const string ParamProjectId = "@projectId";
        public const string ParamScene = "@scene";
        public const string ParamClip = "@clip";
    }

    private readonly string _dbPath;
    private readonly IDataProtector? _protector;
    private readonly ILogger<UserDatabaseService> _logger;
    private readonly BillingOptions _billing;
    private readonly string? _workspaceRoot;
    private readonly object _initLock = new();
    private bool _initialized;

    public UserDatabaseService(
        IOptions<PageToMovieOptions> options,
        IDataProtectionProvider? dataProtection = null,
        ILogger<UserDatabaseService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UserDatabaseService>.Instance;
        _protector = dataProtection?.CreateProtector("PageToMovie.UserApiKeys");
        _billing = options?.Value?.Billing ?? new BillingOptions();
        _workspaceRoot = options?.Value?.WorkspaceRoot;

        var workspace = options?.Value?.WorkspaceRoot;
        var dataDir = ResolveDataDirectory(workspace);
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "pagetomovie.db");

        EnsureDatabaseInitialized();
    }

    /// <summary>
    /// Pick a durable data dir. Order:
    /// <list type="number">
    /// <item>Env <c>PageToMovie_USER_DB_DIR</c> / <c>PAGETOMOVIE_USER_DB_DIR</c></item>
    /// <item>Isolated <see cref="PageToMovieOptions.WorkspaceRoot"/> under the process temp path (unit tests)</item>
    /// <item>Container volume <c>/data</c> or <c>/app/data</c> (Railway)</item>
    /// <item>WorkspaceRoot/data, else temp PageToMovie/data</item>
    /// </list>
    /// </summary>
    public static string ResolveDataDirectory(string? workspace)
    {
        var envDir = Environment.GetEnvironmentVariable("PageToMovie_USER_DB_DIR")
                     ?? Environment.GetEnvironmentVariable("PAGETOMOVIE_USER_DB_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
            return envDir.Trim();

        // Unit tests pass a unique temp WorkspaceRoot — never share C:\data / /data with them.
        if (IsIsolatedTestWorkspace(workspace) && workspace is { } isolatedRoot)
            return Path.Combine(isolatedRoot.Trim(), "data");

        if (Directory.Exists(ContainerDataDir))
            return ContainerDataDir;
        if (Directory.Exists(AppContainerDataDir))
            return AppContainerDataDir;

        // Local Visual Studio / Windows: keep tokens & users outside the repo so Clean/Rebuild
        // never deletes OAuth (YouTube) or personal API keys. Workspace still holds projects.
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
            {
                var stable = Path.Combine(local, "PageToMovie", "data");
                Directory.CreateDirectory(stable);
                // One-time: copy legacy workspace/data DB if stable is empty.
                TryMigrateLegacyWorkspaceData(workspace, stable);
                return stable;
            }
        }
        catch { /* fall through */ }

        if (!string.IsNullOrWhiteSpace(workspace))
            return Path.Combine(workspace.Trim(), "data");

        return Path.Combine(Path.GetTempPath(), "PageToMovie", "data");
    }


    /// <summary>
    /// Copy <c>pagetomovie.db</c> from repo workspace/data into LocalAppData when the
    /// stable store has no DB yet (preserves YouTube OAuth after the path change).
    /// </summary>
    static void TryMigrateLegacyWorkspaceData(string? workspace, string stableDir)
    {
        try
        {
            var destDb = Path.Combine(stableDir, "pagetomovie.db");
            if (File.Exists(destDb))
                return;
            if (string.IsNullOrWhiteSpace(workspace))
                return;
            var srcDb = Path.Combine(workspace.Trim(), "data", "pagetomovie.db");
            if (!File.Exists(srcDb))
                return;
            File.Copy(srcDb, destDb, overwrite: false);
            foreach (var name in new[] { "pagetomovie.db-wal", "pagetomovie.db-shm" })
            {
                var s = Path.Combine(workspace.Trim(), "data", name);
                var d = Path.Combine(stableDir, name);
                if (File.Exists(s) && !File.Exists(d))
                    File.Copy(s, d, overwrite: false);
            }
        }
        catch { /* best effort */ }
    }

    private static bool IsIsolatedTestWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return false;
        try
        {
            var full = Path.GetFullPath(workspace.Trim());
            var temp = Path.GetFullPath(Path.GetTempPath());
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Path.GetFullPath can throw for malformed workspace roots; treat as not isolated.
            return false;
        }
    }

    private string ConnectionString => $"Data Source={_dbPath};Cache=Shared;Pooling=True;";

    /// <summary>
    /// Ensures SQLite database and users table exist with WAL mode pragmas enabled.
    /// </summary>
    public void EnsureDatabaseInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            try
            {
                using var conn = new SqliteConnection(ConnectionString);
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        PRAGMA journal_mode = WAL;
                        PRAGMA busy_timeout = 5000;
                        PRAGMA synchronous = NORMAL;
                        PRAGMA temp_store = MEMORY;
                        PRAGMA cache_size = -8000;
                    ";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
                        CREATE TABLE IF NOT EXISTS {SqlLit.Users} (
                            user_id TEXT PRIMARY KEY,
                            username TEXT NOT NULL UNIQUE,
                            password_hash TEXT NOT NULL,
                            encrypted_xai_api_key TEXT,
                            encrypted_gemini_api_key TEXT,
                            encrypted_anthropic_api_key TEXT,
                            encrypted_fal_api_key TEXT,
                            role TEXT NOT NULL DEFAULT 'User',
                            created_at TEXT NOT NULL,
                            last_login_at TEXT,
                            credits_balance_usd {SqlLit.RealNotNullDefault0},
                            credits_lifetime_granted_usd {SqlLit.RealNotNullDefault0},
                            credits_lifetime_used_usd {SqlLit.RealNotNullDefault0}
                        );
                    ";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS user_api_keys (
                            user_id TEXT NOT NULL,
                            provider_id TEXT NOT NULL,
                            encrypted_api_key TEXT NOT NULL,
                            updated_at TEXT NOT NULL,
                            PRIMARY KEY (user_id, provider_id)
                        );
                    ";
                    cmd.ExecuteNonQuery();
                }

                // Database Schema Migrations & Version Tracking (PRAGMA user_version)
                using (var vCmd = conn.CreateCommand())
                {
                    vCmd.CommandText = PragmaUserVersion;
                    var curVer = Convert.ToInt32(vCmd.ExecuteScalar() ?? 0);

                    // Migration v1 -> v2: Ensure provider key columns including Fal.ai
                    EnsureColumn(conn, SqlLit.Users, "encrypted_gemini_api_key", "TEXT");
                    EnsureColumn(conn, SqlLit.Users, "encrypted_anthropic_api_key", "TEXT");
                    EnsureColumn(conn, SqlLit.Users, "encrypted_fal_api_key", "TEXT");

                    if (curVer < 2)
                    {
                        using var setVer = conn.CreateCommand();
                        setVer.CommandText = "PRAGMA user_version = 2;";
                        setVer.ExecuteNonQuery();
                        _logger.LogInformation("Migrated SQLite schema to user_version 2 (added provider key columns)");
                    }

                    if (curVer < 3)
                    {
                        // Migration v2 -> v3: Auto-copy legacy column keys into unified user_api_keys table
                        using (var copyCmd = conn.CreateCommand())
                        {
                            copyCmd.CommandText = $@"
                                INSERT OR IGNORE INTO user_api_keys (user_id, provider_id, encrypted_api_key, updated_at)
                                SELECT user_id, 'grok', encrypted_xai_api_key, datetime('now') FROM {SqlLit.Users} WHERE encrypted_xai_api_key IS NOT NULL AND encrypted_xai_api_key != '';
                                
                                INSERT OR IGNORE INTO user_api_keys (user_id, provider_id, encrypted_api_key, updated_at)
                                SELECT user_id, 'gemini', encrypted_gemini_api_key, datetime('now') FROM {SqlLit.Users} WHERE encrypted_gemini_api_key IS NOT NULL AND encrypted_gemini_api_key != '';

                                INSERT OR IGNORE INTO user_api_keys (user_id, provider_id, encrypted_api_key, updated_at)
                                SELECT user_id, 'anthropic', encrypted_anthropic_api_key, datetime('now') FROM {SqlLit.Users} WHERE encrypted_anthropic_api_key IS NOT NULL AND encrypted_anthropic_api_key != '';

                                INSERT OR IGNORE INTO user_api_keys (user_id, provider_id, encrypted_api_key, updated_at)
                                SELECT user_id, 'fal', encrypted_fal_api_key, datetime('now') FROM {SqlLit.Users} WHERE encrypted_fal_api_key IS NOT NULL AND encrypted_fal_api_key != '';
                            ";
                            copyCmd.ExecuteNonQuery();
                        }

                        using var setVer3 = conn.CreateCommand();
                        setVer3.CommandText = "PRAGMA user_version = 3;";
                        setVer3.ExecuteNonQuery();
                        _logger.LogInformation("Migrated SQLite schema to user_version 3 (unified dynamic user_api_keys table)");
                    }

                    if (curVer < 4)
                    {
                        // Migration v3 -> v4: generation_errors table created unconditionally below
                        // (CREATE TABLE IF NOT EXISTS, same idempotent style as user_api_calls) —
                        // this block only advances the version marker + logs the migration event.
                        using var setVer4 = conn.CreateCommand();
                        setVer4.CommandText = "PRAGMA user_version = 4;";
                        setVer4.ExecuteNonQuery();
                        _logger.LogInformation("Migrated SQLite schema to user_version 4 (added generation_errors table)");
                    }
                }

                // User billing credits (list-rate USD; 1 credit = $0.01).
                EnsureColumn(conn, SqlLit.Users, "credits_balance_usd", SqlLit.RealNotNullDefault0);
                EnsureColumn(conn, SqlLit.Users, "credits_lifetime_granted_usd", SqlLit.RealNotNullDefault0);
                EnsureColumn(conn, SqlLit.Users, "credits_lifetime_used_usd", SqlLit.RealNotNullDefault0);

                // User Terms of Service acceptance tracking
                EnsureColumn(conn, SqlLit.Users, "terms_accepted_at", "TEXT");
                EnsureColumn(conn, SqlLit.Users, "terms_version", "TEXT");

                // Admin disable (soft ban) — blocks login / API without deleting ledger.
                EnsureColumn(conn, SqlLit.Users, "is_disabled", "INTEGER NOT NULL DEFAULT 0");

                // Forgot-password request marker (legacy admin path; email reset preferred).
                EnsureColumn(conn, SqlLit.Users, "password_reset_requested_at", "TEXT");
                EnsureColumn(conn, SqlLit.Users, "email", "TEXT");
                EnsureColumn(conn, SqlLit.Users, "email_confirmed_at", "TEXT");
                EnsureColumn(conn, SqlLit.Users, "active_project_id", "TEXT");

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
                        CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email
                        ON {SqlLit.Users}(email) WHERE email IS NOT NULL AND TRIM(email) != '';
                    ";
                    try { cmd.ExecuteNonQuery(); } catch { /* index may already exist */ }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS auth_tokens (
                            token_hash TEXT PRIMARY KEY,
                            user_id TEXT NOT NULL,
                            purpose TEXT NOT NULL,
                            expires_at TEXT NOT NULL,
                            created_at TEXT NOT NULL,
                            used_at TEXT
                        );
                        CREATE INDEX IF NOT EXISTS idx_auth_tokens_user ON auth_tokens(user_id);
                    ";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS credit_ledger (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            user_id TEXT NOT NULL,
                            ts TEXT NOT NULL,
                            kind TEXT NOT NULL,
                            amount_usd REAL NOT NULL,
                            balance_after_usd REAL NOT NULL,
                            project_id TEXT,
                            note TEXT,
                            meta_kind TEXT
                        );
                        CREATE INDEX IF NOT EXISTS idx_credit_ledger_user ON credit_ledger(user_id);
                        CREATE INDEX IF NOT EXISTS idx_credit_ledger_ts ON credit_ledger(ts);
                    ";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
                        CREATE TABLE IF NOT EXISTS {SqlLit.UserApiCalls} (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            user_id TEXT NOT NULL,
                            ts TEXT NOT NULL,
                            project_id TEXT,
                            job_id TEXT,
                            kind TEXT NOT NULL,
                            mode TEXT,
                            provider TEXT,
                            model TEXT,
                            endpoint TEXT,
                            http_status INTEGER,
                            ok INTEGER NOT NULL DEFAULT 1,
                            duration_ms INTEGER,
                            estimated_usd REAL,
                            currency TEXT NOT NULL DEFAULT 'USD',
                            scene INTEGER,
                            clip INTEGER,
                            char_key TEXT,
                            resolution TEXT,
                            duration_sec REAL,
                            input_tokens INTEGER,
                            output_tokens INTEGER,
                            prompt_chars INTEGER,
                            response_chars INTEGER,
                            request_id TEXT,
                            error TEXT,
                            purpose TEXT,
                            fakes INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE INDEX IF NOT EXISTS idx_user_api_calls_user_ts ON {SqlLit.UserApiCalls}(user_id, ts);
                        CREATE INDEX IF NOT EXISTS idx_user_api_calls_project ON {SqlLit.UserApiCalls}(project_id);
                        CREATE INDEX IF NOT EXISTS idx_user_api_calls_kind ON {SqlLit.UserApiCalls}(kind);
                    ";
                    cmd.ExecuteNonQuery();
                }

                // User-facing cost bucket (screenplay / characters / video / voice / music / other).
                EnsureColumn(conn, SqlLit.UserApiCalls, "category", "TEXT");
                // Customer charge (list × admin multiplier) — per-user actual charges.
                EnsureColumn(conn, SqlLit.UserApiCalls, "charge_usd", "REAL");
                EnsureColumn(conn, SqlLit.UserApiCalls, "charge_multiplier", "REAL");
                // Retry attempt number (ApiCallTelemetry.Attempt) — needed to derive "succeeded after retry"
                // in the AI-call analytics rollup (see GetAiCallRawDataAsync).
                EnsureColumn(conn, SqlLit.UserApiCalls, "attempt", "INTEGER");
                // Canonical outcome (AiCallOutcome, stored as its string name) — set once at write time
                // in ProjectTelemetryService.LogApiCallAsync; replaces read-time string-guessing.
                EnsureColumn(conn, SqlLit.UserApiCalls, "outcome", "TEXT");
                try
                {
                    using var idxCmd = conn.CreateCommand();
                    idxCmd.CommandText =
                        $"CREATE INDEX IF NOT EXISTS idx_user_api_calls_category ON {SqlLit.UserApiCalls}(category);";
                    idxCmd.ExecuteNonQuery();
                }
                catch { /* ignore */ }

                // generation_errors (v4): partial-coverage / structural-gate / transient-retry
                // events — a different concept from user_api_calls (which logs every call,
                // success or failure). See GenerationErrorLogger.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS generation_errors (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ts TEXT NOT NULL,
                            user_id TEXT,
                            project_id TEXT,
                            job_id TEXT,
                            scene INTEGER,
                            clip INTEGER,
                            stage TEXT NOT NULL,
                            provider TEXT,
                            model TEXT,
                            error_type TEXT NOT NULL,
                            error_message TEXT,
                            http_status INTEGER,
                            requested_count INTEGER,
                            returned_count INTEGER,
                            missing_ids_json TEXT,
                            attempt INTEGER NOT NULL DEFAULT 1,
                            resolved INTEGER NOT NULL DEFAULT 0,
                            request_summary TEXT,
                            response_summary TEXT
                        );
                        CREATE INDEX IF NOT EXISTS idx_generation_errors_project_ts ON generation_errors(project_id, ts);
                        CREATE INDEX IF NOT EXISTS idx_generation_errors_type_ts ON generation_errors(error_type, ts);
                    ";
                    cmd.ExecuteNonQuery();
                }

                // H1–H9: durable video take events for takes-per-clip learning (fail-open dual-write).
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS video_take_events (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            ts TEXT NOT NULL,
                            project_id TEXT NOT NULL,
                            user_id TEXT,
                            scene INTEGER NOT NULL,
                            clip INTEGER NOT NULL,
                            take_index INTEGER NOT NULL DEFAULT 1,
                            take_kind TEXT NOT NULL,
                            reason TEXT,
                            model TEXT,
                            resolution TEXT,
                            list_usd REAL,
                            duration_sec REAL,
                            key_mode TEXT,
                            stable_beat_id TEXT,
                            had_char_refs INTEGER NOT NULL DEFAULT 0,
                            had_loc_ref INTEGER NOT NULL DEFAULT 0,
                            minutes_since_prev REAL,
                            contribute INTEGER NOT NULL DEFAULT 1
                        );
                        CREATE INDEX IF NOT EXISTS idx_video_take_events_project
                            ON video_take_events(project_id, scene, clip);
                        CREATE INDEX IF NOT EXISTS idx_video_take_events_ts
                            ON video_take_events(ts);
                        CREATE INDEX IF NOT EXISTS idx_video_take_events_kind
                            ON video_take_events(take_kind);
                    ";
                    cmd.ExecuteNonQuery();
                }

                // v5: attribute orphaned cost rows (no user / no project) to Bud Cribar + development.
                // Also backfill charge_usd. Detects old DBs via PRAGMA user_version < 5 (Railway).
                using (var vCmd5 = conn.CreateCommand())
                {
                    vCmd5.CommandText = PragmaUserVersion;
                    var verAfterTables = Convert.ToInt32(vCmd5.ExecuteScalar() ?? 0);
                    if (verAfterTables < 5)
                    {
                        MigrateLegacyCostAttributionV5(conn, _billing);
                        using var setVer5 = conn.CreateCommand();
                        setVer5.CommandText = "PRAGMA user_version = 5;";
                        setVer5.ExecuteNonQuery();
                        _logger.LogInformation(
                            "Migrated SQLite schema to user_version 5 (legacy cost attribution → {User}/{Project})",
                            string.IsNullOrWhiteSpace(_billing.LegacyCostOwnerUserId)
                                ? DefaultOperatorUserId
                                : _billing.LegacyCostOwnerUserId.Trim(),
                            string.IsNullOrWhiteSpace(_billing.LegacyCostProjectId)
                                ? "development"
                                : _billing.LegacyCostProjectId.Trim());
                    }
                }

                // v6: one operator account — handle budcribar + email budcribar@msn.com.
                // Merges alias accounts (email-shaped usernames, msn.com folder ids) into primary,
                // reassigns all spend/estimates/credits, and rehomes project folders on disk.
                using (var vCmd6 = conn.CreateCommand())
                {
                    vCmd6.CommandText = PragmaUserVersion;
                    var ver6 = Convert.ToInt32(vCmd6.ExecuteScalar() ?? 0);
                    if (ver6 < 6)
                    {
                        MigrateCanonicalAccountV6(conn, _billing);
                        using var setVer6 = conn.CreateCommand();
                        setVer6.CommandText = "PRAGMA user_version = 6;";
                        setVer6.ExecuteNonQuery();
                        _logger.LogInformation(
                            "Migrated SQLite schema to user_version 6 (canonical account {User} / {Email})",
                            string.IsNullOrWhiteSpace(_billing.LegacyCostOwnerUserId)
                                ? DefaultOperatorUserId
                                : _billing.LegacyCostOwnerUserId.Trim(),
                            string.IsNullOrWhiteSpace(_billing.CanonicalAccountEmail)
                                ? "budcribar@msn.com"
                                : _billing.CanonicalAccountEmail.Trim());
                    }
                }

                // v7: video_take_events (CREATE IF NOT EXISTS above is idempotent).
                using (var vCmd7 = conn.CreateCommand())
                {
                    vCmd7.CommandText = PragmaUserVersion;
                    var ver7 = Convert.ToInt32(vCmd7.ExecuteScalar() ?? 0);
                    if (ver7 < 7)
                    {
                        using var setVer7 = conn.CreateCommand();
                        setVer7.CommandText = "PRAGMA user_version = 7;";
                        setVer7.ExecuteNonQuery();
                        _logger.LogInformation("Migrated SQLite schema to user_version 7 (video_take_events)");
                    }
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $@"
                        INSERT INTO {SqlLit.Users} (user_id, username, password_hash, role, created_at, email_confirmed_at)
                        VALUES ('admin', 'admin', @hash, 'Admin', {SqlLit.ParamCreated}, {SqlLit.ParamCreated})
                        ON CONFLICT(user_id) DO UPDATE SET
                            password_hash = @hash,
                            role = 'Admin',
                            email_confirmed_at = COALESCE({SqlLit.Users}.email_confirmed_at, {SqlLit.ParamCreated});
                    ";
                    cmd.Parameters.AddWithValue("@hash", HashPassword("admin"));
                    cmd.Parameters.AddWithValue(SqlLit.ParamCreated, DateTime.UtcNow.ToString("o"));
                    cmd.ExecuteNonQuery();
                }

                _initialized = true;
                _logger.LogInformation("SQLite database initialized at {DbPath} (WAL mode enabled)", _dbPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to initialize SQLite database at {_dbPath}.", ex);
            }
        }
    }

    /// <summary>
    /// One-shot migration for Railway / old DBs: rows with missing user and/or project
    /// are assigned to the legacy owner (Bud Cribar / <c>budcribar</c>) and project <c>development</c>.
    /// Legacy column charge_usd is no longer written; display multiplies estimated_usd at read time.
    /// </summary>
    private void MigrateLegacyCostAttributionV5(SqliteConnection conn, BillingOptions billing)
    {
        var ownerId = string.IsNullOrWhiteSpace(billing.LegacyCostOwnerUserId)
            ? DefaultOperatorUserId
            : billing.LegacyCostOwnerUserId.Trim();
        var ownerName = string.IsNullOrWhiteSpace(billing.LegacyCostOwnerUsername)
            ? "Bud Cribar"
            : billing.LegacyCostOwnerUsername.Trim();
        var projectId = string.IsNullOrWhiteSpace(billing.LegacyCostProjectId)
            ? "development"
            : billing.LegacyCostProjectId.Trim();
        var mult = PageToMovie.Core.Billing.ChargePricing.ClampMultiplier(billing.ChargeMultiplier);

        // Ensure owner user exists so spend summaries resolve a real account.
        using (var ensureUser = conn.CreateCommand())
        {
            ensureUser.CommandText = $@"
                INSERT INTO {SqlLit.Users} (user_id, username, password_hash, role, created_at, email_confirmed_at)
                VALUES (@id, {SqlLit.ParamName}, '', 'User', {SqlLit.ParamCreated}, {SqlLit.ParamCreated})
                ON CONFLICT(user_id) DO UPDATE SET
                    username = CASE
                        WHEN {SqlLit.Users}.username IS NULL OR TRIM({SqlLit.Users}.username) = '' OR LOWER({SqlLit.Users}.username) = LOWER({SqlLit.Users}.user_id)
                        THEN {SqlLit.ParamName}
                        ELSE {SqlLit.Users}.username
                    END;";
            ensureUser.Parameters.AddWithValue("@id", ownerId);
            ensureUser.Parameters.AddWithValue(SqlLit.ParamName, ownerName);
            ensureUser.Parameters.AddWithValue(SqlLit.ParamCreated, DateTime.UtcNow.ToString("o"));
            ensureUser.ExecuteNonQuery();
        }

        // Placeholders used before real BYOK attribution (empty, local default, unknown).
        // Does NOT reassign costs already owned by real signed-in users.
        const string orphanUserSql = @"
            user_id IS NULL
            OR TRIM(user_id) = ''
            OR LOWER(TRIM(user_id)) IN ('local', 'unknown', 'none', 'system', 'anonymous')";

        int apiUser = 0, apiProj = 0, apiCharge = 0, genUser = 0, genProj = 0, creditUser = 0, creditProj = 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                UPDATE {SqlLit.UserApiCalls}
                SET user_id = {SqlLit.ParamOwner}
                WHERE {orphanUserSql};";
            cmd.Parameters.AddWithValue(SqlLit.ParamOwner, ownerId);
            apiUser = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                UPDATE {SqlLit.UserApiCalls}
                SET project_id = {SqlLit.ParamProject}
                WHERE project_id IS NULL OR TRIM(project_id) = '';";
            cmd.Parameters.AddWithValue(SqlLit.ParamProject, projectId);
            apiProj = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                UPDATE {SqlLit.UserApiCalls}
                SET charge_usd = ROUND(estimated_usd * @mult, 6),
                    charge_multiplier = @mult
                WHERE estimated_usd IS NOT NULL
                  AND estimated_usd > 0
                  AND (charge_usd IS NULL OR charge_usd <= 0);";
            cmd.Parameters.AddWithValue("@mult", mult);
            apiCharge = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                UPDATE generation_errors
                SET user_id = {SqlLit.ParamOwner}
                WHERE {orphanUserSql};";
            cmd.Parameters.AddWithValue(SqlLit.ParamOwner, ownerId);
            genUser = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                UPDATE generation_errors
                SET project_id = {SqlLit.ParamProject}
                WHERE project_id IS NULL OR TRIM(project_id) = '';";
            cmd.Parameters.AddWithValue(SqlLit.ParamProject, projectId);
            genProj = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                UPDATE credit_ledger
                SET user_id = {SqlLit.ParamOwner}
                WHERE {orphanUserSql};";
            cmd.Parameters.AddWithValue(SqlLit.ParamOwner, ownerId);
            creditUser = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                UPDATE credit_ledger
                SET project_id = {SqlLit.ParamProject}
                WHERE project_id IS NULL OR TRIM(project_id) = '';";
            cmd.Parameters.AddWithValue(SqlLit.ParamProject, projectId);
            creditProj = cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                INSERT INTO credit_ledger (user_id, ts, kind, amount_usd, balance_after_usd, project_id, note, meta_kind)
                VALUES (
                    {SqlLit.ParamOwner},
                    @ts,
                    'adjust',
                    0,
                    COALESCE((SELECT credits_balance_usd FROM {SqlLit.Users} WHERE user_id = {SqlLit.ParamOwner}), 0),
                    {SqlLit.ParamProject},
                    @note,
                    'legacy_cost_attribution_v5');";
            cmd.Parameters.AddWithValue(SqlLit.ParamOwner, ownerId);
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue(SqlLit.ParamProject, projectId);
            cmd.Parameters.AddWithValue("@note",
                $"v5 migrate: api_user={apiUser} api_project={apiProj} api_charge={apiCharge} " +
                $"gen_user={genUser} gen_project={genProj} credit_user={creditUser} credit_project={creditProj}");
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation(
            "Legacy cost attribution v5: owner={Owner} project={Project} mult={Mult} " +
            "api_user={ApiUser} api_project={ApiProj} api_charge={ApiCharge} " +
            "gen_user={GenUser} gen_project={GenProj} credit_user={CreditUser} credit_project={CreditProj}",
            ownerId, projectId, mult, apiUser, apiProj, apiCharge, genUser, genProj, creditUser, creditProj);
    }

    /// <summary>
    /// One-shot v6: collapse dual operator accounts into a single handle + email, and move
    /// <b>all</b> spend / estimates / credits / keys from aliases onto that account.
    /// Also rehomes <c>projects/{alias}/…</c> folders and rewrites project.json ownerUserId.
    /// </summary>
    private void MigrateCanonicalAccountV6(SqliteConnection conn, BillingOptions billing)
    {
        var (primaryId, primaryHandle, primaryEmail) = ResolveV6PrimaryIdentity(billing);
        var aliasCandidates = CollectV6AliasCandidates(conn, billing, primaryId, primaryHandle, primaryEmail);
        var aliasUserIds = ResolveV6AliasUserIds(conn, aliasCandidates, primaryId);
        FreeV6AliasUniqueSlots(conn, aliasUserIds);
        EnsureV6PrimaryUserRow(conn, primaryId, primaryHandle, primaryEmail);

        int spendMoved = 0, creditsMoved = 0, keysMoved = 0, aliasesRemoved = 0;
        foreach (var aliasId in aliasUserIds)
        {
            MergeOneV6AliasIntoPrimary(
                conn, aliasId, primaryId, primaryHandle, primaryEmail,
                ref spendMoved, ref creditsMoved, ref keysMoved, ref aliasesRemoved);
        }

        FinalizeV6PrimaryHandleEmail(conn, primaryId, primaryHandle, primaryEmail);
        var projectsTouched = RehomeAliasProjectsV6(primaryId, aliasUserIds, aliasCandidates);
        RewriteV6ProjectIdPrefixes(conn, primaryId, aliasUserIds, aliasCandidates);

        _logger.LogInformation(
            "v6 canonical account merge → {Primary} ({Handle}, {Email}): aliases_removed={Aliases} " +
            "api_calls_moved={Spend} credit_rows_moved={Credits} keys_upserted={Keys} projects_rehomed={Projects}",
            primaryId, primaryHandle, primaryEmail, aliasesRemoved, spendMoved, creditsMoved, keysMoved, projectsTouched);
    }

    private static (string PrimaryId, string PrimaryHandle, string PrimaryEmail) ResolveV6PrimaryIdentity(BillingOptions billing)
    {
        var primaryId = string.IsNullOrWhiteSpace(billing.LegacyCostOwnerUserId)
            ? DefaultOperatorUserId
            : billing.LegacyCostOwnerUserId.Trim();
        var primaryHandle = string.IsNullOrWhiteSpace(billing.CanonicalAccountUsername)
            ? primaryId
            : billing.CanonicalAccountUsername.Trim();
        // Login handle must not contain spaces or @ — fall back to user id.
        if (primaryHandle.Contains(' ', StringComparison.Ordinal) ||
            primaryHandle.Contains('@', StringComparison.Ordinal))
            primaryHandle = primaryId;
        var primaryEmail = string.IsNullOrWhiteSpace(billing.CanonicalAccountEmail)
            ? "budcribar@msn.com"
            : NormalizeEmail(billing.CanonicalAccountEmail) ?? "budcribar@msn.com";
        return (primaryId, primaryHandle, primaryEmail);
    }

    private static HashSet<string> CollectV6AliasCandidates(
        SqliteConnection conn,
        BillingOptions billing,
        string primaryId,
        string primaryHandle,
        string primaryEmail)
    {
        var aliasCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (billing.AccountMergeAliasIds ?? "")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddV6Alias(aliasCandidates, part, primaryId, primaryHandle);
        AddV6Alias(aliasCandidates, primaryEmail, primaryId, primaryHandle);
        // Local-part of email (budcribar from budcribar@msn.com) is NOT an alias if it equals primary.

        CollectV6AliasesByEmail(conn, aliasCandidates, primaryId, primaryHandle, primaryEmail);
        CollectV6AliasesFromAllUsers(conn, aliasCandidates, primaryId, primaryHandle, primaryEmail);
        return aliasCandidates;
    }

    private static void AddV6Alias(HashSet<string> aliasCandidates, string? raw, string primaryId, string primaryHandle)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var t = raw.Trim();
        if (string.Equals(t, primaryId, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(t, primaryHandle, StringComparison.OrdinalIgnoreCase)) return;
        aliasCandidates.Add(t);
        var seg = ProjectOwnership.SanitizeOwnerSegment(t);
        if (seg.Length > 0 &&
            !string.Equals(seg, primaryId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(seg, ProjectOwnership.SanitizeOwnerSegment(primaryHandle), StringComparison.OrdinalIgnoreCase))
            aliasCandidates.Add(seg);
    }

    private static void CollectV6AliasesByEmail(
        SqliteConnection conn,
        HashSet<string> aliasCandidates,
        string primaryId,
        string primaryHandle,
        string primaryEmail)
    {
        // Any DB row whose email is the canonical email but user_id is not primary → alias.
        using var findByEmail = conn.CreateCommand();
        findByEmail.CommandText = $@"
                SELECT user_id, username, email FROM {SqlLit.Users}
                WHERE email IS NOT NULL AND LOWER(TRIM(email)) = LOWER({SqlLit.ParamEmail});";
        findByEmail.Parameters.AddWithValue(SqlLit.ParamEmail, primaryEmail);
        using var r = findByEmail.ExecuteReader();
        while (r.Read())
        {
            var uid = r.IsDBNull(0) ? "" : r.GetString(0);
            if (!string.Equals(uid, primaryId, StringComparison.OrdinalIgnoreCase))
                AddV6Alias(aliasCandidates, uid, primaryId, primaryHandle);
            if (!r.IsDBNull(1)) AddV6Alias(aliasCandidates, r.GetString(1), primaryId, primaryHandle);
        }
    }

    private static void CollectV6AliasesFromAllUsers(
        SqliteConnection conn,
        HashSet<string> aliasCandidates,
        string primaryId,
        string primaryHandle,
        string primaryEmail)
    {
        // Any user_id / username that sanitizes to a known alias segment (e.g. budcribarmsn.com).
        using var findAll = conn.CreateCommand();
        findAll.CommandText = $"SELECT user_id, username, email FROM {SqlLit.Users};";
        using var r = findAll.ExecuteReader();
        var snapshot = new List<(string Id, string? Name, string? Email)>();
        while (r.Read())
        {
            snapshot.Add((
                DbString(r, 0) ?? "",
                DbString(r, 1),
                DbString(r, 2)));
        }
        r.Close();

        var knownSegs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in aliasCandidates)
        {
            var s = ProjectOwnership.SanitizeOwnerSegment(c);
            if (s.Length > 0)
                knownSegs.Add(s);
        }
        // Always treat msn.com email-shaped handles as aliases of budcribar when configured.
        knownSegs.Add("budcribarmsn_com");
        knownSegs.Add("budcribar_msn_com");

        foreach (var row in snapshot)
            AddV6AliasFromSnapshotRow(aliasCandidates, knownSegs, row, primaryId, primaryHandle, primaryEmail);
    }

    private static void AddV6AliasFromSnapshotRow(
        HashSet<string> aliasCandidates,
        HashSet<string> knownSegs,
        (string Id, string? Name, string? Email) row,
        string primaryId,
        string primaryHandle,
        string primaryEmail)
    {
        if (string.Equals(row.Id, primaryId, StringComparison.OrdinalIgnoreCase))
            return;
        var idSeg = ProjectOwnership.SanitizeOwnerSegment(row.Id);
        var nameSeg = ProjectOwnership.SanitizeOwnerSegment(row.Name);
        if (knownSegs.Contains(idSeg) || knownSegs.Contains(nameSeg))
        {
            AddV6Alias(aliasCandidates, row.Id, primaryId, primaryHandle);
            AddV6Alias(aliasCandidates, row.Name, primaryId, primaryHandle);
        }
        if (!string.IsNullOrWhiteSpace(row.Email) &&
            string.Equals(NormalizeEmail(row.Email), primaryEmail, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.Id, primaryId, StringComparison.OrdinalIgnoreCase))
            AddV6Alias(aliasCandidates, row.Id, primaryId, primaryHandle);
    }

    private static HashSet<string> ResolveV6AliasUserIds(
        SqliteConnection conn,
        HashSet<string> aliasCandidates,
        string primaryId)
    {
        // Resolve actual alias user_ids present in DB (match id or username case-insensitively).
        var aliasUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cand in aliasCandidates)
        {
            using var q = conn.CreateCommand();
            q.CommandText = $@"
                SELECT user_id FROM {SqlLit.Users}
                WHERE LOWER(user_id) = LOWER(@c) OR LOWER(username) = LOWER(@c)
                LIMIT 1;";
            q.Parameters.AddWithValue("@c", cand);
            var found = q.ExecuteScalar()?.ToString();
            if (!string.IsNullOrWhiteSpace(found) &&
                !string.Equals(found, primaryId, StringComparison.OrdinalIgnoreCase))
                aliasUserIds.Add(found.Trim());
        }
        return aliasUserIds;
    }

    private static void FreeV6AliasUniqueSlots(SqliteConnection conn, HashSet<string> aliasUserIds)
    {
        // Free unique username/email on aliases BEFORE we assign them to primary.
        foreach (var aliasId in aliasUserIds)
        {
            using var free = conn.CreateCommand();
            free.CommandText = $@"
                UPDATE {SqlLit.Users} SET
                    email = NULL,
                    username = 'merged_' || user_id || '_' || substr(hex(randomblob(3)), 1, 6)
                WHERE user_id = {SqlLit.ParamAlias};";
            free.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            free.ExecuteNonQuery();
        }
    }

    private static void EnsureV6PrimaryUserRow(
        SqliteConnection conn,
        string primaryId,
        string primaryHandle,
        string primaryEmail)
    {
        // Ensure primary user row exists (email/handle applied after alias unique slots freed).
        using var ensure = conn.CreateCommand();
        ensure.CommandText = $@"
                INSERT INTO {SqlLit.Users} (user_id, username, password_hash, role, created_at, email, email_confirmed_at)
                VALUES (@id, @handle, '', 'User', {SqlLit.ParamCreated}, {SqlLit.ParamEmail}, {SqlLit.ParamCreated})
                ON CONFLICT(user_id) DO NOTHING;";
        ensure.Parameters.AddWithValue("@id", primaryId);
        ensure.Parameters.AddWithValue("@handle", primaryHandle);
        ensure.Parameters.AddWithValue(SqlLit.ParamEmail, primaryEmail);
        ensure.Parameters.AddWithValue(SqlLit.ParamCreated, DateTime.UtcNow.ToString("o"));
        ensure.ExecuteNonQuery();
    }

    private static void MergeOneV6AliasIntoPrimary(
        SqliteConnection conn,
        string aliasId,
        string primaryId,
        string primaryHandle,
        string primaryEmail,
        ref int spendMoved,
        ref int creditsMoved,
        ref int keysMoved,
        ref int aliasesRemoved)
    {
        // --- API keys: prefer primary when both set; otherwise take alias ---
        using (var keys = conn.CreateCommand())
        {
            keys.CommandText = $@"
                    INSERT INTO user_api_keys (user_id, provider_id, encrypted_api_key, updated_at)
                    SELECT {SqlLit.ParamPrimary}, provider_id, encrypted_api_key, updated_at
                    FROM user_api_keys WHERE user_id = {SqlLit.ParamAlias}
                    ON CONFLICT(user_id, provider_id) DO UPDATE SET
                        encrypted_api_key = CASE
                            WHEN user_api_keys.encrypted_api_key IS NULL
                              OR TRIM(user_api_keys.encrypted_api_key) = ''
                            THEN excluded.encrypted_api_key
                            ELSE user_api_keys.encrypted_api_key
                        END,
                        updated_at = excluded.updated_at;";
            keys.Parameters.AddWithValue(SqlLit.ParamPrimary, primaryId);
            keys.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            keysMoved += keys.ExecuteNonQuery();
        }
        using (var delKeys = conn.CreateCommand())
        {
            delKeys.CommandText = $"DELETE FROM user_api_keys WHERE user_id = {SqlLit.ParamAlias};";
            delKeys.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            delKeys.ExecuteNonQuery();
        }

        // --- Spend / estimates (user_api_calls) ---
        using (var api = conn.CreateCommand())
        {
            api.CommandText = $"UPDATE {SqlLit.UserApiCalls} SET user_id = {SqlLit.ParamPrimary} WHERE user_id = {SqlLit.ParamAlias};";
            api.Parameters.AddWithValue(SqlLit.ParamPrimary, primaryId);
            api.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            spendMoved += api.ExecuteNonQuery();
        }

        // --- Generation errors ---
        using (var gen = conn.CreateCommand())
        {
            gen.CommandText = $"UPDATE generation_errors SET user_id = {SqlLit.ParamPrimary} WHERE user_id = {SqlLit.ParamAlias};";
            gen.Parameters.AddWithValue(SqlLit.ParamPrimary, primaryId);
            gen.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            gen.ExecuteNonQuery();
        }

        // --- Credit ledger ---
        using (var led = conn.CreateCommand())
        {
            led.CommandText = $"UPDATE credit_ledger SET user_id = {SqlLit.ParamPrimary} WHERE user_id = {SqlLit.ParamAlias};";
            led.Parameters.AddWithValue(SqlLit.ParamPrimary, primaryId);
            led.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            creditsMoved += led.ExecuteNonQuery();
        }

        // --- Auth tokens ---
        using (var tok = conn.CreateCommand())
        {
            tok.CommandText = $"UPDATE auth_tokens SET user_id = {SqlLit.ParamPrimary} WHERE user_id = {SqlLit.ParamAlias};";
            tok.Parameters.AddWithValue(SqlLit.ParamPrimary, primaryId);
            tok.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            tok.ExecuteNonQuery();
        }

        // --- Merge credit balances + pick best password / role / confirmation ---
        string? aliasPass = null;
        string? aliasRole = null;
        string? aliasEmailConfirmed = null;
        double aliasBal = 0, aliasGranted = 0, aliasUsed = 0;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = $@"
                    SELECT password_hash, role, email_confirmed_at,
                           COALESCE(credits_balance_usd, 0),
                           COALESCE(credits_lifetime_granted_usd, 0),
                           COALESCE(credits_lifetime_used_usd, 0)
                    FROM {SqlLit.Users} WHERE user_id = {SqlLit.ParamAlias} LIMIT 1;";
            read.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            using var r = read.ExecuteReader();
            if (r.Read())
            {
                aliasPass = DbString(r, 0);
                aliasRole = DbString(r, 1);
                aliasEmailConfirmed = DbString(r, 2);
                aliasBal = DbDouble(r, 3) ?? 0;
                aliasGranted = DbDouble(r, 4) ?? 0;
                aliasUsed = DbDouble(r, 5) ?? 0;
            }
        }

        using (var mergeUser = conn.CreateCommand())
        {
            mergeUser.CommandText = $@"
                    UPDATE {SqlLit.Users} SET
                        username = @handle,
                        email = {SqlLit.ParamEmail},
                        email_confirmed_at = COALESCE(email_confirmed_at, @aliasConfirmed),
                        password_hash = CASE
                            WHEN password_hash IS NULL OR TRIM(password_hash) = '' THEN COALESCE(@aliasPass, password_hash)
                            ELSE password_hash
                        END,
                        role = CASE
                            WHEN LOWER(COALESCE(role, '')) = 'admin' OR LOWER(COALESCE(@aliasRole, '')) = 'admin'
                            THEN 'Admin' ELSE COALESCE(role, 'User')
                        END,
                        credits_balance_usd = COALESCE(credits_balance_usd, 0) + @aliasBal,
                        credits_lifetime_granted_usd = COALESCE(credits_lifetime_granted_usd, 0) + @aliasGranted,
                        credits_lifetime_used_usd = COALESCE(credits_lifetime_used_usd, 0) + @aliasUsed
                    WHERE user_id = {SqlLit.ParamPrimary};";
            mergeUser.Parameters.AddWithValue("@handle", primaryHandle);
            mergeUser.Parameters.AddWithValue(SqlLit.ParamEmail, primaryEmail);
            mergeUser.Parameters.AddWithValue("@aliasConfirmed", (object?)aliasEmailConfirmed ?? DBNull.Value);
            mergeUser.Parameters.AddWithValue("@aliasPass", (object?)aliasPass ?? DBNull.Value);
            mergeUser.Parameters.AddWithValue("@aliasRole", (object?)aliasRole ?? DBNull.Value);
            mergeUser.Parameters.AddWithValue("@aliasBal", aliasBal);
            mergeUser.Parameters.AddWithValue("@aliasGranted", aliasGranted);
            mergeUser.Parameters.AddWithValue("@aliasUsed", aliasUsed);
            mergeUser.Parameters.AddWithValue(SqlLit.ParamPrimary, primaryId);
            mergeUser.ExecuteNonQuery();
        }

        // Drop alias user row (children already reassigned).
        using (var del = conn.CreateCommand())
        {
            del.CommandText = $"DELETE FROM {SqlLit.Users} WHERE user_id = {SqlLit.ParamAlias};";
            del.Parameters.AddWithValue(SqlLit.ParamAlias, aliasId);
            aliasesRemoved += del.ExecuteNonQuery();
        }
    }

    private void FinalizeV6PrimaryHandleEmail(
        SqliteConnection conn,
        string primaryId,
        string primaryHandle,
        string primaryEmail)
    {
        // Ensure primary has handle + email even when no alias rows existed.
        using var finalize = conn.CreateCommand();
        // Free username/email unique slots held by nothing after deletes.
        finalize.CommandText = $@"
                UPDATE {SqlLit.Users} SET
                    username = @handle,
                    email = {SqlLit.ParamEmail},
                    email_confirmed_at = COALESCE(email_confirmed_at, @confirmed)
                WHERE user_id = {SqlLit.ParamPrimary};";
        finalize.Parameters.AddWithValue("@handle", primaryHandle);
        finalize.Parameters.AddWithValue(SqlLit.ParamEmail, primaryEmail);
        finalize.Parameters.AddWithValue("@confirmed", DateTime.UtcNow.ToString("o"));
        finalize.Parameters.AddWithValue(SqlLit.ParamPrimary, primaryId);
        try { finalize.ExecuteNonQuery(); }
        catch (SqliteException ex)
        {
            // Username/email unique conflict with a non-alias account — leave as-is, log.
            _logger.LogWarning(ex,
                "v6 could not set handle/email on {Primary} (unique conflict)", primaryId);
        }
    }

    private static void RewriteV6ProjectIdPrefixes(
        SqliteConnection conn,
        string primaryId,
        HashSet<string> aliasUserIds,
        HashSet<string> aliasCandidates)
    {
        // Rewrite project_id prefixes in cost tables (alias/slug → primary/slug).
        foreach (var alias in aliasUserIds.Concat(aliasCandidates).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var oldSeg = ProjectOwnership.SanitizeOwnerSegment(alias);
            var newSeg = ProjectOwnership.SanitizeOwnerSegment(primaryId);
            if (string.IsNullOrEmpty(oldSeg) || string.Equals(oldSeg, newSeg, StringComparison.OrdinalIgnoreCase))
                continue;
            var oldPrefix = oldSeg + "/";
            var newPrefix = newSeg + "/";
            foreach (var table in new[] { SqlLit.UserApiCalls, "generation_errors", "credit_ledger", "video_take_events" })
            {
                using var up = conn.CreateCommand();
                up.CommandText = RewriteProjectIdPrefixSql(table);
                BindProjectIdPrefixParams(up, newPrefix, oldPrefix);
                try { up.ExecuteNonQuery(); }
                catch { /* table may lack project_id in weird schemas */ }
            }
        }
    }

    /// <summary>
    /// Move <c>projects/{aliasSeg}/{slug}</c> → <c>projects/{primarySeg}/{slug}</c> and
    /// rewrite project.json ownerUserId. Best-effort; never deletes target if already present.
    /// </summary>
    private int RehomeAliasProjectsV6(
        string primaryId,
        IEnumerable<string> aliasUserIds,
        IEnumerable<string> aliasCandidates)
    {
        var projectsRoot = ResolveProjectsRootForMigration();
        if (projectsRoot is null || !Directory.Exists(projectsRoot))
            return 0;

        var primarySeg = ProjectOwnership.SanitizeOwnerSegment(primaryId);
        if (string.IsNullOrEmpty(primarySeg))
            primarySeg = DefaultOperatorUserId;

        var segs = BuildV6AliasSegments(primaryId, aliasUserIds, aliasCandidates);
        var targetOwnerDir = Path.Combine(projectsRoot, primarySeg);
        Directory.CreateDirectory(targetOwnerDir);

        int moved = 0;
        foreach (var seg in segs)
        {
            var srcOwnerDir = Path.Combine(projectsRoot, seg);
            if (!Directory.Exists(srcOwnerDir))
                continue;

            foreach (var projectDir in Directory.GetDirectories(srcOwnerDir))
            {
                var dest = Path.Combine(targetOwnerDir, Path.GetFileName(projectDir));
                TryMoveV6ProjectDir(projectDir, dest, primaryId, ref moved);
            }

            DeleteEmptyV6OwnerDir(srcOwnerDir);
        }

        PatchV6WorkspacePointers(projectsRoot, segs, primarySeg);
        PatchV6FlatLegacyOwners(projectsRoot, segs, primarySeg, aliasUserIds, primaryId);
        return moved;
    }

    private static HashSet<string> BuildV6AliasSegments(
        string primaryId,
        IEnumerable<string> aliasUserIds,
        IEnumerable<string> aliasCandidates)
    {
        var primarySeg = ProjectOwnership.SanitizeOwnerSegment(primaryId);
        if (string.IsNullOrEmpty(primarySeg))
            primarySeg = DefaultOperatorUserId;

        var segs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primarySeg };
        // Collect every alias folder segment we might own.
        foreach (var a in aliasUserIds.Concat(aliasCandidates))
        {
            var s = ProjectOwnership.SanitizeOwnerSegment(a);
            if (s.Length > 0 && !string.Equals(s, primarySeg, StringComparison.OrdinalIgnoreCase))
                segs.Add(s);
        }
        segs.Remove(primarySeg);
        return segs;
    }

    private void TryMoveV6ProjectDir(string projectDir, string dest, string primaryId, ref int moved)
    {
        var slug = Path.GetFileName(projectDir);
        if (string.IsNullOrWhiteSpace(slug) ||
            string.Equals(slug, "workspace.json", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (Directory.Exists(dest))
            {
                // Target already has this slug — keep target, patch owner on source in place then skip move.
                PatchProjectOwnerFile(projectDir, primaryId);
                PatchProjectOwnerFile(dest, primaryId);
                return;
            }

            Directory.Move(projectDir, dest);
            PatchProjectOwnerFile(dest, primaryId);
            moved++;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "v6 could not rehome project {Src} → {Dest}", projectDir, dest);
            try { PatchProjectOwnerFile(projectDir, primaryId); } catch { /* ignore */ }
        }
    }

    private static void DeleteEmptyV6OwnerDir(string srcOwnerDir)
    {
        // Drop empty alias owner folder
        try
        {
            if (Directory.Exists(srcOwnerDir) &&
                !Directory.EnumerateFileSystemEntries(srcOwnerDir).Any())
                Directory.Delete(srcOwnerDir);
        }
        catch { /* ignore */ }
    }

    private void PatchV6WorkspacePointers(string projectsRoot, HashSet<string> segs, string primarySeg)
    {
        // Patch workspace.json active project pointer if it pointed at an alias path.
        try
        {
            var wsPath = Path.Combine(projectsRoot, "workspace.json");
            if (File.Exists(wsPath))
            {
                var text = File.ReadAllText(wsPath);
                var changed = text;
                foreach (var seg in segs)
                {
                    changed = changed.Replace($"\"{seg}/", $"\"{primarySeg}/", StringComparison.OrdinalIgnoreCase);
                    changed = changed.Replace($"/{seg}/", $"/{primarySeg}/", StringComparison.OrdinalIgnoreCase);
                }
                if (!string.Equals(changed, text, StringComparison.Ordinal))
                    File.WriteAllText(wsPath, changed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "v6 could not patch workspace.json active project");
        }
    }

    private static void PatchV6FlatLegacyOwners(
        string projectsRoot,
        HashSet<string> segs,
        string primarySeg,
        IEnumerable<string> aliasUserIds,
        string primaryId)
    {
        // Flat legacy projects with wrong ownerUserId field only
        try
        {
            foreach (var dir in Directory.GetDirectories(projectsRoot))
            {
                var name = Path.GetFileName(dir);
                if (segs.Contains(name) || string.Equals(name, primarySeg, StringComparison.OrdinalIgnoreCase))
                    continue;
                var meta = Path.Combine(dir, "project.json");
                if (!File.Exists(meta)) continue;
                // Nested owner dirs already handled; flat project.json at projects/Slug
                try
                {
                    var json = File.ReadAllText(meta);
                    if (JsonContainsAnyAlias(json, aliasUserIds))
                        PatchProjectOwnerFile(dir, primaryId);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    private static bool JsonContainsAnyAlias(string json, IEnumerable<string> aliasUserIds)
    {
        return aliasUserIds.Any(a => json.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static void PatchProjectOwnerFile(string projectDir, string ownerUserId)
    {
        var metaPath = Path.Combine(projectDir, "project.json");
        if (!File.Exists(metaPath)) return;
        try
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                           File.ReadAllText(metaPath))
                       ?? new Dictionary<string, JsonElement>();
            // Rebuild as dictionary<object?> for write
            var dict = new Dictionary<string, object?>();
            foreach (var kv in meta)
            {
                dict[kv.Key] = kv.Value.ValueKind switch
                {
                    JsonValueKind.String => kv.Value.GetString(),
                    JsonValueKind.Number => kv.Value.TryGetInt64(out var l) ? l : kv.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => kv.Value.GetRawText(),
                };
            }
            dict["ownerUserId"] = ownerUserId;
            // Keep id in sync when it was owner/slug
            if (dict.TryGetValue("id", out var idObj) && idObj is string idStr && idStr.Contains('/'))
            {
                var slash = idStr.LastIndexOf('/');
                if (slash > 0)
                {
                    var slug = idStr[(slash + 1)..];
                    var seg = ProjectOwnership.SanitizeOwnerSegment(ownerUserId);
                    dict["id"] = $"{seg}/{slug}";
                }
            }
            File.WriteAllText(metaPath,
                JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch
        {
            // best-effort rewrite of project.json owner fields during alias rehome
        }
    }

    private string? ResolveProjectsRootForMigration()
    {
        foreach (var candidate in new[]
                 {
                     _workspaceRoot,
                     Directory.Exists(ContainerDataDir) ? ContainerDataDir : null,
                     Directory.Exists(AppContainerDataDir) ? AppContainerDataDir : null,
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var projects = Path.Combine(candidate.Trim(), SqlLit.Projects);
            if (Directory.Exists(projects))
                return projects;
            // Workspace may be repo root with projects/ beside host/
            if (Directory.Exists(Path.Combine(candidate.Trim(), SqlLit.Projects)))
                return Path.Combine(candidate.Trim(), SqlLit.Projects);
        }

        // Walk up from app base
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var projects = Path.Combine(dir.FullName, SqlLit.Projects);
                if (Directory.Exists(projects))
                    return projects;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static string RewriteProjectIdPrefixSql(string table) => table switch
    {
        SqlLit.UserApiCalls =>
            $"""
            UPDATE {SqlLit.UserApiCalls}
            SET project_id = @newPrefix || SUBSTR(project_id, @oldLen + 1)
            WHERE project_id IS NOT NULL
              AND (project_id = @oldSeg OR project_id LIKE @oldPrefixLike);
            """,
        "generation_errors" =>
            """
            UPDATE generation_errors
            SET project_id = @newPrefix || SUBSTR(project_id, @oldLen + 1)
            WHERE project_id IS NOT NULL
              AND (project_id = @oldSeg OR project_id LIKE @oldPrefixLike);
            """,
        "credit_ledger" =>
            """
            UPDATE credit_ledger
            SET project_id = @newPrefix || SUBSTR(project_id, @oldLen + 1)
            WHERE project_id IS NOT NULL
              AND (project_id = @oldSeg OR project_id LIKE @oldPrefixLike);
            """,
        "video_take_events" =>
            """
            UPDATE video_take_events
            SET project_id = @newPrefix || SUBSTR(project_id, @oldLen + 1)
            WHERE project_id IS NOT NULL
              AND (project_id = @oldSeg OR project_id LIKE @oldPrefixLike);
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unsupported table.")
    };

    private static void BindProjectIdPrefixParams(SqliteCommand cmd, string newPrefix, string oldPrefix)
    {
        cmd.Parameters.AddWithValue("@newPrefix", newPrefix);
        cmd.Parameters.AddWithValue("@oldLen", oldPrefix.Length);
        cmd.Parameters.AddWithValue("@oldSeg", oldPrefix.TrimEnd(Path.AltDirectorySeparatorChar));
        cmd.Parameters.AddWithValue("@oldPrefixLike", oldPrefix + "%");
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string typeSql)
    {
        using var check = conn.CreateCommand();
        check.CommandText = PragmaTableInfoSql(table);
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = AlterAddColumnSql(table, column, typeSql);
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Race-safe: another process may have added the column between PRAGMA and ALTER.
        }
    }

    private static string PragmaTableInfoSql(string table) => table switch
    {
        SqlLit.Users => $"PRAGMA table_info({SqlLit.Users})",
        SqlLit.UserApiCalls => $"PRAGMA table_info({SqlLit.UserApiCalls})",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unsupported table.")
    };

    private static string AlterAddColumnSql(string table, string column, string typeSql) => (table, column, typeSql) switch
    {
        (SqlLit.Users, "encrypted_gemini_api_key", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN encrypted_gemini_api_key TEXT",
        (SqlLit.Users, "encrypted_anthropic_api_key", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN encrypted_anthropic_api_key TEXT",
        (SqlLit.Users, "encrypted_fal_api_key", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN encrypted_fal_api_key TEXT",
        (SqlLit.Users, "credits_balance_usd", SqlLit.RealNotNullDefault0) => $"ALTER TABLE {SqlLit.Users} ADD COLUMN credits_balance_usd {SqlLit.RealNotNullDefault0}",
        (SqlLit.Users, "credits_lifetime_granted_usd", SqlLit.RealNotNullDefault0) => $"ALTER TABLE {SqlLit.Users} ADD COLUMN credits_lifetime_granted_usd {SqlLit.RealNotNullDefault0}",
        (SqlLit.Users, "credits_lifetime_used_usd", SqlLit.RealNotNullDefault0) => $"ALTER TABLE {SqlLit.Users} ADD COLUMN credits_lifetime_used_usd {SqlLit.RealNotNullDefault0}",
        (SqlLit.Users, "terms_accepted_at", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN terms_accepted_at TEXT",
        (SqlLit.Users, "terms_version", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN terms_version TEXT",
        (SqlLit.Users, "is_disabled", "INTEGER NOT NULL DEFAULT 0") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN is_disabled INTEGER NOT NULL DEFAULT 0",
        (SqlLit.Users, "password_reset_requested_at", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN password_reset_requested_at TEXT",
        (SqlLit.Users, "email", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN email TEXT",
        (SqlLit.Users, "email_confirmed_at", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN email_confirmed_at TEXT",
        (SqlLit.Users, "active_project_id", "TEXT") => $"ALTER TABLE {SqlLit.Users} ADD COLUMN active_project_id TEXT",
        (SqlLit.UserApiCalls, "category", "TEXT") => $"ALTER TABLE {SqlLit.UserApiCalls} ADD COLUMN category TEXT",
        (SqlLit.UserApiCalls, "charge_usd", "REAL") => $"ALTER TABLE {SqlLit.UserApiCalls} ADD COLUMN charge_usd REAL",
        (SqlLit.UserApiCalls, "charge_multiplier", "REAL") => $"ALTER TABLE {SqlLit.UserApiCalls} ADD COLUMN charge_multiplier REAL",
        (SqlLit.UserApiCalls, "attempt", "INTEGER") => $"ALTER TABLE {SqlLit.UserApiCalls} ADD COLUMN attempt INTEGER",
        (SqlLit.UserApiCalls, "outcome", "TEXT") => $"ALTER TABLE {SqlLit.UserApiCalls} ADD COLUMN outcome TEXT",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unsupported column migration.")
    };

    public async Task<UserEntity?> GetUserByIdAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = UserSelectByIdSql;
        cmd.Parameters.AddWithValue("@id", userId.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ReadUserFromReader(reader);

        return null;
    }

    public async Task<UserEntity?> GetUserByUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = UserSelectByUsernameSql;
        cmd.Parameters.AddWithValue(SqlLit.ParamName, username.Trim());

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ReadUserFromReader(reader);

        return null;
    }

    /// <summary>
    /// Privacy-safe handle search: returns usernames only (never emails).
    /// Exact match first, then prefix matches. Disabled accounts excluded.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchUsernamesAsync(
        string query,
        int take = 8,
        CancellationToken ct = default)
    {
        var q = (query ?? "").Trim().TrimStart('@');
        if (q.Length < 1) return Array.Empty<string>();
        take = Math.Clamp(take, 1, 20);

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        // Exact first (ORDER BY exact), then prefix; hide disabled; never return email
        cmd.CommandText = $"""
            SELECT username FROM {SqlLit.Users}
            WHERE COALESCE(is_disabled, 0) = 0
              AND username IS NOT NULL
              AND TRIM(username) != ''
              AND (
                    LOWER(username) = LOWER(@exact)
                 OR LOWER(username) LIKE LOWER(@prefix)
              )
            ORDER BY CASE WHEN LOWER(username) = LOWER(@exact) THEN 0 ELSE 1 END,
                     LENGTH(username),
                     username COLLATE NOCASE
            LIMIT {SqlLit.ParamTake}
            """;
        cmd.Parameters.AddWithValue("@exact", q);
        cmd.Parameters.AddWithValue("@prefix", q + "%");
        cmd.Parameters.AddWithValue(SqlLit.ParamTake, take);

        var list = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0).Trim();
            if (name.Length == 0) continue;
            // Prefer not listing pure email usernames as "handles" in search
            if (name.Contains('@', StringComparison.Ordinal)) continue;
            list.Add(name);
        }
        return list;
    }

    /// <summary>Resolve by user_id, then username (case-insensitive).</summary>
    public async Task<UserEntity?> ResolveUserAsync(string userIdOrName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userIdOrName)) return null;
        var byId = await GetUserByIdAsync(userIdOrName, ct).ConfigureAwait(false);
        if (byId is not null) return byId;
        return await GetUserByUsernameAsync(userIdOrName, ct).ConfigureAwait(false);
    }

    public const string AuthPurposeEmailConfirm = "email_confirm";
    public const string AuthPurposePasswordReset = "password_reset";

    private const string UserSelectSql = $@"
            SELECT user_id, username, password_hash,
                   encrypted_xai_api_key, encrypted_gemini_api_key, encrypted_anthropic_api_key, encrypted_fal_api_key,
                   role, created_at, last_login_at,
                   COALESCE(credits_balance_usd, 0),
                   COALESCE(credits_lifetime_granted_usd, 0),
                   COALESCE(credits_lifetime_used_usd, 0),
                   COALESCE(is_disabled, 0),
                   email,
                   email_confirmed_at
            FROM {SqlLit.Users}";

    private const string UserSelectByIdSql = UserSelectSql + " WHERE user_id = @id LIMIT 1";
    private const string UserSelectByUsernameSql = UserSelectSql + " WHERE LOWER(username) = LOWER(@name) LIMIT 1";
    private const string UserSelectOrderedByUsernameSql = UserSelectSql + " ORDER BY LOWER(username)";
    private const string UserSelectByEmailSql = UserSelectSql + " WHERE LOWER(email) = @e LIMIT 1";

    /// <summary>Saves or updates a user's encrypted xAI API key in SQLite.</summary>
    public Task SaveXaiApiKeyAsync(string userId, string? apiKey, CancellationToken ct = default) =>
        SaveProviderApiKeyAsync(userId, "grok", apiKey, ct);

    /// <summary>
    /// Saves a personal provider key dynamically into user_api_keys. Empty/whitespace clears the stored key.
    /// Provider: grok, gemini, anthropic, fal, replicate, or any arbitrary provider ID.
    /// </summary>
    public async Task SaveProviderApiKeyAsync(string userId, string providerId, string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(providerId)) return;
        var normId = NormalizeProvider(providerId);

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            using var delCmd = conn.CreateCommand();
            delCmd.CommandText = $"DELETE FROM user_api_keys WHERE user_id = {SqlLit.ParamUserId} AND provider_id = @providerId";
            delCmd.Parameters.AddWithValue(SqlLit.ParamUserId, userId.Trim());
            delCmd.Parameters.AddWithValue("@providerId", normId);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return;
        }

        var encrypted = EncryptApiKey(apiKey.Trim());
        using var upsertCmd = conn.CreateCommand();
        upsertCmd.CommandText = $@"
            INSERT INTO user_api_keys (user_id, provider_id, encrypted_api_key, updated_at)
            VALUES ({SqlLit.ParamUserId}, @providerId, @key, @updated)
            ON CONFLICT(user_id, provider_id) DO UPDATE SET
                encrypted_api_key = excluded.encrypted_api_key,
                updated_at = excluded.updated_at;";
        upsertCmd.Parameters.AddWithValue(SqlLit.ParamUserId, userId.Trim());
        upsertCmd.Parameters.AddWithValue("@providerId", normId);
        upsertCmd.Parameters.AddWithValue("@key", encrypted);
        upsertCmd.Parameters.AddWithValue("@updated", DateTime.UtcNow.ToString("o"));
        await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies provider API key updates dynamically from the request.
    /// </summary>
    public async Task UpdateUserSettingsAsync(string userId, UpdateUserSettingsRequest req, CancellationToken ct = default)
    {
        if (req.ProviderApiKeys is { Count: > 0 })
        {
            foreach (var kvp in req.ProviderApiKeys)
            {
                await SaveProviderApiKeyAsync(userId, kvp.Key, kvp.Value, ct).ConfigureAwait(false);
            }
        }
    }


    /// <summary>
    /// Append one API call for BYOK cost attribution. Never throws to callers — telemetry must not break gen.
    /// </summary>

    public async Task<List<UserApiCallRow>> ListUserApiCallsAsync(
        string userId,
        int take = 100,
        CancellationToken ct = default)
    {
        EnsureDatabaseInitialized();
        var list = new List<UserApiCallRow>();
        if (string.IsNullOrWhiteSpace(userId)) return list;
        take = Math.Clamp(take, 1, 500);
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT id, user_id, ts, project_id, job_id, kind, mode, category, provider, model, endpoint,
                   http_status, ok, duration_ms, estimated_usd, currency, scene, clip, char_key,
                   resolution, duration_sec, input_tokens, output_tokens, purpose, error, fakes
            FROM {SqlLit.UserApiCalls}
            WHERE user_id = {SqlLit.ParamUserId}
            ORDER BY id DESC
            LIMIT {SqlLit.ParamTake}";
        cmd.Parameters.AddWithValue(SqlLit.ParamUserId, userId.Trim());
        cmd.Parameters.AddWithValue(SqlLit.ParamTake, take);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadUserApiCallRow(r));
        return list;
    }

    private static string? DbString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? DbInt32(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static long? DbInt64(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static double? DbDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static string ResolveCallCategory(SqliteDataReader r)
    {
        var kind = r.GetString(5);
        var mode = DbString(r, 6);
        var category = DbString(r, 7);
        if (string.IsNullOrWhiteSpace(category))
            return CostCategories.Resolve(kind, mode);
        return CostCategories.Resolve(kind, mode, category);
    }

    private static UserApiCallRow ReadUserApiCallRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        UserId = r.GetString(1),
        Ts = r.GetString(2),
        ProjectId = DbString(r, 3),
        JobId = DbString(r, 4),
        Kind = r.GetString(5),
        Mode = DbString(r, 6),
        Category = ResolveCallCategory(r),
        Provider = DbString(r, 8),
        Model = DbString(r, 9),
        Endpoint = DbString(r, 10),
        HttpStatus = DbInt32(r, 11),
        Ok = r.GetInt32(12) != 0,
        DurationMs = DbInt64(r, 13),
        EstimatedUsd = DbDouble(r, 14),
        Currency = DbString(r, 15) ?? "USD",
        Scene = DbInt32(r, 16),
        Clip = DbInt32(r, 17),
        CharKey = DbString(r, 18),
        Resolution = DbString(r, 19),
        DurationSec = DbDouble(r, 20),
        InputTokens = DbInt32(r, 21),
        OutputTokens = DbInt32(r, 22),
        Purpose = DbString(r, 23),
        Error = DbString(r, 24),
        Fakes = r.GetInt32(25) != 0,
    };


    /// <summary>
    /// H1/H9 — dual-write a video take for studio aggregates. Never throws (fail-open).
    /// </summary>
    public async Task TryInsertVideoTakeEventAsync(VideoTakeEventRecord rec, CancellationToken ct = default)
    {
        try
        {
            if (rec is null || string.IsNullOrWhiteSpace(rec.ProjectId)) return;
            EnsureDatabaseInitialized();
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO video_take_events (
                    ts, project_id, user_id, scene, clip, take_index, take_kind, reason,
                    model, resolution, list_usd, duration_sec, key_mode, stable_beat_id,
                    had_char_refs, had_loc_ref, minutes_since_prev, contribute)
                VALUES (
                    @ts, {SqlLit.ParamProjectId}, {SqlLit.ParamUserId}, {SqlLit.ParamScene}, {SqlLit.ParamClip}, @takeIndex, @takeKind, @reason,
                    @model, @resolution, @listUsd, @durationSec, @keyMode, @stableBeatId,
                    @hadChar, @hadLoc, @minutesPrev, @contribute)
                """;
            var ts = string.IsNullOrWhiteSpace(rec.Ts)
                ? DateTimeOffset.UtcNow.ToString("o")
                : rec.Ts;
            cmd.Parameters.AddWithValue("@ts", ts);
            cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, rec.ProjectId.Trim());
            cmd.Parameters.AddWithValue(SqlLit.ParamUserId, (object?)rec.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(SqlLit.ParamScene, rec.Scene);
            cmd.Parameters.AddWithValue(SqlLit.ParamClip, rec.Clip);
            cmd.Parameters.AddWithValue("@takeIndex", Math.Max(1, rec.TakeIndex));
            cmd.Parameters.AddWithValue("@takeKind",
                VideoTakeKinds.Normalize(rec.TakeKind, VideoTakeKinds.Initial));
            cmd.Parameters.AddWithValue("@reason",
                (object?)VideoTakeReasons.NormalizeOptional(rec.Reason) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@model", (object?)rec.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@resolution", (object?)rec.Resolution ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@listUsd", rec.ListUsd is double lu ? lu : DBNull.Value);
            cmd.Parameters.AddWithValue("@durationSec", rec.DurationSec is double ds ? ds : DBNull.Value);
            cmd.Parameters.AddWithValue("@keyMode", (object?)rec.KeyMode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@stableBeatId", (object?)rec.StableBeatId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hadChar", rec.HadCharRefs ? 1 : 0);
            cmd.Parameters.AddWithValue("@hadLoc", rec.HadLocRef ? 1 : 0);
            cmd.Parameters.AddWithValue("@minutesPrev",
                rec.MinutesSincePrevTake is double m ? m : DBNull.Value);
            cmd.Parameters.AddWithValue("@contribute", rec.ContributeToStudioAverages ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // H9 fail-open
            _logger.LogDebug(ex, "TryInsertVideoTakeEventAsync failed (ignored)");
        }
    }

    /// <summary>
    /// H3 — set reason on the latest matching take event (optional take_index). Fail-open returns false.
    /// </summary>
    public async Task<bool> TrySetVideoTakeReasonAsync(
        string projectId,
        int scene,
        int clip,
        string reason,
        int? takeIndex = null,
        CancellationToken ct = default)
    {
        try
        {
            var r = VideoTakeReasons.NormalizeOptional(reason);
            if (r is null || string.IsNullOrWhiteSpace(projectId)) return false;
            EnsureDatabaseInitialized();
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            if (takeIndex is > 0)
            {
                cmd.CommandText = $"""
                    UPDATE video_take_events
                    SET reason = @reason
                    WHERE id = (
                        SELECT id FROM video_take_events
                        WHERE project_id = {SqlLit.ParamProjectId} AND scene = {SqlLit.ParamScene} AND clip = {SqlLit.ParamClip}
                          AND take_index = @takeIndex
                        ORDER BY id DESC LIMIT 1)
                    """;
                cmd.Parameters.AddWithValue("@takeIndex", takeIndex.Value);
            }
            else
            {
                cmd.CommandText = $"""
                    UPDATE video_take_events
                    SET reason = @reason
                    WHERE id = (
                        SELECT id FROM video_take_events
                        WHERE project_id = {SqlLit.ParamProjectId} AND scene = {SqlLit.ParamScene} AND clip = {SqlLit.ParamClip}
                        ORDER BY id DESC LIMIT 1)
                    """;
            }
            cmd.Parameters.AddWithValue("@reason", r);
            cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, projectId.Trim());
            cmd.Parameters.AddWithValue(SqlLit.ParamScene, scene);
            cmd.Parameters.AddWithValue(SqlLit.ParamClip, clip);
            var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return n > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TrySetVideoTakeReasonAsync failed");
            return false;
        }
    }

    public const int MinTakesClipSamples = 12;

    /// <summary>
    /// H4/H7/H9 — aggregate takes-per-clip. Global scope only includes contribute=1 rows.
    /// Never throws — empty stats on failure.
    /// </summary>
    public async Task<TakesTelemetryStats> GetTakesTelemetryStatsAsync(
        string? projectId = null,
        CancellationToken ct = default)
    {
        var stats = new TakesTelemetryStats
        {
            Scope = string.IsNullOrWhiteSpace(projectId) ? "global" : "project",
        };
        try
        {
            EnsureDatabaseInitialized();
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var pid = string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim();

            await FillClipTakeStatsAsync(conn, pid, stats, ct).ConfigureAwait(false);
            await FillTakeKindSharesAsync(conn, pid, stats, ct).ConfigureAwait(false);
            await FillReasonCountsAsync(conn, pid, stats, ct).ConfigureAwait(false);
            await FillWeeklyTakeBucketsAsync(conn, pid, stats, ct).ConfigureAwait(false);

            stats.SufficientForBlend = stats.ClipSampleCount >= MinTakesClipSamples;
            stats.Notes = TakesTelemetryNotes(stats);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetTakesTelemetryStatsAsync failed");
            stats.Notes = "Take telemetry unavailable (fail-open).";
        }
        return stats;
    }

    private static string TakesTelemetryNotes(TakesTelemetryStats stats)
    {
        if (stats.ClipSampleCount == 0) return "No take events yet.";
        if (stats.SufficientForBlend)
            return $"n={stats.ClipSampleCount} clips; p50={stats.P50TakesPerClip:0.##} takes/clip.";
        return $"n={stats.ClipSampleCount} clips (need {MinTakesClipSamples} for blend); p50={stats.P50TakesPerClip:0.##}.";
    }

    private static void BindTakesProjectId(SqliteCommand cmd, string projectId) =>
        cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, projectId);

    private static async Task FillClipTakeStatsAsync(
        SqliteConnection conn, string projectId, TakesTelemetryStats stats, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT project_id, scene, clip, MAX(take_index) AS takes
            FROM video_take_events
            WHERE ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
              AND ({SqlLit.ParamProjectId} != '' OR contribute = 1)
            GROUP BY project_id, scene, clip
            """;
        BindTakesProjectId(cmd, projectId);
        var takes = new List<int>();
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            takes.Add(Math.Max(1, r.GetInt32(3)));
        stats.ClipSampleCount = takes.Count;
        if (takes.Count == 0) return;
        takes.Sort();
        stats.MeanTakesPerClip = Math.Round(takes.Average(), 3);
        stats.P25TakesPerClip = PercentileSorted(takes, 0.25);
        stats.P50TakesPerClip = PercentileSorted(takes, 0.50);
        stats.P75TakesPerClip = PercentileSorted(takes, 0.75);
        stats.RegenRate = Math.Round(takes.Count(t => t >= 2) / (double)takes.Count, 4);
    }

    private static async Task FillTakeKindSharesAsync(
        SqliteConnection conn, string projectId, TakesTelemetryStats stats, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT take_kind, COUNT(*)
            FROM video_take_events
            WHERE ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
              AND ({SqlLit.ParamProjectId} != '' OR contribute = 1)
            GROUP BY take_kind
            """;
        BindTakesProjectId(cmd, projectId);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var byKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var k = r.IsDBNull(0) ? "unknown" : r.GetString(0);
            var c = r.GetInt32(1);
            byKind[k] = c;
            stats.EventCount += c;
        }
        if (stats.EventCount <= 0) return;
        stats.InitialShare = TakeKindShare(byKind, stats.EventCount, VideoTakeKinds.Initial);
        stats.UserRegenShare = TakeKindShare(byKind, stats.EventCount, VideoTakeKinds.UserRegen);
        stats.QaAutoShare = TakeKindShare(byKind, stats.EventCount, VideoTakeKinds.QaAuto);
        stats.FillHolesShare = TakeKindShare(byKind, stats.EventCount, VideoTakeKinds.FillHoles);
        stats.StaleRegenShare = TakeKindShare(byKind, stats.EventCount, VideoTakeKinds.StaleRegen);
    }

    private static double TakeKindShare(Dictionary<string, int> byKind, int eventCount, string kind) =>
        byKind.TryGetValue(kind, out var n) ? Math.Round(n / (double)eventCount, 4) : 0;

    private static async Task FillReasonCountsAsync(
        SqliteConnection conn, string projectId, TakesTelemetryStats stats, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT reason, COUNT(*)
            FROM video_take_events
            WHERE reason IS NOT NULL AND TRIM(reason) != ''
              AND ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
              AND ({SqlLit.ParamProjectId} != '' OR contribute = 1)
            GROUP BY reason
            """;
        BindTakesProjectId(cmd, projectId);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            stats.Reasons[r.GetString(0)] = r.GetInt32(1);
    }

    private static async Task FillWeeklyTakeBucketsAsync(
        SqliteConnection conn, string projectId, TakesTelemetryStats stats, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT substr(ts, 1, 10) AS day, project_id, scene, clip, MAX(take_index)
            FROM video_take_events
            WHERE ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
              AND ({SqlLit.ParamProjectId} != '' OR contribute = 1)
              AND ts >= @since
            GROUP BY day, project_id, scene, clip
            """;
        BindTakesProjectId(cmd, projectId);
        cmd.Parameters.AddWithValue("@since",
            DateTime.UtcNow.AddDays(-84).ToString("yyyy-MM-dd"));
        var byWeek = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            AccumulateWeekTake(byWeek, r);
        foreach (var kv in byWeek.OrderBy(k => k.Key))
            AddWeeklyBucket(stats, kv.Key, kv.Value);
    }

    private static void AccumulateWeekTake(Dictionary<string, List<int>> byWeek, SqliteDataReader r)
    {
        var day = r.IsDBNull(0) ? "" : r.GetString(0);
        if (!DateTime.TryParse(day, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return;
        var weekStart = d.Date.AddDays(-(int)d.DayOfWeek).ToString("yyyy-MM-dd");
        if (!byWeek.TryGetValue(weekStart, out var list))
        {
            list = new List<int>();
            byWeek[weekStart] = list;
        }
        list.Add(Math.Max(1, r.GetInt32(4)));
    }

    private static void AddWeeklyBucket(TakesTelemetryStats stats, string weekStart, List<int> list)
    {
        if (list.Count == 0) return;
        stats.Weekly.Add(new TakesTelemetryWeekBucket
        {
            WeekStart = weekStart,
            ClipSampleCount = list.Count,
            MeanTakesPerClip = Math.Round(list.Average(), 3),
            RegenRate = Math.Round(list.Count(t => t >= 2) / (double)list.Count, 4),
        });
    }

    private static double PercentileSorted(List<int> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return 0;
        if (sortedAsc.Count == 1) return sortedAsc[0];
        var idx = (sortedAsc.Count - 1) * p;
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sortedAsc[lo];
        var w = idx - lo;
        return Math.Round(sortedAsc[lo] * (1 - w) + sortedAsc[hi] * w, 3);
    }

    /// <summary>
    /// Aggregate list-rate spend from user_api_calls for estimate refinement.
    /// When <paramref name="userId"/> is null/empty, uses all users (portfolio prior).
    /// </summary>
    public async Task<ApiCostHistoryStats> GetApiCostHistoryStatsAsync(
        string? userId = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        EnsureDatabaseInitialized();
        var stats = new ApiCostHistoryStats();
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    COALESCE(NULLIF(TRIM(category), ''), NULLIF(TRIM(kind), ''), 'other') AS cat,
                    COUNT(*),
                    COALESCE(SUM(estimated_usd), 0),
                    COALESCE(AVG(estimated_usd), 0)
                FROM {SqlLit.UserApiCalls}
                WHERE ok = 1
                  AND estimated_usd IS NOT NULL
                  AND estimated_usd > 0
                  AND ({SqlLit.ParamUserId} = '' OR user_id = {SqlLit.ParamUserId})
                  AND ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
                GROUP BY cat
                """;
            cmd.Parameters.AddWithValue(SqlLit.ParamUserId, string.IsNullOrWhiteSpace(userId) ? "" : userId.Trim());
            cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim());
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var cat = CostCategories.Resolve(r.GetString(0), null, r.GetString(0));
                var count = r.GetInt32(1);
                var sum = r.GetDouble(2);
                var avg = r.GetDouble(3);
                stats.TotalCalls += count;
                stats.TotalUsd += sum;
                if (!stats.ByCategory.TryGetValue(cat, out var row))
                {
                    row = new CategoryCostStats { Category = cat };
                    stats.ByCategory[cat] = row;
                }
                row.Count += count;
                row.TotalUsd += sum;
                row.AvgUsd = row.Count > 0 ? row.TotalUsd / row.Count : 0;
                _ = avg;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetApiCostHistoryStatsAsync failed");
        }
        return stats;
    }

    /// <summary>
    /// Actual spend grouped by provider (then category). Returns both list-rate (COGS) and
    /// customer charge amounts. <paramref name="userId"/> null/empty = all users;
    /// <paramref name="projectId"/> null/empty = all projects.
    /// </summary>
    public async Task<ApiCostByProviderStats> GetApiCostByProviderAsync(
        string? userId = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        EnsureDatabaseInitialized();
        var stats = new ApiCostByProviderStats();
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            // Display charge = list (estimated_usd) × current admin multiplier; DB stores list only.
            cmd.CommandText = $"""
                SELECT
                    COALESCE(NULLIF(TRIM(provider), ''), 'unknown') AS prov,
                    COALESCE(NULLIF(TRIM(category), ''), NULLIF(TRIM(kind), ''), 'other') AS cat,
                    COUNT(*),
                    COALESCE(SUM(estimated_usd), 0),
                    COALESCE(SUM(estimated_usd), 0) * @chargeMult
                FROM {SqlLit.UserApiCalls}
                WHERE ok = 1
                  AND estimated_usd IS NOT NULL
                  AND estimated_usd > 0
                  AND ({SqlLit.ParamUserId} = '' OR user_id = {SqlLit.ParamUserId})
                  AND ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
                GROUP BY prov, cat
                """;
            cmd.Parameters.AddWithValue(SqlLit.ParamUserId, string.IsNullOrWhiteSpace(userId) ? "" : userId.Trim());
            cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim());
            cmd.Parameters.AddWithValue("@chargeMult", PageToMovie.Core.Billing.ChargePricing.ClampMultiplier(_billing.ChargeMultiplier));
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var provider = r.GetString(0);
                var cat = CostCategories.Resolve(r.GetString(1), null, r.GetString(1));
                var count = r.GetInt32(2);
                var listSum = r.GetDouble(3);
                var chargeSum = r.GetDouble(4);

                if (!stats.ByProvider.TryGetValue(provider, out var prow))
                {
                    prow = new ProviderCostStats { Provider = provider };
                    stats.ByProvider[provider] = prow;
                }
                prow.Count += count;
                prow.TotalListUsd += listSum;
                prow.TotalChargeUsd += chargeSum;
                prow.TotalUsd += chargeSum; // customer-facing default
                if (!prow.ByCategory.TryGetValue(cat, out var crow))
                {
                    crow = new CategoryCostStats { Category = cat };
                    prow.ByCategory[cat] = crow;
                }
                crow.Count += count;
                crow.TotalListUsd += listSum;
                crow.TotalChargeUsd += chargeSum;
                crow.TotalUsd += chargeSum;
                crow.AvgUsd = crow.Count > 0 ? crow.TotalChargeUsd / crow.Count : 0;

                stats.TotalCalls += count;
                stats.TotalListUsd += listSum;
                stats.TotalChargeUsd += chargeSum;
                stats.TotalUsd += chargeSum;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetApiCostByProviderAsync failed");
        }
        return stats;
    }

    /// <summary>
    /// Per-user spend: grand total, by project, by vendor (provider), by category.
    /// Source of truth: <c>user_api_calls</c> (requires UserId on every API log).
    /// </summary>
    public async Task<UserSpendSummary> GetUserSpendSummaryAsync(
        string userId,
        string? projectId = null,
        CancellationToken ct = default)
    {
        EnsureDatabaseInitialized();
        var summary = new UserSpendSummary
        {
            UserId = string.IsNullOrWhiteSpace(userId) ? "" : userId.Trim(),
        };
        if (string.IsNullOrWhiteSpace(summary.UserId))
            return summary;

        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // Totals + by project
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT
                        COALESCE(NULLIF(TRIM(project_id), ''), '(no project)') AS proj,
                        COUNT(*),
                        COALESCE(SUM(estimated_usd), 0),
                        COALESCE(SUM(estimated_usd), 0) * @chargeMult
                    FROM {SqlLit.UserApiCalls}
                    WHERE ok = 1
                      AND user_id = {SqlLit.ParamUserId}
                      AND estimated_usd IS NOT NULL
                      AND estimated_usd > 0
                      AND ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
                    GROUP BY proj
                    ORDER BY 4 DESC
                    """;
                cmd.Parameters.AddWithValue(SqlLit.ParamUserId, summary.UserId);
                cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim());
            cmd.Parameters.AddWithValue("@chargeMult", PageToMovie.Core.Billing.ChargePricing.ClampMultiplier(_billing.ChargeMultiplier));
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var proj = r.GetString(0);
                    var count = r.GetInt32(1);
                    var listSum = r.GetDouble(2);
                    var chargeSum = r.GetDouble(3);
                    summary.ByProject.Add(new ProjectSpendRow
                    {
                        ProjectId = proj,
                        Calls = count,
                        ListUsd = Math.Round(listSum, 4),
                        ChargeUsd = Math.Round(chargeSum, 4),
                    });
                    summary.TotalCalls += count;
                    summary.TotalListUsd += listSum;
                    summary.TotalChargeUsd += chargeSum;
                }
            }

            summary.TotalListUsd = Math.Round(summary.TotalListUsd, 4);
            summary.TotalChargeUsd = Math.Round(summary.TotalChargeUsd, 4);

            // By provider (reuse filter)
            var byProv = await GetApiCostByProviderAsync(summary.UserId, projectId, ct).ConfigureAwait(false);
            summary.ByProvider = byProv.ByProvider
                .OrderByDescending(kv => kv.Value.TotalChargeUsd)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            // By category (user-facing buckets)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT
                        COALESCE(NULLIF(TRIM(category), ''), NULLIF(TRIM(kind), ''), 'other') AS cat,
                        COUNT(*),
                        COALESCE(SUM(estimated_usd), 0),
                        COALESCE(SUM(estimated_usd), 0) * @chargeMult
                    FROM {SqlLit.UserApiCalls}
                    WHERE ok = 1
                      AND user_id = {SqlLit.ParamUserId}
                      AND estimated_usd IS NOT NULL
                      AND estimated_usd > 0
                      AND ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
                    GROUP BY cat
                    """;
                cmd.Parameters.AddWithValue(SqlLit.ParamUserId, summary.UserId);
                cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim());
            cmd.Parameters.AddWithValue("@chargeMult", PageToMovie.Core.Billing.ChargePricing.ClampMultiplier(_billing.ChargeMultiplier));
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var cat = CostCategories.Resolve(r.GetString(0), null, r.GetString(0));
                    var count = r.GetInt32(1);
                    var listSum = r.GetDouble(2);
                    var chargeSum = r.GetDouble(3);
                    summary.ByCategory[cat] = new CategoryCostStats
                    {
                        Category = cat,
                        Count = count,
                        TotalListUsd = Math.Round(listSum, 4),
                        TotalChargeUsd = Math.Round(chargeSum, 4),
                        TotalUsd = Math.Round(chargeSum, 4),
                        AvgUsd = count > 0 ? Math.Round(chargeSum / count, 4) : 0,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetUserSpendSummaryAsync failed for {UserId}", summary.UserId);
        }

        return summary;
    }

    public async Task InsertUserApiCallAsync(ApiCallTelemetry rec, CancellationToken ct = default)
    {
        if (rec is null || string.IsNullOrWhiteSpace(rec.UserId))
            return;

        EnsureDatabaseInitialized();
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {SqlLit.UserApiCalls} (
                    user_id, ts, project_id, job_id, kind, mode, category, provider, model, endpoint,
                    http_status, ok, duration_ms, estimated_usd, charge_usd, charge_multiplier, currency,
                    scene, clip, char_key, resolution, duration_sec,
                    input_tokens, output_tokens, prompt_chars, response_chars,
                    request_id, error, purpose, fakes, attempt, outcome)
                VALUES (
                    {SqlLit.ParamUserId}, @ts, {SqlLit.ParamProjectId}, @jobId, @kind, @mode, @category, @provider, @model, @endpoint,
                    @httpStatus, @ok, @durationMs, @estimatedUsd, @chargeUsd, @chargeMultiplier, @currency,
                    {SqlLit.ParamScene}, {SqlLit.ParamClip}, @charKey, @resolution, @durationSec,
                    @inputTokens, @outputTokens, @promptChars, @responseChars,
                    @requestId, @error, @purpose, @fakes, @attempt, @outcome)";
            var ts = (rec.Ts ?? DateTimeOffset.UtcNow).ToString("o");
            var purpose = CostCategories.Resolve(rec.Kind, rec.Mode, rec.Category);
            if (!string.IsNullOrWhiteSpace(rec.Mode))
                purpose = $"{purpose}:{rec.Mode}";
            cmd.Parameters.AddWithValue(SqlLit.ParamUserId, rec.UserId.Trim());
            cmd.Parameters.AddWithValue("@ts", ts);
            cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, (object?)rec.ProjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@jobId", (object?)rec.JobId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kind", rec.Kind ?? "");
            cmd.Parameters.AddWithValue("@mode", (object?)rec.Mode ?? DBNull.Value);
            var category = CostCategories.Resolve(rec.Kind, rec.Mode, rec.Category);
            cmd.Parameters.AddWithValue("@category", category);
            cmd.Parameters.AddWithValue("@provider", (object?)rec.Provider ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@model", (object?)rec.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@endpoint", (object?)rec.Endpoint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@httpStatus", (object?)rec.HttpStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ok", rec.Ok ? 1 : 0);
            cmd.Parameters.AddWithValue("@durationMs", (object?)rec.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@estimatedUsd", (object?)rec.EstimatedUsd ?? DBNull.Value);
            // List rate only — multiplier is display-time (and optional credit debit), not stored.
            cmd.Parameters.AddWithValue("@chargeUsd", DBNull.Value);
            cmd.Parameters.AddWithValue("@chargeMultiplier", DBNull.Value);
            cmd.Parameters.AddWithValue("@currency", "USD");
            cmd.Parameters.AddWithValue(SqlLit.ParamScene, (object?)rec.Scene ?? DBNull.Value);
            cmd.Parameters.AddWithValue(SqlLit.ParamClip, (object?)rec.Clip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@charKey", (object?)rec.CharKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@resolution", (object?)rec.Resolution ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@durationSec", (object?)rec.DurationSec ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@inputTokens", (object?)rec.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@outputTokens", (object?)rec.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@promptChars", (object?)rec.PromptChars ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@responseChars", (object?)rec.ResponseChars ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@requestId", (object?)rec.RequestId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@error", string.IsNullOrWhiteSpace(rec.Error) ? DBNull.Value : (rec.Error.Length > 500 ? rec.Error[..500] : rec.Error));
            cmd.Parameters.AddWithValue("@purpose", purpose ?? "");
            cmd.Parameters.AddWithValue("@fakes", rec.Fakes ? 1 : 0);
            cmd.Parameters.AddWithValue("@attempt", (object?)rec.Attempt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@outcome", rec.Outcome is { } oc ? oc.ToString() : (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertUserApiCallAsync failed for {UserId}", rec.UserId);
        }
    }

    /// <summary>
    /// Read side of the AI-call feedback loop: aggregates the most recent <paramref name="maxRows"/> rows of
    /// <c>user_api_calls</c> (across all users/projects) into per-op/per-model rollups + raw failure rows, for
    /// <see cref="AiCallAnalyticsService"/> to shape into <see cref="AiCallAnalyticsDto"/>. Replaces the old
    /// per-project JSONL scan now that every telemetry write already lands in this table.
    /// </summary>
    public async Task<AiCallAnalyticsRawData> GetAiCallRawDataAsync(int maxRows = 4000, CancellationToken ct = default)
    {
        var raw = new AiCallAnalyticsRawData();
        EnsureDatabaseInitialized();
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using (var tmp = conn.CreateCommand())
            {
                tmp.CommandText = $"CREATE TEMP TABLE recent_calls AS SELECT * FROM {SqlLit.UserApiCalls} ORDER BY id DESC LIMIT @maxRows;";
                tmp.Parameters.AddWithValue("@maxRows", maxRows);
                await tmp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        COUNT(*),
                        SUM(CASE WHEN ok = 1 AND COALESCE(attempt, 1) <= 1 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN ok = 1 AND attempt > 1 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN ok = 0 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN fakes = 1 THEN 1 ELSE 0 END),
                        COALESCE(SUM(COALESCE(charge_usd, estimated_usd, 0)), 0),
                        COALESCE(AVG(duration_ms), 0),
                        COUNT(DISTINCT project_id)
                    FROM recent_calls
                    """;
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    raw.TotalCalls = r.GetInt32(0);
                    raw.OkCalls = r.GetInt32(1);
                    raw.RetriedCalls = r.GetInt32(2);
                    raw.FailedCalls = r.GetInt32(3);
                    raw.FakeCalls = r.GetInt32(4);
                    raw.TotalCostUsd = r.GetDouble(5);
                    raw.AvgDurationMs = r.GetDouble(6);
                    raw.ProjectsScanned = r.GetInt32(7);
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        LOWER(TRIM(COALESCE(NULLIF(TRIM(kind), ''), '(unknown)'))) AS op,
                        COUNT(*),
                        SUM(CASE WHEN ok = 1 AND attempt > 1 THEN 1 ELSE 0 END),
                        SUM(CASE WHEN ok = 0 THEN 1 ELSE 0 END),
                        COALESCE(SUM(COALESCE(charge_usd, estimated_usd, 0)), 0),
                        COALESCE(AVG(duration_ms), 0)
                    FROM recent_calls
                    GROUP BY op
                    ORDER BY 2 DESC
                    """;
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    raw.Ops.Add(new AiOpStat
                    {
                        Op = r.GetString(0),
                        Calls = r.GetInt32(1),
                        Retried = r.GetInt32(2),
                        Failed = r.GetInt32(3),
                        CostUsd = Math.Round(r.GetDouble(4), 4),
                        AvgDurationMs = Math.Round(r.GetDouble(5), 0),
                    });
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        COALESCE(NULLIF(TRIM(model), ''), '(none)') AS mdl,
                        MAX(COALESCE(provider, '')),
                        COUNT(*),
                        SUM(CASE WHEN ok = 0 THEN 1 ELSE 0 END),
                        COALESCE(SUM(COALESCE(charge_usd, estimated_usd, 0)), 0)
                    FROM recent_calls
                    GROUP BY mdl
                    ORDER BY 3 DESC
                    """;
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    raw.Models.Add(new AiModelStat
                    {
                        Model = r.GetString(0),
                        Provider = r.GetString(1),
                        Calls = r.GetInt32(2),
                        Failed = r.GetInt32(3),
                        CostUsd = Math.Round(r.GetDouble(4), 4),
                    });
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT ts, kind, model, http_status, error, COALESCE(project_id, ''), outcome
                    FROM recent_calls
                    WHERE ok = 0
                    ORDER BY ts DESC
                    LIMIT 400
                    """;
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    DateTimeOffset? ts = r.IsDBNull(0) ? null : DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture);
                    raw.Failures.Add(new AiCallFailureRow
                    {
                        Ts = ts,
                        Kind = r.IsDBNull(1) ? "" : r.GetString(1),
                        Model = r.IsDBNull(2) ? "(none)" : r.GetString(2),
                        HttpStatus = r.IsDBNull(3) ? null : r.GetInt32(3),
                        Error = r.IsDBNull(4) ? null : r.GetString(4),
                        ProjectId = r.GetString(5),
                        Outcome = r.IsDBNull(6) ? "error" : r.GetString(6),
                    });
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(NULLIF(TRIM(mode), ''), 'unspecified') AS reason, COUNT(*)
                    FROM recent_calls
                    WHERE kind = 'style_gate_override'
                    GROUP BY reason
                    ORDER BY 2 DESC
                    """;
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    raw.OverrideReasons.Add(new AiOverrideReasonStat
                    {
                        Reason = r.GetString(0),
                        Count = r.GetInt32(1),
                    });
                }
            }

            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = "DROP TABLE IF EXISTS recent_calls;";
                await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetAiCallRawDataAsync failed");
        }

        return raw;
    }

    /// <summary>
    /// Append one generation-error row (partial coverage / structural gate / transient retry).
    /// Never throws to callers — same swallow-and-warn contract as <see cref="InsertUserApiCallAsync"/>.
    /// Prefer <see cref="GenerationErrorLogger"/> over calling this directly.
    /// </summary>
    public async Task InsertGenerationErrorAsync(GenerationErrorRecord rec, CancellationToken ct = default)
    {
        if (rec is null) return;
        EnsureDatabaseInitialized();
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO generation_errors (
                    ts, user_id, project_id, job_id, scene, clip, stage, provider, model,
                    error_type, error_message, http_status, requested_count, returned_count,
                    missing_ids_json, attempt, resolved, request_summary, response_summary)
                VALUES (
                    @ts, {SqlLit.ParamUserId}, {SqlLit.ParamProjectId}, @jobId, {SqlLit.ParamScene}, {SqlLit.ParamClip}, @stage, @provider, @model,
                    @errorType, @errorMessage, @httpStatus, @requestedCount, @returnedCount,
                    @missingIdsJson, @attempt, @resolved, @requestSummary, @responseSummary)";
            var ts = (rec.Ts ?? DateTimeOffset.UtcNow).ToString("o");
            static string? Trunc500(string? s) => string.IsNullOrEmpty(s) ? s : (s.Length > 500 ? s[..500] : s);
            cmd.Parameters.AddWithValue("@ts", ts);
            cmd.Parameters.AddWithValue(SqlLit.ParamUserId, (object?)rec.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, (object?)rec.ProjectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@jobId", (object?)rec.JobId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(SqlLit.ParamScene, (object?)rec.Scene ?? DBNull.Value);
            cmd.Parameters.AddWithValue(SqlLit.ParamClip, (object?)rec.Clip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@stage", rec.Stage ?? "");
            cmd.Parameters.AddWithValue("@provider", (object?)rec.Provider ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@model", (object?)rec.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@errorType", rec.ErrorType ?? "");
            cmd.Parameters.AddWithValue("@errorMessage", (object?)Trunc500(rec.ErrorMessage) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@httpStatus", (object?)rec.HttpStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@requestedCount", (object?)rec.RequestedCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@returnedCount", (object?)rec.ReturnedCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@missingIdsJson",
                rec.MissingIds is { Count: > 0 } ids ? JsonSerializer.Serialize(ids) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@attempt", rec.Attempt);
            cmd.Parameters.AddWithValue("@resolved", rec.Resolved ? 1 : 0);
            cmd.Parameters.AddWithValue("@requestSummary", (object?)Trunc500(rec.RequestSummary) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@responseSummary", (object?)Trunc500(rec.ResponseSummary) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InsertGenerationErrorAsync failed (stage={Stage})", rec.Stage);
        }
    }

    /// <summary>Admin panel read: recent generation_errors rows, optionally filtered.</summary>
    public async Task<List<GenerationErrorRow>> ListGenerationErrorsAsync(
        string? errorType = null,
        string? projectId = null,
        int take = 100,
        CancellationToken ct = default)
    {
        EnsureDatabaseInitialized();
        var list = new List<GenerationErrorRow>();
        take = Math.Clamp(take, 1, 500);
        try
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT id, ts, user_id, project_id, job_id, scene, clip, stage, provider, model,
                       error_type, error_message, http_status, requested_count, returned_count,
                       missing_ids_json, attempt, resolved, request_summary, response_summary
                FROM generation_errors
                WHERE (@errorType = '' OR error_type = @errorType)
                  AND ({SqlLit.ParamProjectId} = '' OR project_id = {SqlLit.ParamProjectId})
                ORDER BY id DESC
                LIMIT {SqlLit.ParamTake}";
            cmd.Parameters.AddWithValue("@errorType", string.IsNullOrWhiteSpace(errorType) ? "" : errorType.Trim());
            cmd.Parameters.AddWithValue(SqlLit.ParamProjectId, string.IsNullOrWhiteSpace(projectId) ? "" : projectId.Trim());
            cmd.Parameters.AddWithValue(SqlLit.ParamTake, take);
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(ReadGenerationErrorRow(r));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ListGenerationErrorsAsync failed");
        }
        return list;
    }

    private static GenerationErrorRow ReadGenerationErrorRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Ts = r.GetString(1),
        UserId = DbString(r, 2),
        ProjectId = DbString(r, 3),
        JobId = DbString(r, 4),
        Scene = DbInt32(r, 5),
        Clip = DbInt32(r, 6),
        Stage = r.GetString(7),
        Provider = DbString(r, 8),
        Model = DbString(r, 9),
        ErrorType = r.GetString(10),
        ErrorMessage = DbString(r, 11),
        HttpStatus = DbInt32(r, 12),
        RequestedCount = DbInt32(r, 13),
        ReturnedCount = DbInt32(r, 14),
        MissingIdsJson = DbString(r, 15),
        Attempt = r.GetInt32(16),
        Resolved = r.GetInt32(17) != 0,
        RequestSummary = DbString(r, 18),
        ResponseSummary = DbString(r, 19),
    };

    public async Task<string?> GetDecryptedXaiApiKeyAsync(string userId, CancellationToken ct = default) =>
        await GetDecryptedProviderApiKeyAsync(userId, "grok", ct).ConfigureAwait(false);

    public async Task<string?> GetDecryptedProviderApiKeyAsync(string userId, string providerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(providerId)) return null;
        var normId = NormalizeProvider(providerId);

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT encrypted_api_key FROM user_api_keys WHERE user_id = {SqlLit.ParamUserId} AND provider_id = @providerId";
        cmd.Parameters.AddWithValue(SqlLit.ParamUserId, userId.Trim());
        cmd.Parameters.AddWithValue("@providerId", normId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is string encrypted && !string.IsNullOrWhiteSpace(encrypted))
        {
            try
            {
                return DecryptApiKey(encrypted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetDecryptedProviderApiKeyAsync failed for {UserId}/{Provider}", userId, providerId);
            }
        }

        // Fallback check against legacy columns for backward compatibility if user_api_keys table row was deleted
        var user = await GetUserByIdAsync(userId, ct).ConfigureAwait(false);
        if (user is not null)
        {
            var legacyEncrypted = GetEncryptedFromEntity(user, providerId);
            if (!string.IsNullOrWhiteSpace(legacyEncrypted))
            {
                try { return DecryptApiKey(legacyEncrypted); }
                catch (Exception)
                {
                    // Legacy ciphertext from a rotated data-protection key cannot be recovered.
                    return null;
                }
            }
        }

        return null;
    }

    public async Task<UserSettingsDto> GetUserSettingsDtoAsync(string userId, CancellationToken ct = default)
    {
        var user = await GetUserByIdAsync(userId, ct).ConfigureAwait(false);
        var username = user?.Username ?? userId;

        // Fetch all encrypted keys for this user from user_api_keys table
        var personalKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = new SqliteConnection(ConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT provider_id, encrypted_api_key FROM user_api_keys WHERE user_id = {SqlLit.ParamUserId}";
            cmd.Parameters.AddWithValue(SqlLit.ParamUserId, userId.Trim());
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var pid = reader.GetString(0);
                var enc = reader.GetString(1);
                var plain = DecryptOptional(enc);
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    personalKeys[pid] = plain;
                }
            }
        }

        // Fallback for legacy columns if user_api_keys table hasn't populated them yet
        if (!personalKeys.ContainsKey("grok") && DecryptOptional(user?.EncryptedXaiApiKey) is { } x) personalKeys["grok"] = x;
        if (!personalKeys.ContainsKey(ProviderGemini) && DecryptOptional(user?.EncryptedGeminiApiKey) is { } g) personalKeys[ProviderGemini] = g;
        if (!personalKeys.ContainsKey(ProviderAnthropic) && DecryptOptional(user?.EncryptedAnthropicApiKey) is { } a) personalKeys[ProviderAnthropic] = a;
        if (!personalKeys.ContainsKey("fal") && DecryptOptional(user?.EncryptedFalApiKey) is { } f) personalKeys["fal"] = f;

        // Dynamically discover providers from models_catalog.json (enabled models + requiredEnvKeys).
        var providers = SupportedModelCatalog.BuildProviderKeyRows();
        foreach (var row in providers)
        {
            var pId = NormalizeProvider(row.ProviderId);
            row.ProviderId = pId;

            // Fake test vendor (fakes mode only): key-free but always "configured", so its jobs read
            // ready — no "Need key", no add-key panel. Never present in real mode.
            if (string.Equals(pId, "fake", StringComparison.OrdinalIgnoreCase)
                && SupportedModelCatalog.FakeCatalogEnabled())
            {
                row.HasPersonalKey = true;
                row.MaskedPersonalKey = "fake";
                row.HasServerKey = true;
                row.ActiveSource = "fake";
                continue;
            }

            personalKeys.TryGetValue(pId, out var personal);
            var hasPersonal = !string.IsNullOrWhiteSpace(personal);
            var hasServer = row.RequiredEnvKeys.Any(EnvPresent);
            row.HasPersonalKey = hasPersonal;
            row.MaskedPersonalKey = MaskKey(personal);
            row.HasServerKey = hasServer;
            // BYOK: "Active" means personal key only; server env is shown but not active spend.
            row.ActiveSource = hasPersonal ? "personal" : "none";
        }

        return new UserSettingsDto
        {
            UserId = user?.UserId ?? userId,
            Username = username,
            Providers = providers,
        };
    }

    public async Task InsertUserAsync(UserEntity user, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {SqlLit.Users} (
                user_id, username, password_hash,
                encrypted_xai_api_key, encrypted_gemini_api_key, encrypted_anthropic_api_key, encrypted_fal_api_key,
                role, created_at, last_login_at,
                credits_balance_usd, credits_lifetime_granted_usd, credits_lifetime_used_usd,
                is_disabled, email, email_confirmed_at)
            VALUES (@id, {SqlLit.ParamName}, @hash, @xai, @gemini, @anthropic, @fal, @role, {SqlLit.ParamCreated}, @login,
                    @bal, @granted, @used, @disabled, {SqlLit.ParamEmail}, @email_confirmed)
            ON CONFLICT(user_id) DO UPDATE SET
                username = excluded.username,
                encrypted_xai_api_key = COALESCE(excluded.encrypted_xai_api_key, {SqlLit.Users}.encrypted_xai_api_key),
                encrypted_gemini_api_key = COALESCE(excluded.encrypted_gemini_api_key, {SqlLit.Users}.encrypted_gemini_api_key),
                encrypted_anthropic_api_key = COALESCE(excluded.encrypted_anthropic_api_key, {SqlLit.Users}.encrypted_anthropic_api_key),
                encrypted_fal_api_key = COALESCE(excluded.encrypted_fal_api_key, {SqlLit.Users}.encrypted_fal_api_key);
        ";
        cmd.Parameters.AddWithValue("@id", user.UserId);
        cmd.Parameters.AddWithValue(SqlLit.ParamName, user.Username);
        cmd.Parameters.AddWithValue("@hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("@xai", (object?)user.EncryptedXaiApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gemini", (object?)user.EncryptedGeminiApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@anthropic", (object?)user.EncryptedAnthropicApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fal", (object?)user.EncryptedFalApiKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", user.Role);
        cmd.Parameters.AddWithValue(SqlLit.ParamCreated, user.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@login", (object?)user.LastLoginAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bal", user.CreditsBalanceUsd);
        cmd.Parameters.AddWithValue("@granted", user.CreditsLifetimeGrantedUsd);
        cmd.Parameters.AddWithValue("@used", user.CreditsLifetimeUsedUsd);
        cmd.Parameters.AddWithValue("@disabled", user.IsDisabled ? 1 : 0);
        cmd.Parameters.AddWithValue(SqlLit.ParamEmail, (object?)NormalizeEmail(user.Email) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@email_confirmed",
            (object?)user.EmailConfirmedAt?.ToString("o") ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>True when the account exists and is admin-disabled.</summary>
    public async Task<bool> IsUserDisabledAsync(string? userIdOrName, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(userIdOrName ?? "", ct).ConfigureAwait(false);
        return user?.IsDisabled == true;
    }

    /// <summary>
    /// Enable or disable an account. Returns null when user not found.
    /// Caller must enforce self-disable and last-admin rules.
    /// </summary>
    public async Task<UserCreditSummaryDto?> SetUserDisabledAsync(
        string userId,
        bool disabled,
        CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return null;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {SqlLit.Users} SET is_disabled = @d WHERE user_id = @id";
        cmd.Parameters.AddWithValue("@d", disabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", user.UserId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        user.IsDisabled = disabled;
        return ToCreditSummary(user);
    }

    /// <summary>Count non-disabled accounts with Role = Admin.</summary>
    public async Task<int> CountActiveAdminsAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COUNT(*) FROM {SqlLit.Users}
            WHERE LOWER(role) = 'admin'
              AND COALESCE(is_disabled, 0) = 0";
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar ?? 0);
    }

    /// <summary>
    /// Hard-delete user row + credit ledger. Does not touch projects/demos (API orchestrates those).
    /// </summary>
    public async Task<bool> HardDeleteUserAsync(string userId, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(userId, ct).ConfigureAwait(false);
        if (user is null) return false;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = (SqliteTransaction)await conn
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        using (var delLedger = conn.CreateCommand())
        {
            delLedger.Transaction = tx;
            delLedger.CommandText = "DELETE FROM credit_ledger WHERE user_id = @id OR LOWER(user_id) = LOWER(@id)";
            delLedger.Parameters.AddWithValue("@id", user.UserId);
            await delLedger.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var delUser = conn.CreateCommand())
        {
            delUser.Transaction = tx;
            delUser.CommandText = $"DELETE FROM {SqlLit.Users} WHERE user_id = @id";
            delUser.Parameters.AddWithValue("@id", user.UserId);
            var rows = await delUser.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Hard-deleted user {UserId} ({Username})", user.UserId, user.Username);
        return true;
    }

    /// <summary>True when password matches the stored hash for this user.</summary>
    public static bool VerifyPasswordHash(UserEntity user, string password)
    {
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
            return false;
        var hash = HashPassword(password ?? "");
        return string.Equals(user.PasswordHash, hash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Marks a password-reset request if the account exists. Does not reveal whether it exists.
    /// </summary>
    public async Task NotePasswordResetRequestedAsync(string usernameOrId, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(usernameOrId, ct).ConfigureAwait(false);
        if (user is null || user.IsDisabled) return;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {SqlLit.Users} SET password_reset_requested_at = @t WHERE user_id = @id";
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", user.UserId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Password reset requested for user {UserId}", user.UserId);
    }

    /// <summary>Sets a new password hash and clears any forgot-password marker.</summary>
    public async Task<bool> SetPasswordAsync(string userId, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        newPassword ??= "";
        if (newPassword.Length < 4) return false;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {SqlLit.Users}
            SET password_hash = @hash,
                password_reset_requested_at = NULL
            WHERE user_id = @id";
        cmd.Parameters.AddWithValue("@hash", HashPassword(newPassword));
        cmd.Parameters.AddWithValue("@id", userId.Trim());
        var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (n > 0)
            _logger.LogInformation("Password set for user {UserId}", userId.Trim());
        return n > 0;
    }

    public async Task<Dictionary<string, DateTimeOffset>> GetPasswordResetRequestedMapAsync(
        CancellationToken ct = default)
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT user_id, password_reset_requested_at
            FROM {SqlLit.Users}
            WHERE password_reset_requested_at IS NOT NULL
              AND TRIM(password_reset_requested_at) != ''";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var raw = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                map[id] = when;
        }
        return map;
    }

    // ── Credits ──────────────────────────────────────────────────────────────

    public async Task<List<UserCreditSummaryDto>> ListUserCreditSummariesAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = UserSelectOrderedByUsernameSql;

        var list = new List<UserCreditSummaryDto>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ToCreditSummary(ReadUserFromReader(reader)));

        var resets = await GetPasswordResetRequestedMapAsync(ct).ConfigureAwait(false);
        foreach (var u in list)
        {
            if (resets.TryGetValue(u.UserId, out var when))
                u.PasswordResetRequestedAt = when;
        }
        return list;
    }

    public async Task<UserCreditSummaryDto?> GetUserCreditSummaryAsync(string userId, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(userId, ct).ConfigureAwait(false);
        return user is null ? null : ToCreditSummary(user);
    }

    public async Task<AdminCreditsOverviewDto> GetAdminCreditsOverviewAsync(
        int recentLedger = 40,
        CancellationToken ct = default)
    {
        var users = await ListUserCreditSummariesAsync(ct).ConfigureAwait(false);
        var ledger = await GetRecentCreditLedgerAsync(Math.Clamp(recentLedger, 1, 200), ct).ConfigureAwait(false);

        return new AdminCreditsOverviewDto
        {
            UserCount = users.Count,
            TotalBalanceUsd = users.Sum(u => u.CreditsBalanceUsd),
            TotalGrantedUsd = users.Sum(u => u.CreditsLifetimeGrantedUsd),
            TotalUsedUsd = users.Sum(u => u.CreditsLifetimeUsedUsd),
            Users = users,
            RecentLedger = ledger,
            UsdPerCredit = CreditUnits.UsdPerCredit,
        };
    }

    public async Task<List<CreditLedgerEntryDto>> GetRecentCreditLedgerAsync(
        int take = 40,
        CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT id, user_id, ts, kind, amount_usd, balance_after_usd, project_id, note, meta_kind
            FROM credit_ledger
            ORDER BY id DESC
            LIMIT {SqlLit.ParamTake}";
        cmd.Parameters.AddWithValue(SqlLit.ParamTake, Math.Clamp(take, 1, 500));

        var list = new List<CreditLedgerEntryDto>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadLedgerEntry(reader));
        return list;
    }

    /// <summary>
    /// Per-user in-process gate so concurrent ASP.NET requests for the same user
    /// cannot race read-modify-write even under SQLite deferred transactions.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        CreditLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Atomically apply a credit delta. Positive = grant, negative = debit/claw-back.
    /// Updates balance + lifetime counters and appends a ledger row.
    /// Uses BEGIN IMMEDIATE + SQL relative UPDATE so concurrent debits/grants cannot lose updates.
    /// </summary>
    public async Task<UserCreditSummaryDto?> ApplyCreditDeltaAsync(
        string userId,
        double amountUsd,
        string kind,
        string? note,
        string? metaKind,
        string? projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        // Round to 4 decimal places (cents of a cent) for stable math.
        amountUsd = Math.Round(amountUsd, 4, MidpointRounding.AwayFromZero);
        if (Math.Abs(amountUsd) < 0.00005)
            return await GetUserCreditSummaryAsync(userId, ct).ConfigureAwait(false);

        var lockKey = userId.Trim().ToLowerInvariant();
        var gate = CreditLocks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyCreditDeltaCoreAsync(userId, amountUsd, kind, note, metaKind, projectId, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<UserCreditSummaryDto?> ApplyCreditDeltaCoreAsync(
        string userId,
        double amountUsd,
        string kind,
        string? note,
        string? metaKind,
        string? projectId,
        CancellationToken ct)
    {
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // IMMEDIATE locks the DB for write at begin — prevents concurrent deferred txs
        // from both reading the same balance and losing an update.
        using var tx = (SqliteTransaction)await conn
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        string resolvedUserId;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText =
                $"SELECT user_id FROM {SqlLit.Users} WHERE user_id = @id OR LOWER(username) = LOWER(@id) LIMIT 1";
            find.Parameters.AddWithValue("@id", userId.Trim());
            var found = await find.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (found is null || found is DBNull)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }
            resolvedUserId = Convert.ToString(found) ?? userId.Trim();
        }

        var grantDelta = amountUsd > 0 ? amountUsd : 0d;
        var usedDelta = amountUsd < 0 ? Math.Abs(amountUsd) : 0d;

        // Relative UPDATE so the column math happens inside the write lock.
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $@"
                UPDATE {SqlLit.Users} SET
                    credits_balance_usd = ROUND(credits_balance_usd + @amt, 4),
                    credits_lifetime_granted_usd = ROUND(credits_lifetime_granted_usd + @grant, 4),
                    credits_lifetime_used_usd = ROUND(credits_lifetime_used_usd + @used, 4)
                WHERE user_id = @id";
            upd.Parameters.AddWithValue("@amt", amountUsd);
            upd.Parameters.AddWithValue("@grant", grantDelta);
            upd.Parameters.AddWithValue("@used", usedDelta);
            upd.Parameters.AddWithValue("@id", resolvedUserId);
            var n = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (n == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }
        }

        UserEntity user;
        using (var reload = conn.CreateCommand())
        {
            reload.Transaction = tx;
            reload.CommandText = UserSelectByIdSql;
            reload.Parameters.AddWithValue("@id", resolvedUserId);
            using var reader = await reload.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }
            user = ReadUserFromReader(reader);
        }

        var ts = DateTimeOffset.UtcNow;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = @"
                INSERT INTO credit_ledger
                    (user_id, ts, kind, amount_usd, balance_after_usd, project_id, note, meta_kind)
                VALUES (@uid, @ts, @kind, @amt, @bal, @proj, @note, @meta)";
            ins.Parameters.AddWithValue("@uid", user.UserId);
            ins.Parameters.AddWithValue("@ts", ts.ToString("o"));
            ins.Parameters.AddWithValue("@kind", string.IsNullOrWhiteSpace(kind) ? "adjust" : kind.Trim());
            ins.Parameters.AddWithValue("@amt", amountUsd);
            ins.Parameters.AddWithValue("@bal", user.CreditsBalanceUsd);
            ins.Parameters.AddWithValue("@proj", (object?)projectId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
            ins.Parameters.AddWithValue("@meta", (object?)metaKind ?? DBNull.Value);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ToCreditSummary(user);
    }

    private static UserCreditSummaryDto ToCreditSummary(UserEntity u) => new()
    {
        UserId = u.UserId,
        Username = u.Username,
        Role = u.Role,
        CreatedAt = u.CreatedAt,
        LastLoginAt = u.LastLoginAt,
        HasXaiApiKey = !string.IsNullOrWhiteSpace(u.EncryptedXaiApiKey),
        IsDisabled = u.IsDisabled,
        CreditsBalanceUsd = u.CreditsBalanceUsd,
        CreditsLifetimeGrantedUsd = u.CreditsLifetimeGrantedUsd,
        CreditsLifetimeUsedUsd = u.CreditsLifetimeUsedUsd,
    };

    private static CreditLedgerEntryDto ReadLedgerEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        UserId = reader.GetString(1),
        Ts = DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTimeOffset.UtcNow,
        Kind = reader.GetString(3),
        AmountUsd = reader.GetDouble(4),
        BalanceAfterUsd = reader.GetDouble(5),
        ProjectId = reader.IsDBNull(6) ? null : reader.GetString(6),
        Note = reader.IsDBNull(7) ? null : reader.GetString(7),
        MetaKind = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private string? DecryptOptional(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try
        {
            return DecryptApiKey(encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DecryptOptional failed — treating personal key as missing");
            return null;
        }
    }

    private static bool EnvPresent(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static string? MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key.Length > 8)
            return key.Substring(0, 4) + "..." + key.Substring(key.Length - 4);
        return "****";
    }

    private static ProviderKeyStatusDto BuildProviderStatus(
        string providerId,
        string displayName,
        string family,
        string? personal,
        bool hasServer,
        bool supportsVideoGen,
        bool supportsVideoReview,
        bool supportsImageGen,
        bool supportsScriptPlanning,
        bool supportsImageVision,
        string? notes)
    {
        var hasPersonal = !string.IsNullOrWhiteSpace(personal);
        var caps = new List<string>();
        if (supportsVideoGen) caps.Add("Video Gen");
        if (supportsVideoReview) caps.Add("Video Review");
        if (supportsImageGen) caps.Add("Image Gen");
        if (supportsScriptPlanning) caps.Add("Script & Planning");
        if (supportsImageVision) caps.Add("Image Vision / OCR");
        if (caps.Count == 0) caps.Add("—");

        return new ProviderKeyStatusDto
        {
            ProviderId = providerId,
            DisplayName = displayName,
            Family = family,
            HasPersonalKey = hasPersonal,
            MaskedPersonalKey = MaskKey(personal),
            HasServerKey = hasServer,
            ActiveSource = hasPersonal ? "personal" : hasServer ? "server" : "none",
            CapabilitiesSummary = string.Join(", ", caps),
            SupportsVideo = supportsVideoGen || supportsVideoReview,
            SupportsImage = supportsImageGen,
            SupportsChat = supportsScriptPlanning,
            SupportsVision = supportsImageVision,
            SupportsVideoGen = supportsVideoGen,
            SupportsVideoReview = supportsVideoReview,
            SupportsImageGen = supportsImageGen,
            SupportsScriptPlanning = supportsScriptPlanning,
            SupportsImageVision = supportsImageVision,
            Notes = notes,
        };
    }

    private static string? ProviderColumn(string providerId) =>
        NormalizeProvider(providerId) switch
        {
            "grok" => "encrypted_xai_api_key",
            ProviderGemini => "encrypted_gemini_api_key",
            ProviderAnthropic => "encrypted_anthropic_api_key",
            "fal" => "encrypted_fal_api_key",
            _ => null,
        };

    private static string NormalizeProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return "";
        var p = providerId.Trim().ToLowerInvariant();
        return p switch
        {
            "xai" or "grok" => "grok",
            "google" or ProviderGemini => ProviderGemini,
            "claude" or ProviderAnthropic => ProviderAnthropic,
            "fal" or "fal.ai" => "fal",
            "openai" or "oai" => "openai",
            "suno" => "suno",
            "aimusicapi" or "ai-music-api" => "aimusicapi",
            "elevenlabs" or "eleven" => "elevenlabs",
            _ => p,
        };
    }

    private static string? GetEncryptedFromEntity(UserEntity user, string providerId) =>
        NormalizeProvider(providerId) switch
        {
            "grok" => user.EncryptedXaiApiKey,
            ProviderGemini => user.EncryptedGeminiApiKey,
            ProviderAnthropic => user.EncryptedAnthropicApiKey,
            "fal" => user.EncryptedFalApiKey,
            _ => null,
        };

    private static void SetEncryptedOnEntity(UserEntity user, string providerId, string? encrypted)
    {
        switch (NormalizeProvider(providerId))
        {
            case "grok": user.EncryptedXaiApiKey = encrypted; break;
            case ProviderGemini: user.EncryptedGeminiApiKey = encrypted; break;
            case ProviderAnthropic: user.EncryptedAnthropicApiKey = encrypted; break;
        }
    }

    private string EncryptApiKey(string plainText)
    {
        if (_protector != null)
            return _protector.Protect(plainText);

        return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
    }

    private string DecryptApiKey(string cipherText)
    {
        if (cipherText.StartsWith("plain:"))
        {
            var raw = cipherText.Substring(6);
            return Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        }

        if (_protector is null)
        {
            // No protector: only accept plain: payloads (dev). Never return opaque ciphertext as a key.
            throw new InvalidOperationException(
                "Cannot decrypt personal API key (DataProtection not configured). Re-save the key in Configuration.");
        }

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (Exception ex)
        {
            // Common on Railway after redeploy without a Volume on /data: DP keys rotate and
            // stored ciphertexts become unreadable. Returning ciphertext as the API key caused
            // "Key Active" in UI with 401s on xAI. Treat as missing instead.
            _logger.LogWarning(ex,
                "Failed to decrypt API key with DataProtector — re-save the key in Configuration " +
                "(and mount a Railway Volume at /data so keys survive restarts)");
            throw new InvalidOperationException(
                "Personal API key could not be decrypted (encryption keys changed after redeploy). " +
                "Open Configuration, re-save your xAI / Grok key. Mount a Railway Volume at /data " +
                "so the key and data-protection store persist.", ex);
        }
    }

    public static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password + "PageToMovieSalt"));
        return Convert.ToBase64String(bytes);
    }

    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        return email.Trim().ToLowerInvariant();
    }

    public static bool IsValidEmail(string? email)
    {
        var e = NormalizeEmail(email);
        if (e is null || e.Length < 5 || e.Length > 254) return false;
        var at = e.IndexOf('@');
        if (at <= 0 || at != e.LastIndexOf('@')) return false;
        var domain = e[(at + 1)..];
        return domain.Contains('.') && !e.Contains(' ');
    }

    /// <summary>Legacy accounts with no email are treated as confirmed.</summary>
    public static bool IsEmailConfirmed(UserEntity? user)
    {
        if (user is null) return false;
        if (string.IsNullOrWhiteSpace(user.Email)) return true;
        return user.EmailConfirmedAt is not null;
    }

    public async Task<UserEntity?> GetUserByEmailAsync(string email, CancellationToken ct = default)
    {
        var e = NormalizeEmail(email);
        if (e is null) return null;
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = UserSelectByEmailSql;
        cmd.Parameters.AddWithValue("@e", e);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ReadUserFromReader(reader);
        return null;
    }

    /// <summary>Creates a single-use token; returns the raw token (email to the user). Stores only a hash.</summary>
    public async Task<string> CreateAuthTokenAsync(
        string userId,
        string purpose,
        TimeSpan lifetime,
        CancellationToken ct = default)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = HashToken(raw);
        var now = DateTimeOffset.UtcNow;
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        // Invalidate previous unused tokens of same purpose for this user
        using (var clear = conn.CreateCommand())
        {
            clear.CommandText = @"
                DELETE FROM auth_tokens
                WHERE user_id = @u AND purpose = @p AND used_at IS NULL";
            clear.Parameters.AddWithValue("@u", userId.Trim());
            clear.Parameters.AddWithValue("@p", purpose);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO auth_tokens (token_hash, user_id, purpose, expires_at, created_at)
            VALUES (@h, @u, @p, @exp, @c)";
        cmd.Parameters.AddWithValue("@h", hash);
        cmd.Parameters.AddWithValue("@u", userId.Trim());
        cmd.Parameters.AddWithValue("@p", purpose);
        cmd.Parameters.AddWithValue("@exp", (now + lifetime).ToString("o"));
        cmd.Parameters.AddWithValue("@c", now.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return raw;
    }

    /// <summary>Validates and consumes a token. Returns user_id or null.</summary>
    public async Task<string?> ConsumeAuthTokenAsync(
        string rawToken,
        string purpose,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = HashToken(rawToken.Trim());
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        string? userId = null;
        string? expRaw = null;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = @"
                SELECT user_id, expires_at, used_at FROM auth_tokens
                WHERE token_hash = @h AND purpose = @p LIMIT 1";
            sel.Parameters.AddWithValue("@h", hash);
            sel.Parameters.AddWithValue("@p", purpose);
            using var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null;
            }
            userId = r.GetString(0);
            expRaw = r.GetString(1);
            if (!r.IsDBNull(2))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return null; // already used
            }
        }
        if (!DateTimeOffset.TryParse(expRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var exp) || exp < DateTimeOffset.UtcNow)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE auth_tokens SET used_at = @t WHERE token_hash = @h";
            upd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("o"));
            upd.Parameters.AddWithValue("@h", hash);
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return userId;
    }

    /// <summary>Finds user_id associated with a token hash regardless of whether it has been consumed.</summary>
    public async Task<string?> GetUserIdFromAuthTokenHashAsync(
        string rawToken,
        string purpose,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = HashToken(rawToken.Trim());
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var sel = conn.CreateCommand();
        sel.CommandText = @"
            SELECT user_id FROM auth_tokens
            WHERE token_hash = @h AND purpose = @p LIMIT 1";
        sel.Parameters.AddWithValue("@h", hash);
        sel.Parameters.AddWithValue("@p", purpose);
        var obj = await sel.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return obj is string uid && !string.IsNullOrWhiteSpace(uid) ? uid : null;
    }

    public async Task<bool> ConfirmEmailAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {SqlLit.Users} SET email_confirmed_at = @t WHERE LOWER(user_id) = LOWER(@id) OR user_id = @id";
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", userId.Trim());
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Gets the user's active project preference from SQLite.</summary>
    public async Task<string?> GetUserActiveProjectAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT active_project_id FROM {SqlLit.Users} WHERE LOWER(user_id) = LOWER(@id) OR user_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", userId.Trim());
        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return obj is string pid && !string.IsNullOrWhiteSpace(pid) ? pid.Trim() : null;
    }

    /// <summary>Sets the user's active project preference in SQLite.</summary>
    public async Task SetUserActiveProjectAsync(string userId, string? projectId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {SqlLit.Users} SET active_project_id = @pid WHERE LOWER(user_id) = LOWER(@id) OR user_id = @id";
        cmd.Parameters.AddWithValue("@pid", (object?)projectId?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", userId.Trim());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> AcceptTermsAsync(string userId, string termsVersion = "1.0", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var trimmed = userId.Trim();
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {SqlLit.Users} (user_id, username, password_hash, created_at, terms_accepted_at, terms_version)
            VALUES (@id, {SqlLit.ParamName}, '', @t, @t, @v)
            ON CONFLICT(user_id) DO UPDATE SET terms_accepted_at = @t, terms_version = @v;";
        cmd.Parameters.AddWithValue("@id", trimmed);
        cmd.Parameters.AddWithValue(SqlLit.ParamName, trimmed);
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@v", termsVersion);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> HasAcceptedTermsAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT terms_accepted_at FROM {SqlLit.Users} WHERE user_id = @id";
        cmd.Parameters.AddWithValue("@id", userId.Trim());
        var val = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return val != null && val != DBNull.Value && !string.IsNullOrWhiteSpace(val.ToString());
    }

    public static string HashToken(string raw)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes("ptm-token:" + raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static UserEntity ReadUserFromReader(SqliteDataReader reader)
    {
        // 0 id, 1 name, 2 hash, 3 xai, 4 gemini, 5 anthropic, 6 fal, 7 role, 8 created, 9 login,
        // 10 balance, 11 granted, 12 used, 13 is_disabled, 14 email, 15 email_confirmed_at
        DateTimeOffset? confirmed = null;
        if (reader.FieldCount > 15 && !reader.IsDBNull(15))
        {
            var raw = reader.GetString(15);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c)) confirmed = c;
        }
        return new UserEntity
        {
            UserId = reader.GetString(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2),
            EncryptedXaiApiKey = reader.IsDBNull(3) ? null : reader.GetString(3),
            EncryptedGeminiApiKey = reader.IsDBNull(4) ? null : reader.GetString(4),
            EncryptedAnthropicApiKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            EncryptedFalApiKey = reader.IsDBNull(6) ? null : reader.GetString(6),
            Role = reader.GetString(7),
            CreatedAt = DateTime.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow,
            LastLoginAt = reader.IsDBNull(9) ? null : (DateTime.TryParse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ldt) ? ldt : null),
            CreditsBalanceUsd = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetDouble(10) : 0,
            CreditsLifetimeGrantedUsd = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetDouble(11) : 0,
            CreditsLifetimeUsedUsd = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetDouble(12) : 0,
            IsDisabled = reader.FieldCount > 13 && !reader.IsDBNull(13) && reader.GetInt64(13) != 0,
            Email = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
            EmailConfirmedAt = confirmed,
        };
    }
}

/// <summary>One row from user_api_calls (list-rate cost attribution).</summary>
public sealed class UserApiCallRow
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Ts { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? JobId { get; set; }
    public string Kind { get; set; } = "";
    public string? Mode { get; set; }
    /// <summary>User-facing cost bucket (<see cref="CostCategories"/>).</summary>
    public string Category { get; set; } = CostCategories.Other;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Endpoint { get; set; }
    public int? HttpStatus { get; set; }
    public bool Ok { get; set; }
    public long? DurationMs { get; set; }
    public double? EstimatedUsd { get; set; }
    public string Currency { get; set; } = "USD";
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? CharKey { get; set; }
    public string? Resolution { get; set; }
    public double? DurationSec { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public string? Purpose { get; set; }
    public string? Error { get; set; }
    public bool Fakes { get; set; }
}

/// <summary>One row from generation_errors (admin panel read model). See <see cref="GenerationErrorRecord"/> for the write side.</summary>
public sealed class GenerationErrorRow
{
    public long Id { get; set; }
    public string Ts { get; set; } = "";
    public string? UserId { get; set; }
    public string? ProjectId { get; set; }
    public string? JobId { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string Stage { get; set; } = "";
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string ErrorType { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public int? HttpStatus { get; set; }
    public int? RequestedCount { get; set; }
    public int? ReturnedCount { get; set; }
    public string? MissingIdsJson { get; set; }
    public int Attempt { get; set; }
    public bool Resolved { get; set; }
    public string? RequestSummary { get; set; }
    public string? ResponseSummary { get; set; }
}


/// <summary>Portfolio / user API spend aggregates for cost estimate refinement (list rates).</summary>
public sealed class ApiCostHistoryStats
{
    public int TotalCalls { get; set; }
    /// <summary>List-rate sum (COGS / refinement). Not customer charge.</summary>
    public double TotalUsd { get; set; }
    public Dictionary<string, CategoryCostStats> ByCategory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CategoryCostStats
{
    public string Category { get; set; } = CostCategories.Other;
    public int Count { get; set; }
    /// <summary>Customer charge (or list rate when charge not tracked). Prefer <see cref="TotalChargeUsd"/>.</summary>
    public double TotalUsd { get; set; }
    public double TotalListUsd { get; set; }
    public double TotalChargeUsd { get; set; }
    public double AvgUsd { get; set; }
}

/// <summary>Spend grouped by vendor/provider (xAI, Google, ElevenLabs, …).</summary>
public sealed class ApiCostByProviderStats
{
    public int TotalCalls { get; set; }
    /// <summary>Customer charge total (default for UI).</summary>
    public double TotalUsd { get; set; }
    public double TotalListUsd { get; set; }
    public double TotalChargeUsd { get; set; }
    public Dictionary<string, ProviderCostStats> ByProvider { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderCostStats
{
    public string Provider { get; set; } = "unknown";
    public int Count { get; set; }
    public double TotalUsd { get; set; }
    public double TotalListUsd { get; set; }
    public double TotalChargeUsd { get; set; }
    public Dictionary<string, CategoryCostStats> ByCategory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Signed-in user spend: total, per project, per vendor, per category.</summary>
public sealed class UserSpendSummary
{
    public string UserId { get; set; } = "";
    public int TotalCalls { get; set; }
    public double TotalListUsd { get; set; }
    public double TotalChargeUsd { get; set; }
    public List<ProjectSpendRow> ByProject { get; set; } = new();
    public Dictionary<string, ProviderCostStats> ByProvider { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CategoryCostStats> ByCategory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProjectSpendRow
{
    public string ProjectId { get; set; } = "";
    public int Calls { get; set; }
    public double ListUsd { get; set; }
    public double ChargeUsd { get; set; }
}

/// <summary>
/// Unshaped aggregates from <see cref="UserDatabaseService.GetAiCallRawDataAsync"/> — <see cref="AiCallAnalyticsService"/>
/// turns this into <see cref="AiCallAnalyticsDto"/> (failure classification, learnings, window note).
/// </summary>
public sealed class AiCallAnalyticsRawData
{
    public int ProjectsScanned { get; set; }
    public int TotalCalls { get; set; }
    public int OkCalls { get; set; }
    public int RetriedCalls { get; set; }
    public int FailedCalls { get; set; }
    public int FakeCalls { get; set; }
    public double TotalCostUsd { get; set; }
    public double AvgDurationMs { get; set; }
    public List<AiOpStat> Ops { get; set; } = new();
    public List<AiModelStat> Models { get; set; } = new();
    public List<AiCallFailureRow> Failures { get; set; } = new();
    public List<AiOverrideReasonStat> OverrideReasons { get; set; } = new();
}

/// <summary>One failed call, pre-classification — <see cref="AiCallAnalyticsService"/> applies <c>ClassifyFailure</c>.</summary>
public sealed class AiCallFailureRow
{
    public DateTimeOffset? Ts { get; set; }
    public string Kind { get; set; } = "";
    public string Model { get; set; } = "";
    public int? HttpStatus { get; set; }
    public string? Error { get; set; }
    public string ProjectId { get; set; } = "";
    /// <summary>The canonical outcome (AiCallOutcome.ToString()), set at write time — see ProjectTelemetryService.LogApiCallAsync.</summary>
    public string Outcome { get; set; } = "";
}
