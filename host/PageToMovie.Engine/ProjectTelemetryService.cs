using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Append-only project telemetry under <c>projects/{id}/telemetry/</c>:
/// <list type="bullet">
/// <item><c>api_calls.jsonl</c> — live model/API calls (full prompts)</item>
/// <item><c>media_ops.jsonl</c> — optional condensed local media ops (legacy name was ffmpeg.jsonl)</item>
/// </list>
/// Project id from <see cref="UseProject"/> scope, else <see cref="ProjectStore.ActiveProjectId"/>.
/// Native server ffmpeg is gone; media_ops is rarely written.
/// </summary>
public sealed class ProjectTelemetryService
{
    private static readonly AsyncLocal<string?> ScopedProjectId = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly Regex ProgressFluff = new(
        @"^(frame=|fps=|bitrate=|total_size=|out_time|dup=|drop=|speed=|progress=)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ProjectStore _projects;
    private readonly UserDatabaseService? _userDb;
    private readonly CostReportService? _costs;
    private readonly ILogger<ProjectTelemetryService> _log;

    public ProjectTelemetryService(
        ProjectStore projects,
        ILogger<ProjectTelemetryService> log,
        UserDatabaseService? userDb = null,
        CostReportService? costs = null)
    {
        _projects = projects;
        _log = log;
        _userDb = userDb;
        _costs = costs;
    }

    /// <summary>Bind telemetry writes to a project for the current async flow.</summary>
    public IDisposable UseProject(string projectId)
    {
        var prev = ScopedProjectId.Value;
        ScopedProjectId.Value = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        return new ScopePop(() => ScopedProjectId.Value = prev);
    }

    public string? CurrentProjectId =>
        !string.IsNullOrWhiteSpace(ScopedProjectId.Value)
            ? ScopedProjectId.Value
            : string.IsNullOrWhiteSpace(_projects.ActiveProjectId)
                ? null
                : _projects.ActiveProjectId;

    public async Task<string> TelemetryDirAsync(string projectId, CancellationToken ct = default) =>
        Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "telemetry");

    public string TelemetryDir(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "telemetry");

    public async Task<string> ApiCallsPathAsync(string projectId, CancellationToken ct = default) =>
        Path.Combine(await TelemetryDirAsync(projectId, ct).ConfigureAwait(false), "api_calls.jsonl");

    public string ApiCallsPath(string projectId) =>
        Path.Combine(TelemetryDir(projectId), "api_calls.jsonl");

    /// <summary>Preferred path for local media-op telemetry (replaces ffmpeg.jsonl).</summary>
    public async Task<string> MediaOpsPathAsync(string projectId, CancellationToken ct = default) =>
        Path.Combine(await TelemetryDirAsync(projectId, ct).ConfigureAwait(false), "media_ops.jsonl");

    public string MediaOpsPath(string projectId) =>
        Path.Combine(TelemetryDir(projectId), "media_ops.jsonl");

    /// <summary>Legacy alias for <see cref="MediaOpsPath"/>.</summary>
    [Obsolete("Use MediaOpsPath — server no longer runs native ffmpeg.")]
    public string FfmpegPath(string projectId) => MediaOpsPath(projectId);

