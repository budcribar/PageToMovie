using System.Net.Http.Headers;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// xAI batch speech-to-text (<c>POST /v1/stt</c>). Model id, endpoint, and key slot
/// come from the catalog row. Streaming (<c>wss://api.x.ai/v1/stt</c>) is not used.
/// </summary>
public sealed class XaiSpeechToTextClient : ITranscriptionClient
{
    /// <summary>Documented REST upload cap (docs.x.ai speech-to-text, reviewed 2026-10-14).</summary>
    public const int MaxFileBytes = 500 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<XaiSpeechToTextClient> _log;

    public XaiSpeechToTextClient(HttpClient http, ILogger<XaiSpeechToTextClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(SupportedModelCatalog.XaiApiBase.TrimEnd('/') + "/");
    }

    public async Task<ScribeResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        string? languageCode = null,
        string? modelId = null,
        CancellationToken ct = default)
    {
        if (audio is null || audio.Length < 128)
            return ScribeResult.Fail("Audio is empty or too short.");
        if (audio.Length > MaxFileBytes)
            return ScribeResult.Fail("Audio exceeds the 500 MB speech-to-text limit.");

        var entry = ResolveEntry(modelId);
        if (entry is null)
            return ScribeResult.Fail("Speech to text: no xAI model selected.");

        var apiKey = ResolveApiKey(entry);
        if (string.IsNullOrWhiteSpace(apiKey))
            return ScribeResult.Fail($"Speech to text: no API key for catalog provider '{entry.ProviderId}'.");

        try
        {
            using var form = BuildForm(audio, fileName, languageCode, entry);
            using var req = new HttpRequestMessage(HttpMethod.Post, entry.EndpointPath.TrimStart('/'));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Content = form;

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("xAI speech-to-text failed {Status}: {Body}", (int)resp.StatusCode, ElevenLabsClientHelpers.Trunc(body));
                return ScribeResult.Fail($"Speech to text failed ({(int)resp.StatusCode}): {ElevenLabsClientHelpers.Trunc(body, 160)}");
            }

            return ParseResponse(body);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "xAI speech-to-text exception");
            return ScribeResult.Fail(ex.Message);
        }
    }

    private static SupportedModelEntry? ResolveEntry(string? modelId)
    {
        if (!ProjectModelSelection.IsUsableModelId(modelId))
            return null;
        var entry = SupportedModelCatalog.Find(modelId, ModelCapability.Transcription);
        return entry is { Enabled: true, Provider: ModelProviderFamily.Xai } ? entry : null;
    }

    private static string? ResolveApiKey(SupportedModelEntry entry)
    {
        var ambient = ProviderApiKey.Clean(ApiKeyScope.Get(entry.ProviderId));
        if (!string.IsNullOrWhiteSpace(ambient))
            return ambient;

        foreach (var env in entry.RequiredEnvKeys)
        {
            var value = ProviderApiKey.Clean(Environment.GetEnvironmentVariable(env));
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static MultipartFormDataContent BuildForm(
        byte[] audio, string fileName, string? languageCode, SupportedModelEntry entry)
    {
        // Option fields must precede the file. Words (start/end/text) are returned by default.
        var form = new MultipartFormDataContent
        {
            { new StringContent(entry.Id), "model" },
        };
        if (!string.IsNullOrWhiteSpace(languageCode))
            form.Add(new StringContent(languageCode.Trim()), "language");

        var fileContent = new ByteArrayContent(audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(ElevenLabsClientHelpers.GuessAudioMime(fileName));
        form.Add(fileContent, "file", string.IsNullOrWhiteSpace(fileName) ? "segment.wav" : Path.GetFileName(fileName));
        return form;
    }

    private static ScribeResult ParseResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
        var lang = root.TryGetProperty("language", out var l) ? l.GetString() : null;
        return new ScribeResult(true, text ?? "", ParseWords(root), lang, null);
    }

    private static List<ScribeWord> ParseWords(JsonElement root)
    {
        var words = new List<ScribeWord>();
        if (!root.TryGetProperty("words", out var wordsEl) || wordsEl.ValueKind != JsonValueKind.Array)
            return words;
        foreach (var w in wordsEl.EnumerateArray())
            words.Add(ReadWord(w));
        return words;
    }

    private static ScribeWord ReadWord(JsonElement w)
    {
        var text = w.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
        var start = w.TryGetProperty("start", out var sEl) && sEl.TryGetDouble(out var sv) ? sv : 0;
        var end = w.TryGetProperty("end", out var eEl) && eEl.TryGetDouble(out var ev) ? ev : 0;
        return new ScribeWord(text, start, end, "word");
    }
}
