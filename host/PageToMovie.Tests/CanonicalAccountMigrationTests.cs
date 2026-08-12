using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public class CanonicalAccountMigrationTests
{
    [Fact]
    public void V6_merges_alias_account_spend_keys_and_projects_onto_budcribar()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm-v6-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(root, "data");
        var projectsDir = Path.Combine(root, "projects");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(projectsDir);

        // Alias project folder (email-shaped owner)
        var aliasProject = Path.Combine(projectsDir, "budcribarmsn_com", "Mary");
        Directory.CreateDirectory(aliasProject);
        File.WriteAllText(Path.Combine(aliasProject, "project.json"),
            """{"id":"budcribarmsn_com/Mary","title":"Mary","ownerUserId":"budcribarmsn.com"}""");

        // Primary-owned project already correct
        var primaryProject = Path.Combine(projectsDir, "budcribar", "Buster");
        Directory.CreateDirectory(primaryProject);
        File.WriteAllText(Path.Combine(primaryProject, "project.json"),
            """{"id":"budcribar/Buster","title":"Buster","ownerUserId":"budcribar"}""");

        var dbPath = Path.Combine(dataDir, "pagetomovie.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA user_version = 5;
                CREATE TABLE users (
                    user_id TEXT PRIMARY KEY,
                    username TEXT NOT NULL UNIQUE,
                    password_hash TEXT NOT NULL,
                    role TEXT NOT NULL DEFAULT 'User',
                    created_at TEXT NOT NULL,
                    email TEXT,
                    email_confirmed_at TEXT,
                    credits_balance_usd REAL NOT NULL DEFAULT 0,
                    credits_lifetime_granted_usd REAL NOT NULL DEFAULT 0,
                    credits_lifetime_used_usd REAL NOT NULL DEFAULT 0,
                    is_disabled INTEGER NOT NULL DEFAULT 0
                );
                CREATE UNIQUE INDEX idx_users_email ON users(email)
                    WHERE email IS NOT NULL AND TRIM(email) != '';
                CREATE TABLE user_api_keys (
                    user_id TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    encrypted_api_key TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (user_id, provider_id)
                );
                CREATE TABLE user_api_calls (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id TEXT NOT NULL,
                    ts TEXT NOT NULL,
                    project_id TEXT,
                    kind TEXT NOT NULL,
                    ok INTEGER NOT NULL DEFAULT 1,
                    estimated_usd REAL,
                    charge_usd REAL,
                    charge_multiplier REAL
                );
                CREATE TABLE credit_ledger (
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
                CREATE TABLE generation_errors (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    user_id TEXT,
                    project_id TEXT,
                    stage TEXT NOT NULL,
                    error_type TEXT NOT NULL,
                    attempt INTEGER NOT NULL DEFAULT 1,
                    resolved INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE auth_tokens (
                    token_hash TEXT PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    purpose TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    used_at TEXT
                );

                INSERT INTO users (user_id, username, password_hash, role, created_at, email, email_confirmed_at,
                                   credits_balance_usd, credits_lifetime_granted_usd, credits_lifetime_used_usd)
                VALUES
                  ('budcribar', 'budcribar', 'hash-primary', 'Admin', '2026-01-01T00:00:00Z', NULL, '2026-01-01T00:00:00Z',
                   1.00, 2.00, 0.50),
                  ('budcribarmsn.com', 'budcribarmsn.com', 'hash-alias', 'User', '2026-02-01T00:00:00Z',
                   'budcribar@msn.com', '2026-02-01T00:00:00Z', 3.25, 4.00, 1.00);

                INSERT INTO user_api_keys (user_id, provider_id, encrypted_api_key, updated_at)
                VALUES
                  ('budcribar', 'grok', 'key-primary-grok', '2026-01-01'),
                  ('budcribarmsn.com', 'elevenlabs', 'key-alias-el', '2026-02-01'),
                  ('budcribarmsn.com', 'grok', 'key-alias-grok', '2026-02-01');

                INSERT INTO user_api_calls (user_id, ts, project_id, kind, ok, estimated_usd, charge_usd)
                VALUES
                  ('budcribarmsn.com', '2026-03-01T00:00:00Z', 'budcribarmsn_com/Mary', 'chat', 1, 2.50, 2.50),
                  ('budcribarmsn.com', '2026-03-02T00:00:00Z', 'budcribarmsn_com/Mary', 'image', 1, 1.00, 1.00),
                  ('budcribar', '2026-03-03T00:00:00Z', 'budcribar/Buster', 'video', 1, 9.00, 9.00);

                INSERT INTO credit_ledger (user_id, ts, kind, amount_usd, balance_after_usd, project_id, note)
                VALUES ('budcribarmsn.com', '2026-03-01T00:00:00Z', 'debit', -0.50, 2.75, 'budcribarmsn_com/Mary', 'est');

                INSERT INTO generation_errors (ts, user_id, project_id, stage, error_type)
                VALUES ('2026-03-01T00:00:00Z', 'budcribarmsn.com', 'budcribarmsn_com/Mary', 'tts', 'timeout');
                """;
            cmd.ExecuteNonQuery();
        }

        var opts = Options.Create(new PageToMovieOptions
        {
            WorkspaceRoot = root,
            Billing = new BillingOptions
            {
                LegacyCostOwnerUserId = "budcribar",
                LegacyCostOwnerUsername = "Bud Cribar",
                CanonicalAccountUsername = "budcribar",
                CanonicalAccountEmail = "budcribar@msn.com",
                AccountMergeAliasIds = "budcribarmsn.com,budcribarmsn_com,budcribar@msn.com",
            },
        });

        _ = new UserDatabaseService(opts);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var ver = conn.CreateCommand();
            ver.CommandText = "PRAGMA user_version;";
            Assert.Equal(7, Convert.ToInt32(ver.ExecuteScalar()));

            // Alias user gone
            using var u = conn.CreateCommand();
            u.CommandText = "SELECT COUNT(*) FROM users WHERE user_id = 'budcribarmsn.com'";
            Assert.Equal(0, Convert.ToInt32(u.ExecuteScalar()));

            // Primary has handle + email
            using var me = conn.CreateCommand();
            me.CommandText = "SELECT username, email, credits_balance_usd FROM users WHERE user_id = 'budcribar'";
            using var r = me.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("budcribar", r.GetString(0));
            Assert.Equal("budcribar@msn.com", r.GetString(1));
            Assert.Equal(4.25, r.GetDouble(2), 3); // 1.00 + 3.25

            // All api calls under budcribar
            using var api = conn.CreateCommand();
            api.CommandText = "SELECT COUNT(*) FROM user_api_calls WHERE user_id = 'budcribar'";
            Assert.Equal(3, Convert.ToInt32(api.ExecuteScalar()));
            using var apiAlias = conn.CreateCommand();
            apiAlias.CommandText = "SELECT COUNT(*) FROM user_api_calls WHERE user_id = 'budcribarmsn.com'";
            Assert.Equal(0, Convert.ToInt32(apiAlias.ExecuteScalar()));

            // Project ids rewritten on spend rows
            using var proj = conn.CreateCommand();
            proj.CommandText = "SELECT COUNT(*) FROM user_api_calls WHERE project_id LIKE 'budcribar/Mary%'";
            Assert.Equal(2, Convert.ToInt32(proj.ExecuteScalar()));

            // Keys merged: primary keeps grok; gains elevenlabs
            using var keys = conn.CreateCommand();
            keys.CommandText = "SELECT provider_id, encrypted_api_key FROM user_api_keys WHERE user_id = 'budcribar' ORDER BY provider_id";
            using var kr = keys.ExecuteReader();
            var map = new Dictionary<string, string>();
            while (kr.Read()) map[kr.GetString(0)] = kr.GetString(1);
            Assert.Equal("key-primary-grok", map["grok"]); // primary preferred
            Assert.Equal("key-alias-el", map["elevenlabs"]);
        }

        // Project folder rehomed
        Assert.True(Directory.Exists(Path.Combine(projectsDir, "budcribar", "Mary")));
        Assert.False(Directory.Exists(Path.Combine(projectsDir, "budcribarmsn_com", "Mary")));
        var meta = File.ReadAllText(Path.Combine(projectsDir, "budcribar", "Mary", "project.json"));
        Assert.Contains("budcribar", meta, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("budcribarmsn.com", meta, StringComparison.OrdinalIgnoreCase);
    }
}