    public async Task LogApiCallAsync(ApiCallTelemetry rec, CancellationToken ct = default)
    {
        var projectId = rec.ProjectId ?? CurrentProjectId;
        rec.UserId ??= UserApiCallScope.UserId;
        if (rec.Ts is null)
            rec.Ts = DateTimeOffset.UtcNow;
        // Canonical outcome — set once, here, from the transport-level signals every call site
        // already provides. A caller with semantic context the transport layer lacks (e.g. a vision
        // gate rejecting on content) can set rec.Outcome explicitly before calling; this only fills
        // the gap when nobody has.
        rec.Outcome ??= ClassifyOutcome(rec);
        // Catalog is the single source of truth for model + provider identity on every log line.
        ApplyCatalogIdentity(rec);
        rec.EstimatedUsd ??= EstimateListRateUsd(rec);
        // Always a user-facing cost bucket (same ids as Estimate & cost pie).
        rec.Category = CostCategories.Resolve(rec.Kind, rec.Mode, rec.Category);
        // Charge is display/debit only — never the stored "actual" in SQLite.
        // estimated_usd stays list rate; ChargeUsd is ephemeral for credit debit.
        if (rec.EstimatedUsd is > 0 && rec.ChargeUsd is null && _costs is not null)
        {
            var mult = _costs.GetChargeMultiplier();
            rec.ChargeMultiplier = mult;
            rec.ChargeUsd = PageToMovie.Core.Billing.ChargePricing.ToCharge(rec.EstimatedUsd.Value, mult);
        }

        // Project jsonl (full prompts) when a project is in scope.
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            rec.ProjectId = projectId;
            try
            {
                await AppendJsonlAsync(await ApiCallsPathAsync(projectId, ct).ConfigureAwait(false), rec, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "api_calls append failed for {ProjectId}", projectId);
            }
        }
        else
        {
            _log.LogDebug("api_calls project skip — no project id (kind={Kind})", rec.Kind);
        }

        // Always attribute to user SQLite when we know who paid / whose key ran.
        if (_userDb is not null && !string.IsNullOrWhiteSpace(rec.UserId))
        {
            try
            {
                await _userDb.InsertUserApiCallAsync(rec, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "user_api_calls insert failed for {UserId}", rec.UserId);
            }
        }

