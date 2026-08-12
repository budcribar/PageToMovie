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
/// Fal.ai serverless GPU image generation client (Flux.1 Dev / Schnell).
/// Direct endpoint: https://fal.run/fal-ai/flux/dev
/// </summary>
public sealed class FalImageClient : IImageClient
{
    public const string ApiBase = "https://fal.run/";

    private readonly HttpClient _http;
    private readonly ILogger<FalImageClient> _log;

    public FalImageClient(
        HttpClient http,
        IOptions<PageToMovieOptions> opts,
        ILogger<FalImageClient> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(ApiBase);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private static string? ResolveApiKey() => ProviderApiKey.ResolveFal();

    public async Task<IReadOnlyList<byte[]>> GenerateVariantsAsync(
        string prompt,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey()
            ?? throw new InvalidOperationException($"Fal.ai API key is missing. Set {SupportedModelCatalog.FalApiKeyEnv} in environment or Configuration.");

        model = ProjectModelSelection.RequireExplicit(model, ModelCapability.Image, "Fal image generation");

        var imgSize = aspectRatio switch
        {
            "16:9" => "landscape_16_9",
            "9:16" => "portrait_16_9",
            "4:3" => "landscape_4_3",
            "3:4" => "portrait_4_3",
            _ => "square_hd",
        };

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["image_size"] = imgSize,
            ["num_images"] = Math.Clamp(n, 1, 4),
            ["enable_safety_checker"] = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, model.TrimStart('/'));
        req.Headers.Authorization = new AuthenticationHeaderValue("Key", apiKey);
        req.Content = JsonContent.Create(payload);

        var sw = Stopwatch.StartNew();
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("Fal.ai Flux image gen failed HTTP {Status} ({Elapsed}ms): {Body}", resp.StatusCode, sw.ElapsedMilliseconds, body);
            throw new InvalidOperationException($"Fal.ai error {resp.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var results = new List<byte[]>();

        if (doc.RootElement.TryGetProperty("images", out var imagesArr) && imagesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var imgEl in imagesArr.EnumerateArray())
            {
                if (imgEl.TryGetProperty("url", out var urlEl) && urlEl.GetString() is { Length: > 0 } url)
                {
                    var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                    results.Add(bytes);
                }
            }
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException($"Fal.ai returned zero images: {body}");
        }

        return results;
    }

    public Task<IReadOnlyList<byte[]>> EditVariantsAsync(
        string prompt,
        IReadOnlyList<string> referenceImagePaths,
        int n = 3,
        string aspectRatio = "1:1",
        string? model = null,
        int maxRefs = 0,
        string? costumeRefPath = null,
        bool illustratedMedium = true,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        return GenerateVariantsAsync(prompt, n, aspectRatio, model, ct);
    }
}
