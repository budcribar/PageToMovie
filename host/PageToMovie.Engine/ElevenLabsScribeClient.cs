using System.Net.Http.Headers;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>One transcribed token from Scribe (word or spacing/audio-event), with its timing.</summary>
public sealed record ScribeWord(string Text, double Start, double End, string? Type);

/// <summary>Result of a Scribe speech-to-text transcription.</summary>
public sealed record ScribeResult(bool Ok, string? Text, IReadOnlyList<ScribeWord> Words, string? LanguageCode, string? Error)
{
    public static ScribeResult Fail(string error) => new(false, null, Array.Empty<ScribeWord>(), null, error);
}

/// <summary>
/// ElevenLabs "Scribe" speech-to-text (POST /v1/speech-to-text). Reuses the same
/// <c>ElevenLabs_API_KEY</c> as the voice clone / TTS and Eleven Music. Used to VERIFY that a
/// detected dialogue window actually contains the expected narrator line (→ confident line↔window
/// mapping for the dub overlay + read-along text), never to transcribe the user's own takes.
/// </summary>
public sealed class ElevenLabsScribeClient
{
    // scribe_v1 is the broadly-available STT model; word-level timestamps are returned by default.
    private const string DefaultModel = "scribe_v1";

    private readonly HttpClient _http;
    private readonly ILogger<ElevenLabsScribeClient> _log;

    public ElevenLabsScribeClient(HttpClient http, ILogger<ElevenLabsScribeClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(SupportedModelCatalog.ElevenLabsApiBase.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar);
    }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey() => ProviderApiKey.ResolveElevenLabs();

    public async Task<ScribeResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        string? languageCode = null,
        CancellationToken ct = default)
    {
        if (audio is null || audio.Length < 128)
            return ScribeResult.Fail("Audio is empty or too short.");

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return ScribeResult.Fail("ElevenLabs_API_KEY not configured.");

        try
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(DefaultModel), "model_id" },
                { new StringContent("word"), "timestamps_granularity" },
            };
            if (!string.IsNullOrWhiteSpace(languageCode))
                form.Add(new StringContent(languageCode.Trim()), "language_code");

            var fileContent = new ByteArrayContent(audio);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessAudioMime(fileName));
            form.Add(fileContent, "file", string.IsNullOrWhiteSpace(fileName) ? "segment.wav" : Path.GetFileName(fileName));

            using var req = new HttpRequestMessage(HttpMethod.Post, "speech-to-text");
            req.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
            req.Content = form;

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Scribe STT failed {Status}: {Body}", (int)resp.StatusCode, Trunc(body));
                return ScribeResult.Fail($"Scribe failed ({(int)resp.StatusCode}): {Trunc(body, 160)}");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
            var lang = root.TryGetProperty("language_code", out var l) ? l.GetString() : null;

            var words = new List<ScribeWord>();
            if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in wordsEl.EnumerateArray())
                {
                    var wt = w.TryGetProperty("text", out var wtEl) ? wtEl.GetString() ?? "" : "";
                    var start = w.TryGetProperty("start", out var sEl) && sEl.TryGetDouble(out var sv) ? sv : 0;
                    var end = w.TryGetProperty("end", out var eEl) && eEl.TryGetDouble(out var ev) ? ev : 0;
                    var type = w.TryGetProperty("type", out var tyEl) ? tyEl.GetString() : "word";
                    words.Add(new ScribeWord(wt, start, end, type));
                }
            }

            return new ScribeResult(true, text ?? "", words, lang, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scribe STT exception");
            return ScribeResult.Fail(ex.Message);
        }
    }

    private static string GuessAudioMime(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };

    private static string Trunc(string? s, int max = 240) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
