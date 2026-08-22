using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>
/// Where one project-relative media file's bytes actually live right now: on server disk,
/// registered as synced to the client (MediaRegistryService row / .client.json marker), both,
/// or neither. Single source of truth for "is this media present" — client-storage is the
/// primary path for every media kind this product generates (video clips today; audio/music
/// tracks are the next one), and server copies get pruned within minutes of a confirmed client
/// sync (see ServerMediaPruningService), so "present" essentially never means "real bytes on
/// this machine" once a file has synced.
/// </summary>
public sealed class MediaSyncStatus
{
    public bool OnServer { get; init; }
    public long ServerSizeBytes { get; init; }
    public bool OnClient { get; init; }
    public string? Sha256 { get; init; }
    public long ClientSizeBytes { get; init; }
    public string RelativePath { get; init; } = "";
    public bool Present => OnServer || OnClient;
}

/// <summary>
/// Callers already have <c>ProjectStore.GetProjectDir(projectId)</c> in hand, so this takes
/// <paramref name="projectDir"/> directly rather than depending on ProjectStore itself — avoids
/// a circular DI reference (ProjectStore already needs this for the Takes list) and keeps the
/// locator a plain, reusable utility for any media kind.
/// </summary>
public sealed class MediaSyncLocator
{
    private readonly MediaRegistryService? _registry;

    public MediaSyncLocator(MediaRegistryService? registry = null)
    {
        _registry = registry;
    }

    public async Task<MediaSyncStatus> GetStatusAsync(
        string projectId, string projectDir, string relativePath, CancellationToken ct = default)
    {
        var normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        var fullPath = Path.Combine(projectDir, normalized.Replace('/', Path.DirectorySeparatorChar));

        var onServer = File.Exists(fullPath) && new FileInfo(fullPath).Length >= 1024;
        var serverSize = onServer ? new FileInfo(fullPath).Length : 0;

        MediaObjectDto? reg = _registry is null
            ? null
            : await _registry.TryGetAsync(projectId, normalized, ct).ConfigureAwait(false);

        return new MediaSyncStatus
        {
            OnServer = onServer,
            ServerSizeBytes = serverSize,
            OnClient = reg is not null,
            Sha256 = reg?.Sha256,
            ClientSizeBytes = reg?.SizeBytes ?? 0,
            RelativePath = normalized,
        };
    }

    /// <summary>The one check every "is this media file present" call site should use instead of
    /// reimplementing File.Exists + marker/registry logic independently — that duplication is
    /// exactly what produced the video-extend gate, predecessor-upload, and Takes-list bugs.</summary>
    public async Task<bool> IsPresentAsync(
        string projectId, string projectDir, string relativePath, CancellationToken ct = default)
        => (await GetStatusAsync(projectId, projectDir, relativePath, ct).ConfigureAwait(false)).Present;

    public Task<MediaSyncStatus> GetClipStatusAsync(
        string projectId, string projectDir, int scene, int clip, CancellationToken ct = default)
    {
        var videoDir = Path.Combine(projectDir, "assets", "video");
        var rel = ClipSidecarService.CurrentTakeRelativePath(videoDir, scene, clip)
            ?? ClipTakeNaming.TakeRelativePath(scene, clip, 1);
        return GetStatusAsync(projectId, projectDir, rel, ct);
    }
}
