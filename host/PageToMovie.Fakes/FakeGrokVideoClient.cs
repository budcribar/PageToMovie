using System.Collections.Concurrent;
using System.Diagnostics;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Fakes;

/// <summary>
/// Fake video client: delay + real MP4 fixtures (1s / 5s / 10s).
/// Extensions optionally concat previous clip + new segment when ffmpeg is on PATH.
/// </summary>
public sealed class FakeGrokVideoClient : IVideoClient
{
    private const string ExtendPrefix = "extend:";

    private readonly PageToMovieOptions _opts;
    private readonly ILogger<FakeGrokVideoClient> _log;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ConcurrentDictionary<string, string> _pending = new();
    private readonly ConcurrentDictionary<string, (string? FileId, long? ExpiresAtUnixSeconds)> _fileRefs = new();
    private int _submitCount;
    private int _clipRoundRobin;

    public FakeGrokVideoClient(IOptions<PageToMovieOptions> opts, ILogger<FakeGrokVideoClient> log, ProjectTelemetryService telemetry)
    {
        _opts = opts.Value;
        _log = log;
        _telemetry = telemetry;
    }

    public bool IsConfigured => true;

    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null,
        string? aspectRatio = null)
    {
        var n = Interlocked.Increment(ref _submitCount);
        var fakes = _opts.Fakes ?? new FakesOptions();
        if (fakes.RateLimitEveryN > 0 && n % fakes.RateLimitEveryN == 0)
            throw new InvalidOperationException("Fake 429: rate limit (RateLimitEveryN)");

        await Task.Delay(Math.Max(0, fakes.VideoDelayMs), ct);

        if (fakes.FailRate > 0 && Random.Shared.NextDouble() < fakes.FailRate)
            throw new InvalidOperationException("Fake video generation failed (FailRate)");

        // Honor catalog feature flags for the selected video model so fakes can exercise
        // continue / ref-count / duration combinations the same way real providers would.
        ValidateAgainstCatalog(model, durationSeconds, referenceImagePaths, continueFromVideoPath);

        var id = "fake-" + Guid.NewGuid().ToString("N")[..12];
        var fixture = ResolveFixturePath(fakes.VideoMode, durationSeconds, Interlocked.Increment(ref _clipRoundRobin));
        // Mark extensions so Download can concat prev + new segment when possible
        if (!string.IsNullOrWhiteSpace(continueFromVideoPath) && File.Exists(continueFromVideoPath))
            _pending[id] = ExtendPrefix + continueFromVideoPath + "|" + fixture;
        else
            _pending[id] = fixture;
        _log.LogInformation(
            "Fake video submit {Id} duration={Dur}s fixture={Fixture} refs={Refs} startFrame={Start} continue={Cont} promptLen={Len}",
            id, durationSeconds, Path.GetFileName(fixture),
            referenceImagePaths?.Count ?? 0,
            startFrameImagePath is null ? "-" : Path.GetFileName(startFrameImagePath),
            continueFromVideoPath is null ? "-" : Path.GetFileName(continueFromVideoPath),
            prompt?.Length ?? 0);
        try
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = string.IsNullOrWhiteSpace(continueFromVideoPath) ? "video" : "video-extend",
                Model = model,
                PromptChars = prompt?.Length ?? 0,
                DurationSec = durationSeconds,
                Resolution = resolution,
                RefsAttached = referenceImagePaths is { Count: > 0 },
                Fakes = true,
                Ok = true,
            }, ct).ConfigureAwait(false);
        }
        catch { /* telemetry is best-effort */ }
        return id;
    }

    public async Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        onProgress?.Invoke("status=pending (fake)");
        await Task.Delay(Math.Max(50, (_opts.Fakes?.VideoDelayMs ?? 0) / 4), ct);
        onProgress?.Invoke("status=done (fake)");
        if (!_pending.TryGetValue(requestId, out var fixture))
        {
            await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.ProviderFailed, 0, 1, ok: false, "unknown fake request_id", ct, fakes: true);
            throw new InvalidOperationException($"Unknown fake request_id {requestId}");
        }
        await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.Ok, 0, 1, ok: true, null, ct, fakes: true);
        // Simulate xAI's optional Files-API storage (real GrokVideoClient requests it on every
        // submit) so fakes-mode tests can exercise the video-edit file_id-reuse path without a
        // live account — a generous unexpired TTL, since exercising the "expired" branch is the
        // edit-side fake's job (FakeGrokVideoEditClient's RejectFileIdEdits knob), not this one's.
        _fileRefs[requestId] = ("fake-file-" + Guid.NewGuid().ToString("N")[..12],
            DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());
        return "fake-fixture:" + fixture;
    }

    public (string? FileId, long? ExpiresAtUnixSeconds) TryGetStoredFileReference(string requestId) =>
        _fileRefs.TryGetValue(requestId, out var v) ? v : (null, null);

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        await Task.Yield();
        string token;
        if (url.StartsWith("fake-fixture:", StringComparison.OrdinalIgnoreCase))
            token = url["fake-fixture:".Length..];
        else if (_pending.Values.FirstOrDefault() is { } f)
            token = f;
        else
            token = ResolveFixturePath(_opts.Fakes?.VideoMode, 10, 0);

        string? prevPath = null;
        string fixture = token;
        if (token.StartsWith(ExtendPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var pipe = token.IndexOf('|');
            if (pipe > 0)
            {
                prevPath = token[ExtendPrefix.Length..pipe];
                fixture = token[(pipe + 1)..];
            }
            else
            {
                fixture = token[ExtendPrefix.Length..];
            }
        }

        if (!File.Exists(fixture))
            throw new FileNotFoundException(
                "Fake video fixture missing. Expected real MP4 under PageToMovie.Fakes/Fixtures/ (clip_tiny_1s.mp4, clip_5s.mp4, clip_merge_10s.mp4).",
                fixture);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath));

        double seconds;
        if (!string.IsNullOrWhiteSpace(prevPath) && File.Exists(prevPath) && await TryConcatAsync(prevPath, fixture, destPath, ct).ConfigureAwait(false))
        {
            seconds = await ProbeDurationSecondsAsync(destPath, ct).ConfigureAwait(false)
                      ?? (await ProbeDurationSecondsAsync(prevPath, ct).ConfigureAwait(false) ?? 0) + (await ProbeDurationSecondsAsync(fixture, ct).ConfigureAwait(false) ?? 0);
            _log.LogInformation("Fake extend concat {Prev} + {Fixture} → {Path}", Path.GetFileName(prevPath), Path.GetFileName(fixture), destPath);
        }
        else
        {
            File.Copy(fixture, destPath, overwrite: true);
            seconds = await ProbeDurationSecondsAsync(destPath, ct).ConfigureAwait(false) ?? GuessDurationFromName(fixture);
        }

        try
        {
            await MediaDurationProbe.WriteDurationSidecarAsync(destPath, seconds, ct);
        }
        catch { /* ignore */ }

        _log.LogInformation("Fake download {Bytes} bytes ({Sec:0.##}s) → {Path}", new FileInfo(destPath).Length, seconds, destPath);
    }

    /// <summary>
    /// Apply <see cref="SupportedModelCatalog"/> limits for <paramref name="model"/>.
    /// Unknown model ids are allowed (caller may use non-catalog test ids) — only known
    /// Video entries enforce continue / refs / duration.
    /// </summary>
    public static void ValidateAgainstCatalog(
        string? model,
        int durationSeconds,
        IReadOnlyList<string>? referenceImagePaths,
        string? continueFromVideoPath)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException(
                "Fake video: model id is required. Unknown/empty models have no capabilities.");

        var entry = SupportedModelCatalog.Find(model.Trim(), ModelCapability.Video);
        if (entry is null)
            throw new InvalidOperationException(
                $"Fake video: model '{model}' is not in models_catalog.json as Video. " +
                "Unknown models have no capabilities.");

        if (!entry.Enabled)
            throw new InvalidOperationException(
                $"Fake video: model '{entry.Id}' is disabled in the catalog.");

        if (!string.IsNullOrWhiteSpace(continueFromVideoPath) && !entry.SupportsVideoContinue)
        {
            throw new InvalidOperationException(
                $"Fake video: model '{entry.Id}' does not support video continue/extend " +
                $"(supportsVideoContinue=false). Choose a model with continue, or omit continueFromVideoPath.");
        }

        var refCount = referenceImagePaths?.Count ?? 0;
        if (entry.MaxReferenceImages == 0 && refCount > 0)
        {
            throw new InvalidOperationException(
                $"Fake video: model '{entry.Id}' does not support reference images (maxReferenceImages=0).");
        }

        if (entry.MaxReferenceImages is { } maxRefs && maxRefs > 0 && refCount > maxRefs)
        {
            throw new InvalidOperationException(
                $"Fake video: model '{entry.Id}' allows at most {maxRefs} reference image(s); got {refCount}.");
        }

        if (entry.AllowedDurationsSeconds is { Count: > 0 } allowed &&
            !allowed.Contains(durationSeconds))
        {
            throw new InvalidOperationException(
                $"Fake video: model '{entry.Id}' only allows durations [{string.Join(", ", allowed)}]s; got {durationSeconds}s.");
        }

        if (entry.MinClipDurationSeconds is { } minD && durationSeconds < minD)
        {
            throw new InvalidOperationException(
                $"Fake video: model '{entry.Id}' min duration is {minD}s; got {durationSeconds}s.");
        }

        var maxD = entry.AbsMaxClipDurationSeconds ?? entry.MaxClipDurationSeconds;
        if (maxD is { } max && durationSeconds > max)
        {
            throw new InvalidOperationException(
                $"Fake video: model '{entry.Id}' max duration is {max}s; got {durationSeconds}s.");
        }
    }

    /// <summary>Pick fixture by duration band; rotate scene-colored clips when available.</summary>
    public static string ResolveFixturePath(string? videoMode, int durationSeconds, int roundRobin = 0)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var light = Path.Combine(fixtures, "clip_tiny_1s.mp4");
        var mid = Path.Combine(fixtures, "clip_5s.mp4");
        var merge = Path.Combine(fixtures, "clip_merge_10s.mp4");

        if (TryLoadLightFixture(videoMode, light, mid, merge, out var lightPath))
            return lightPath;

        if (TrySceneVariedClip(fixtures, durationSeconds, roundRobin, out var scenePath))
            return scenePath;

        var preferred = PreferredByDuration(durationSeconds, light, mid, merge);
        return FirstExisting(preferred, merge, mid, light, fixtures);
    }

    private static bool TryLoadLightFixture(
        string? videoMode, string light, string mid, string merge, out string path)
    {
        path = "";
        if (!string.Equals(videoMode, "LoadLight", StringComparison.OrdinalIgnoreCase))
            return false;
        if (File.Exists(light)) { path = light; return true; }
        if (File.Exists(mid)) { path = mid; return true; }
        if (File.Exists(merge)) { path = merge; return true; }
        return false;
    }

    private static bool TrySceneVariedClip(
        string fixtures, int durationSeconds, int roundRobin, out string path)
    {
        path = "";
        if (!Directory.Exists(fixtures) || durationSeconds <= 0 || durationSeconds > 4)
            return false;
        var scenes = Directory.GetFiles(fixtures, "clip_scene_*_3s.mp4");
        if (scenes.Length == 0)
            return false;
        path = scenes[Math.Abs(roundRobin) % scenes.Length];
        return true;
    }

    private static string PreferredByDuration(int durationSeconds, string light, string mid, string merge)
    {
        if (durationSeconds <= 2)
            return light;
        if (durationSeconds <= 6)
            return File.Exists(mid) ? mid : merge;
        return merge;
    }

    private static string FirstExisting(string preferred, string merge, string mid, string light, string fixtures)
    {
        if (File.Exists(preferred)) return preferred;
        if (File.Exists(merge)) return merge;
        if (File.Exists(mid)) return mid;
        if (File.Exists(light)) return light;

        if (Directory.Exists(fixtures))
        {
            var any = Directory.GetFiles(fixtures, "*.mp4").FirstOrDefault();
            if (any is not null) return any;
        }

        return preferred;
    }

    static double GuessDurationFromName(string path)
    {
        var n = Path.GetFileName(path).ToLowerInvariant();
        if (n.Contains("10s") || n.Contains("merge")) return 10;
        if (n.Contains("5s")) return 5;
        if (n.Contains("3s")) return 3;
        if (n.Contains("1s") || n.Contains("tiny")) return 1;
        return 5;
    }

    static async Task<double?> ProbeDurationSecondsAsync(string path, CancellationToken ct)
    {
        try
        {
            var ffprobe = FindOnPath("ffprobe");
            if (ffprobe is null) return null;
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outputTask = p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            if (p.ExitCode == 0 && double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sec))
                return sec;
        }
        catch { /* optional */ }
        return null;
    }

    static async Task<bool> TryConcatAsync(string prev, string next, string dest, CancellationToken ct)
    {
        try
        {
            var ffmpeg = FindOnPath("ffmpeg");
            if (ffmpeg is null) return false;
            var list = Path.Combine(Path.GetTempPath(), "ptm-fake-concat-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
            var prevEsc = Path.GetFullPath(prev).Replace("'", "'\\''");
            var nextEsc = Path.GetFullPath(next).Replace("'", "'\\''");
            await File.WriteAllTextAsync(list, $"file '{prevEsc}'\nfile '{nextEsc}'\n", ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -f concat -safe 0 -i \"{list}\" -c copy \"{dest}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            try { File.Delete(list); } catch { /* ignore */ }
            return p.ExitCode == 0 && File.Exists(dest) && new FileInfo(dest).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows())
            {
                var exe = candidate + ".exe";
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }
}
