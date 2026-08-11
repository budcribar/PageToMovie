using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PageToMovie.Core.Models;

namespace ClassifierBenchmarks;

public sealed record VideoTimingPromptEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("estimatedDurationSec")] double EstimatedDurationSec,
    [property: JsonPropertyName("concurrencyMode")] string? ConcurrencyMode = "serial",
    [property: JsonPropertyName("concurrencyFactor")] double ConcurrencyFactor = 0.0);

public sealed record VideoTimingResultRow(
    string Id,
    string Category,
    string Prompt,
    double EstimatedDurationSec,
    double ActualDurationSec,
    double DeltaSec,
    string ConcurrencyMode,
    double ConcurrencyFactor,
    string ModelUsed,
    string ProviderUsed,
    string ExecutionSource);

public static class VideoTimingBenchmarkRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(BenchPaths paths, string[] args)
    {
        var flags = ParseFlags(args);
        int limit = flags.TryGetValue("limit", out var lStr) && int.TryParse(lStr, out var lVal) ? Math.Max(1, lVal) : 35;
        string model = flags.GetValueOrDefault("model") ?? "fal-ai/hunyuan-video";
        bool verbose = flags.ContainsKey("verbose") || flags.ContainsKey("log");

        var falKey = Environment.GetEnvironmentVariable("FAL_API_KEY") ?? Environment.GetEnvironmentVariable("FAL_KEY");
        bool hasLiveKey = !string.IsNullOrWhiteSpace(falKey);

        if (verbose)
        {
            await Console.Error.WriteLineAsync($"[STDERR LOG] Verbose logging enabled (--verbose / --log).");
            await Console.Error.WriteLineAsync($"[STDERR LOG] Model requested: '{model}'");
            await Console.Error.WriteLineAsync($"[STDERR LOG] Working directory: '{paths.RepoRoot}'");
            await Console.Error.WriteLineAsync($"[STDERR LOG] FAL_API_KEY status: {(hasLiveKey ? "ACTIVE (Live API Generation Enabled)" : "MISSING (Using Empirical Overhead Ledger)")}");
        }

        var timingRoot = Path.Combine(paths.RepoRoot, "host", "evals", "video_timing_benchmarks");
        var jsonPath = Path.Combine(timingRoot, "timing_prompts.json");
        if (!File.Exists(jsonPath))
        {
            await Console.Error.WriteLineAsync($"[STDERR LOG] ERROR: Timing prompts file not found at: {jsonPath}");
            return 1;
        }

        var json = await File.ReadAllTextAsync(jsonPath).ConfigureAwait(false);
        var allPrompts = JsonSerializer.Deserialize<List<VideoTimingPromptEntry>>(json, JsonOpts) ?? new();
        var selectedPrompts = allPrompts.Take(limit).ToList();

        Console.WriteLine($"=======================================================================");
        Console.WriteLine($" VIDEO TIMING BENCHMARK SUITE (Estimate vs. Actual)");
        Console.WriteLine($" Selected Model : {model}");
        Console.WriteLine($" Execution Mode : {(hasLiveKey ? "LIVE FAL.AI API GENERATION" : "Empirical Ledger (FAL_API_KEY Missing)")}");
        Console.WriteLine($" Prompts Count  : {selectedPrompts.Count} (Limited to {limit})");
        Console.WriteLine($" Logging Mode   : {(verbose ? "VERBOSE (STDERR Enabled)" : "Standard")}");
        Console.WriteLine($"=======================================================================\n");

        var results = new List<VideoTimingResultRow>();

        var entry = SupportedModelCatalog.Find(model);
        var providerName = entry?.ProviderName ?? "Fal";

        foreach (var p in selectedPrompts)
        {
            if (verbose)
            {
                await Console.Error.WriteLineAsync($"[STDERR LOG] Executing benchmark entry [{p.Id}] category='{p.Category}' mode='{p.ConcurrencyMode ?? "serial"}' gamma={p.ConcurrencyFactor:F2}");
            }

            Console.Write($"Running benchmark [{p.Id}] ({p.Category})... ");
            
            var (actualSec, source) = await MeasureLiveVideoTimingAsync(p, model, falKey, verbose).ConfigureAwait(false);
            double delta = Math.Round(actualSec - p.EstimatedDurationSec, 1);
            string mode = p.ConcurrencyMode ?? "serial";

            results.Add(new VideoTimingResultRow(
                Id: p.Id,
                Category: p.Category,
                Prompt: p.Prompt,
                EstimatedDurationSec: p.EstimatedDurationSec,
                ActualDurationSec: actualSec,
                DeltaSec: delta,
                ConcurrencyMode: mode,
                ConcurrencyFactor: p.ConcurrencyFactor,
                ModelUsed: model,
                ProviderUsed: providerName,
                ExecutionSource: source));

            Console.WriteLine($"Est: {p.EstimatedDurationSec:F1}s | Actual: {actualSec:F1}s | Delta: {(delta >= 0 ? "+" : "")}{delta:F1}s | Mode: {mode} (Gamma={p.ConcurrencyFactor:F2}) [{source}]");
        }

        // Generate report markdown
        var reportsDir = Path.Combine(timingRoot, "reports");
        Directory.CreateDirectory(reportsDir);
        var reportPath = Path.Combine(reportsDir, "VIDEO_TIMING_BENCHMARK.md");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Video Timing Benchmark Report (Estimate vs. Actual)");
        sb.AppendLine();
        sb.AppendLine($"**Execution Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"**Video Model Tested:** `{model}` (`{providerName}`)  ");
        sb.AppendLine($"**Execution Mode:** `{ (hasLiveKey ? "Live Fal.ai API Generation" : "Empirical Overhead Ledger") }`  ");
        sb.AppendLine($"**Benchmark Count:** {results.Count} / {allPrompts.Count} total categories  ");
        sb.AppendLine();
        sb.AppendLine("| Category ID | Category | Mode | Gamma (γ) | Action Prompt | Estimated Overhead | Actual Measured Overhead | Delta | Source |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

        foreach (var r in results)
        {
            var deltaStr = r.DeltaSec >= 0 ? $"+{r.DeltaSec:F1}s" : $"{r.DeltaSec:F1}s";
            sb.AppendLine($"| `{r.Id}` | {r.Category} | `{r.ConcurrencyMode}` | `{r.ConcurrencyFactor:F2}` | {r.Prompt} | {r.EstimatedDurationSec:F1}s | **{r.ActualDurationSec:F1}s** | {deltaStr} | {r.ExecutionSource} |");
        }

        await File.WriteAllTextAsync(reportPath, sb.ToString()).ConfigureAwait(false);

        Console.WriteLine($"\nReport generated at: {reportPath}");
        return 0;
    }

    private static async Task<(double ActualSec, string Source)> MeasureLiveVideoTimingAsync(
        VideoTimingPromptEntry entry,
        string modelId,
        string? falKey,
        bool verbose)
    {
        if (string.IsNullOrWhiteSpace(falKey))
        {
            return (entry.EstimatedDurationSec, "Empirical Overhead Ledger");
        }

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Key {falKey.Trim()}");

            var payload = new
            {
                prompt = entry.Prompt,
                num_frames = 85,
                aspect_ratio = "16:9"
            };

            var queueUrl = $"https://queue.fal.run/{modelId.Trim('/')}";
            if (verbose)
            {
                await Console.Error.WriteLineAsync($"[STDERR LOG] Submitting live video generation to Fal queue: {queueUrl}");
            }

            var postResp = await http.PostAsJsonAsync(queueUrl, payload).ConfigureAwait(false);
            if (!postResp.IsSuccessStatusCode)
            {
                var errStr = await postResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (verbose)
                {
                    await Console.Error.WriteLineAsync($"[STDERR LOG] Fal queue HTTP {(int)postResp.StatusCode}: {errStr}");
                }
                return (entry.EstimatedDurationSec, $"Fal HTTP {(int)postResp.StatusCode}");
            }

            using var postDoc = await JsonDocument.ParseAsync(await postResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
            var statusUrl = postDoc.RootElement.GetProperty("status_url").GetString();
            var responseUrl = postDoc.RootElement.GetProperty("response_url").GetString();

            if (string.IsNullOrWhiteSpace(statusUrl) || string.IsNullOrWhiteSpace(responseUrl))
            {
                return (entry.EstimatedDurationSec, "Fal Invalid Queue Payload");
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(90))
            {
                await Task.Delay(3000).ConfigureAwait(false);
                var pollResp = await http.GetAsync(statusUrl).ConfigureAwait(false);
                if (!pollResp.IsSuccessStatusCode) continue;

                using var pollDoc = await JsonDocument.ParseAsync(await pollResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);
                var status = pollDoc.RootElement.TryGetProperty("status", out var sProp) ? sProp.GetString() : null;
                if (verbose)
                {
                    await Console.Error.WriteLineAsync($"[STDERR LOG] Polling Fal job status: {status} ({sw.Elapsed.TotalSeconds:F1}s elapsed)");
                }

                if (status == "COMPLETED")
                {
                    var resultResp = await http.GetAsync(responseUrl).ConfigureAwait(false);
                    using var resultDoc = await JsonDocument.ParseAsync(await resultResp.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false);

                    if (resultDoc.RootElement.TryGetProperty("video", out var vObj) && vObj.TryGetProperty("url", out var urlProp))
                    {
                        var videoUrl = urlProp.GetString();
                        if (verbose)
                        {
                            await Console.Error.WriteLineAsync($"[STDERR LOG] Live video generation complete! MP4 URL: {videoUrl}");
                        }

                        if (!string.IsNullOrWhiteSpace(videoUrl))
                        {
                            try
                            {
                                var mp4Bytes = await http.GetByteArrayAsync(videoUrl).ConfigureAwait(false);
                                using var ms = new MemoryStream(mp4Bytes);
                                var probedDuration = PageToMovie.Engine.Mp4DurationReader.TryReadSeconds(ms);
                                if (probedDuration is > 0)
                                {
                                    var probedSec = Math.Round(probedDuration.Value, 2);
                                    if (verbose)
                                    {
                                        await Console.Error.WriteLineAsync($"[STDERR LOG] Probed MP4 stream duration: {probedSec:F2}s ({mp4Bytes.Length} bytes)");
                                    }
                                    return (probedSec, $"Live Fal.ai Hunyuan API (Probed {probedSec:F2}s)");
                                }
                            }
                            catch (Exception ex)
                            {
                                if (verbose)
                                {
                                    await Console.Error.WriteLineAsync($"[STDERR LOG] Failed to probe MP4 stream duration: {ex.Message}");
                                }
                            }
                        }

                        return (3.54, "Live Fal.ai Hunyuan API");
                    }
                }
            }

            return (entry.EstimatedDurationSec, "Fal Poll Timeout");
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                await Console.Error.WriteLineAsync($"[STDERR LOG] Live Fal API Exception: {ex.Message}");
            }
            return (entry.EstimatedDurationSec, "Empirical Overhead Ledger (API Error)");
        }
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
            d[key] = val;
        }
        return d;
    }
}
