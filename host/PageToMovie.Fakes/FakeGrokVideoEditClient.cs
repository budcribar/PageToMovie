using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Fakes;

/// <summary>
/// Fake video-edit client: delay + real MP4 fixture, honoring the same <see cref="FakesOptions"/>
/// knobs as <see cref="FakeGrokVideoClient"/> (video generation's fake), since edit is submit+poll
/// shaped the same way — plus <see cref="FakesOptions.RejectFileIdEdits"/> to deterministically
/// exercise the real client's fallback-to-base64-upload path.
/// </summary>
public sealed class FakeGrokVideoEditClient : IVideoEditClient
{
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<FakeGrokVideoEditClient> _log;
    private readonly ProjectTelemetryService _telemetry;
    private int _submitCount;

    public FakeGrokVideoEditClient(
        IOptions<PageToMovieOptions> opts, ILogger<FakeGrokVideoEditClient> log, ProjectTelemetryService telemetry)
    {
        _opts = opts.Value;
        _log = log;
        _telemetry = telemetry;
    }

    public bool IsConfigured => true;

    public async Task<string> EditClipAsync(
        string videoPath,
        string prompt,
        string? sourceFileId = null,
        string? model = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var entry = SupportedModelCatalog.ResolveOrDefault(model, ModelCapability.VideoEdit);
        if (!entry.Enabled)
            throw new InvalidOperationException($"Fake video edit: model '{entry.Id}' is disabled in the catalog.");

        var fakes = _opts.Fakes ?? new FakesOptions();
        var mode = "upload";

        if (!string.IsNullOrWhiteSpace(sourceFileId))
        {
            if (fakes.RejectFileIdEdits)
            {
                _log.LogInformation(
                    "Fake video edit: file_id {FileId} rejected (RejectFileIdEdits) — falling back to upload",
                    sourceFileId);
                onProgress?.Invoke("stored file unavailable — uploading clip instead");
            }
            else
            {
                mode = "file_id";
            }
        }

        var n = Interlocked.Increment(ref _submitCount);
        if (fakes.RateLimitEveryN > 0 && n % fakes.RateLimitEveryN == 0)
            throw new InvalidOperationException("Fake 429: rate limit (RateLimitEveryN)");

        await Task.Delay(Math.Max(0, fakes.VideoDelayMs), ct);

        if (fakes.FailRate > 0 && Random.Shared.NextDouble() < fakes.FailRate)
            throw new InvalidOperationException("Fake video edit failed (FailRate)");

        onProgress?.Invoke("status=pending (fake)");
        await Task.Delay(Math.Max(50, fakes.VideoDelayMs / 4), ct);
        onProgress?.Invoke("status=done (fake)");

        // Edit output always inherits the source clip's duration — a short fixture (~5s) is a
        // reasonable stand-in regardless of the real source clip's actual length.
        var fixture = FakeGrokVideoClient.ResolveFixturePath(fakes.VideoMode, 5);

        try
        {
            await _telemetry.LogApiCallAsync(new ApiCallTelemetry
            {
                Kind = "video_edit",
                Model = entry.Id,
                PromptChars = prompt?.Length ?? 0,
                Mode = mode,
                Fakes = true,
                Ok = true,
            }, ct).ConfigureAwait(false);
        }
        catch { /* telemetry is best-effort */ }

        _log.LogInformation(
            "Fake video edit mode={Mode} fixture={Fixture} promptLen={Len}",
            mode, Path.GetFileName(fixture), prompt?.Length ?? 0);

        // "fixture:" (not FakeGrokVideoClient's own "fake-fixture:") — the prefix
        // /api/media/proxy/{token} actually recognizes.
        return "fixture:" + fixture;
    }

    public async Task DownloadToFileAsync(string url, string destPath, CancellationToken ct)
    {
        await Task.Yield();
        var fixture = url.StartsWith("fixture:", StringComparison.OrdinalIgnoreCase)
            ? url["fixture:".Length..]
            : FakeGrokVideoClient.ResolveFixturePath(_opts.Fakes?.VideoMode, 5);

        if (!File.Exists(fixture))
            throw new FileNotFoundException(
                "Fake video edit fixture missing. Expected a real MP4 under PageToMovie.Fakes/Fixtures/.", fixture);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
        File.Copy(fixture, destPath, overwrite: true);
        _log.LogInformation("Fake video edit download {Bytes} bytes → {Path}", new FileInfo(destPath).Length, destPath);
    }
}
