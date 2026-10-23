using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// xAI Custom Voices + Text to Speech (<c>POST /v1/custom-voices</c>, <c>POST /v1/tts</c>).
/// Uses the same api.x.ai base and catalog key slot as Grok chat/video. English only:
/// every request sends <see cref="EnglishLanguage"/>. Catalog ids are provisional — xAI has
/// not published a TTS model id — so this client sends the catalog <c>endpointPath</c> and
/// never a model field. Explicit, human-triggered only.
/// </summary>
public sealed class XaiVoiceCloneClient : IVoiceCloneClient
{
    /// <summary>BCP-47 code sent on every xAI voice request. This product path is English-only.</summary>
    public const string EnglishLanguage = "en";

    /// <summary>
    /// Shown when custom-voice create returns HTTP 403.
    /// Docs: Enterprise-gated, United States only (not Illinois).
    /// </summary>
    public const string EnterpriseCustomVoiceError =
        "xAI custom voices are not enabled for this team. " +
        "POST /v1/custom-voices is Enterprise-gated and available only in the United States (not Illinois). " +
        "Create the voice in the xAI console, or enable the create API on an Enterprise plan.";

    private readonly HttpClient _http;
    private readonly ILogger<XaiVoiceCloneClient> _log;

    public XaiVoiceCloneClient(HttpClient http, ILogger<XaiVoiceCloneClient> log)
    {
        _http = http;
        _log = log;
        ProviderHttpHelpers.EnsureTrailingSlashBaseAddress(_http, SupportedModelCatalog.XaiApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey(null));

    public Task<string?> CloneVoiceAsync(
        string sampleAudioPath,
        string? model = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sampleAudioPath))
            throw new ArgumentException("sampleAudioPath required", nameof(sampleAudioPath));
        if (!File.Exists(sampleAudioPath))
            throw new FileNotFoundException("Voice-clone reference sample not found.", sampleAudioPath);
        var entry = RequireModel(model, cloneStep: true);
        return CloneCoreAsync(sampleAudioPath, entry, ct);
    }

    public Task<string?> SynthesizeSpeechAsync(
        string text,
        string voiceId,
        string? model = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text required", nameof(text));
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("voiceId required — clone a voice first.", nameof(voiceId));
        var entry = RequireModel(model, cloneStep: false);
        var maxLen = entry.MaxPromptLength
            ?? throw new InvalidOperationException(
                $"Voice model '{entry.Id}' has no maxPromptLength in models_catalog.json.");
        if (text.Length > maxLen)
            throw new InvalidOperationException(
                $"Text is {text.Length} characters — exceeds this voice model's {maxLen}-character limit per call. Split into multiple calls.");
        return SpeakCoreAsync(text, voiceId, entry, ct);
    }

    /// <summary>MIME type for a reference clip. Formats xAI documents as accepted, plus a safe default.</summary>
    public static string ResolveAudioContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".opus" => "audio/opus",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "audio/webm",
        _ => "application/octet-stream",
    };

    private async Task<string?> CloneCoreAsync(string sampleAudioPath, SupportedModelEntry entry, CancellationToken ct)
    {
        var apiKey = ResolveApiKey(entry);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("xAI API key is missing — skipping voice clone.");
            return null;
        }

        var endpoint = RelativeEndpoint(entry);
        using var form = new MultipartFormDataContent();
        var name = Path.GetFileNameWithoutExtension(sampleAudioPath);
        if (!string.IsNullOrWhiteSpace(name))
            form.Add(new StringContent(name), "name");
        form.Add(new StringContent(EnglishLanguage), "language");

        var bytes = await File.ReadAllBytesAsync(sampleAudioPath, ct).ConfigureAwait(false);
        var fileName = Path.GetFileName(sampleAudioPath);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(ResolveAudioContentType(fileName));
        form.Add(file, "file", string.IsNullOrWhiteSpace(fileName) ? "reference.wav" : fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        ProviderHttpHelpers.ApplyBearer(req, apiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw StatusError(resp.StatusCode, body, customVoice: true);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("xAI custom-voice response was not JSON.");
        }

        using (doc)
        {
            if (doc.RootElement.TryGetProperty("voice_id", out var idEl) &&
                idEl.GetString() is { Length: > 0 } voiceId)
            {
                _log.LogInformation("xAI custom voice created: {VoiceId}", voiceId);
                return voiceId;
            }
        }

        _log.LogError("xAI custom-voice response missing voice_id: {Body}", ProviderHttpHelpers.Trim(body, 400));
        return null;
    }

    private async Task<string?> SpeakCoreAsync(
        string text, string voiceId, SupportedModelEntry entry, CancellationToken ct)
    {
        var apiKey = ResolveApiKey(entry);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("xAI API key is missing — skipping speech synthesis.");
            return null;
        }

        var endpoint = RelativeEndpoint(entry);
        var payload = new Dictionary<string, string>
        {
            ["text"] = text,
            ["voice_id"] = voiceId,
            ["language"] = EnglishLanguage,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        ProviderHttpHelpers.ApplyBearer(req, apiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw StatusError(resp.StatusCode, err, customVoice: false);
        }

        var mime = resp.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        if (mime.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("xAI text-to-speech returned JSON instead of audio.");

        var audio = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (audio.Length == 0)
            throw new InvalidOperationException("xAI text-to-speech returned no audio.");
        return VoiceSpeechPayload.ToDataUri(audio, mime);
    }

    private static SupportedModelEntry RequireModel(string? model, bool cloneStep)
    {
        SupportedModelEntry? hit = null;
        if (!string.IsNullOrWhiteSpace(model))
        {
            hit = SupportedModelCatalog.Find(model, ModelCapability.Voice)
                  ?? SupportedModelCatalog.Find(model);
        }

        if (hit is { Enabled: true, Provider: ModelProviderFamily.Xai } && hit.IsVoiceCloneStep == cloneStep)
            return hit;

        if (hit is { Enabled: true, Provider: ModelProviderFamily.Xai })
        {
            var pair = FindStep(cloneStep);
            if (pair is not null)
                return pair;
        }

        throw new InvalidOperationException(cloneStep
            ? "Voice clone model is not selected or not an xAI clone model. Open Settings and choose the English xAI voice option."
            : "Voice speech model is not selected or not an xAI speak model. Open Settings and choose the English xAI voice option.");
    }

    private static SupportedModelEntry? FindStep(bool cloneStep) =>
        SupportedModelCatalog.ForCapability(ModelCapability.Voice)
            .FirstOrDefault(m => m.Provider == ModelProviderFamily.Xai && m.IsVoiceCloneStep == cloneStep);

    private static string RelativeEndpoint(SupportedModelEntry entry)
    {
        var path = (entry.EndpointPath ?? "").Trim().TrimStart('/');
        if (path.Length == 0)
            throw new InvalidOperationException(
                $"Voice model '{entry.Id}' has no endpointPath in models_catalog.json.");
        return path;
    }

    private static string? ResolveApiKey(SupportedModelEntry? entry)
    {
        var providerId = entry?.ProviderId;
        if (string.IsNullOrWhiteSpace(providerId))
            providerId = SupportedModelCatalog.ProviderIdForApiBase(SupportedModelCatalog.XaiApiBase);
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var ambient = ProviderApiKey.Clean(ApiKeyScope.Get(providerId));
            if (!string.IsNullOrWhiteSpace(ambient))
                return ambient;
        }

        IEnumerable<string> keys = entry is { RequiredEnvKeys.Count: > 0 }
            ? entry.RequiredEnvKeys
            : SupportedModelCatalog.ForCapability(ModelCapability.Voice)
                .Where(m => m.Provider == ModelProviderFamily.Xai)
                .SelectMany(m => m.RequiredEnvKeys)
                .Distinct(StringComparer.Ordinal);
        foreach (var env in keys)
        {
            var value = ProviderApiKey.Clean(Environment.GetEnvironmentVariable(env));
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static InvalidOperationException StatusError(HttpStatusCode status, string body, bool customVoice)
    {
        var detail = ProviderHttpHelpers.Trim(body, 300);
        if (customVoice && status == HttpStatusCode.Forbidden)
        {
            var message = string.IsNullOrEmpty(detail)
                ? EnterpriseCustomVoiceError
                : EnterpriseCustomVoiceError + " " + detail;
            return new InvalidOperationException(message);
        }

        return new InvalidOperationException(
            $"xAI voice request failed HTTP {(int)status}. {detail}");
    }
}
