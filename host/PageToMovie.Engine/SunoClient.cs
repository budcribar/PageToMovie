using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Suno background-music generation via sunoapi.org — an unofficial third-party Suno reseller
/// (Suno itself has no public API as of 2026-07). Submits a generation task, then polls for
/// completion (no public webhook receiver exists here, so polling only). Unlike Fal.ai's
/// stable-audio, this provider documents a real duration control (10-360s on model V5_5 custom
/// mode) — see SupportedModelCatalog's suno-v5-5 entry — which is the whole reason to have it:
/// scenes over Fal's 47s cap don't need to be stitched from independently-generated segments.
/// </summary>
public sealed class SunoClient : IAudioClient
{
    public const string ApiBase = "https://api.sunoapi.org/api/v1/";

    private readonly HttpClient _http;
    private readonly ILogger<SunoClient> _log;

    public SunoClient(HttpClient http, ILogger<SunoClient> log)
    {
        _http = http;
        _log = log;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey()
    {
        var key = ApiKeyScope.CurrentSuno
            ?? Environment.GetEnvironmentVariable(SupportedModelCatalog.SunoApiKeyEnv);
        return ProviderApiKey.Clean(key);
    }

    public async Task<string?> GenerateMusicTrackAsync(
        string prompt,
        int durationSeconds,
        string? model = null,
        CancellationToken ct = default,
        Action<string>? onProgress = null,
        bool isVocal = false,
        string? lyrics = null)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("Suno (sunoapi.org) API key is missing — skipping audio generation.");
            return null;
        }

        var clampedDuration = Math.Clamp(durationSeconds, 10, 360);
        // Wire model comes from Settings/catalog id (e.g. suno-v5-5 → vendor token V5_5 via notes/id).
        var wireModel = ResolveSunoWireModel(model);
        var payload = new Dictionary<string, object?>
        {
            ["customMode"] = true,
            ["instrumental"] = !isVocal,
            // "style" carries genre/mood tags either way; when singing, "prompt" carries the actual
            // lyrics to sing — Suno's customMode API keeps these as two separate fields.
            ["style"] = prompt,
            ["prompt"] = isVocal ? (lyrics ?? "") : "",
            ["title"] = "Scene Score",
            ["model"] = wireModel,
            ["duration"] = clampedDuration,
            // No public webhook receiver here — we poll record-info instead. Their docs list
            // callBackUrl as required; empty string is accepted in practice by this class of API.
            ["callBackUrl"] = "",
        };

        return await SubmitToSunoAsync(payload, apiKey, onProgress, ct).ConfigureAwait(false);
    }

    private Task<string?> SubmitToSunoAsync(
        Dictionary<string, object?> payload, string apiKey, Action<string>? onProgress, CancellationToken ct) =>
        MusicResellerHttp.SubmitAndPollAsync(
            new HttpCall(_http, apiKey, _log, ct, onProgress), "generate", payload,
            "Submitting to Suno (sunoapi.org)…",
            "Suno (sunoapi.org) submit failed HTTP {Status}: {Body}",
            "Suno submit failed: HTTP ",
            ReadSunoSubmitTaskId,
            "Suno (sunoapi.org) submit returned unparseable JSON: {Body}",
            "Suno (sunoapi.org) submit response had no taskId: {Body}",
            (taskId, pollCt) => MusicResellerHttp.TickGetAndHandleAsync(
                _http, $"generate/record-info?taskId={Uri.EscapeDataString(taskId)}", apiKey, _log,
                "Suno (sunoapi.org) poll HTTP {Status}: {Body}",
                body => TryHandlePollBody(body, onProgress), pollCt),
            "Suno (sunoapi.org) generation timed out after {Timeout} for task {TaskId}",
            "Suno generation timed out.");

    private static string? ReadSunoSubmitTaskId(JsonElement root) =>
        root.TryGetProperty("data", out var dataEl)
        && dataEl.TryGetProperty("taskId", out var idEl)
            ? idEl.GetString()
            : null;

    /// <summary>Done=true ends the poll loop (success URL, SUCCESS-without-URL, or vendor failure).</summary>
    private (bool Done, string? AudioUrl) TryHandlePollBody(string body, Action<string>? onProgress)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl))
                return (false, null);

            var status = dataEl.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            onProgress?.Invoke($"Suno status: {status ?? "unknown"}");

            if (string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                return HandleSuccessStatus(dataEl, body);
            if (IsSunoFailureStatus(status))
                return HandleFailureStatus(dataEl, status);
            // PENDING / TEXT_SUCCESS / FIRST_SUCCESS — keep polling.
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Suno (sunoapi.org) poll returned unparseable JSON: {Body}", body);
        }
        return (false, null);
    }

    private (bool Done, string? AudioUrl) HandleSuccessStatus(JsonElement dataEl, string body)
    {
        var audioUrl = TryReadFirstAudioUrl(dataEl);
        if (!string.IsNullOrWhiteSpace(audioUrl))
        {
            _log.LogInformation("Suno (sunoapi.org) audio ready: {Url}", audioUrl);
            return (true, audioUrl);
        }
        _log.LogError("Suno (sunoapi.org) status SUCCESS but no audioUrl found: {Body}", body);
        return (true, null);
    }

    private static string? TryReadFirstAudioUrl(JsonElement dataEl)
    {
        if (!dataEl.TryGetProperty("response", out var responseEl) ||
            !responseEl.TryGetProperty("sunoData", out var sunoDataEl) ||
            sunoDataEl.ValueKind != JsonValueKind.Array ||
            sunoDataEl.GetArrayLength() <= 0)
            return null;
        var first = sunoDataEl[0];
        return first.TryGetProperty("audioUrl", out var urlEl) ? urlEl.GetString() : null;
    }

    private static bool IsSunoFailureStatus(string? status) =>
        status is "CREATE_TASK_FAILED" or "GENERATE_AUDIO_FAILED"
            or "CALLBACK_EXCEPTION" or "SENSITIVE_WORD_ERROR";

    private (bool Done, string? AudioUrl) HandleFailureStatus(JsonElement dataEl, string? status)
    {
        var errorMessage = dataEl.TryGetProperty("errorMessage", out var errEl) ? errEl.GetString() : null;
        _log.LogError("Suno (sunoapi.org) generation failed: {Status} {Error}", status, errorMessage);
        return (true, null);
    }

    /// <summary>
    /// Map catalog model id (e.g. suno-v5-5) to the vendor wire token required by sunoapi.org.
    /// Wire token is derived from the catalog id/displayName only — no private const defaults.
    /// </summary>
    private static string ResolveSunoWireModel(string? model)
    {
        var entry = SupportedModelCatalog.Find(model, ModelCapability.Audio)
                    ?? SupportedModelCatalog.Find(model);
        if (entry is null || !entry.Enabled)
            throw new InvalidOperationException(
                "Background music: no Suno model selected. Open Settings → Studio coverage and choose a music model.");

        // Vendor API expects tokens like V5_5; catalog uses suno-v5-5.
        var id = entry.Id;
        if (id.StartsWith("suno-v", StringComparison.OrdinalIgnoreCase))
        {
            var rest = id["suno-".Length..].Replace('-', '_').ToUpperInvariant();
            return rest; // v5-5 → V5_5
        }
        if (id.StartsWith("V", StringComparison.OrdinalIgnoreCase) && id.Contains('_'))
            return id;
        // Last resort: uppercase display-ish token from id
        return id.Replace('-', '_').ToUpperInvariant();
    }
}
