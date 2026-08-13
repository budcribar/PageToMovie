using System.Linq;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Pulls the connected channel's uploads into the demo catalog so /demo lists YouTube as SoT.
/// </summary>
public sealed class YouTubeChannelGallerySync
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private readonly YouTubeAuthService _youTube;
    private readonly DemoCatalogService _demos;
    private readonly ILogger<YouTubeChannelGallerySync> _log;
    private readonly object _gate = new();
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccessUtc = DateTimeOffset.MinValue;
    private string? _lastError;

    public YouTubeChannelGallerySync(
        YouTubeAuthService youTube,
        DemoCatalogService demos,
        ILogger<YouTubeChannelGallerySync> log)
    {
        _youTube = youTube;
        _demos = demos;
        _log = log;
    }

    public DateTimeOffset? LastSuccessUtc
    {
        get { lock (_gate) return _lastSuccessUtc == DateTimeOffset.MinValue ? null : _lastSuccessUtc; }
    }

    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>
    /// Sync channel → catalog. When <paramref name="force"/> is false, skips if last attempt was recent.
    /// No-op when OAuth is not connected.
    /// </summary>
    public async Task<(int Added, int Updated, int Total, bool Skipped)> EnsureSyncedAsync(
        bool force = false,
        string? createdBy = "youtube-channel",
        int maxVideos = 50,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!force && DateTimeOffset.UtcNow - _lastAttemptUtc < MinInterval)
                return (0, 0, 0, true);
            _lastAttemptUtc = DateTimeOffset.UtcNow;
        }

        if (!_youTube.IsConfigured || !await _youTube.IsConnectedAsync(ct).ConfigureAwait(false))
        {
            lock (_gate) _lastError = "YouTube not connected";
            return (0, 0, 0, true);
        }

        try
        {
            // Successful list (0 or N videos) is authoritative. Exceptions are glitches — do not hide.
            var uploads = await _youTube.ListChannelUploadsAsync(maxVideos, ct).ConfigureAwait(false);
            var (added, updated, _) = await _demos.SyncFromChannelUploadsAsync(uploads, createdBy, ct).ConfigureAwait(false);
            var hidden = await _demos.HideDemosNotOnChannelAsync(
                uploads.Select(u => u.VideoId).ToList(),
                listIsAuthoritative: true, ct: ct).ConfigureAwait(false);
            lock (_gate)
            {
                _lastSuccessUtc = DateTimeOffset.UtcNow;
                _lastError = null; // clean list (even 0 videos) is success, not a glitch
            }
            _log.LogInformation(
                "YouTube channel sync: {Total} videos ({Added} new, {Updated} updated, {Hidden} not on channel)",
                uploads.Count, added, updated, hidden);
            return (added, updated, uploads.Count, false);
        }
        catch (Exception ex)
        {
            // Glitch / API error: leave gallery as-is (do not hide, do not assume channel is empty).
            lock (_gate) _lastError = ex.Message;
            _log.LogWarning(ex, "YouTube channel gallery sync failed — gallery unchanged");
            if (force) throw;
            return (0, 0, 0, false);
        }
    }
}
