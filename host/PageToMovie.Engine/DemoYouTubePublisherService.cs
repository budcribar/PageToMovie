using Google.Apis.Upload;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Uploads a published demo's local movie.mp4 to YouTube (reusing the same authorized
/// channel connection as the Review page's WIP-movie upload — <see cref="YouTubeAuthService"/>).
/// On first publish: upload, store YoutubeId, delete local movie.mp4.
/// On re-publish (Item 11 / V2): when a local movie exists beside an existing YoutubeId,
/// upload a new video, update the gallery pointer, then best-effort delete the old YouTube ID.
/// Never throws — failures keep the previous YoutubeId (if any) and leave the local file so the
/// gallery can still stream from the server.
/// </summary>
public sealed class DemoYouTubePublisherService
{
    private readonly DemoCatalogService _demos;
    private readonly YouTubeAuthService _youTube;
    private const string FailedStatus = "failed";
    private readonly ILogger<DemoYouTubePublisherService> _log;

    public DemoYouTubePublisherService(
        DemoCatalogService demos,
        YouTubeAuthService youTube,
        ILogger<DemoYouTubePublisherService> log)
    {
        _demos = demos;
        _youTube = youTube;
        _log = log;
    }

    /// <summary>True once ClientId/ClientSecret/RedirectUri are configured (still needs a connected channel).</summary>
    public bool IsConfigured => _youTube.IsConfigured;

    public async Task PublishAsync(string demoId, CancellationToken ct = default)
    {
        var entry = await _demos.TryGetAsync(demoId, ct).ConfigureAwait(false);
        if (entry is null)
            return;

        var path = _demos.ResolveMoviePath(demoId);
        var oldYoutubeId = string.IsNullOrWhiteSpace(entry.YoutubeId) ? null : entry.YoutubeId.Trim();

        // Already on YouTube and no new local file → nothing to do.
        if (oldYoutubeId is not null && path is null)
        {
            _log.LogDebug("Demo {Id} already on YouTube ({Yt}) with no local movie — skip.", demoId, oldYoutubeId);
            return;
        }

        // No local movie and never uploaded → nothing to publish.
        if (path is null)
        {
            _log.LogDebug("Demo {Id} has no local movie.mp4 to upload.", demoId);
            return;
        }

        var isReplace = oldYoutubeId is not null;
        await _demos.SetYouTubeUploadStatusAsync(demoId, "uploading", ct: ct).ConfigureAwait(false);

        var youtube = await TryGetYouTubeServiceAsync(demoId, ct).ConfigureAwait(false);
        if (youtube is null)
            return;

        await UploadDemoMovieAsync(demoId, path, oldYoutubeId, isReplace, youtube, entry, ct).ConfigureAwait(false);
    }

    private async Task<Google.Apis.YouTube.v3.YouTubeService?> TryGetYouTubeServiceAsync(
        string demoId, CancellationToken ct)
    {
        Google.Apis.YouTube.v3.YouTubeService? youtube;
        try
        {
            youtube = await _youTube.GetServiceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "YouTube auth failed publishing demo {Id}", demoId);
            await _demos.SetYouTubeUploadStatusAsync(demoId, FailedStatus, error: ex.Message, ct: ct).ConfigureAwait(false);
            return null;
        }

        if (youtube is null)
        {
            await _demos.SetYouTubeUploadStatusAsync(
                demoId, FailedStatus,
                error: "YouTube channel not connected (admin: connect it from Review).", ct: ct).ConfigureAwait(false);
            return null;
        }

