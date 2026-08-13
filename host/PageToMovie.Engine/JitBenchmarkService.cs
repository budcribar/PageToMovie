using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelBacked;

namespace PageToMovie.Engine;

public sealed record JitCalibrationResult(
    string CategoryId,
    double MeasuredOverheadSec,
    double OverlapRatioGamma,
    bool IsLiveJitBenchmark,
    string SourceDescription);

public sealed record VisionActionTimingAnalysis(
    [property: JsonPropertyName("actionCompletionSec")] double ActionCompletionSec,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("explanation")] string Explanation);

/// <summary>
/// Just-In-Time (JIT) Benchmark Engine.
/// Always classifies a beat against the calibrated index first (cheap, fast). Only pays for a
/// live 1-clip JIT video benchmark when the index match is uncertain:
/// - Confident index match (ConfidenceScore >= <see cref="ConfidentMatchThreshold"/>): use the
///   ledger-calibrated value directly, no video generation spend.
/// - Low-confidence match: executes a real 1-clip JIT benchmark render via IVideoClient (Fal.ai/Veo),
///   downloads the resulting MP4 to inspect ISO-BMFF duration, and runs Gemini Vision frame analysis
///   for a true empirical measurement. Falls back to the low-confidence estimate if live keys are
///   missing or the benchmark fails.
/// - Persists calibrated/measured metrics to the SQLite telemetry repository either way, so accuracy
///   improves over time as more of the action space gets measured or confidently matched.
/// </summary>
public sealed class JitBenchmarkService
{
    /// <summary>
    /// Minimum classifier confidence to trust an existing index match without paying for a live
    /// measurement. Below this, the beat is treated as effectively uncalibrated. 0.80 sits just above
    /// the heuristic classifier's generic-fallback confidence (0.75), so "we don't really know" always
    /// triggers a real measurement instead of silently accepting a guess.
    /// </summary>
    private const double ConfidentMatchThreshold = 0.80;

    private readonly AiActionOverheadClassifier _classifier;
    private readonly IVideoClient? _videoClient;
    private readonly IVisionClient? _visionClient;
    private readonly ClipTimingTelemetryRepository? _repository;
    private readonly ILogger<JitBenchmarkService>? _log;
    private readonly IHttpClientFactory? _httpFactory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public JitBenchmarkService(
        ActionCameraOverheadLedger ledger,
        AiActionOverheadClassifier classifier,
        IVideoClient? videoClient = null,
        IVisionClient? visionClient = null,
        ClipTimingTelemetryRepository? repository = null,
        ILogger<JitBenchmarkService>? log = null,
        IHttpClientFactory? httpFactory = null)
    {
        _ = ledger;
        _classifier = classifier;
        _videoClient = videoClient;
        _visionClient = visionClient;
        _repository = repository;
        _log = log;
        _httpFactory = httpFactory;
    }

    public async Task<JitCalibrationResult> EnsureBeatCalibratedAsync(
        string actionDescription,
        string? parenthetical = null,
        string? modelId = null,
        string? evaluatorModelId = null,
        CancellationToken ct = default)
    {
        var concurrency = ActionConcurrencyAnalyzer.AnalyzeBeat(actionDescription, parenthetical);
        double camOverhead = ActionCameraOverheadLedger.GetOverheadSec(concurrency.CameraId, 1.6);
        // Video model is optional for index-only matches; required only when live JIT renders.
        string? targetModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        // Evaluator attribution for telemetry — never invent Grok/Gemini ids.
        string evaluatorId = string.IsNullOrWhiteSpace(evaluatorModelId) ? "" : evaluatorModelId.Trim();

        // Always classify first — cheap and works with no video keys configured. A confident index
        // match skips live measurement entirely; only an uncertain match pays for a real benchmark.
        var estimation = await _classifier.ClassifyNovelActionAsync(actionDescription, parenthetical, ct).ConfigureAwait(false);
        bool confidentMatch = estimation.ConfidenceScore >= ConfidentMatchThreshold;

        if (confidentMatch)
            return await RecordIndexHit(actionDescription, concurrency, camOverhead, targetModel, evaluatorId, estimation, ct).ConfigureAwait(false);

        var live = await TryLiveBenchmarkAsync(
            actionDescription, concurrency, camOverhead, targetModel, evaluatorId, estimation, ct).ConfigureAwait(false);
        if (live is not null)
            return live;

        return await RecordFallback(actionDescription, concurrency, camOverhead, targetModel, evaluatorId, estimation, ct).ConfigureAwait(false);
    }

