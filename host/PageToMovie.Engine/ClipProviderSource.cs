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
public sealed record ClipProviderSource(string? SourceUrl, string? SourceFileId, double LeadInSeconds, double? DurationSeconds)
{
    public bool HasProviderCopy => !string.IsNullOrWhiteSpace(SourceUrl) || !string.IsNullOrWhiteSpace(SourceFileId);
    public bool IsCombined => LeadInSeconds > 0.1;

    public const string LeadInProperty = "provider_lead_in_seconds";

    private static readonly Regex ClipNameRx = new(@"scene_(\d{2})_clip_(\d{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
            return new ClipProviderSource(Str("source_url"), Str("source_file_id"), Num(LeadInProperty) ?? 0, Num("duration_seconds"));
        }
        catch { return null; }
    }

    public static ClipProviderSource? ReadForMp4(string mp4Path) => Read(FindLatestSidecarPathForMp4(mp4Path));
    public static ClipProviderSource? ReadForClip(string videoDir, int scene, int clip) => Read(FindLatestSidecarPath(videoDir, scene, clip));

    /// <summary>A temp copy of the clip. <see cref="LeadInSecondsRemaining"/> is > 0 when the copy is
    /// still the combined video (no native ffmpeg to trim it): consumers must skip that head.</summary>
    public sealed record Materialized(string Path, double LeadInSecondsRemaining)
    {
        public bool IsStandalone => LeadInSecondsRemaining <= 0.1;
    }

    /// <summary>
    /// Bring the clip's bytes to a temp file from the provider copy: download source_url and, when
    /// the copy is combined, drop the lead-in with native ffmpeg. Where ffmpeg is not available
    /// (production image has none — the browser is the trimmer) the combined file is returned with
    /// <see cref="Materialized.LeadInSecondsRemaining"/> set so the consumer can offset instead.
    /// Returns null when there is no usable provider copy. Caller deletes the temp file. Nothing is
    /// written into the project — media does not live on the server.
    /// </summary>
    public static async Task<Materialized?> TryMaterializeAsync(
        ClipProviderSource? src, CancellationToken ct, Func<string, string, CancellationToken, Task>? download = null)
    {
        if (src is null || string.IsNullOrWhiteSpace(src.SourceUrl)) return null;
        var raw = Path.Combine(Path.GetTempPath(), $"ptm_clip_{Guid.NewGuid():N}.mp4");
        try
        {
            if (src.SourceUrl.StartsWith("fixture:", StringComparison.OrdinalIgnoreCase))
            {
                // Fakes: the provider copy is a local fixture file.
                var fixture = src.SourceUrl["fixture:".Length..];
                if (!File.Exists(fixture)) return null;
                File.Copy(fixture, raw, overwrite: true);
            }
            else if (download is not null)
                await download(src.SourceUrl, raw, ct).ConfigureAwait(false);
            else
            {
                using var resp = await Http.GetAsync(src.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(raw);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            if (!File.Exists(raw) || new FileInfo(raw).Length < 1024) { TryDelete(raw); return null; }
            if (!src.IsCombined) return new Materialized(raw, 0);

            var trimmed = Path.Combine(Path.GetTempPath(), $"ptm_clip_{Guid.NewGuid():N}.mp4");
            if (NativeFfmpeg.TryTrimHead(raw, trimmed, src.LeadInSeconds))
            {
                TryDelete(raw);
                return new Materialized(trimmed, 0);
            }
            TryDelete(trimmed);
            return new Materialized(raw, src.LeadInSeconds);
        }
        catch
        {
            TryDelete(raw);
            return null;
        }
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
