using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>Server-side, content-addressed identity for uploaded book text.</summary>
public sealed class BookTextRegistryService
{
    private readonly string _connectionString;

    public BookTextRegistryService(IOptions<PageToMovieOptions> options)
    {
        var dir = UserDatabaseService.ResolveDataDirectory(options.Value.WorkspaceRoot);
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={Path.Combine(dir, "pagetomovie.db")};Cache=Shared;Pooling=True;";
        EnsureSchema();
    }

    public Task<BookTextIdentity> RegisterAsync(
        string text, string userId, string? projectId, ProjectVisibility visibility,
        CancellationToken ct = default) =>
        RegisterAsync(text, userId, projectId, visibility.ToString(), ct);

    public async Task<BookTextIdentity> RegisterAsync(
        string text, string userId, string? projectId = null, string visibility = "Private",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Book text is required.", nameof(text));
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = "book_" + hash[..24];
        var now = DateTime.UtcNow.ToString("o");

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO book_texts (book_id, sha256, text_content, byte_count, created_at)
                VALUES (@id, @hash, @text, @bytes, @now)
                ON CONFLICT(sha256) DO NOTHING;
                INSERT INTO book_text_access (book_id, user_id, project_id, visibility_mode, linked_at)
                VALUES (@id, @user, @project, @visibility, @now)
                ON CONFLICT(book_id, user_id, project_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.Parameters.AddWithValue("@bytes", bytes.Length);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@user", userId);
            cmd.Parameters.AddWithValue("@project", projectId ?? "");
            cmd.Parameters.AddWithValue("@visibility", NormalizeVisibility(visibility));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new(id, hash, bytes.Length, text);
    }

    public async Task<BookTextIdentity?> ResolveAsync(
        string idOrHash, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrHash)) return null;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.book_id, b.sha256, b.byte_count, b.text_content
            FROM book_texts b
            JOIN book_text_access a ON a.book_id = b.book_id
            WHERE (b.book_id = @key OR b.sha256 = LOWER(@key))
              AND (a.user_id = @user OR a.visibility_mode IN ('Public', 'Forkable'))
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@key", idOrHash.Trim());
        cmd.Parameters.AddWithValue("@user", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3))
            : null;
    }

    public async Task LinkToProjectAsync(
        string bookId, string userId, string projectId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_text_access (book_id, user_id, project_id, visibility_mode, linked_at)
            SELECT b.book_id, @user, @project, 'Private', @now
            FROM book_texts b
            WHERE b.book_id = @book AND EXISTS (
                SELECT 1 FROM book_text_access a
                WHERE a.book_id=b.book_id
                  AND (a.user_id=@user OR a.visibility_mode='Forkable'))
            ON CONFLICT(book_id, user_id, project_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@book", bookId);
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            throw new KeyNotFoundException($"Book '{bookId}' does not exist.");
    }

    public async Task SetProjectVisibilityAsync(
        string userId, string projectId, string visibility, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE book_text_access SET visibility_mode=@visibility
            WHERE user_id=@user AND project_id=@project;
            """;
        cmd.Parameters.AddWithValue("@visibility", NormalizeVisibility(visibility));
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@project", projectId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task LinkForkAsync(
        string sourceProjectId,
        string targetUserId,
        string targetProjectId,
        bool invitationAuthorized,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_text_access (book_id, user_id, project_id, visibility_mode, linked_at)
            SELECT DISTINCT book_id, @user, @target, 'Private', @now
            FROM book_text_access
            WHERE project_id=@source
              AND (@invited=1 OR visibility_mode='Forkable')
            ON CONFLICT(book_id, user_id, project_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@source", sourceProjectId);
        cmd.Parameters.AddWithValue("@user", targetUserId);
        cmd.Parameters.AddWithValue("@target", targetProjectId);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@invited", invitationAuthorized ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<DerivedBookArtifact> RegisterArtifactAsync(
        string bookId,
        string userId,
        string artifactKind,
        string content,
        string modelId,
        string promptVersion,
        string promptSha256,
        double temperature,
        string behaviorVersionsJson,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Derived artifact content is required.", nameof(content));
        var derivationHash = DerivationHash(
            bookId, artifactKind, modelId, promptVersion, promptSha256, temperature, behaviorVersionsJson);
        var artifactId = "artifact_" + derivationHash[..24];
        var contentHash = Hash(content);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_derived_artifacts
                (artifact_id, derivation_sha256, book_id, artifact_kind, content, content_sha256,
                 model_id, prompt_version, prompt_sha256, temperature, behavior_versions_json, created_at)
            SELECT @id, @derivation, @book, @kind, @content, @contentHash,
                   @model, @promptVersion, @promptHash, @temperature, @behaviors, @now
            WHERE EXISTS (
                SELECT 1 FROM book_text_access
                WHERE book_id = @book AND user_id = @user)
            ON CONFLICT(derivation_sha256) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@id", artifactId);
        cmd.Parameters.AddWithValue("@derivation", derivationHash);
        cmd.Parameters.AddWithValue("@book", bookId);
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@kind", artifactKind);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@contentHash", contentHash);
        cmd.Parameters.AddWithValue("@model", modelId);
        cmd.Parameters.AddWithValue("@promptVersion", promptVersion);
        cmd.Parameters.AddWithValue("@promptHash", promptSha256.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@temperature", temperature);
        cmd.Parameters.AddWithValue("@behaviors", behaviorVersionsJson);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
        {
            await using var access = conn.CreateCommand();
            access.CommandText = "SELECT 1 FROM book_text_access WHERE book_id=@book AND user_id=@user LIMIT 1;";
            access.Parameters.AddWithValue("@book", bookId);
            access.Parameters.AddWithValue("@user", userId);
            if (await access.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                throw new UnauthorizedAccessException("The caller does not have access to this book identity.");
        }
        return new(artifactId, derivationHash, bookId, artifactKind, contentHash, content);
    }

    public Task<DerivedBookArtifact?> FindArtifactAsync(
        string bookId,
        string userId,
        string artifactKind,
        string modelId,
        string promptVersion,
        string promptSha256,
        double temperature,
        string behaviorVersionsJson,
        CancellationToken ct = default) =>
        ResolveArtifactByDerivationHashAsync(
            DerivationHash(bookId, artifactKind, modelId, promptVersion, promptSha256,
                temperature, behaviorVersionsJson), userId, ct);

    public Task<DerivedBookArtifact?> ResolveArtifactAsync(
        string artifactId, string userId, CancellationToken ct = default) =>
        QueryDerivedArtifactAsync("artifact_id", "@id", artifactId, userId, ct);

    private Task<DerivedBookArtifact?> ResolveArtifactByDerivationHashAsync(
        string derivationHash, string userId, CancellationToken ct) =>
        QueryDerivedArtifactAsync("derivation_sha256", "@hash", derivationHash, userId, ct);

    /// <summary>
    /// Fetch a derived-artifact row keyed on a single access-gated column, mapped to
    /// <see cref="DerivedBookArtifact"/> (or null). <paramref name="keyColumn"/> and
    /// <paramref name="keyParam"/> are compile-time-constant internal identifiers (not user
    /// input), so interpolating them into the SQL carries no injection risk.
    /// </summary>
    private async Task<DerivedBookArtifact?> QueryDerivedArtifactAsync(
        string keyColumn, string keyParam, string keyValue, string userId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.artifact_id, d.derivation_sha256, d.book_id, d.artifact_kind,
                   d.content_sha256, d.content
            FROM book_derived_artifacts d
            JOIN book_text_access a ON a.book_id=d.book_id
            WHERE d.{keyColumn}={keyParam}
              AND (a.user_id=@user OR a.visibility_mode IN ('Public', 'Forkable')) LIMIT 1;
            """;
        cmd.Parameters.AddWithValue(keyParam, keyValue);
        cmd.Parameters.AddWithValue("@user", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5))
            : null;
    }


    // ── Provider file handles (xAI file_id, etc.) ─────────────────────────

    public async Task<ProviderBookFile?> GetProviderFileAsync(
        string bookId, string provider = "xai", CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT book_id, provider, file_id, expires_at_unix, last_response_id, created_at, updated_at
            FROM book_provider_files
            WHERE book_id=@book AND provider=@provider
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@book", bookId);
        cmd.Parameters.AddWithValue("@provider", provider.Trim().ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new ProviderBookFile(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    public async Task UpsertProviderFileAsync(
        string bookId,
        string provider,
        string fileId,
        long? expiresAtUnix,
        string? lastResponseId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(fileId))
            throw new ArgumentException("bookId and fileId required.");
        provider = provider.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow.ToString("o");
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_provider_files
                (book_id, provider, file_id, expires_at_unix, last_response_id, created_at, updated_at)
            VALUES (@book, @provider, @file, @exp, @resp, @now, @now)
            ON CONFLICT(book_id, provider) DO UPDATE SET
                file_id=excluded.file_id,
                expires_at_unix=excluded.expires_at_unix,
                last_response_id=COALESCE(excluded.last_response_id, book_provider_files.last_response_id),
                updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@book", bookId);
        cmd.Parameters.AddWithValue("@provider", provider);
        cmd.Parameters.AddWithValue("@file", fileId);
        cmd.Parameters.AddWithValue("@exp", (object?)expiresAtUnix ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@resp", (object?)lastResponseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateLastResponseIdAsync(
        string bookId, string provider, string? responseId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE book_provider_files
            SET last_response_id=@resp, updated_at=@now
            WHERE book_id=@book AND provider=@provider;
            """;
        cmd.Parameters.AddWithValue("@book", bookId);
        cmd.Parameters.AddWithValue("@provider", provider.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@resp", (object?)responseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Admin dashboard: books, derived artifacts, provider files.</summary>
    public async Task<BookCacheAdminSnapshot> GetAdminCacheSnapshotAsync(
        int takeBooks = 100, CancellationToken ct = default)
    {
        takeBooks = Math.Clamp(takeBooks, 1, 500);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        long bookCount = 0, artifactCount = 0, providerFileCount = 0, totalBookBytes = 0;
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*), COALESCE(SUM(byte_count),0) FROM book_texts;";
            await using var r = await c.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                bookCount = r.GetInt64(0);
                totalBookBytes = r.GetInt64(1);
            }
        }
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM book_derived_artifacts;";
            artifactCount = Convert.ToInt64(await c.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM book_provider_files;";
            try
            {
                providerFileCount = Convert.ToInt64(await c.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
            }
            catch { providerFileCount = 0; }
        }

        var books = new List<BookCacheAdminRow>();
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = """
                SELECT b.book_id, b.sha256, b.byte_count, b.created_at,
                       (SELECT COUNT(*) FROM book_derived_artifacts d WHERE d.book_id=b.book_id) AS artifacts,
                       (SELECT COUNT(*) FROM book_text_access a WHERE a.book_id=b.book_id) AS links,
                       pf.file_id, pf.provider, pf.expires_at_unix, pf.last_response_id, pf.updated_at,
                       SUBSTR(b.text_content, 1, 500) AS text_head,
                       (SELECT GROUP_CONCAT(DISTINCT project_id) FROM book_text_access a WHERE a.book_id=b.book_id AND a.project_id != '') AS projects
                FROM book_texts b
                LEFT JOIN book_provider_files pf ON pf.book_id=b.book_id AND pf.provider='xai'
                ORDER BY b.created_at DESC
                LIMIT @take;
                """;
            c.Parameters.AddWithValue("@take", takeBooks);
            await using var r = await c.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var textHead = r.IsDBNull(11) ? null : r.GetString(11);
                var title = ExtractBookTitle(textHead);
                var proj = r.IsDBNull(12) ? "" : r.GetString(12);

                books.Add(new BookCacheAdminRow(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetInt32(2),
                    r.GetString(3),
                    r.GetInt32(4),
                    r.GetInt32(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetInt64(8),
                    r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetString(10),
                    title,
                    proj));
            }
        }

        var artifacts = new List<ArtifactCacheAdminRow>();
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = """
                SELECT artifact_id, book_id, artifact_kind, model_id, prompt_version,
                       temperature, created_at, LENGTH(content)
                FROM book_derived_artifacts
                ORDER BY created_at DESC
                LIMIT 80;
                """;
            await using var r = await c.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                artifacts.Add(new ArtifactCacheAdminRow(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetDouble(5), r.GetString(6), r.GetInt32(7)));
            }
        }

        return new BookCacheAdminSnapshot(bookCount, artifactCount, providerFileCount, totalBookBytes, books, artifacts);
    }

    public static string ExtractBookTitle(string? textHead)
    {
        if (string.IsNullOrWhiteSpace(textHead)) return "Untitled Book";
        var lines = textHead.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
            {
                var title = line["Title:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(title)) return title;
            }
        }
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("/*") || line.StartsWith("//") || line.StartsWith("#")) continue;
            return line.Length > 60 ? line[..57] + "…" : line;
        }
        return "Untitled Book";
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS book_texts (
                book_id TEXT PRIMARY KEY,
                sha256 TEXT NOT NULL UNIQUE,
                text_content TEXT NOT NULL,
                byte_count INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS book_text_access (
                book_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                project_id TEXT NOT NULL DEFAULT '',
                visibility_mode TEXT NOT NULL DEFAULT 'Private',
                linked_at TEXT NOT NULL,
                PRIMARY KEY (book_id, user_id, project_id),
                FOREIGN KEY (book_id) REFERENCES book_texts(book_id)
            );
            CREATE INDEX IF NOT EXISTS idx_book_text_access_user ON book_text_access(user_id, book_id);
            CREATE TABLE IF NOT EXISTS book_derived_artifacts (
                artifact_id TEXT PRIMARY KEY,
                derivation_sha256 TEXT NOT NULL UNIQUE,
                book_id TEXT NOT NULL,
                artifact_kind TEXT NOT NULL,
                content TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                model_id TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                prompt_sha256 TEXT NOT NULL,
                temperature REAL NOT NULL,
                behavior_versions_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (book_id) REFERENCES book_texts(book_id)
            );
            CREATE INDEX IF NOT EXISTS idx_book_artifacts_book_kind
                ON book_derived_artifacts(book_id, artifact_kind);
            CREATE TABLE IF NOT EXISTS book_provider_files (
                book_id TEXT NOT NULL,
                provider TEXT NOT NULL,
                file_id TEXT NOT NULL,
                expires_at_unix INTEGER,
                last_response_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (book_id, provider),
                FOREIGN KEY (book_id) REFERENCES book_texts(book_id)
            );
            CREATE INDEX IF NOT EXISTS idx_book_provider_files_file ON book_provider_files(file_id);
            """;
        cmd.ExecuteNonQuery();
        try
        {
            using var migrate = conn.CreateCommand();
            migrate.CommandText = "ALTER TABLE book_text_access ADD COLUMN visibility_mode TEXT NOT NULL DEFAULT 'Private';";
            migrate.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Existing databases already containing the column are current.
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string DerivationHash(
        string bookId, string artifactKind, string modelId, string promptVersion,
        string promptSha256, double temperature, string behaviorVersionsJson) =>
        Hash(string.Join("\n", bookId, artifactKind, modelId, promptVersion,
            promptSha256.ToLowerInvariant(),
            temperature.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            behaviorVersionsJson));

    private static string NormalizeVisibility(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "public" => "Public",
        "open" or "forkable" => "Forkable",
        _ => "Private",
    };
}

public sealed record BookTextIdentity(string BookId, string Sha256, int ByteCount, string Text);
public sealed record DerivedBookArtifact(
    string ArtifactId,
    string DerivationSha256,
    string BookId,
    string ArtifactKind,
    string ContentSha256,
    string Content);

public sealed record ProviderBookFile(
    string BookId,
    string Provider,
    string FileId,
    long? ExpiresAtUnix,
    string? LastResponseId,
    string CreatedAt,
    string UpdatedAt);

public sealed record BookCacheAdminRow(
    string BookId,
    string Sha256,
    int ByteCount,
    string CreatedAt,
    int ArtifactCount,
    int AccessLinkCount,
    string? ProviderFileId,
    string? Provider,
    long? FileExpiresAtUnix,
    string? LastResponseId,
    string? ProviderFileUpdatedAt,
    string BookTitle = "Untitled Book",
    string Projects = "");

public sealed record ArtifactCacheAdminRow(
    string ArtifactId,
    string BookId,
    string ArtifactKind,
    string ModelId,
    string PromptVersion,
    double Temperature,
    string CreatedAt,
    int ContentBytes);

public sealed record BookCacheAdminSnapshot(
    long BookCount,
    long ArtifactCount,
    long ProviderFileCount,
    long TotalBookBytes,
    IReadOnlyList<BookCacheAdminRow> Books,
    IReadOnlyList<ArtifactCacheAdminRow> RecentArtifacts);