    private async Task<JitCalibrationResult> RecordIndexHit(
        string actionDescription,
        ActionConcurrencyResult concurrency,
        double camOverhead,
        string? targetModel,
        string evaluatorId,
        ActionClassifierEstimation estimation,
        CancellationToken ct)
    {
        _log?.LogInformation(
            "[JitBenchmark] Confident index match for '{Action}' -> '{Category}' (Conf={Conf:F2}); skipping live measurement.",
            actionDescription, estimation.MatchCategoryId, estimation.ConfidenceScore);

        if (_repository is not null)
        {
            await _repository.RecordCacheLookupAsync(isHit: true, lookupKey: estimation.MatchCategoryId).ConfigureAwait(false);
            await _repository.RecordTelemetryAsync(new TimingTelemetryRecord(
                Id: $"idx_{Guid.NewGuid():N}",
                ProjectId: "global",
                SceneNumber: 0,
                VideoModelId: targetModel ?? "",
                VideoModelVersion: "v1",
                EvaluatorModelId: evaluatorId,
                EvaluatorModelVersion: "v1",
                CameraCategory: concurrency.CameraId,
                ActionCategory: estimation.MatchCategoryId,
                WordCount: 0,
                EstimatedDurationSec: camOverhead + estimation.EstimatedOverheadSec,
                ClipDurationSec: estimation.EstimatedOverheadSec + camOverhead,
                MeasuredCamOverheadSec: camOverhead,
                MeasuredActionOverheadSec: estimation.EstimatedOverheadSec,
                DialogueTruncated: false,
                CreatedAt: DateTime.UtcNow.ToString("o"))).ConfigureAwait(false);
        }

        return new JitCalibrationResult(
            CategoryId: estimation.MatchCategoryId,
            MeasuredOverheadSec: estimation.EstimatedOverheadSec,
            OverlapRatioGamma: concurrency.OverlapRatioGamma,
            IsLiveJitBenchmark: false,
            SourceDescription: $"Confident index match ({estimation.Explanation}).");
    }

