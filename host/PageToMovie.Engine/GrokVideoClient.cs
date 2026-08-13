using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// xAI Grok video generate / poll / download client.
/// </summary>
public sealed class GrokVideoClient : IVideoClient
{
    public const string ApiBase = SupportedModelCatalog.XaiApiBase;
    /// <summary>Full prompt first; on length errors, shorten and retry up to this many times.</summary>
    public const int MaxPromptLengthRetries = 5;

    /// <summary>
    /// Request xAI persist the generated video to the Files API so a later video-edit call can
    /// reuse its file_id instead of re-uploading (see <see cref="IVideoEditClient"/>). Capped at
    /// xAI's own maximum (30 days) — file_id reuse is always an optimization on top of the
    /// locally-stored clip, never required, so a shorter/longer TTL only affects how often the
    /// edit path falls back to base64 upload, not correctness.
    /// </summary>
    private const int StorageExpiresAfterSeconds = 2_592_000;

    private readonly HttpClient _http;
    private readonly PageToMovieOptions _opts;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GrokVideoClient> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    /// <summary>request_id → stored file reference, populated by <see cref="PollForVideoUrlAsync"/>
    /// when the completed job's response includes <c>video.file_output</c> (i.e. storage was
    /// requested and succeeded). In-memory only — a process restart simply means the next edit on
    /// that clip falls back to base64 upload, same as if storage had expired.</summary>
    private readonly ConcurrentDictionary<string, (string? FileId, long? ExpiresAtUnixSeconds)> _fileRefs = new();

