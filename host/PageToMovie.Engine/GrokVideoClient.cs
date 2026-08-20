using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// Persist the generated video on xAI Files. <c>filename</c> is required (422 without it).
    /// <c>public_url: true</c> asks for an unauthenticated durable link. Omit <c>expires_after</c>
    /// so xAI keeps the file until we delete it. Clips are downloadable via Files content GET.
    /// </summary>
    internal static Dictionary<string, object?> PermanentVideoStorageOptions() =>
        new()
        {
            ["filename"] = $"grok-video-{Guid.NewGuid():N}.mp4",
            ["public_url"] = true,
        };

    private readonly HttpClient _http;
    private readonly PageToMovieOptions _opts;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GrokVideoClient> _log;
    private readonly GenerationErrorLogger? _errorLogger;
    private readonly XaiResponsesClient? _files;

    /// <summary>request_id → stored file reference, populated by <see cref="PollForVideoUrlAsync"/>
    /// when the completed job's response includes <c>video.file_output</c> (i.e. storage was
    /// requested and succeeded). In-memory only — a process restart simply means the next edit on
    /// that clip falls back to base64 upload, same as if storage had expired.</summary>
    private readonly ConcurrentDictionary<string, StoredVideoFileRef> _fileRefs = new();

    public GrokVideoClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GrokVideoClient> log,
        GenerationErrorLogger? errorLogger = null,
        XaiResponsesClient? files = null)
    {
        _http = http;
        _opts = opts.Value;
        _telemetry = telemetry;
        _log = log;
        _errorLogger = errorLogger;
        _files = files;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GrokProviderHttp.ResolveApiKey());

    public string CatalogProviderId => SupportedModelCatalog.ProviderIdForApiBase(ApiBase);

    /// <param name="referenceImagePaths">Character/style refs → API <c>reference_images</c> + prompt <c>&lt;IMAGE_n&gt;</c> tags.</param>
    /// <param name="startFrameImagePath">Optional first-frame still (image-to-video). Prefer video continue when possible.</param>
    /// <param name="continueFromVideoPath">Previous clip MP4 — uses <c>/videos/extensions</c> (official continue).</param>
    /// <param name="extendSourceFileId">Predecessor video file_id on xAI provider server — uses <c>/videos/extensions</c> with zero upload.</param>
    public async Task<string> SubmitGenerationAsync(
        string prompt,
        int durationSeconds,
        string resolution,
        string model,
        CancellationToken ct,
        IReadOnlyList<string>? referenceImagePaths = null,
        string? startFrameImagePath = null,
        string? continueFromVideoPath = null,
        string? aspectRatio = null,
        string? extendSourceFileId = null)
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
            maxRefsForModel, aspectRatio, extendSourceFileId, ct).ConfigureAwait(false);

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
        public string? AspectRatio { get; init; }
        public string? ContinueFromVideoPath { get; init; }
        public string? StartFrameImagePath { get; init; }
        public string? ExtendSourceFileId { get; init; }
    }

    private static bool IsExistingMediaPath(string? p) =>
        MediaDataUri.IsExistingMediaPath(p);

    private async Task<SubmitSetup> BuildSubmitSetupAsync(
        int durationSeconds,
        string resolution,
        string model,
        IReadOnlyList<string>? referenceImagePaths,
        string? startFrameImagePath,
        string? continueFromVideoPath,
        int maxRefsForModel,
        string? aspectRatio,
        string? extendSourceFileId,
        CancellationToken ct)
    {
        var refs = (referenceImagePaths ?? Array.Empty<string>())
            .Where(IsExistingMediaPath)
            .Take(maxRefsForModel)
            .ToList();
        var hasContinue = !string.IsNullOrWhiteSpace(extendSourceFileId) || IsExistingMediaPath(continueFromVideoPath);
        var hasStart = IsExistingMediaPath(startFrameImagePath);

        ApplySubmitRefPriority(refs, ref hasContinue, ref hasStart, startFrameImagePath);

        if (hasStart || refs.Count > 0 || hasContinue)
            durationSeconds = ClipDurationEstimator.ResolveActualDurationForModel(
                model, durationSeconds, isExtensionMode: true);

        var (videoUri, startUri, refObjs) = await EncodeSubmitMediaAsync(
            hasContinue, hasStart, startFrameImagePath, refs, ct).ConfigureAwait(false);

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
            AspectRatio = aspectRatio,
            ContinueFromVideoPath = continueFromVideoPath,
            StartFrameImagePath = startFrameImagePath,
            ExtendSourceFileId = extendSourceFileId,
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
        string? startFrameImagePath,
        List<string> refs,
        CancellationToken ct)
    {
        if (hasContinue)
        {
            // A local predecessor MP4 is uploaded to xAI Files once and referenced by file_id (see
            // UploadVideoFileAsync) instead of being inlined as a multi-MB data-URI on every attempt.
            // The upload is the same path a browser-trimmed delta takes — no server copy needed.
            return (null, null, null);
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
            var requestId = await SubmitOnceAsync(setup, current, ct).ConfigureAwait(false);
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

    private Task<string> SubmitOnceAsync(
        SubmitSetup setup, string current, CancellationToken ct)
    {
        if (setup.HasContinue)
        {
            return SubmitExtendOnceAsync(setup, current, ct);
        }

        return SubmitFreshOnceAsync(setup, current, ct);
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
        SubmitSetup setup,
        string prompt,
        CancellationToken ct)
    {
        var hasFileId = !string.IsNullOrWhiteSpace(setup.ExtendSourceFileId);
        var extendFileId = setup.ExtendSourceFileId;
        if (!hasFileId && !string.IsNullOrWhiteSpace(setup.ContinueFromVideoPath) && File.Exists(setup.ContinueFromVideoPath))
        {
            extendFileId = await UploadVideoFileAsync(setup.ContinueFromVideoPath, ct).ConfigureAwait(false);
            hasFileId = true;
        }
        var videoDict = hasFileId
            ? new Dictionary<string, object?> { ["file_id"] = extendFileId }
            : new Dictionary<string, object?> { ["url"] = setup.VideoUri ?? string.Empty };

        var extPayload = new Dictionary<string, object?>
        {
            ["model"] = setup.Model,
            ["prompt"] = prompt,
            // duration = length of NEW extension only (not total)
            ["duration"] = setup.DurationSeconds,
            ["video"] = videoDict,
            // Persist to Files API (file_id). "filename" is required (422 without it).
            ["storage_options"] = PermanentVideoStorageOptions(),
        };
        // resolution/aspect may be ignored on extensions; still send when API allows
        if (!string.IsNullOrWhiteSpace(setup.Resolution))
            extPayload["resolution"] = setup.Resolution;

        _log.LogInformation(
            "Grok video EXTEND from={Source} extensionDur={Dur}s promptLen={Len}",
            hasFileId ? setup.ExtendSourceFileId : Path.GetFileName(setup.ContinueFromVideoPath), setup.DurationSeconds, prompt.Length);

        // If file_id is used and fails (e.g. expired handle), attempt fallback to uploading local file if available.
        try
        {
            return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                async _ =>
                {
                    using var extResp = await GrokProviderHttp.SendJsonAsync(_http, HttpMethod.Post, "videos/extensions", extPayload, ct);
                    return await ProviderHttpHelpers.ReadRequiredJsonStringAsync(
                        extResp, ct, "request_id",
                        "Grok video extend",
                        "Grok extend response missing request_id",
                        errorTrim: 500);
                },
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("grok_video_extend_submit", setup.Model, $"durationSec={setup.DurationSeconds}", attemptNum, ex, ct),
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (hasFileId && !string.IsNullOrWhiteSpace(setup.ContinueFromVideoPath) && File.Exists(setup.ContinueFromVideoPath))
        {
            _log.LogWarning(ex, "Grok video extend file_id '{FileId}' failed; falling back to local file upload '{Path}'",
                setup.ExtendSourceFileId, setup.ContinueFromVideoPath);
            var freshId = await UploadVideoFileAsync(setup.ContinueFromVideoPath, ct).ConfigureAwait(false);
            extPayload["video"] = new Dictionary<string, object?> { ["file_id"] = freshId };
            using var extResp = await GrokProviderHttp.SendJsonAsync(_http, HttpMethod.Post, "videos/extensions", extPayload, ct);
            return await ProviderHttpHelpers.ReadRequiredJsonStringAsync(
                extResp, ct, "request_id",
                "Grok video extend fallback",
                "Grok extend fallback response missing request_id",
                errorTrim: 500);
        }
    }

    private async Task<string> SubmitFreshOnceAsync(
        SubmitSetup setup,
        string prompt,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = setup.Model,
            ["prompt"] = prompt,
            ["duration"] = setup.DurationSeconds,
            ["aspect_ratio"] = ResolveAspectRatio(setup.Model, setup.AspectRatio).ToApiString(),
            ["resolution"] = setup.Resolution,
            // Persist to Files (file_id for edit/extend) and request public_url for playback.
            ["storage_options"] = PermanentVideoStorageOptions(),
        };

        if (setup.StartUri is not null)
        {
            payload["image"] = new Dictionary<string, object?> { ["url"] = setup.StartUri };
            _log.LogInformation(
                "Grok video image-to-video startFrame={Frame} promptLen={Len} duration={Dur}s",
                Path.GetFileName(setup.StartFrameImagePath), prompt.Length, setup.DurationSeconds);
        }
        else if (setup.RefObjs is { Count: > 0 })
        {
            payload["reference_images"] = setup.RefObjs;
            _log.LogInformation(
                "Grok video reference-to-video refs={N} promptLen={Len} duration={Dur}s",
                setup.Refs.Count, prompt.Length, setup.DurationSeconds);
        }
        else
        {
            _log.LogInformation(
                "Grok video text-to-video promptLen={Len} duration={Dur}s",
                prompt.Length, setup.DurationSeconds);
        }

        // Same reasoning as SubmitExtendOnceAsync: a lost response here is unrecoverable either
        // way (no request_id to find the job), so automatic retry is no riskier than the manual
        // retry a human would do on seeing the same failure.
        return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
            async _ =>
            {
                using var resp = await GrokProviderHttp.SendJsonAsync(_http, HttpMethod.Post, "videos/generations", payload, ct);
                return await ProviderHttpHelpers.ReadRequiredJsonStringAsync(
                    resp, ct, "request_id",
                    "Grok submit",
                    "Grok response missing request_id");
            },
            isTransient: AiRetryPolicy.IsTransientChatFailure,
            maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
            backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
            onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("grok_video_submit", setup.Model, $"durationSec={setup.DurationSeconds}", attemptNum, ex, ct),
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fresh-generation aspect ratio: requested aspect ratio if given, else catalog's
    /// <c>DefaultAspectRatio</c> for the requested model, falling back to the historical hardcoded "16:9".
    /// (Not used for video-extend — xAI docs say aspect_ratio isn't accepted there; the extension
    /// always inherits the source clip's ratio.)
    /// </summary>
    private static AspectRatio ResolveAspectRatio(string model, string? requestedAspectRatio = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedAspectRatio))
            return MediaEngineEnumExtensions.ParseAspectRatio(requestedAspectRatio);
        return MediaEngineEnumExtensions.ParseAspectRatio(
            SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).DefaultAspectRatio ?? "16:9");
    }

    /// <summary>
    /// Upload a local MP4 to xAI Files (<c>POST /files</c>, multipart) and return its <c>file_id</c>.
    /// xAI keeps user uploads until deleted (<c>expires_at: null</c>) and <c>videos/extensions</c>
    /// accepts them as input — verified live 2026-08-18: upload → extend → new clip. This is how a
    /// browser-trimmed extension delta re-enters the chain with a fresh id, and how a dead handle is
    /// recovered from the user's local copy, without the server ever keeping the bytes.
    /// </summary>
    public async Task<string> UploadVideoFileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await UploadVideoStreamAsync(fs, Path.GetFileName(path), ct).ConfigureAwait(false);
    }

    public async Task<string?> TryUploadVideoStreamAsync(Stream mp4, string fileName, string? model, CancellationToken ct) =>
        await UploadVideoStreamAsync(mp4, fileName, ct).ConfigureAwait(false);

    /// <summary>Stream form of <see cref="UploadVideoFileAsync"/> (browser → server relay, no disk).</summary>
    public async Task<string> UploadVideoStreamAsync(Stream mp4, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("assistants"), "purpose");
        var part = new StreamContent(mp4);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        form.Add(part, "file", string.IsNullOrWhiteSpace(fileName) ? "clip.mp4" : fileName);
        using var resp = await GrokProviderHttp.SendAsync(_http, HttpMethod.Post, "files", form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"xAI video upload HTTP {(int)resp.StatusCode}: {(body.Length > 600 ? body[..600] : body)}");
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("xAI video upload response had no id.");
        _log.LogInformation("Grok video file uploaded → {FileId} ({Name})", id, fileName);
        return id!;
    }

    private static Task<string> FileToDataUriAsync(string path, CancellationToken ct) =>

        MediaDataUri.FileToDataUriAsync(path, ct);

    public async Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var (deadline, poll) = VideoClientHelpers.PollWindow(_opts);
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

            if (VideoClientHelpers.IsPollFailedOrExpired(status))
                await HandlePollFailedOrExpiredAsync(requestId, root, body, status, sw, polls, ct).ConfigureAwait(false);

            var progress = root.TryGetProperty("progress", out var pr) ? pr.ToString() : null;
            onProgress?.Invoke(VideoClientHelpers.FormatPollProgress(status, progress));
            await Task.Delay(TimeSpan.FromSeconds(poll), ct);
        }

        return await VideoClientHelpers.ThrowTimedOutAsync(
            _telemetry, requestId, sw, polls, _opts.GrokTimeoutSeconds,
            $"Grok job timed out after {_opts.GrokTimeoutSeconds}s", ct);
    }

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
                Error = ProviderHttpHelpers.Trim(ex.Message, 400),
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
        using var resp = await GrokProviderHttp.SendAsync(_http, HttpMethod.Get, $"videos/{requestId}", content: null, ct);
        return await ProviderHttpHelpers.ReadSuccessBodyAsync(resp, ct, "Grok poll");
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
        var stored = ParseFileOutput(video);
        if (stored.HasFileId || stored.HasPublicUrl)
            _fileRefs[requestId] = stored;
    }

    /// <summary>Read <c>video.file_output</c> (file_id + durable public_url) from a poll body.</summary>
    internal static StoredVideoFileRef ParseFileOutput(JsonElement video)
    {
        if (!video.TryGetProperty("file_output", out var fileOutput) ||
            fileOutput.ValueKind != JsonValueKind.Object)
            return StoredVideoFileRef.Empty;

        var fileId = fileOutput.TryGetProperty("file_id", out var fid) ? fid.GetString() : null;
        long? expiresAt = null;
        if (fileOutput.TryGetProperty("expires_at", out var exp) && exp.TryGetInt64(out var expVal))
            expiresAt = expVal;
        var publicUrl = fileOutput.TryGetProperty("public_url", out var pu) ? pu.GetString() : null;
        return new StoredVideoFileRef(fileId, expiresAt, publicUrl);
    }

    private async Task HandlePollFailedOrExpiredAsync(
        string requestId, JsonElement root, string body, string? status, Stopwatch sw, int polls, CancellationToken ct)
    {
        var detail = VideoClientHelpers.PollErrorDetail(root, body);
        await _telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = "video_poll",
            Endpoint = $"videos/{requestId}",
            RequestId = requestId,
            HttpStatus = 200,
            DurationMs = sw.ElapsedMilliseconds,
            Attempt = polls,
            Mode = status,
            Error = ProviderHttpHelpers.Trim(detail, 500),
            Ok = false,
        }, ct);
        await _telemetry.LogOutcomeAsync(null, requestId, VideoClientHelpers.ExpiredOrFailed(status), sw.ElapsedMilliseconds, polls, ok: false, ProviderHttpHelpers.Trim(detail, 500), ct);
        throw new InvalidOperationException($"Grok job {status}: {ProviderHttpHelpers.Trim(detail, 400)}");
    }

    public StoredVideoFileRef TryGetStoredFileReference(string requestId) =>
        _fileRefs.TryGetValue(requestId, out var v) ? v : StoredVideoFileRef.Empty;

    public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct) =>
        ProviderHttpHelpers.DownloadToFileAsync(_http, url, destPath, ct, _log);

    /// <summary>
    /// Files content GET via <see cref="XaiResponsesClient.OpenFileContentStreamAsync"/> —
    /// the only Files downloader. Required when the catalog routes this clip to this client.
    /// </summary>
    public async Task<Stream?> OpenStoredFileStreamAsync(string fileId, string? model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return null;
        if (_files is null)
            throw new InvalidOperationException("xAI Files client is not configured.");
        return await _files.OpenFileContentStreamAsync(fileId, ct).ConfigureAwait(false);
    }
}
