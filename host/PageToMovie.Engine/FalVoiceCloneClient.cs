using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Fal.ai / MiniMax voice-clone + text-to-speech client. Two queue-based calls (same
/// submit → poll status → fetch result convention as <see cref="FalVideoClient"/>):
/// <c>fal-ai/minimax/voice-clone</c> (reference sample → provider voice id) and
/// <c>fal-ai/minimax/speech-02-hd</c> (text + voice id → speech audio). Default model ids
/// resolved from models_catalog.json via <see cref="SupportedModelEntry.IsVoiceCloneStep"/>.
/// Explicit, human-triggered only — never call this from an automatic job/pipeline; every call
/// spends real provider money ($1.50/clone, $0.10/1000 chars as of 2026-08 — see catalog notes).
/// </summary>
public sealed class FalVoiceCloneClient : IVoiceCloneClient
{
    public const string ApiBase = SupportedModelCatalog.FalApiBase;

    private const int MaxPollAttempts = 60; // * 3s delay = 3 minutes — clone/TTS are fast, unlike video gen
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly ILogger<FalVoiceCloneClient> _log;

    public FalVoiceCloneClient(HttpClient http, ILogger<FalVoiceCloneClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey() => ProviderApiKey.ResolveFal();

    /// <summary>Resolves the catalog's clone-shaped Voice model — explicit <paramref name="model"/> id if it
    /// resolves to a clone-shaped entry, else the first enabled clone-shaped Fal Voice model.</summary>
    private static SupportedModelEntry ResolveCloneModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            var hit = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                      ?? SupportedModelCatalog.Find(model);
            if (hit is { IsVoiceCloneStep: true, Enabled: true }) return hit;
            // Selected speak model for Fal → use same-provider clone step.
            if (hit is not null && hit.Provider == ModelProviderFamily.Fal)
            {
                var pair = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                    .FirstOrDefault(m => m.IsVoiceCloneStep && m.Provider == ModelProviderFamily.Fal && m.Enabled);
                if (pair is not null) return pair;
            }
        }
        throw new InvalidOperationException(
            "Voice clone model is not selected or not a clone-capable catalog model. " +
            "Open Settings → Voice clone and choose MiniMax Voice Clone (Fal.ai).");
    }

    /// <summary>Resolves the catalog's speak-shaped Voice model (mirror of <see cref="ResolveCloneModel"/>).</summary>
    private static SupportedModelEntry ResolveSpeakModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            var hit = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                      ?? SupportedModelCatalog.Find(model);
            if (hit is { IsVoiceCloneStep: false, Enabled: true }) return hit;
            if (hit is not null && hit.Provider == ModelProviderFamily.Fal)
            {
                var pair = SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                    .FirstOrDefault(m => !m.IsVoiceCloneStep && m.Provider == ModelProviderFamily.Fal && m.Enabled);
                if (pair is not null) return pair;
            }
        }
        throw new InvalidOperationException(
            "Voice speech model is not selected. Open Settings → Voice clone and ensure a Fal MiniMax speak model is available.");
    }

    public async Task<string?> CloneVoiceAsync(
        string sampleAudioPath,
        string? model = null,
        CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("Fal.ai API key is missing — skipping voice clone.");
            return null;
        }
        if (!File.Exists(sampleAudioPath))
            throw new FileNotFoundException("Voice-clone reference sample not found.", sampleAudioPath);

        var entry = ResolveCloneModel(model);
        var audioDataUri = await ToDataUriAsync(sampleAudioPath, ct).ConfigureAwait(false);
        var payload = new Dictionary<string, object?> { ["audio_url"] = audioDataUri };

        using var doc = await SubmitAndPollAsync(entry.EndpointPath, payload, apiKey, ct).ConfigureAwait(false);
        if (doc is null) return null;

        if (doc.RootElement.TryGetProperty("custom_voice_id", out var idEl) &&
            idEl.GetString() is { Length: > 0 } voiceId)
        {
            _log.LogInformation("Fal.ai voice clone succeeded: {VoiceId}", voiceId);
            return voiceId;
        }

        _log.LogError("Fal.ai voice-clone result missing custom_voice_id: {Body}", doc.RootElement.GetRawText());
        return null;
    }

    public async Task<string?> SynthesizeSpeechAsync(
        string text,
        string voiceId,
        string? model = null,
        CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("Fal.ai API key is missing — skipping speech synthesis.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text required", nameof(text));
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("voiceId required — clone a voice first.", nameof(voiceId));

        var entry = ResolveSpeakModel(model);
        var maxLen = entry.MaxPromptLength
            ?? throw new InvalidOperationException(
                $"Voice model '{entry.Id}' has no maxPromptLength in models_catalog.json.");
        if (text.Length > maxLen)
            throw new InvalidOperationException(
                $"Text is {text.Length} characters — exceeds this voice model's {maxLen}-character limit per call. Split into multiple calls.");

        var payload = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["voice_setting"] = new Dictionary<string, object?> { ["voice_id"] = voiceId },
        };

        using var doc = await SubmitAndPollAsync(entry.EndpointPath, payload, apiKey, ct).ConfigureAwait(false);
        if (doc is null) return null;

        if (doc.RootElement.TryGetProperty("audio", out var audioEl) &&
            audioEl.TryGetProperty("url", out var urlEl) &&
            urlEl.GetString() is { Length: > 0 } audioUrl)
        {
            return audioUrl;
        }

        _log.LogError("Fal.ai speech synthesis result missing audio.url: {Body}", doc.RootElement.GetRawText());
        return null;
    }

    private async Task<JsonDocument?> SubmitAndPollAsync(
        string endpoint,
        Dictionary<string, object?> payload,
        string apiKey,
        CancellationToken ct)
    {
        var requestId = await SubmitQueueRequestAsync(endpoint, payload, apiKey, ct).ConfigureAwait(false);
        if (requestId is null) return null;

        var statusUrl = $"{endpoint}/requests/{requestId}/status";
        var resultUrl = $"{endpoint}/requests/{requestId}";

        for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var (hardFail, statusBody) = await ReadPollStatusAsync(endpoint, statusUrl, apiKey, ct).ConfigureAwait(false);
            if (hardFail) return null;
            if (statusBody is null) continue;

            using var statusDoc = JsonDocument.Parse(statusBody);
            var status = statusDoc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";

            var (terminal, doc) = await TryTakeTerminalResultAsync(
                endpoint, status, resultUrl, apiKey, statusDoc.RootElement, ct).ConfigureAwait(false);
            if (terminal) return doc;

            await Task.Delay(PollDelay, ct).ConfigureAwait(false);
        }

        _log.LogError("Fal.ai {Endpoint} request {RequestId} timed out after {Seconds}s", endpoint, requestId, MaxPollAttempts * PollDelay.TotalSeconds);
        return null;
    }

    private async Task<string?> SubmitQueueRequestAsync(
        string endpoint, Dictionary<string, object?> payload, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
        req.Content = JsonContent.Create(payload);

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Fal.ai {Endpoint} submit failed HTTP {Status} ({Elapsed}ms): {Body}", endpoint, resp.StatusCode, sw.ElapsedMilliseconds, body);
            return null;
        }

        using var submitDoc = JsonDocument.Parse(body);
        if (!submitDoc.RootElement.TryGetProperty("request_id", out var reqIdEl) ||
            reqIdEl.GetString() is not { Length: > 0 } requestId)
        {
            _log.LogError("Fal.ai {Endpoint} response missing request_id: {Body}", endpoint, body);
            return null;
        }
        return requestId;
    }

    private async Task<(bool HardFail, string? Body)> ReadPollStatusAsync(
        string endpoint, string statusUrl, string apiKey, CancellationToken ct)
    {
        using var statusReq = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        statusReq.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
        using var statusResp = await _http.SendAsync(statusReq, ct).ConfigureAwait(false);
        var statusBody = await statusResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (statusResp.IsSuccessStatusCode)
            return (false, statusBody);
        if (statusResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return (false, null);
        }
        _log.LogError("Fal.ai {Endpoint} status query failed HTTP {Status}: {Body}", endpoint, statusResp.StatusCode, statusBody);
        return (true, null);
    }

    private async Task<(bool Terminal, JsonDocument? Doc)> TryTakeTerminalResultAsync(
        string endpoint, string status, string resultUrl, string apiKey, JsonElement statusRoot, CancellationToken ct)
    {
        if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            using var resultReq = new HttpRequestMessage(HttpMethod.Get, resultUrl);
            resultReq.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
            using var resultResp = await _http.SendAsync(resultReq, ct).ConfigureAwait(false);
            var resultBody = await resultResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resultResp.IsSuccessStatusCode)
            {
                _log.LogError("Fal.ai {Endpoint} result fetch failed HTTP {Status}: {Body}", endpoint, resultResp.StatusCode, resultBody);
                return (true, null);
            }
            return (true, JsonDocument.Parse(resultBody));
        }

        if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            var err = statusRoot.TryGetProperty("error", out var eEl) ? eEl.GetString() : "Job failed";
            _log.LogError("Fal.ai {Endpoint} failed: {Error}", endpoint, err);
            return (true, null);
        }

        return (false, null);
    }

    private static async Task<string> ToDataUriAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var contentType = ResolveAudioContentType(path);
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>Exposed for tests — pure MIME-type mapping, no I/O.</summary>
    public static string ResolveAudioContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".aac" => "audio/aac",
        ".flac" => "audio/flac",
        ".webm" => "audio/webm",
        _ => "audio/mpeg",
    };
}
