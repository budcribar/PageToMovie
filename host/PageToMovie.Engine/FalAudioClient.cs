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

        using var posted = await FalHttp.TryPostJsonAsync(
            _http, _log, model.TrimStart('/'), apiKey, payload, "audio gen", ct).ConfigureAwait(false);
        if (posted is null) return null;

        // Parse standard Fal audio response shapes: audio_file.url, audio.url, or a bare url.
        var audioUrl = FalHttp.TryGetObjectUrl(posted.Root, "audio_file")
            ?? FalHttp.TryGetObjectUrl(posted.Root, "audio")
            ?? (posted.Root.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null);

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            _log.LogError("Fal.ai returned no audio URL: {Body}", posted.Body);
            return null;
        }

        _log.LogInformation("Fal.ai audio generated successfully ({Elapsed}ms): {Url}", posted.ElapsedMs, audioUrl);
        // Return the provider URL directly — the caller proxies it to the client (same as
        // video); the server never downloads generated media bytes into its own memory/disk.
        return audioUrl;
    }
}