        return youtube;
    }

    private async Task UploadDemoMovieAsync(
        string demoId,
        string path,
        string? oldYoutubeId,
        bool isReplace,
        Google.Apis.YouTube.v3.YouTubeService youtube,
        DemoCatalogService.DemoEntry entry,
        CancellationToken ct)
    {
        try
        {
            // Re-read entry for latest metadata (title/privacy) while uploading.
            entry = await _demos.TryGetAsync(demoId, ct).ConfigureAwait(false) ?? entry;
            // Channel is app-operated; default unlisted so gallery can embed without open YT browse.
            var privacy = entry.PrivacyStatus is "private" or "unlisted" or "public"
                ? entry.PrivacyStatus
                : "unlisted";
            var video = new Video
            {
                Snippet = new VideoSnippet
                {
                    Title = string.IsNullOrWhiteSpace(entry.Title) ? demoId : entry.Title,
                    Description = entry.Description ?? "",
                    CategoryId = "1", // Film & Animation
                    Tags = entry.Tags,
                },
                Status = new VideoStatus
                {
                    PrivacyStatus = privacy,
                    MadeForKids = entry.MadeForKids,
                    SelfDeclaredMadeForKids = entry.MadeForKids,
                    Embeddable = true,
                },
            };

            await using var stream = File.OpenRead(path);
            var upload = youtube.Videos.Insert(video, "snippet,status", stream, "video/mp4");
            string? videoId = null;
            upload.ResponseReceived += v => videoId = v.Id;

            var result = await upload.UploadAsync(ct).ConfigureAwait(false);
            if (result.Status != UploadStatus.Completed || string.IsNullOrWhiteSpace(videoId))
            {
                var err = result.Exception?.Message ?? $"Upload status: {result.Status}";
                _log.LogWarning("YouTube upload incomplete for demo {Id}: {Error}", demoId, err);
                // Keep previous YoutubeId on V2 failure so gallery still embeds the old video.
                await _demos.SetYouTubeUploadStatusAsync(demoId, FailedStatus, error: err, ct: ct).ConfigureAwait(false);
                return;
            }

            var url = $"https://youtu.be/{videoId}";
            await _demos.SetYouTubeUploadStatusAsync(demoId, "done", videoId, url, ct: ct).ConfigureAwait(false);
            _log.LogInformation(
                isReplace
                    ? "Demo {Id} YouTube V2 published: {Url} (replaced {Old})"
                    : "Demo {Id} published to YouTube: {Url}",
                demoId, url, oldYoutubeId);

            // Best-effort cleanup of staged demo movie.mp4 to conserve server disk space
            TryDeleteLocalMovieFile(path);

            // Mode A: best-effort delete of the obsolete v1 video (requires youtube.force-ssl scope).
            if (isReplace &&
                !string.Equals(oldYoutubeId, videoId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(oldYoutubeId))
            {
                await TryDeleteYouTubeVideoAsync(youtube, oldYoutubeId, demoId, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "YouTube upload failed for demo {Id}", demoId);
            await _demos.SetYouTubeUploadStatusAsync(demoId, FailedStatus, error: ex.Message, ct: ct).ConfigureAwait(false);
        }
    }

    private void TryDeleteLocalMovieFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to clean up demo movie file {Path} after YouTube publish", path);
        }
    }

    private async Task TryDeleteYouTubeVideoAsync(
        Google.Apis.YouTube.v3.YouTubeService youtube,
        string oldVideoId,
        string demoId,
        CancellationToken ct)
    {
        try
        {
            await youtube.Videos.Delete(oldVideoId).ExecuteAsync(ct).ConfigureAwait(false);
            _log.LogInformation(
                "Deleted obsolete YouTube video {OldId} after V2 replace for demo {DemoId}",
                oldVideoId, demoId);
        }
        catch (Exception ex)
        {
            // Common when the connected token only has youtube.upload (no force-ssl).
            // Gallery already points at V2 — leave v1 on the channel for manual cleanup.
            _log.LogWarning(
                ex,
                "Could not delete old YouTube video {OldId} for demo {DemoId} after V2 replace. " +
                "Reconnect YouTube with full channel scopes if deletes should be automatic.",
                oldVideoId, demoId);
        }
    }
}
