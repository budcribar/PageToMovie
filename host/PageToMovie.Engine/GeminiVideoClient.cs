using System.Diagnostics;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Google Veo video client via Gemini's long-running-operation pattern
/// (<c>predictLongRunning</c> → poll <c>operations.get</c> → download).
///
/// CONFIDENCE NOTE: unlike <see cref="AnthropicChatClient"/> / <see cref="GeminiChatClient"/>
/// (well-documented, stable request/response shapes), the exact field path for the finished
/// video inside the operation's <c>response</c> object is the part of this file most likely
/// to need adjustment against a real account — <see cref="ExtractVideoUri"/> tries several
/// plausible paths defensively, but treat this class as needing a live smoke test before
/// production use, same as any provider added without API access to verify against.
/// </summary>
public sealed class GeminiVideoClient : IVideoClient
{
    public const string ApiBase = SupportedModelCatalog.GoogleApiBase;

    private const string VideoKind = "video";
    private readonly HttpClient _http;
    private readonly PageToMovieOptions _opts;
    private readonly ProjectTelemetryService _telemetry;
    private readonly ILogger<GeminiVideoClient> _log;
    private readonly GenerationErrorLogger? _errorLogger;

    public GeminiVideoClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ProjectTelemetryService telemetry,
        ILogger<GeminiVideoClient> log,
        GenerationErrorLogger? errorLogger = null)
    {
        _http = http;
        _opts = opts.Value;
        _telemetry = telemetry;
        _log = log;
        _errorLogger = errorLogger;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    /// <summary>
    /// Veo does not have a direct equivalent of Grok's reference_images / video-extend on the
    /// same endpoint family yet in this client — only text-to-video and image-to-video
    /// (first frame) are implemented. <paramref name="continueFromVideoPath"/> and multiple
    /// <paramref name="referenceImagePaths"/> are not supported; passing them throws rather
    /// than silently ignoring continuity, since a silently-wrong clip is worse than a loud stop.
    /// </summary>
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
        if (!string.IsNullOrWhiteSpace(continueFromVideoPath) || !string.IsNullOrWhiteSpace(extendSourceFileId))
            throw new NotSupportedException(
                "GeminiVideoClient does not implement clip-to-clip continue yet — " +
                "use image-to-video (startFrameImagePath) as a fallback continuity strategy, " +
                "or route this clip to Grok.");
        if (referenceImagePaths is { Count: > 0 })
            throw new NotSupportedException(
                "GeminiVideoClient does not implement multi reference-image conditioning yet — " +
                "use a single startFrameImagePath, or route this clip to Grok.");

        var hasStart = !string.IsNullOrWhiteSpace(startFrameImagePath) && File.Exists(startFrameImagePath);
        // Model-aware, not a bare hardcoded 1-10 — a duration Stage 2 correctly planned using this
        // model's real bounds must not get silently re-clamped back down to a generic range at the
        // actual API call, cutting off dialogue/action that was paced to fit. Veo specifically
        // documents only 4/6/8 second durations (a discrete set, not a continuous range) —
        // ResolveActualDurationForModel snaps to the nearest allowed value for models that declare
        // one, and falls back to a plain min/max clamp (matching the prior hardcoded 1/10) otherwise.
        durationSeconds = ClipDurationEstimator.ResolveActualDurationForModel(model, durationSeconds);

        var instance = new Dictionary<string, object?> { ["prompt"] = prompt };
        if (hasStart)
        {
            var (mime, b64) = await ProviderMediaHelpers.FileToBase64Async(startFrameImagePath!, ct).ConfigureAwait(false);
            instance["image"] = new Dictionary<string, object?>
            {
                ["bytesBase64Encoded"] = b64,
                ["mimeType"] = mime,
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["instances"] = new object[] { instance },
            ["parameters"] = new Dictionary<string, object?>
            {
                ["aspectRatio"] = ResolveAspectRatio(model, aspectRatio),
                ["durationSeconds"] = durationSeconds,
                ["resolution"] = NormalizeResolution(resolution),
            },
        };

        var endpoint = $"models/{Uri.EscapeDataString(model)}:predictLongRunning";
        var mode = hasStart ? "image-to-video" : "text-to-video";
        var sw = Stopwatch.StartNew();
        try
        {
            // Same reasoning as GrokVideoClient: a lost submit response is unrecoverable either
            // way (no operation name to find the job by), so automatic retry is no riskier than
            // the manual retry a human would do on seeing the same failure.
            return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                async _ =>
                {
                    using var resp = await SendJsonAsync(HttpMethod.Post, endpoint, payload, ct).ConfigureAwait(false);
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                        {
                            Kind = VideoKind,
                            Mode = mode,
                            Endpoint = endpoint,
                            Model = model,
                            HttpStatus = (int)resp.StatusCode,
                            DurationMs = sw.ElapsedMilliseconds,
                            Prompt = prompt,
                            PromptChars = prompt.Length,
                            Resolution = resolution,
                            DurationSec = durationSeconds,
                            Error = ProviderHttpHelpers.Trim(body, 400),
                            Ok = false,
                        }, ct);
                        throw ChatHttpStatusException.FromResponse(resp,
                            $"Gemini {endpoint} HTTP {(int)resp.StatusCode}: {ProviderHttpHelpers.Trim(body, 400)}");
                    }

                    var opName = ProviderHttpHelpers.RequireJsonString(
                        body, "name", "Gemini predictLongRunning response missing operation name");

                    await _telemetry.LogApiCallAsync(new ApiCallTelemetry
                    {
                        Kind = VideoKind,
                        Mode = mode,
                        Endpoint = endpoint,
                        Model = model,
                        HttpStatus = (int)resp.StatusCode,
                        RequestId = opName,
                        DurationMs = sw.ElapsedMilliseconds,
                        Prompt = prompt,
                        PromptChars = prompt.Length,
                        Resolution = resolution,
                        DurationSec = durationSeconds,
                        Ok = true,
                    }, ct);
                    return opName;
                },
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) => _errorLogger.LogRetryAttemptAsync("gemini_video_submit", model, $"durationSec={durationSeconds}", attemptNum, ex, ct),
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ChatHttpStatusException and not InvalidOperationException and not NotSupportedException)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = VideoKind,
                Mode = mode,
                Endpoint = endpoint,
                Model = model,
                DurationMs = sw.ElapsedMilliseconds,
                Prompt = prompt,
                Error = ex.Message,
                Ok = false,
            }, ct);
            throw;
        }
    }

    public async Task<string> PollForVideoUrlAsync(
        string requestId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        var (deadline, poll) = VideoClientHelpers.PollWindow(_opts);
        var sw = Stopwatch.StartNew();
        var polls = 0;
        // requestId is the full operation name returned by SubmitGenerationAsync, e.g.
        // "models/veo-3.1/operations/abc123" — the operations.get path is that name directly.
        var opPath = requestId.TrimStart('/');

        var retriedAnyPoll = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            polls++;

            var body = await FetchOperationBodyAsync(opPath, requestId, sw, polls, () => retriedAnyPoll = true, ct)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            await ThrowIfOperationErrorAsync(root, opPath, requestId, sw, polls, ct).ConfigureAwait(false);

            if (root.TryGetProperty("done", out var doneEl) &&
                doneEl.ValueKind == JsonValueKind.True)
            {
                return await CompleteIfDoneAsync(root, body, opPath, requestId, sw, polls, retriedAnyPoll, ct)
                    .ConfigureAwait(false);
            }

            onProgress?.Invoke($"operation not done (poll {polls})");
            await Task.Delay(TimeSpan.FromSeconds(poll), ct).ConfigureAwait(false);
        }

        return await VideoClientHelpers.ThrowTimedOutAsync(
            _telemetry, requestId, sw, polls, _opts.GrokTimeoutSeconds,
            $"Gemini video operation timed out after {_opts.GrokTimeoutSeconds}s", ct);
    }

    /// <summary>
    /// Each poll GET is idempotent (safe to retry freely, no billing risk) — unlike submit,
    /// there's no reason NOT to retry a transient blip here. Previously one bad poll threw
    /// and abandoned tracking of an already-submitted, already-paying job entirely.
    /// </summary>
    private async Task<string> FetchOperationBodyAsync(
        string opPath, string requestId, Stopwatch sw, int polls, Action onPollRetry, CancellationToken ct)
    {
        try
        {
            return await AiRetryPolicy.ExecuteWithTransientRetryAsync(
                async _ =>
                {
                    using var resp = await SendAsync(HttpMethod.Get, opPath, content: null, ct).ConfigureAwait(false);
                    return await ProviderHttpHelpers.ReadSuccessBodyAsync(
                        resp, ct, "Gemini operation poll").ConfigureAwait(false);
                },
                isTransient: AiRetryPolicy.IsTransientChatFailure,
                maxAttempts: AiRetryPolicy.DefaultTransientMaxAttempts,
                backoffBaseMs: AiRetryPolicy.DefaultTransientBackoffMs,
                onRetry: (attemptNum, ex) =>
                {
                    onPollRetry();
                    return _errorLogger.LogRetryAttemptAsync("gemini_video_poll", null, $"requestId={requestId}; poll={polls}", attemptNum, ex, ct);
                },
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video_poll",
                Endpoint = opPath,
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

    private async Task ThrowIfOperationErrorAsync(
        JsonElement root, string opPath, string requestId, Stopwatch sw, int polls, CancellationToken ct)
    {
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
        {
            var detail = errEl.ToString();
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video_poll",
                Endpoint = opPath,
                RequestId = requestId,
                HttpStatus = 200,
                DurationMs = sw.ElapsedMilliseconds,
                Attempt = polls,
                Mode = "failed",
                Error = ProviderHttpHelpers.Trim(detail, 500),
                Ok = false,
            }, ct);
            await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.ProviderFailed, sw.ElapsedMilliseconds, polls, ok: false, ProviderHttpHelpers.Trim(detail, 500), ct);
            throw new InvalidOperationException($"Gemini video operation failed: {ProviderHttpHelpers.Trim(detail, 400)}");
        }
    }

    private async Task<string> CompleteIfDoneAsync(
        JsonElement root, string body, string opPath, string requestId, Stopwatch sw, int polls, bool retriedAnyPoll, CancellationToken ct)
    {
        var url = ExtractVideoUri(root);
        if (url is null)
        {
            await _telemetry.LogOutcomeAsync(null, requestId, VideoJobOutcome.ProviderFailed, sw.ElapsedMilliseconds, polls, ok: false, "done with no video URI", ct);
            throw new InvalidOperationException(
                $"Gemini operation done but no video URI found in response " +
                $"(schema may differ from expected — see class-level CONFIDENCE NOTE): " +
                $"{ProviderHttpHelpers.Trim(body, 500)}");
        }
        await _telemetry.LogApiCallAsync(new ApiCallTelemetry
        {
            Kind = "video_poll",
            Endpoint = opPath,
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

    /// <summary>
    /// Tries several plausible paths for the generated video's URI inside a finished
    /// operation's <c>response</c> object, since this is the part of the Veo long-running-
    /// operation schema this class is least certain about (see class-level CONFIDENCE NOTE).
    /// Public so tests can exercise it against sample payloads without a live API call.
    /// </summary>
    public static string? ExtractVideoUri(JsonElement operationRoot)
    {
        if (!operationRoot.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
            return null;

        // Path A: response.generateVideoResponse.generatedSamples[0].video.uri
        if (response.TryGetProperty("generateVideoResponse", out var gvr) &&
            gvr.TryGetProperty("generatedSamples", out var samples) &&
            samples.ValueKind == JsonValueKind.Array &&
            samples.GetArrayLength() > 0)
        {
            var s0 = samples[0];
            if (s0.TryGetProperty(VideoKind, out var v0) &&
                v0.TryGetProperty("uri", out var u0) &&
                u0.GetString() is { Length: > 0 } uri0)
                return uri0;
        }

        // Path B: response.videos[0].uri
        if (response.TryGetProperty("videos", out var videos) &&
            videos.ValueKind == JsonValueKind.Array &&
            videos.GetArrayLength() > 0)
        {
            var v0 = videos[0];
            if (v0.TryGetProperty("uri", out var u0) && u0.GetString() is { Length: > 0 } uri0)
                return uri0;
        }

        // Path C: response.video.uri (single-sample shape)
        if (response.TryGetProperty(VideoKind, out var v1) &&
            v1.TryGetProperty("uri", out var u1) &&
            u1.GetString() is { Length: > 0 } uri1)
            return uri1;

        return null;
    }

    /// <summary>Gemini/Veo never requests xAI-style Files API storage — file_id reuse is a
    /// Grok-only optimization (see <see cref="IVideoEditClient"/>).</summary>
    public StoredVideoFileRef TryGetStoredFileReference(string requestId) => StoredVideoFileRef.Empty;

    public Task DownloadToFileAsync(string url, string destPath, CancellationToken ct) =>
        ProviderHttpHelpers.DownloadToFileAsync(
            _http, url, destPath, ct, _log,
            configureRequest: req => ProviderHttpHelpers.ApplyGoogleApiKey(req, ResolveApiKey()));

    /// <summary>
    /// Requested aspect ratio if given, else catalog's <c>DefaultAspectRatio</c> for the requested Veo model,
    /// falling back to the historical hardcoded "16:9" for models the catalog doesn't cover yet.
    /// </summary>
    private static string ResolveAspectRatio(string model, string? requestedAspectRatio = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedAspectRatio))
            return requestedAspectRatio;
        return SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.Video).DefaultAspectRatio ?? "16:9";
    }

    private static string NormalizeResolution(string resolution) =>
        (resolution ?? "").Trim().ToLowerInvariant() switch
        {
            "1080p" => "1080p",
            "720p" => "720p",
            _ => "720p", // Veo's minimum documented resolution tier; 480p is Grok-specific
        };

    private static string? ResolveApiKey() =>
        ApiKeyScope.CurrentGemini
        ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.GoogleApiKeyEnv);

    private Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string uri, object payload, CancellationToken ct) =>
        ProviderHttpHelpers.SendJsonAsync(
            _http, method, uri, payload, ct,
            req => ProviderHttpHelpers.ApplyGoogleApiKey(req, ResolveApiKey()));

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, HttpContent? content, CancellationToken ct) =>
        ProviderHttpHelpers.SendAsync(
            _http, method, uri, content, ct,
            req => ProviderHttpHelpers.ApplyGoogleApiKey(req, ResolveApiKey()));
}