    public GrokVideoClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokVideoClient> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _http = http;
        _opts = opts.Value;
        _telemetry = telemetry;
        _log = log;
        _errorLogger = errorLogger;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase.TrimEnd('/') + '/');
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    /// <param name="referenceImagePaths">Character/style refs → API <c>reference_images</c> + prompt <c>&lt;IMAGE_n&gt;</c> tags.</param>
    /// <param name="startFrameImagePath">Optional first-frame still (image-to-video). Prefer video continue when possible.</param>
    /// <param name="continueFromVideoPath">Previous clip MP4 — uses <c>/videos/extensions</c> (official continue).</param>
    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null)
    {
        // Catalog maxReferenceImages only — never invent 7 (or any default).
        var videoEntry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video);
        if (videoEntry.MaxReferenceImages is not { } maxRefsForModel)
            throw new InvalidOperationException(
                $"Video model '{videoEntry.Id}' has no maxReferenceImages in models_catalog.json. " +
                "Add the real API limit — do not invent a default.");

        var setup = await BuildSubmitSetupAsync(
            durationSeconds, resolution, model,
            referenceImagePaths, startFrameImagePath, continueFromVideoPath,
            maxRefsForModel, ct).ConfigureAwait(false);

        var original = prompt ?? "";
        Exception? lastLengthError = null;
        for (var attempt = 0; attempt <= MaxPromptLengthRetries; attempt++)
        {
            var (requestId, lengthError) = await TrySubmitAttemptAsync(setup, original, attempt, ct).ConfigureAwait(false);
            if (requestId is not null)
                return requestId;
            lastLengthError = lengthError;
        }

        throw lastLengthError
              ?? new InvalidOperationException("Grok video submit failed after prompt length retries.");
    }

    private sealed class SubmitSetup
    {
        public required List<string> Refs { get; init; }
        public required bool HasContinue { get; init; }
        public required bool HasStart { get; init; }
        public required int DurationSeconds { get; init; }
        public string? VideoUri { get; init; }
        public string? StartUri { get; init; }
        public List<object?>? RefObjs { get; init; }
        public required string Mode { get; init; }
        public required string Kind { get; init; }
        public required List<string> RefNames { get; init; }
        public required int PromptHardCap { get; init; }
        public required string Model { get; init; }
        public required string Resolution { get; init; }
        public string? ContinueFromVideoPath { get; init; }
        public string? StartFrameImagePath { get; init; }
    }

    private static bool IsExistingMediaPath(string? p) =>
        !string.IsNullOrWhiteSpace(p) && File.Exists(p);

    private async Task<SubmitSetup> BuildSubmitSetupAsync(
        int durationSeconds,
        string resolution,
        string model,
        IReadOnlyList<string>? referenceImagePaths,
        string? startFrameImagePath,
        string? continueFromVideoPath,
        int maxRefsForModel,
        CancellationToken ct)
    {
        var refs = (referenceImagePaths ?? Array.Empty<string>())
            .Where(IsExistingMediaPath)
            .Take(maxRefsForModel)
            .ToList();
        var hasContinue = IsExistingMediaPath(continueFromVideoPath);
        var hasStart = IsExistingMediaPath(startFrameImagePath);

        ApplySubmitRefPriority(refs, ref hasContinue, ref hasStart, startFrameImagePath);

        if (hasStart || refs.Count > 0 || hasContinue)
            durationSeconds = ClipDurationEstimator.ResolveActualDurationForModel(
                model, durationSeconds, isExtensionMode: true);

        var (videoUri, startUri, refObjs) = await EncodeSubmitMediaAsync(
            hasContinue, hasStart, continueFromVideoPath, startFrameImagePath, refs, ct).ConfigureAwait(false);

        var promptHardCap = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video)
            .MaxPromptLength
            ?? throw new InvalidOperationException(
                $"Video model has no maxPromptLength in models_catalog.json.");

        return new SubmitSetup
        {
            Refs = refs,
            HasContinue = hasContinue,
            HasStart = hasStart,
            DurationSeconds = durationSeconds,
            VideoUri = videoUri,
            StartUri = startUri,
            RefObjs = refObjs,
            Mode = ResolveSubmitMode(hasContinue, hasStart, refs.Count),
            Kind = hasContinue ? "video_extend" : "video",
            RefNames = refs.Select(Path.GetFileName).OfType<string>().ToList(),
            PromptHardCap = promptHardCap,
            Model = model,
            Resolution = resolution,
            ContinueFromVideoPath = continueFromVideoPath,
            StartFrameImagePath = startFrameImagePath,
        };
    }

    private void ApplySubmitRefPriority(
        List<string> refs, ref bool hasContinue, ref bool hasStart, string? startFrameImagePath)
    {
        // Priority: video-continue > start-frame > reference images > text
        if (hasContinue)
        {
            refs.Clear();
            hasStart = false;
            return;
        }
        if (hasStart && refs.Count > 0)
        {
            _log.LogWarning(
                "Grok video: start frame + reference_images not allowed together — using start frame only ({Start})",
                Path.GetFileName(startFrameImagePath));
            refs.Clear();
        }
    }

    private static string ResolveSubmitMode(bool hasContinue, bool hasStart, int refCount)
    {
        if (hasContinue) return "video-extend";
        if (hasStart) return "image-to-video";
        if (refCount > 0) return "reference-to-video";
        return "text-to-video";
    }

    private static async Task<(string? VideoUri, string? StartUri, List<object?>? RefObjs)> EncodeSubmitMediaAsync(
        bool hasContinue,
        bool hasStart,
        string? continueFromVideoPath,
        string? startFrameImagePath,
        List<string> refs,
        CancellationToken ct)
    {
        if (hasContinue)
        {
            string? videoUri = null;
            if (continueFromVideoPath is not null)
                videoUri = await FileToDataUriAsync(continueFromVideoPath, ct);
            return (videoUri, null, null);
        }
        if (hasStart)
        {
            string? startUri = null;
            if (startFrameImagePath is not null)
                startUri = await FileToDataUriAsync(startFrameImagePath, ct);
            return (null, startUri, null);
        }
        if (refs.Count == 0)
            return (null, null, null);

        var refObjs = new List<object?>();
        foreach (var path in refs)
            refObjs.Add(new Dictionary<string, object?> { ["url"] = await FileToDataUriAsync(path, ct) });
        return (null, null, refObjs);
    }

    private async Task<(string? RequestId, Exception? LengthError)> TrySubmitAttemptAsync(
        SubmitSetup setup, string original, int attempt, CancellationToken ct)
    {
        var current = attempt == 0
            ? original
            : ClipVideoPromptBuilder.ShortenPromptForRetry(original, attempt, setup.PromptHardCap);

        if (attempt > 0)
        {
            _log.LogWarning(
                "Grok video: prompt length reject — retry {Attempt}/{Max} promptLen {From}→{To}",
                attempt, MaxPromptLengthRetries, original.Length, current.Length);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var (requestId, endpoint) = await SubmitOnceAsync(setup, current, ct).ConfigureAwait(false);
            await _telemetry.LogApiCallAsync(BuildSubmitTelemetry(setup, current, attempt + 1, sw, requestId, ok: true, error: null), ct);
            return (requestId, null);
        }
        catch (Exception ex) when (
            attempt < MaxPromptLengthRetries &&
            ClipVideoPromptBuilder.IsPromptTooLongError(ex.Message))
        {
            await LogSubmitAttemptFailureAsync(setup, sw, current, attempt, ex, ct).ConfigureAwait(false);
            _log.LogWarning(ex, "Grok video: prompt too long (attempt {Attempt})", attempt);
            return (null, ex);
        }
        catch (Exception ex)
        {
            await LogSubmitAttemptFailureAsync(setup, sw, current, attempt, ex, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(string RequestId, string Endpoint)> SubmitOnceAsync(
        SubmitSetup setup, string current, CancellationToken ct)
    {
        if (setup.HasContinue)
        {
            var requestId = await SubmitExtendOnceAsync(
                current, setup.DurationSeconds, setup.Resolution, setup.Model,
                setup.VideoUri ?? string.Empty, setup.ContinueFromVideoPath ?? string.Empty, ct);
            return (requestId, "videos/extensions");
        }

        var freshId = await SubmitFreshOnceAsync(
            current, setup.DurationSeconds, setup.Resolution, setup.Model,
            setup.StartUri, setup.RefObjs, setup.StartFrameImagePath, setup.Refs.Count, ct);
        return (freshId, "videos/generations");
    }

    private ApiCallTelemetry BuildSubmitTelemetry(
        SubmitSetup setup, string current, int attempt, Stopwatch sw, string? requestId, bool ok, string? error) =>
        new()
        {
            Kind = setup.Kind,
            Endpoint = setup.HasContinue ? "videos/extensions" : "videos/generations",
            Model = setup.Model,
            HttpStatus = ok ? 200 : null,
            RequestId = requestId,
            DurationMs = sw.ElapsedMilliseconds,
            Mode = setup.Mode,
            Prompt = current,
            PromptChars = current.Length,
            ReferenceImagePaths = setup.RefNames.Count > 0 ? setup.RefNames : null,
            RefsAttached = setup.Refs.Count > 0 && !setup.HasContinue,
            Resolution = setup.Resolution,
            DurationSec = setup.DurationSeconds,
            Attempt = attempt,
            Error = error,
            Ok = ok,
        };

    private async Task LogSubmitAttemptFailureAsync(
        SubmitSetup setup, Stopwatch sw, string current, int attempt, Exception ex, CancellationToken ct) =>
        await _telemetry.LogApiCallAsync(
            BuildSubmitTelemetry(setup, current, attempt, sw, requestId: null, ok: false, error: ex.Message), ct);

    private async Task<string> SubmitExtendOnceAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        string videoUri,
        string continueFromVideoPath,
        CancellationToken ct)
    {
        var extPayload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            // duration = length of NEW extension only (not total)
            ["duration"] = durationSeconds,
            ["video"] = new Dictionary<string, object?> { ["url"] = videoUri },
            // Ask xAI to persist the result to the Files API so a later video-edit can reuse its
            // file_id — see StorageExpiresAfterSeconds. "filename" is required by the API (a 422
            // without it) — content doesn't matter to xAI, just needs to be a valid, unique name.
            ["storage_options"] = new Dictionary<string, object?>
            {
                ["expires_after"] = StorageExpiresAfterSeconds,
                ["filename"] = $"grok-video-{Guid.NewGuid():N}.mp4",
            },
        };
        // resolution/aspect may be ignored on extensions; still send when API allows
        if (!string.IsNullOrWhiteSpace(resolution))
            extPayload["resolution"] = resolution;

        _log.LogInformation(
            "Grok video EXTEND from={Prev} extensionDur={Dur}s promptLen={Len}",
            Path.GetFileName(continueFromVideoPath), durationSeconds, prompt.Length);

        // Submit retry is safe here even though it's not idempotent: if the response is lost after
        // the server actually created the job, we never got request_id back either way — there's no
        // way to find/reuse that job, retried automatically or not. A human clicking "try again"
        // after seeing the same failure has an identical blind spot; this just does it for them,
        // with proper Retry-After-aware backoff instead of an immediate manual re-click.
        return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
            async _ =>
            {
                using var extResp = await SendJsonAsync(HttpMethod.Post, "videos/extensions", extPayload, ct);
                var extBody = await extResp.Content.ReadAsStringAsync(ct);
                if (!extResp.IsSuccessStatusCode)
                    throw ChatHttpStatusException.FromResponse(extResp,
                        $"Grok video extend HTTP {(int)extResp.StatusCode}: {Trim(extBody, 500)}");

                using var extDoc = JsonDocument.Parse(extBody);
                if (!extDoc.RootElement.TryGetProperty("request_id", out var extRid) ||
                    extRid.GetString() is not { Length: > 0 } extId)
                {
                    throw new InvalidOperationException(
                        $"Grok extend response missing request_id: {Trim(extBody, 300)}");
                }
                return extId;
            },
            isTransient: AiRetryPolicy.IsTransientChatFailure,
            maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
            backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
            onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("grok_video_extend_submit", model, $"durationSec={durationSeconds}", attemptNum, ex, ct),
            ct: ct).ConfigureAwait(false);
    }

    private async Task<string> SubmitFreshOnceAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        string? startUri,
        List<object?>? refObjs,
        string? startFrameImagePath,
        int refCount,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["duration"] = durationSeconds,
            ["aspect_ratio"] = ResolveAspectRatio(model).ToApiString(),
            ["resolution"] = resolution,
            // Ask xAI to persist the result to the Files API so a later video-edit can reuse its
            // file_id — see StorageExpiresAfterSeconds. "filename" is required by the API (a 422
            // without it) — content doesn't matter to xAI, just needs to be a valid, unique name.
            ["storage_options"] = new Dictionary<string, object?>
            {
                ["expires_after"] = StorageExpiresAfterSeconds,
                ["filename"] = $"grok-video-{Guid.NewGuid():N}.mp4",
            },
        };

        if (startUri is not null)
        {
            payload["image"] = new Dictionary<string, object?> { ["url"] = startUri };
            _log.LogInformation(
                "Grok video image-to-video startFrame={Frame} promptLen={Len} duration={Dur}s",
                Path.GetFileName(startFrameImagePath), prompt.Length, durationSeconds);
        }
        else if (refObjs is { Count: > 0 })
        {
            payload["reference_images"] = refObjs;
            _log.LogInformation(
                "Grok video reference-to-video refs={N} promptLen={Len} duration={Dur}s",
                refCount, prompt.Length, durationSeconds);
        }
        else
        {
            _log.LogInformation(
                "Grok video text-to-video promptLen={Len} duration={Dur}s",
                prompt.Length, durationSeconds);
        }

        // Same reasoning as SubmitExtendOnceAsync: a lost response here is unrecoverable either
        // way (no request_id to find the job), so automatic retry is no riskier than the manual
        // retry a human would do on seeing the same failure.
        return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
            async _ =>
            {
                using var resp = await SendJsonAsync(HttpMethod.Post, "videos/generations", payload, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw ChatHttpStatusException.FromResponse(resp,
                        $"Grok submit HTTP {(int)resp.StatusCode}: {Trim(body, 400)}");

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("request_id", out var rid) ||
                    rid.GetString() is not { Length: > 0 } id)
                {
                    throw new InvalidOperationException($"Grok response missing request_id: {Trim(body, 300)}");
                }
                return id;
            },
            isTransient: AiRetryPolicy.IsTransientChatFailure,
            maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
            backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
            onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("grok_video_submit", model, $"durationSec={durationSeconds}", attemptNum, ex, ct),
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fresh-generation aspect ratio: catalog's <c>DefaultAspectRatio</c> for the requested model,
    /// falling back to the historical hardcoded "16:9" for models the catalog doesn't cover yet.
    /// (Not used for video-extend — xAI docs say aspect_ratio isn't accepted there; the extension
    /// always inherits the source clip's ratio.)
    /// </summary>
    private static AspectRatio ResolveAspectRatio(string model) =>
        MediaEngineEnumExtensions.ParseAspectRatio(SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).DefaultAspectRatio ?? "16:9");

    private static Task<string> FileToDataUriAsync(string path, CancellationToken ct) =>
        MediaDataUri.FileToDataUriAsync(path, ct);

    public async Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, _opts.GrokTimeoutSeconds));
        var poll = Math.Max(2, _opts.GrokPollSeconds);
        var sw = Stopwatch.StartNew();
        var polls = 0;
        var retriedAnyPoll = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            polls++;

            var (body, retried) = await PollOnceGetAsync(requestId, polls, sw, ct).ConfigureAwait(false);
            if (retried)
                retriedAnyPoll = true;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
                return await HandlePollDoneAsync(requestId, root, sw, polls, retriedAnyPoll, ct).ConfigureAwait(false);

            if (IsPollFailedOrExpired(status))
                await HandlePollFailedOrExpiredAsync(requestId, root, body, status, sw, polls, ct).ConfigureAwait(false);

            var progress = root.TryGetProperty("progress", out var pr) ? pr.ToString() : null;
            onProgress?.Invoke(FormatPollProgress(status, progress));
            await Task.Delay(TimeSpan.FromSeconds(poll), ct);
        }

        await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.TimedOut, sw.ElapsedMilliseconds, polls, ok: false, $"timed out after {_opts.GrokTimeoutSeconds}s", ct);
        throw new TimeoutException($"Grok job timed out after {_opts.GrokTimeoutSeconds}s");
    }

    private static bool IsPollFailedOrExpired(string? status) =>
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase);

    private static string FormatPollProgress(string? status, string? progress) =>
        progress is null ? $"status={status}" : $"status={status} ({progress}%)";

    private async Task<(string Body, bool Retried)> PollOnceGetAsync(
        string requestId, int polls, Stopwatch sw, CancellationToken ct)
    {
        var retried = false;
        try
        {
            var body = await FetchPollBodyAsync(requestId, polls, () => retried = true, ct).ConfigureAwait(false);
            return (body, retried);
        }
        catch (Exception ex)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video_poll",
                Endpoint = $"videos/{requestId}",
                RequestId = requestId,
                HttpStatus = ex is ChatHttpStatusException hse ? hse.StatusCode : null,
                DurationMs = sw.ElapsedMilliseconds,
                Attempt = polls,
                Error = Trim(ex.Message, 400),
                Ok = false,
            }, ct);
            await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.PollFailed, sw.ElapsedMilliseconds, polls, ok: false, ex.Message, ct);
            throw;
        }
    }

    private async Task<string> FetchPollBodyAsync(
        string requestId, int polls, Action markRetried, CancellationToken ct) =>
        await AiRetryPolicy.ExecuteWithTransientRetryAsync(
            _ => GetPollResponseAsync(requestId, ct),
            isTransient: AiRetryPolicy.IsTransientChatFailure,
            maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
            backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
            onRetry: (attemptNum, ex) =>
            {
                markRetried();
                return _errorLogger.LogRetryAttemptAsync("grok_video_poll", null, $"requestId={requestId}; poll={polls}", attemptNum, ex, ct);
            },
            ct: ct).ConfigureAwait(false);

    private async Task<string> GetPollResponseAsync(string requestId, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"videos/{requestId}", content: null, ct);
        var b = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw ChatHttpStatusException.FromResponse(resp,
                $"Grok poll HTTP {(int)resp.StatusCode}: {Trim(b, 400)}");
        return b;
    }

    private async Task<string> HandlePollDoneAsync(
        string requestId, JsonElement root, Stopwatch sw, int polls, bool retriedAnyPoll, CancellationToken ct)
    {
        if (!TryReadVideoUrl(root, out var url, out var video))
        {
            await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.ProviderFailed, sw.ElapsedMilliseconds, polls, ok: false, "done with no video.url", ct);
            throw new InvalidOperationException("Grok done with no video.url");
        }

        TryCacheFileOutput(requestId, video);
        await _telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = "video_poll",
            Endpoint = $"videos/{requestId}",
            RequestId = requestId,
            HttpStatus = 200,
            DurationMs = sw.ElapsedMilliseconds,
            Attempt = polls,
            Mode = "done",
            Ok = true,
        }, ct);
        await _telemetry.LogOutcomeAsync(null, requestId, retriedAnyPoll ? VideoJobOutcome.OkAfterRetry : VideoJobOutcome.Ok, sw.ElapsedMilliseconds, polls, ok: true, null, ct);
        return url;
    }

    private static bool TryReadVideoUrl(JsonElement root, out string url, out JsonElement video)
    {
        url = "";
        video = default;
        if (!root.TryGetProperty("video", out video))
            return false;
        if (!video.TryGetProperty("url", out var urlEl))
            return false;
        if (urlEl.GetString() is not { Length: > 0 } found)
            return false;
        url = found;
        return true;
    }

    private void TryCacheFileOutput(string requestId, JsonElement video)
    {
        if (!video.TryGetProperty("file_output", out var fileOutput) ||
            fileOutput.ValueKind != JsonValueKind.Object)
            return;

        var fileId = fileOutput.TryGetProperty("file_id", out var fid) ? fid.GetString() : null;
        long? expiresAt = null;
        if (fileOutput.TryGetProperty("expires_at", out var exp) && exp.TryGetInt64(out var expVal))
            expiresAt = expVal;
        if (!string.IsNullOrWhiteSpace(fileId))
            _fileRefs[requestId] = (fileId, expiresAt);
    }

    private async Task HandlePollFailedOrExpiredAsync(
        string requestId, JsonElement root, string body, string? status, Stopwatch sw, int polls, CancellationToken ct)
    {
        var detail = root.TryGetProperty("error", out var err) ? err.ToString() : body;
        await _telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = "video_poll",
            Endpoint = $"videos/{requestId}",
            RequestId = requestId,
            HttpStatus = 200,
            DurationMs = sw.ElapsedMilliseconds,
            Attempt = polls,
            Mode = status,
            Error = Trim(detail, 500),
            Ok = false,
        }, ct);
        await _telemetry.LogOutcomeAsync(null, requestId, string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase) ? VideoJobOutcome.Expired : VideoJobOutcome.ProviderFailed, sw.ElapsedMilliseconds, polls, ok: false, Trim(detail, 500), ct);
        throw new InvalidOperationException($"Grok job {status}: {Trim(detail, 400)}");
    }

    public (string? FileId, long? ExpiresAtUnixSeconds) TryGetStoredFileReference(string requestId) =>
        _fileRefs.TryGetValue(requestId, out var v) ? v : (null, null);

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(destPath);
        if (destDir is not null)
            Directory.CreateDirectory(destDir);
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
        _log.LogInformation("Downloaded {Bytes} bytes → {Path}", new FileInfo(destPath).Length, destPath);
    }

    /// <summary>
    /// Prefer ambient job/request key (multi-user), else process env.
    /// Auth is applied per <see cref="HttpRequestMessage"/> — never on the shared
    /// <see cref="HttpClient.DefaultRequestHeaders"/> (concurrent jobs would race).
    /// </summary>
    private static string? ResolveApiKey() =>
        ApiKeyScope.Current ?? Environment.GetEnvironmentVariable("XAI_API_KEY");

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string uri, object payload, CancellationToken ct)
    {
        var content = JsonContent.Create(payload);
        return await SendAsync(method, uri, content, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, uri) { Content = content };
        var key = ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static string Trim(string s, int n) =>
        s.Length <= n ? s : s[..n];
}