        // Roll chat/vision/other non-video/image spend into the project's actual cost_ledger.
        // Video/image already log their own richer event via CostReportService.RecordVideoGenerationAsync/
        // RecordImageGenerationAsync (called from FilmJobService/CharacterDesignService) — skip those
        // kinds here or spend double-counts.
        if (_costs is not null && !string.IsNullOrWhiteSpace(projectId) && IsLedgerEligibleKind(rec.Kind))
        {
            try
            {
                await _costs.RecordApiCallSpendAsync(rec, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "cost_ledger append failed for {ProjectId}", projectId);
            }
        }
    }

    /// <summary>
    /// Classifies from transport-level signals only (HTTP status, exception message, retry count) —
    /// the same information <c>AiCallAnalyticsService.ClassifyFailure</c> used to guess from at read
    /// time, now applied once, centrally, at write time. Cannot see semantic outcomes a caller alone
    /// knows (<see cref="AiCallOutcome.ValidationReject"/>, <see cref="AiCallOutcome.VisionBlind"/>,
    /// <see cref="AiCallOutcome.CoverageGap"/>, <see cref="AiCallOutcome.Fallback"/>,
    /// <see cref="AiCallOutcome.SchemaInvalid"/>) — those calls set <see cref="ApiCallTelemetry.Outcome"/>
    /// explicitly before logging instead.
    /// </summary>
    private static AiCallOutcome ClassifyOutcome(ApiCallTelemetry rec)
    {
        if (rec.Ok)
            return rec.Attempt is > 1 ? AiCallOutcome.OkAfterRetry : AiCallOutcome.Ok;

        if (rec.HttpStatus is 429) return AiCallOutcome.RateLimited;
        if (rec.HttpStatus is 408 or 504) return AiCallOutcome.Timeout;

        var e = (rec.Error ?? "").ToLowerInvariant();
        if (e.Contains("cancel")) return AiCallOutcome.Cancelled;
        if (e.Contains("timeout") || e.Contains("timed out")) return AiCallOutcome.Timeout;
        if (e.Contains("parse") || e.Contains("unreadable") || e.Contains("missing") && e.Contains("json")
            || e.Contains("invalid json"))
            return AiCallOutcome.ParseError;

        // Any other failure (4xx/5xx, or a network exception that never reached the provider
        // cleanly) — best transport-level bucket is "the provider didn't fulfill this call".
        return AiCallOutcome.ProviderRefusal;
    }

    /// <summary>
    /// Force <see cref="ApiCallTelemetry.Model"/> / <see cref="ApiCallTelemetry.Provider"/> from
    /// <c>models_catalog.json</c>. Never invents providers or keeps hard-coded caller strings when
    /// the model is in the catalog. Unknown models keep their raw model id and clear a mismatched
    /// hard-coded provider so we do not record fake catalog identities.
    /// </summary>
    public static void ApplyCatalogIdentity(ApiCallTelemetry rec)
    {
        if (rec is null) return;
        var entry = SupportedModelCatalog.ResolveForLogging(rec.Model, rec.Kind);
        if (entry is not null)
        {
            rec.Model = entry.Id;
            if (!string.IsNullOrWhiteSpace(entry.ProviderId))
                rec.Provider = entry.ProviderId;
            return;
        }

        // Model not in catalog — do not invent a provider from string heuristics.
        if (!string.IsNullOrWhiteSpace(rec.Provider)
            && SupportedModelCatalog.IsKnownProviderId(rec.Provider))
        {
            // Keep only if it is a real catalog provider id (caller may have set it from catalog).
            rec.Provider = SupportedModelCatalog.NormalizeProviderId(rec.Provider);
        }
        else if (!string.IsNullOrWhiteSpace(rec.Provider))
        {
            // Unknown free-text provider (legacy hardcodes like "XAI") — drop rather than store noise.
            rec.Provider = null;
        }
    }

    private static bool IsLedgerEligibleKind(string? kind) => (kind ?? "").ToLowerInvariant() switch
    {
        "video" or "video_extend" or "video_poll" or "image" or "image_edit" => false,
        _ => true,
    };

    [Obsolete("Use SupportedModelCatalog.CatalogProviderId — catalog is SSoT.")]
    private static string? TryProviderForModel(string model, string? kind) =>
        SupportedModelCatalog.CatalogProviderId(model, kind);

    /// <summary>Catalog list-rate estimate — not a provider invoice line. No invent / no synthetic models.</summary>
    public static double? EstimateListRateUsd(ApiCallTelemetry rec)
    {
        try
        {
            var model = rec.Model ?? "";
            var kind = (rec.Kind ?? "").ToLowerInvariant();
            if (kind is "image" or "image_edit")
            {
                var entry = SupportedModelCatalog.ResolveForLogging(model, kind);
                if (entry?.ImageCostPerImage is not { } unit)
                    return null;
                var n = Math.Max(1, rec.ImageCount ?? 1);
                return Math.Round(unit * n, 6);
            }
            if (kind is "video" or "video_extend")
            {
                var entry = SupportedModelCatalog.ResolveForLogging(model, kind);
                if (entry is null) return null;
                var res = rec.Resolution ?? "480p";
                double? rate = null;
                if (entry.VideoCostPerSecondByResolution is { Count: > 0 } table)
                {
                    if (table.TryGetValue(res, out var r))
                        rate = r;
                    else
                        rate = table.Values.FirstOrDefault();
                }
                if (rate is null or <= 0)
                    return null;
                var sec = rec.DurationSec ?? 6;
                return Math.Round(rate.Value * sec, 6);
            }
            if (kind is "voice" or "tts" or "voice_clone")
            {
                var entry = SupportedModelCatalog.ResolveForLogging(model, kind);
                if (entry is null) return null;
                if (kind is "voice_clone" && entry.CostPerCloneUsd is { } cloneUsd)
                    return Math.Round(cloneUsd, 6);
                if (kind is "tts" && entry.CostPerThousandCharsUsd is { } perK)
                {
                    var chars = Math.Max(0, rec.PromptChars ?? 0);
                    return Math.Round((chars / 1000.0) * perK, 6);
                }
                if (entry.CostPerMinuteUsd is { } perMin && rec.DurationSec is { } sec)
                    return Math.Round((sec / 60.0) * perMin, 6);
                if (entry.CostPerCloneUsd is { } anyVoice)
                    return Math.Round(anyVoice, 6);
                return null;
            }
            if (kind is "audio" or "music")
            {
                // Music models price differently; without a catalog unit, leave null (ledger may set explicitly).
                _ = SupportedModelCatalog.ResolveForLogging(model, kind);
                return null;
            }
            // chat / vision / other text: token rates only from catalog entry (no invent).
            var chat = SupportedModelCatalog.ResolveForLogging(model, kind)
                       ?? SupportedModelCatalog.ResolveForLogging(model, "chat")
                       ?? SupportedModelCatalog.ResolveForLogging(model, "vision");
            if (chat is null) return null;
            var inPerM = chat.InputCostPerMillionTokens ?? 0;
            var outPerM = chat.OutputCostPerMillionTokens ?? 0;
            if (inPerM <= 0 && outPerM <= 0)
                return null;
            var inTok = rec.InputTokens
                        ?? Math.Max(0, (rec.PromptChars ?? ((rec.SystemPrompt?.Length ?? 0) + (rec.UserPrompt?.Length ?? 0))) / 4);
            var outTok = rec.OutputTokens
                         ?? Math.Max(0, (rec.ResponseChars ?? (rec.ResponsePreview?.Length ?? 0)) / 4);
            var usd = (inTok / 1_000_000.0) * inPerM + (outTok / 1_000_000.0) * outPerM;
            return usd > 0 ? Math.Round(usd, 6) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Append a condensed local media op.</summary>
    public async Task LogMediaOpAsync(FfmpegOpTelemetry rec, CancellationToken ct = default)
    {
        var projectId = rec.ProjectId ?? CurrentProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            _log.LogDebug("media_ops skip — no project id (op={Op})", rec.Op);
            return;
        }

        rec.ProjectId = projectId;
        if (rec.Ts is null)
            rec.Ts = DateTimeOffset.UtcNow;

        try
        {
            await AppendJsonlAsync(await MediaOpsPathAsync(projectId, ct).ConfigureAwait(false), rec, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "media_ops append failed for {ProjectId}", projectId);
        }
    }

    /// <summary>
    /// Build condensed media-op telemetry from raw process log + args.
    /// Drops frame/fps spam; keeps interesting lines + sparse progress samples.
    /// </summary>
    public static FfmpegOpTelemetry CondenseMediaOp(
        string op,
        string args,
        IReadOnlyList<string>? inputs,
        string? output,
        int exitCode,
        bool timedOut,
        long wallMs,
        string? rawLog,
        string? ffmpegExe = null,
        int? scene = null,
        int? includedCount = null,
        int? excludedCount = null,
        string? fallback = null,
        string? projectId = null)
    {
        var interesting = new List<string>();
        var progressSamples = new List<string>();
        string? lastTime = null;
        string? lastSpeed = null;

        if (!string.IsNullOrEmpty(rawLog))
        {
            foreach (var line in rawLog.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;

                if (t.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("time=", StringComparison.OrdinalIgnoreCase))
                {
                    lastTime = t.Length > 120 ? t[..120] : t;
                    // Sample sparsely: keep first, then every ~8th-ish by counting
                    if (progressSamples.Count == 0 ||
                        progressSamples.Count < 8 && progressSamples.Count % 2 == 0)
                    {
                        if (progressSamples.Count == 0 ||
                            !string.Equals(progressSamples[^1], lastTime, StringComparison.Ordinal))
                            progressSamples.Add(lastTime);
                    }
                    continue;
                }

                if (t.StartsWith("speed=", StringComparison.OrdinalIgnoreCase))
                {
                    lastSpeed = t;
                    continue;
                }

                if (ProgressFluff.IsMatch(t) && !IsInterestingLogLine(t))
                    continue;

                if (IsInterestingLogLine(t))
                {
                    interesting.Add(t.Length > 300 ? t[..300] : t);
                    if (interesting.Count >= 40) break;
                }
            }
        }

        // Cap progress samples
        if (progressSamples.Count > 8)
        {
            progressSamples = new List<string>
            {
                progressSamples[0],
                progressSamples[progressSamples.Count / 4],
                progressSamples[progressSamples.Count / 2],
                progressSamples[progressSamples.Count * 3 / 4],
                progressSamples[^1],
            };
        }

        if (lastTime is not null &&
            (progressSamples.Count == 0 || progressSamples[^1] != lastTime))
            progressSamples.Add(lastTime);

        return new FfmpegOpTelemetry
        {
            ProjectId = projectId,
            Op = op,
            Args = args,
            Inputs = inputs?.ToList(),
            Output = output,
            ExitCode = exitCode,
            TimedOut = timedOut,
            WallMs = wallMs,
            ToolPath = ffmpegExe,
            FfmpegPath = ffmpegExe,
            Scene = scene,
            IncludedCount = includedCount,
            ExcludedCount = excludedCount,
            Fallback = fallback,
            Progress = progressSamples.Count > 0 ? progressSamples : null,
            StderrInteresting = interesting.Count > 0 ? interesting : null,
            Stats = lastTime is not null || lastSpeed is not null
                ? new Dictionary<string, object?>
                {
                    ["lastTime"] = lastTime,
                    ["lastSpeed"] = lastSpeed,
                }
                : null,
        };
    }

    /// <summary>Legacy alias for <see cref="CondenseMediaOp"/>.</summary>
    [Obsolete("Use CondenseMediaOp.")]
    public static FfmpegOpTelemetry CondenseFfmpegOp(
        string op,
        string args,
        IReadOnlyList<string>? inputs,
        string? output,
        int exitCode,
        bool timedOut,
        long wallMs,
        string? rawLog,
        string? ffmpegExe = null,
        int? scene = null,
        int? includedCount = null,
        int? excludedCount = null,
        string? fallback = null,
        string? projectId = null) =>
        CondenseMediaOp(
            op, args, inputs, output, exitCode, timedOut, wallMs, rawLog,
            ffmpegExe, scene, includedCount, excludedCount, fallback, projectId);

    public static bool IsInterestingLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Invalid", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("No such", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("not found", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Error opening", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Per-path gate, kept on this instance (not JsonlStore's internal one) so a future reader of
    // api_calls.jsonl/media_ops.jsonl can coordinate against the exact same lock instance a write
    // is using (see MtimeValidatedFileCache<T, RealSemaphore>) instead of a read landing mid-append.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileAsyncLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task AppendJsonlAsync(string path, object rec, CancellationToken ct = default)
    {
        var gate = _fileAsyncLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await JsonlStore.AppendAsync(path, rec, JsonOpts, gate, ct).ConfigureAwait(false);
    }

    private sealed class ScopePop : IDisposable
    {
        private readonly Action _onDispose;
        private int _done;
        public ScopePop(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                _onDispose();
        }
    }
}

/// <summary>
/// Canonical outcome taxonomy — replaces the old read-time string-matching in
/// <c>AiCallAnalyticsService.ClassifyFailure</c> (guessed a category from <see cref="ApiCallTelemetry.Error"/>
/// text after the fact) with an explicit classification set once, at write time, in
/// <see cref="ProjectTelemetryService.LogApiCallAsync"/>. <see cref="Fallback"/>/<see cref="CoverageGap"/>/
/// <see cref="SchemaInvalid"/> aren't populated by the central classifier (it only sees transport-level
/// signals — HTTP status, exception type, attempt count — not semantic validation results from
/// <c>ValidatedModelOperation</c>); a caller with that context can set <see cref="ApiCallTelemetry.Outcome"/>
/// explicitly before logging, as <c>CharacterDesignService</c>'s style gate does for
/// <see cref="ValidationReject"/>/<see cref="VisionBlind"/>.
/// </summary>
public enum AiCallOutcome
{
    Ok,
    OkAfterRetry,
    Fallback,
    CoverageGap,
    ValidationReject,
    VisionBlind,
    ParseError,
    SchemaInvalid,
    RateLimited,
    Timeout,
    ProviderRefusal,
    Cancelled,
}

/// <summary>One live API call (full prompts on disk for project review).</summary>
public sealed class ApiCallTelemetry
{
    public DateTimeOffset? Ts { get; set; }
    public string? ProjectId { get; set; }
    /// <summary>Signed-in user who triggered the call (BYOK / cost attribution).</summary>
    public string? UserId { get; set; }
    /// <summary>
    /// Provider id from <c>models_catalog.json</c> (<c>providers[].id</c> / model <c>providerId</c>),
    /// e.g. grok, gemini, openai, anthropic, fal, suno, aimusicapi, elevenlabs.
    /// Filled automatically by <see cref="ProjectTelemetryService.LogApiCallAsync"/> from the model id.
    /// </summary>
    public string? Provider { get; set; }
    /// <summary>List-rate USD estimate at call time (catalog). Not a provider invoice.</summary>
    public double? EstimatedUsd { get; set; }
    /// <summary>Customer charge USD (list × admin charge multiplier). Per-user tracking.</summary>
    public double? ChargeUsd { get; set; }
    /// <summary>Multiplier used when <see cref="ChargeUsd"/> was computed.</summary>
    public double? ChargeMultiplier { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    /// <summary>
    /// User-facing cost bucket (<see cref="CostCategories"/>): screenplay, characters, video, voice, music, other.
    /// Set automatically in <see cref="ProjectTelemetryService.LogApiCallAsync"/> if omitted.
    /// </summary>
    public string? Category { get; set; }
    /// <summary>Transport kind: video | image | chat | … (internal; prefer <see cref="Category"/> for UX).</summary>
    public string Kind { get; set; } = "";
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public int? HttpStatus { get; set; }
    public string? RequestId { get; set; }
    public string? Error { get; set; }
    public long? DurationMs { get; set; }
    public int? Scene { get; set; }
    public int? Clip { get; set; }
    public string? CharKey { get; set; }
    /// <summary>
    /// Call purpose tag. Chat: <c>book_to_fountain</c>, <c>cast_from_screenplay</c>, …
    /// (see <c>ChatCallModes</c>). Video: <c>fresh</c> | <c>video-extend</c> | <c>reseed</c> | …
    /// </summary>
    public string? Mode { get; set; }
    public string? Prompt { get; set; }
    public string? SystemPrompt { get; set; }
    public string? UserPrompt { get; set; }
    public string? ResponsePreview { get; set; }
    public List<string>? ReferenceImagePaths { get; set; }
    public bool? RefsAttached { get; set; }
    public string? Resolution { get; set; }
    public double? DurationSec { get; set; }
    public int? Attempt { get; set; }
    public string? JobId { get; set; }
    public bool Fakes { get; set; }
    public int? ImageCount { get; set; }
    public int? PromptChars { get; set; }
    public int? ResponseChars { get; set; }
    public bool Ok { get; set; } = true;
    /// <summary>
    /// Explicit outcome classification. Leave null to let <see cref="ProjectTelemetryService.LogApiCallAsync"/>
    /// classify it centrally from the transport-level signals above; set it explicitly when the caller has
    /// semantic context the transport layer doesn't (e.g. a vision gate rejecting on content, not transport).
    /// </summary>
    public AiCallOutcome? Outcome { get; set; }
}

/// <summary>One condensed local media operation (historical name: ffmpeg op).</summary>
public sealed class FfmpegOpTelemetry
{
    public DateTimeOffset? Ts { get; set; }
    public string? ProjectId { get; set; }
    public string Op { get; set; } = "";
    public string? Args { get; set; }
    public List<string>? Inputs { get; set; }
    public string? Output { get; set; }
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public long WallMs { get; set; }
    /// <summary>Tool binary path if any (legacy field name FfmpegPath).</summary>
    public string? ToolPath { get; set; }
    public string? FfmpegPath { get; set; }
    public int? Scene { get; set; }
    public int? IncludedCount { get; set; }
    public int? ExcludedCount { get; set; }
    public string? Fallback { get; set; }
    public List<string>? Progress { get; set; }
    public List<string>? StderrInteresting { get; set; }
    public Dictionary<string, object?>? Stats { get; set; }
    public bool Ok => !TimedOut && ExitCode == 0;
}
