using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Probe media duration via MP4 box parse and duration sidecars (no native ffmpeg).
/// </summary>
public sealed class MediaDurationProbe
{
    private const int MaxCacheEntries = 20_000;
    private const int TrimToEntries = 15_000;

    private readonly ConcurrentDictionary<string, (long Ticks, long Length, double Sec)> _cache = new();
    private readonly ILogger<MediaDurationProbe> _log;

    public MediaDurationProbe(IOptions<PageToMovieOptions> opts, ILogger<MediaDurationProbe> log)
    {
        _ = opts;
        _log = log;
    }

    /// <summary>Duration in seconds, or null if unknown / missing file.</summary>
    public Task<double?> GetDurationSecondsAsync(string? mediaPath, CancellationToken ct = default) =>
        TryProbeSecondsAsync(mediaPath, ct);

    public async Task<double?> GetSceneActualDurationSecondsAsync(
        string? compositePath,
        IEnumerable<string> exactClipPaths,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(compositePath) && File.Exists(compositePath))
        {
            var d = await GetDurationSecondsAsync(compositePath, ct).ConfigureAwait(false);
            if (d is > 0) return d;
        }

        double sum = 0;
        var any = false;
        foreach (var clip in exactClipPaths)
        {
            var d = await GetDurationSecondsAsync(clip, ct).ConfigureAwait(false);
            if (d is > 0)
            {
                sum += d.Value;
                any = true;
            }
        }

        return any ? sum : null;
    }

    public static async Task WriteDurationSidecarAsync(
        string mediaPath,
        double durationSeconds,
        CancellationToken ct = default)
    {
        try
        {
            if (durationSeconds <= 0 || string.IsNullOrWhiteSpace(mediaPath)) return;
            var path = mediaPath + ".duration.json";
            var doc = new Dictionary<string, object?>
            {
                ["seconds"] = Math.Round(durationSeconds, 3),
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc) + "\n", ct)
                .ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    public async Task<double?> TryProbeSecondsAsync(string? mediaPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return null;

        try
        {
            var fi = new FileInfo(mediaPath);
            if (fi.Length < 1024) return null;

            var key = fi.FullName;
            if (_cache.TryGetValue(key, out var hit) &&
                hit.Ticks == fi.LastWriteTimeUtc.Ticks &&
                hit.Length == fi.Length)
                return hit.Sec;

            var fromManifest = await TryReadManifestDurationAsync(mediaPath, ct).ConfigureAwait(false);
            if (fromManifest is > 0)
            {
                SetCache(key, fi.LastWriteTimeUtc.Ticks, fi.Length, fromManifest.Value);
                return fromManifest;
            }

            var fromMp4 = await Mp4DurationReader.TryReadSecondsAsync(fi.FullName, ct).ConfigureAwait(false);
            if (fromMp4 is > 0)
            {
                SetCache(key, fi.LastWriteTimeUtc.Ticks, fi.Length, fromMp4.Value);
                return fromMp4;
            }
        }
        catch (Exception ex)
        {
            if (_log.IsEnabled(LogLevel.Debug))
                _log.LogDebug(ex, "Duration probe failed for {Path}", mediaPath);
        }

        return null;
    }

    private void SetCache(string key, long ticks, long length, double sec)
    {
        _cache[key] = (ticks, length, sec);
        if (_cache.Count <= MaxCacheEntries) return;

        // First: drop entries whose backing file is gone — free and always safe.
        foreach (var k in _cache.Keys)
        {
            if (!File.Exists(k))
                _cache.TryRemove(k, out _);
        }

        // Files that stay on disk (the common case) never get evicted by the pass above, so the
        // cache would otherwise grow unbounded despite the size check. Trim the oldest-by-mtime
        // entries so MaxCacheEntries is an actual bound, not just a "delete stale" sweep.
        if (_cache.Count > MaxCacheEntries)
        {
            var toRemove = _cache
                .OrderBy(kv => kv.Value.Ticks)
                .Take(_cache.Count - TrimToEntries)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in toRemove)
                _cache.TryRemove(k, out _);
        }
    }

    private static async Task<double?> TryReadManifestDurationAsync(string mediaPath, CancellationToken ct = default)
    {
        foreach (var candidate in new[]
                 {
                     mediaPath + ".sources.json",
                     mediaPath + ".duration.json",
                 })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var bytes = await File.ReadAllBytesAsync(candidate, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;
                if (root.TryGetProperty("totalDurationSeconds", out var t) && t.TryGetDouble(out var td) && td > 0)
                    return td;
                if (root.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var sd) && sd > 0)
                    return sd;
            }
            catch { /* ignore */ }
        }

        return null;
    }

    /// <summary>Parse ffmpeg-style Duration line (kept for unit tests / log parsing).</summary>
    public static double? TryParseFfmpegDurationLine(string? ffmpegStderrOrLine)
    {
        if (string.IsNullOrWhiteSpace(ffmpegStderrOrLine)) return null;
        var m = CommonRegex.Match(
            ffmpegStderrOrLine,
            @"Duration:\s*(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var h = int.Parse(m.Groups[1].Value, inv);
            var min = int.Parse(m.Groups[2].Value, inv);
            var sec = double.Parse(m.Groups[3].Value, inv);
            var total = h * 3600 + min * 60 + sec;
            return total > 0.05 ? total : null;
        }
        catch
        {
            return null;
        }
    }
}
