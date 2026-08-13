using System.Security.Cryptography;
using System.Text.Json;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Opaque share tokens for public WIP (and future) media streams.
/// Tokens live under <c>{WorkspaceRoot}/_shares/</c> so they resolve without project id in the URL.
/// </summary>
public sealed class MediaShareService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ProjectStore _projects;
    private readonly ILogger<MediaShareService> _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public MediaShareService(ProjectStore projects, ILogger<MediaShareService> log)
    {
        _projects = projects;
        _log = log;
    }

    public string SharesDir => Path.Combine(_projects.WorkspaceRoot, "_shares");

    public sealed class ShareRecord
    {
        public string Token { get; set; } = "";
        public string ProjectId { get; set; } = "";
        /// <summary>wip | preview | composite (future).</summary>
        public string Kind { get; set; } = "wip";
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool Revoked { get; set; }
    }

    /// <summary>
    /// Create or return an existing non-expired WIP share for the project.
    /// </summary>
    public async Task<ShareRecord> EnsureWipShareAsync(string projectId, string? createdBy, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("projectId required", nameof(projectId));

        // Must have a WIP file to share
        var path = _projects.ResolveWipMoviePath(projectId);
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException("WIP movie not found — Play first so the cut is built.");

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(SharesDir);
            // Reuse active share for this project if present
            foreach (var file in Directory.EnumerateFiles(SharesDir, "*.json"))
            {
                if (await TryReadActiveWipShareAsync(file, projectId, ct).ConfigureAwait(false) is { } rec)
                    return rec;
            }

            var token = GenerateToken();
            var record = new ShareRecord
            {
                Token = token,
                ProjectId = projectId.Trim(),
                Kind = "wip",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = createdBy,
                ExpiresAt = lifetime is { } life ? DateTimeOffset.UtcNow.Add(life) : DateTimeOffset.UtcNow.AddDays(30),
            };
            var outPath = Path.Combine(SharesDir, token + ".json");
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(record, JsonOpts) + "\n", ct).ConfigureAwait(false);
            _log.LogInformation("Created WIP share token for project {Project} by {User}", projectId, createdBy);
            return record;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static async Task<ShareRecord?> TryReadActiveWipShareAsync(string file, string projectId, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var rec = JsonSerializer.Deserialize<ShareRecord>(text, JsonOpts);
            if (rec is null || rec.Revoked) return null;
            if (!string.Equals(rec.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(rec.Kind, "wip", StringComparison.OrdinalIgnoreCase)) return null;
            if (rec.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow) return null;
            return rec;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ShareRecord?> TryGetAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        // Token is filename-safe base64url
        if (token.Length is < 16 or > 128) return null;
        if (token.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) return null;

        var path = Path.Combine(SharesDir, token.Trim() + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var rec = JsonSerializer.Deserialize<ShareRecord>(text, JsonOpts);
            if (rec is null || rec.Revoked) return null;
            if (rec.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow) return null;
            if (!string.Equals(rec.Token, token.Trim(), StringComparison.Ordinal)) return null;
            return rec;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken ct = default)
    {
        var rec = await TryGetAsync(token, ct).ConfigureAwait(false);
        if (rec is null) return false;
        rec.Revoked = true;
        var path = Path.Combine(SharesDir, rec.Token + ".json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rec, JsonOpts) + "\n", ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