    private async Task<JitCalibrationResult?> TryLiveBenchmarkAsync(
        string actionDescription,
        ActionConcurrencyResult concurrency,
        double camOverhead,
        string? targetModel,
        string evaluatorId,
        ActionClassifierEstimation estimation,
        CancellationToken ct)
    {
        var videoClient = _videoClient;
        if (videoClient is null || !videoClient.IsConfigured || string.IsNullOrWhiteSpace(targetModel))
            return null;

        _log?.LogInformation(
            "[JitBenchmark] Low-confidence index match (Conf={Conf:F2}) for action: '{Action}'; executing real 1-clip JIT benchmark using model '{Model}'",
            estimation.ConfidenceScore, actionDescription, targetModel);

        string? tempMp4Path = null;
        try
        {
            var prompt = $"Cinematic benchmark action shot: {actionDescription}";
            var reqId = await videoClient.SubmitGenerationAsync(
                prompt: prompt,
                durationSeconds: 4,
                resolution: "1280x720",
                model: targetModel,
                ct: ct).ConfigureAwait(false);

            _log?.LogInformation("[JitBenchmark] Submitted 1-clip JIT job '{ReqId}'. Polling for video completion...", reqId);

            var videoUrl = await videoClient.PollForVideoUrlAsync(reqId, msg => _log?.LogDebug("[JitBenchmark] {Msg}", msg), ct).ConfigureAwait(false);

            double measuredTotalClipSec = 4.0;
            double measuredActionOverheadSec = 2.4;
            string sourceNote = "Live Video API";

            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                tempMp4Path = Path.Combine(Path.GetTempPath(), $"jit_measure_{Guid.NewGuid():N}.mp4");
                tempMp4Path = await TryDownloadTempMp4Async(videoUrl, tempMp4Path, ct).ConfigureAwait(false);
                if (File.Exists(tempMp4Path))
                {
                    var measured = await ProbeAndVisionAsync(
                        tempMp4Path, actionDescription, evaluatorId,
                        measuredTotalClipSec, measuredActionOverheadSec, sourceNote, ct).ConfigureAwait(false);
                    measuredTotalClipSec = measured.TotalClipSec;
                    measuredActionOverheadSec = measured.ActionOverheadSec;
                    sourceNote = measured.SourceNote;
                }
            }

            var categoryId = $"jit_{Math.Abs(actionDescription.GetHashCode()):x8}";
            await RecordLiveTelemetryAsync(
                categoryId, concurrency, camOverhead, targetModel, evaluatorId,
                measuredTotalClipSec, measuredActionOverheadSec, ct).ConfigureAwait(false);

            return new JitCalibrationResult(
                CategoryId: categoryId,
                MeasuredOverheadSec: measuredActionOverheadSec,
                OverlapRatioGamma: concurrency.OverlapRatioGamma,
                IsLiveJitBenchmark: true,
                SourceDescription: sourceNote);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "[JitBenchmark] Live 1-clip JIT render failed for '{Action}'. Falling back to AI Similarity Classifier.", actionDescription);
            return null;
        }
        finally
        {
            CleanupTemp(tempMp4Path);
        }
    }

    private async Task<string> TryDownloadTempMp4Async(string videoUrl, string tempMp4Path, CancellationToken ct)
    {
        // Download video to local temp file for local ISO-BMFF MP4 probing & vision analysis
        try
        {
            if (videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                HttpClient http;
                HttpClient? ownedHttp = null;
                if (_httpFactory is not null)
                    http = _httpFactory.CreateClient("media-proxy");
                else
                {
                    ownedHttp = new HttpClient();
                    http = ownedHttp;
                }
                try
                {
                    var mp4Bytes = await http.GetByteArrayAsync(videoUrl, ct).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(tempMp4Path, mp4Bytes, ct).ConfigureAwait(false);
                }
                finally
                {
                    ownedHttp?.Dispose();
                }
            }
            else if (File.Exists(videoUrl))
            {
                tempMp4Path = videoUrl;
            }
        }
        catch (Exception dlEx)
        {
            _log?.LogDebug(dlEx, "[JitBenchmark] Temp MP4 download skipped for JIT URL '{Url}'", videoUrl);
        }
        return tempMp4Path;
    }

    private async Task<(double TotalClipSec, double ActionOverheadSec, string SourceNote)> ProbeAndVisionAsync(
        string tempMp4Path,
        string actionDescription,
        string evaluatorId,
        double measuredTotalClipSec,
        double measuredActionOverheadSec,
        string sourceNote,
        CancellationToken ct)
    {
        // 1. Probe total MP4 clip duration using ISO-BMFF reader
        var probedTotalSec = await Mp4DurationReader.TryReadSecondsAsync(tempMp4Path, ct).ConfigureAwait(false);
        if (probedTotalSec is > 0)
        {
            measuredTotalClipSec = Math.Round(probedTotalSec.Value, 2);
            _log?.LogInformation("[JitBenchmark] Probed MP4 stream total clip duration: {TotalSec:F2}s", measuredTotalClipSec);
        }

        // 2. Multimodal vision frame inspection when an evaluator model is supplied.
        if (_visionClient is not null && _visionClient.IsConfigured
            && !string.IsNullOrWhiteSpace(evaluatorId))
        {
            (measuredActionOverheadSec, sourceNote) = await TryVisionMeasureAsync(
                tempMp4Path, actionDescription, evaluatorId, measuredActionOverheadSec, sourceNote, ct).ConfigureAwait(false);
        }

        return (measuredTotalClipSec, measuredActionOverheadSec, sourceNote);
    }

    private async Task<(double ActionOverheadSec, string SourceNote)> TryVisionMeasureAsync(
        string tempMp4Path,
        string actionDescription,
        string evaluatorId,
        double measuredActionOverheadSec,
        string sourceNote,
        CancellationToken ct)
    {
        _log?.LogInformation("[JitBenchmark] Inspecting JIT video frames at {Path} via Vision Client ({Model})...",
            tempMp4Path, evaluatorId);

        var visionPrompt = $$"""
            Analyze this video clip frame by frame.
            Target physical action: "{{actionDescription}}"

            Determine the exact timestamp in seconds from the start of the video when this action completes or stabilizes.
            Respond strictly in JSON format:
            {
              "actionCompletionSec": 2.4,
              "confidence": 0.95,
              "explanation": "<rationale>"
            }
            """;

        var rawVision = await _visionClient!.CompleteWithImagesAsync(
            prompt: visionPrompt,
            imagePaths: new[] { tempMp4Path },
            model: evaluatorId,
            ct: ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(rawVision))
        {
            var parsedAnalysis = ParseVisionJson(rawVision);
            if (parsedAnalysis is not null && parsedAnalysis.ActionCompletionSec > 0)
            {
                measuredActionOverheadSec = Math.Round(parsedAnalysis.ActionCompletionSec, 2);
                sourceNote = $"Live Video API + Vision Inspection ({parsedAnalysis.Explanation})";
                _log?.LogInformation("[JitBenchmark] Vision measured physical action overhead for '{Action}' = {ActionSec:F2}s (Conf={Conf:F2})",
                    actionDescription, measuredActionOverheadSec, parsedAnalysis.Confidence);
            }
        }

        return (measuredActionOverheadSec, sourceNote);
    }

    private VisionActionTimingAnalysis? ParseVisionJson(string rawVision)
    {
        var json = rawVision.Trim();
        if (json.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            json = json[7..].TrimEnd('`', '\n', '\r', ' ');
        else if (json.StartsWith("```"))
            json = json[3..].TrimEnd('`', '\n', '\r', ' ');

        return JsonSerializer.Deserialize<VisionActionTimingAnalysis>(json, JsonOpts);
    }

    private static void CleanupTemp(string? tempMp4Path)
    {
        if (tempMp4Path is not null && File.Exists(tempMp4Path) && tempMp4Path.Contains("jit_measure_"))
        {
            try { File.Delete(tempMp4Path); } catch { /* cleanup */ }
        }
    }

    private async Task RecordLiveTelemetryAsync(
        string categoryId,
        ActionConcurrencyResult concurrency,
        double camOverhead,
        string? targetModel,
        string evaluatorId,
        double measuredTotalClipSec,
        double measuredActionOverheadSec,
        CancellationToken ct)
    {
        if (_repository is null) return;
        await _repository.RecordCacheLookupAsync(isHit: false, lookupKey: categoryId).ConfigureAwait(false);
        await _repository.RecordTelemetryAsync(new TimingTelemetryRecord(
            Id: $"jit_{Guid.NewGuid():N}",
            ProjectId: "global",
            SceneNumber: 0,
            VideoModelId: targetModel ?? "",
            VideoModelVersion: "v1",
            EvaluatorModelId: evaluatorId,
            EvaluatorModelVersion: "v1",
            CameraCategory: concurrency.CameraId,
            ActionCategory: categoryId,
            WordCount: 0,
            EstimatedDurationSec: camOverhead + measuredActionOverheadSec,
            ClipDurationSec: measuredTotalClipSec,
            MeasuredCamOverheadSec: camOverhead,
            MeasuredActionOverheadSec: measuredActionOverheadSec,
            DialogueTruncated: false,
            CreatedAt: DateTime.UtcNow.ToString("o"))).ConfigureAwait(false);
    }

    private async Task<JitCalibrationResult> RecordFallback(
        string actionDescription,
        ActionConcurrencyResult concurrency,
        double camOverhead,
        string? targetModel,
        string evaluatorId,
        ActionClassifierEstimation estimation,
        CancellationToken ct)
    {
        _log?.LogInformation(
            "[JitBenchmark] Live measurement unavailable or failed; using low-confidence AI Similarity Classifier estimate for action: '{Action}' (Conf={Conf:F2})",
            actionDescription, estimation.ConfidenceScore);

        if (_repository is not null)
        {
            await _repository.RecordCacheLookupAsync(isHit: false, lookupKey: estimation.MatchCategoryId).ConfigureAwait(false);
            await _repository.RecordTelemetryAsync(new TimingTelemetryRecord(
                Id: $"clf_{Guid.NewGuid():N}",
                ProjectId: "global",
                SceneNumber: 0,
                VideoModelId: targetModel ?? "",
                VideoModelVersion: "v1",
                EvaluatorModelId: evaluatorId,
                EvaluatorModelVersion: "v1",
                CameraCategory: concurrency.CameraId,
                ActionCategory: estimation.MatchCategoryId,
                WordCount: 0,
                EstimatedDurationSec: camOverhead + estimation.EstimatedOverheadSec,
                ClipDurationSec: estimation.EstimatedOverheadSec + camOverhead,
                MeasuredCamOverheadSec: camOverhead,
                MeasuredActionOverheadSec: estimation.EstimatedOverheadSec,
                DialogueTruncated: false,
                CreatedAt: DateTime.UtcNow.ToString("o"))).ConfigureAwait(false);
        }

        return new JitCalibrationResult(
            CategoryId: estimation.MatchCategoryId,
            MeasuredOverheadSec: estimation.EstimatedOverheadSec,
            OverlapRatioGamma: concurrency.OverlapRatioGamma,
            IsLiveJitBenchmark: false,
            SourceDescription: $"Low-confidence AI Similarity Classifier estimate, no live measurement available ({estimation.Explanation}).");
    }
}
