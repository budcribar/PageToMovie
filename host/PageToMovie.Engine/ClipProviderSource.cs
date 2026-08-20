using System.Text.Json;
using System.Text.RegularExpressions;

namespace PageToMovie.Engine;

/// <summary>
/// The provider-hosted copy of a clip, read from its .clip.json sidecar. Sidecars are named per
/// take (<c>scene_01_clip_02_take_01.clip.json</c>), so lookups go by pattern, newest first — an
/// exact <c>scene_01_clip_02.clip.json</c> lookup silently finds nothing.
///
/// For a video-extend clip the provider's file is the COMBINED video (continuation input + new
/// footage); <see cref="LeadInSeconds"/> is how much of its head belongs to the previous clip.
/// Anyone who streams, verifies or re-uploads the provider copy must drop that head first, or the
/// previous clip's footage and lines play twice.
/// </summary>
public sealed record ClipProviderSource(
    string? SourceUrl,
    string? SourceFileId,
    double LeadInSeconds,
    double? DurationSeconds,
    double? ClipStartSeconds = null,
    double? ClipStopSeconds = null)
{
    public bool HasProviderCopy => !string.IsNullOrWhiteSpace(SourceUrl) || !string.IsNullOrWhiteSpace(SourceFileId);
    public bool IsCombined => LeadInSeconds > 0.1;

    public const string LeadInProperty = "provider_lead_in_seconds";
    public const string ClipStartProperty = "provider_clip_start_seconds";
    public const string ClipStopProperty = "provider_clip_stop_seconds";

    private static readonly Regex ClipNameRx = new(@"scene_(\d{2})_clip_(\d{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Newest sidecar for the clip in <paramref name="videoDir"/>, or null.</summary>
    public static string? FindLatestSidecarPath(string videoDir, int scene, int clip)
    {
        if (!Directory.Exists(videoDir)) return null;
        return new DirectoryInfo(videoDir)
            .EnumerateFiles($"scene_{scene:D2}_clip_{clip:D2}*.clip.json")
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>Sidecar for the clip named by an mp4 path (<c>…/scene_01_clip_02[_take_01].mp4</c>).</summary>
    public static string? FindLatestSidecarPathForMp4(string mp4Path)
    {
        var m = ClipNameRx.Match(Path.GetFileName(mp4Path));
        if (!m.Success) return null;
        return FindLatestSidecarPath(Path.GetDirectoryName(mp4Path) ?? "", int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    public static ClipProviderSource? Read(string? sidecarPath)
    {
        if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var r = doc.RootElement;
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double? Num(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
            return new ClipProviderSource(
                Str("source_url"),
                Str("source_file_id"),
                Num(LeadInProperty) ?? 0,
                Num("duration_seconds"),
                Num(ClipStartProperty),
                Num(ClipStopProperty));
        }
        catch { return null; }
    }

    public static ClipProviderSource? ReadForMp4(string mp4Path) => Read(FindLatestSidecarPathForMp4(mp4Path));
    public static ClipProviderSource? ReadForClip(string videoDir, int scene, int clip) => Read(FindLatestSidecarPath(videoDir, scene, clip));

    /// <summary>A temp copy of the clip. <see cref="LeadInSecondsRemaining"/> is > 0 when the copy is
    /// the combined extend video: consumers must skip that head (the API host never trims).</summary>
    public sealed record Materialized(string Path, double LeadInSecondsRemaining)
    {
        public bool IsStandalone => LeadInSecondsRemaining <= 0.1;
    }

    /// <summary>
    /// URL first (durable <c>file_output.public_url</c> or poll <c>video.url</c>), then Files
    /// API <c>file_id</c> as a best-effort (Imagine ids are generate-only). Combined extend
    /// copies stay combined.
    /// </summary>
    public static async Task<T?> TryOpenAsync<T>(
        string? sourceUrl,
        string? sourceFileId,
        Func<string, CancellationToken, Task<T?>> openUrl,
        Func<string, CancellationToken, Task<T?>> openFileId,
        CancellationToken ct) where T : class
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            var fromUrl = await openUrl(sourceUrl, ct).ConfigureAwait(false);
            if (fromUrl is not null) return fromUrl;
        }
        if (!string.IsNullOrWhiteSpace(sourceFileId))
            return await openFileId(sourceFileId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Bring the clip's bytes to a temp file from the provider copy (<c>source_url</c>, then
    /// <c>source_file_id</c> when the public link is empty or 404s). Combined extend files stay
    /// combined — the API host never spawns native ffmpeg. The copy is returned with
    /// <see cref="Materialized.LeadInSecondsRemaining"/> set so the consumer can offset
    /// (dialogue verify / duration probe) instead of trimming. The browser slices playback via
    /// <c>ProviderLeadInSeconds</c>. Returns null when there is no usable provider copy. Caller
    /// deletes the temp file. Nothing is written into the project — media does not live on the server.
    /// </summary>
    public static async Task<Materialized?> TryMaterializeAsync(
        ClipProviderSource? src,
        CancellationToken ct,
        Func<string, string, CancellationToken, Task>? download = null,
        Func<string, string, CancellationToken, Task>? downloadFileId = null)
    {
        if (src is null || !src.HasProviderCopy) return null;
        var raw = Path.Combine(Path.GetTempPath(), $"ptm_clip_{Guid.NewGuid():N}.mp4");
        try
        {
            var got = await TryOpenAsync(
                src.SourceUrl,
                src.SourceFileId,
                (url, token) => TryDownloadUrlToFileAsync(url, raw, download, token),
                (fileId, token) => TryDownloadFileIdToFileAsync(fileId, raw, downloadFileId, token),
                ct).ConfigureAwait(false);
            if (got is not true || !File.Exists(raw) || new FileInfo(raw).Length < 1024)
            {
                TryDelete(raw);
                return null;
            }
            // Combined extend files stay combined; consumers skip the head via LeadInSecondsRemaining.
            return new Materialized(raw, src.IsCombined ? src.LeadInSeconds : 0);
        }
        catch
        {
            TryDelete(raw);
            return null;
        }
    }

    /// <summary>Boxed <see cref="bool"/> so <see cref="TryOpenAsync{T}"/> can treat a failed
    /// URL fetch as null and fall through to <c>file_id</c>.</summary>
    private static async Task<object?> TryDownloadUrlToFileAsync(
        string url, string dest, Func<string, string, CancellationToken, Task>? download, CancellationToken ct)
    {
        try
        {
            if (url.StartsWith("fixture:", StringComparison.OrdinalIgnoreCase))
            {
                var fixture = url["fixture:".Length..];
                if (!File.Exists(fixture)) return null;
                File.Copy(fixture, dest, overwrite: true);
                return File.Exists(dest) ? BooleanBox.True : null;
            }
            if (download is not null)
                await download(url, dest, ct).ConfigureAwait(false);
            else
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                await using var fs = File.Create(dest);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            return File.Exists(dest) && new FileInfo(dest).Length >= 1024 ? BooleanBox.True : null;
        }
        catch
        {
            TryDelete(dest);
            return null;
        }
    }

    private static async Task<object?> TryDownloadFileIdToFileAsync(
        string fileId, string dest, Func<string, string, CancellationToken, Task>? downloadFileId, CancellationToken ct)
    {
        if (downloadFileId is null) return null;
        try
        {
            await downloadFileId(fileId, dest, ct).ConfigureAwait(false);
            return File.Exists(dest) && new FileInfo(dest).Length >= 1024 ? BooleanBox.True : null;
        }
        catch
        {
            TryDelete(dest);
            return null;
        }
    }

    private static class BooleanBox
    {
        public static readonly object True = true;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
