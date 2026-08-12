using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Fal.ai serverless GPU audio &amp; background music generation client (Stable Audio / MusicGen).
/// Direct endpoint: https://fal.run/fal-ai/stable-audio
/// </summary>
public sealed class FalAudioClient : IAudioClient
{
    public const string ApiBase = "https://fal.run/";

    /// <summary>fal-ai/stable-audio's real hard limit — see SupportedModelCatalog's
    /// fal-ai/stable-audio entry (MaxAudioDurationSeconds), the source of truth callers resolve
    /// against. Kept here too since the clamp below needs it regardless of what a caller passes.</summary>
    public const int MaxSegmentDurationSecondsConst = 47;

    private readonly HttpClient _http;
    private readonly ILogger<FalAudioClient> _log;

    public FalAudioClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ILogger<FalAudioClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey() => ProviderApiKey.ResolveFal();

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
            _log.LogWarning("Fal.ai API key is missing — skipping audio generation.");
            return null;
        }

        // None of Fal.ai's instrumental music models have vocal capability — this provider always
        // generates instrumental regardless of what the caller asked for (isVocal/lyrics are
        // ignored, not silently misreported: FilmJobService gates model selection so a vocal
        // request should never route here, but if it somehow does, generating instrumental beats
        // failing the whole take).
        if (isVocal)
            _log.LogWarning("Vocal generation requested but this Fal.ai model has no vocal capability — generating instrumental.");

        model = ProjectModelSelection.RequireExplicit(model, ModelCapability.Audio, "Fal audio generation");
        // Real fal-ai/stable-audio hard limit (not an arbitrary choice — see
        // https://github.com/Stability-AI/stable-audio-tools/issues/154). Callers that need
        // longer coverage generate MaxSegmentDurationSeconds-sized segments and concatenate
        // client-side, same as video's per-clip duration limits.
        durationSeconds = Math.Clamp(durationSeconds, 2, MaxSegmentDurationSecondsConst);

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["seconds_total"] = durationSeconds,
            ["seconds_start"] = 0,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, model.TrimStart('/'));
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
        req.Content = JsonContent.Create(payload);

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Fal.ai audio gen failed HTTP {Status} ({Elapsed}ms): {Body}", resp.StatusCode, sw.ElapsedMilliseconds, body);
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        string? audioUrl = null;

        // Parse standard Fal audio response shapes: audio_file.url, audio.url, or a bare url.
        if (doc.RootElement.TryGetProperty("audio_file", out var audioFileEl) && audioFileEl.TryGetProperty("url", out var urlEl1))
        {
            audioUrl = urlEl1.GetString();
        }
        else if (doc.RootElement.TryGetProperty("audio", out var audioEl) && audioEl.TryGetProperty("url", out var urlEl2))
        {
            audioUrl = urlEl2.GetString();
        }
        else if (doc.RootElement.TryGetProperty("url", out var urlEl3))
        {
            audioUrl = urlEl3.GetString();
        }

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            _log.LogError("Fal.ai returned no audio URL: {Body}", body);
            return null;
        }

        _log.LogInformation("Fal.ai audio generated successfully ({Elapsed}ms): {Url}", sw.ElapsedMilliseconds, audioUrl);
        // Return the provider URL directly — the caller proxies it to the client (same as
        // video); the server never downloads generated media bytes into its own memory/disk.
        return audioUrl;
    }
}