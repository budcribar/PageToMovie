using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Background-music generation via ElevenLabs "Eleven Music" (POST /v1/music). Reuses the same
/// <c>ElevenLabs_API_KEY</c> as <see cref="ElevenLabsVoiceClient"/>, so a studio that already voices
/// the narrator with ElevenLabs can score scenes without funding a separate Suno reseller account.
///
/// Unlike the Suno/Fal music clients (which return a provider-hosted URL the media proxy fetches),
/// ElevenLabs streams the audio bytes back inline. To keep the <see cref="IAudioClient"/> contract
/// (return a URL string) and avoid the API host persisting media on disk, the bytes are handed back
/// as a self-contained <c>data:audio/mpeg;base64,…</c> URL; the media-proxy endpoint decodes it.
/// </summary>
public sealed class ElevenLabsMusicClient : IAudioClient
{
    // ElevenLabs Music enforces 3s–10min song length when music_length_ms is given with a prompt.
    private const int MinLengthMs = 3_000;
    private const int MaxLengthMs = 600_000;

    private readonly HttpClient _http;
    private readonly ILogger<ElevenLabsMusicClient> _log;

    public ElevenLabsMusicClient(HttpClient http, ILogger<ElevenLabsMusicClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(
                SupportedModelCatalog.ElevenLabsApiBase.TrimEnd(Path.AltDirectorySeparatorChar)
                + Path.AltDirectorySeparatorChar);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey() => ProviderApiKey.ResolveElevenLabs();

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
            _log.LogWarning("ElevenLabs Music: ElevenLabs_API_KEY missing — skipping audio generation.");
            onProgress?.Invoke("ElevenLabs music: API key not configured.");
            return null;
        }

        // Eleven Music auto-writes lyrics from the prompt when singing; fold any composed lyrics into
        // the prompt so the sung words match the scene. Instrumental scores set force_instrumental.
        var effectivePrompt = prompt?.Trim() ?? "";
        if (isVocal && !string.IsNullOrWhiteSpace(lyrics))
            effectivePrompt = $"{effectivePrompt}\n\nLyrics to sing:\n{lyrics.Trim()}";

        var lengthMs = Math.Clamp(durationSeconds * 1000, MinLengthMs, MaxLengthMs);
        var wireModel = ResolveWireModel(model);

        var payload = JsonSerializer.Serialize(new
        {
            prompt = effectivePrompt,
            music_length_ms = lengthMs,
            model_id = wireModel,
            force_instrumental = !isVocal,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "music");
        req.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        onProgress?.Invoke("Composing with ElevenLabs Music…");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogError("ElevenLabs Music failed HTTP {Status}: {Body}", (int)resp.StatusCode, Trunc(body));
            onProgress?.Invoke($"ElevenLabs music failed: HTTP {(int)resp.StatusCode} — {ShortReason((int)resp.StatusCode, body)}");
            return null;
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length < 512)
        {
            _log.LogError("ElevenLabs Music returned {Bytes} bytes — treating as empty.", bytes.Length);
            onProgress?.Invoke("ElevenLabs music returned an empty track.");
            return null;
        }

        _log.LogInformation("ElevenLabs Music ready: {Bytes} bytes ({Model}, {LenMs}ms).", bytes.Length, wireModel, lengthMs);
        // Self-contained URL the media proxy decodes — no bytes persisted on the API host.
        return "data:audio/mpeg;base64," + Convert.ToBase64String(bytes);
    }

    /// <summary>Catalog id → ElevenLabs wire model. Anything tagged "v1" pins music_v1; else the
    /// newer music_v2.</summary>
    private static string ResolveWireModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model) && model.Contains("v1", StringComparison.OrdinalIgnoreCase))
            return "music_v1";
        return "music_v2";
    }

    private static string ShortReason(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d))
            {
                string? msg;
                if (d.ValueKind == JsonValueKind.String)
                    msg = d.GetString();
                else if (d.TryGetProperty("message", out var m))
                    msg = m.GetString();
                else
                    msg = null;
                if (!string.IsNullOrWhiteSpace(msg)) return Trunc(msg, 160);
            }
        }
        catch { /* fall through */ }
        return status switch
        {
            401 => "unauthorized (check the ElevenLabs key)",
            402 => "payment required (ElevenLabs account has no credits/plan for Music)",
            403 => "forbidden (this key/plan lacks Music access)",
            422 => "invalid request",
            _ => Trunc(body, 160),
        };
    }

    private static string Trunc(string? s, int max = 240)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
