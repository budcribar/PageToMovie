using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Single-use, expiring invite tokens for the Invite-to-Fork flow. Only a SHA-256 hash of the
/// token is ever stored (same pattern as <see cref="UserDatabaseService"/>'s auth tokens) — the
/// raw token exists only in the URL emailed to the invitee, never on disk.
/// </summary>
public sealed class ProjectInviteService
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(48);

    private readonly string _dbPath;
    private readonly ILogger<ProjectInviteService> _log;
    private readonly object _initLock = new();
    private bool _initialized;

    public ProjectInviteService(IOptions<PageToMovieOptions> options, ILogger<ProjectInviteService>? logger = null)
    {
        _log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectInviteService>.Instance;
        var workspace = options?.Value?.WorkspaceRoot;
        var dataDir = UserDatabaseService.ResolveDataDirectory(workspace);
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "pagetomovie.db");
        EnsureInitialized();
    }

    private string ConnectionString => $"Data Source={_dbPath};Cache=Shared;";

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS project_invites (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    token_hash TEXT NOT NULL UNIQUE,
                    project_id TEXT NOT NULL,
                    inviter_user_id TEXT NOT NULL,
                    target_username TEXT,
                    target_email TEXT,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    accepted_at TEXT,
                    accepted_by_user_id TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_project_invites_project ON project_invites(project_id);
            ";
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private static string HashToken(string raw)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ptm-invite:" + raw));
        return Convert.ToHexStringLower(bytes);
    }

    public sealed record CreatedInvite(string Token, DateTimeOffset ExpiresAt);

    /// <summary>Creates a single-use invite; returns the raw token (only ever exposed here / in the emailed link).</summary>
    public async Task<CreatedInvite> CreateAsync(
        string projectId,
        string inviterUserId,
        string? targetUsername,
        string? targetEmail,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("projectId required");
        if (string.IsNullOrWhiteSpace(targetUsername) && string.IsNullOrWhiteSpace(targetEmail))
            throw new InvalidOperationException("A target handle or email is required.");

        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        var expires = now + DefaultLifetime;

        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO project_invites
                (token_hash, project_id, inviter_user_id, target_username, target_email, created_at, expires_at)
            VALUES (@h, @p, @inv, @u, @e, @c, @x)";
        cmd.Parameters.AddWithValue("@h", HashToken(raw));
        cmd.Parameters.AddWithValue("@p", projectId.Trim());
        cmd.Parameters.AddWithValue("@inv", inviterUserId.Trim());
        cmd.Parameters.AddWithValue("@u", (object?)targetUsername?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@e", (object?)targetEmail?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", now.ToString("o"));
        cmd.Parameters.AddWithValue("@x", expires.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Invite created for project {Project} by {Inviter} (expires {Expires:o})",
            projectId, inviterUserId, expires);
        return new CreatedInvite(raw, expires);
    }

    public sealed record InviteOutcome(bool Ok, string? ProjectId, string? Error);

    /// <summary>
    /// Validates and consumes an invite token (single-use). On success, returns the projectId to
    /// fork. Does not check that <paramref name="acceptingUserId"/> matches any target handle/email
    /// on the invite — a blind-email invite may be accepted by whichever account the recipient
    /// signs into, since the token itself (delivered privately) is the actual authorization.
    /// </summary>
    public async Task<InviteOutcome> ConsumeAsync(string rawToken, string acceptingUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return new InviteOutcome(false, null, "Invite link is missing its token.");
        if (string.IsNullOrWhiteSpace(acceptingUserId))
            return new InviteOutcome(false, null, "Sign in required to accept an invite.");

        var hash = HashToken(rawToken.Trim());
        using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string? projectId = null;
        string? expiresRaw = null;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = @"
                SELECT project_id, expires_at, accepted_at FROM project_invites
                WHERE token_hash = @h LIMIT 1";
            sel.Parameters.AddWithValue("@h", hash);
            using var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return new InviteOutcome(false, null, "This invite link is invalid or was already used.");
            }
            projectId = r.GetString(0);
            expiresRaw = r.GetString(1);
            if (!r.IsDBNull(2))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return new InviteOutcome(false, null, "This invite link was already used.");
            }
        }

        if (!DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires) || expires < DateTimeOffset.UtcNow)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return new InviteOutcome(false, null, "This invite link has expired.");
        }

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = @"
                UPDATE project_invites SET accepted_at = @a, accepted_by_user_id = @u
                WHERE token_hash = @h";
            upd.Parameters.AddWithValue("@a", DateTimeOffset.UtcNow.ToString("o"));
            upd.Parameters.AddWithValue("@u", acceptingUserId.Trim());
            upd.Parameters.AddWithValue("@h", hash);
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);

        _log.LogInformation("Invite for project {Project} accepted by {User}", projectId, acceptingUserId);
        return new InviteOutcome(true, projectId, null);
    }
}
