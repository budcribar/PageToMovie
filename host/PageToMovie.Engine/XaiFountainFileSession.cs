using System.Text;
using PageToMovie.Core.Abstractions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine;

/// <summary>
/// xAI Files + Responses session for a generated Fountain draft.
/// Persists <c>file_id</c> on <see cref="ProjectXaiArtifactFiles"/> keyed by SHA.
/// </summary>
public sealed class XaiFountainFileSession : IFountainFileSession
{
    private readonly XaiResponsesClient _client;
    private readonly string _projectDir;
    private readonly Action<string>? _onProgress;
    private readonly string _kind = ProjectXaiArtifactFiles.KindScreenplayStitch;

    public XaiFountainFileSession(
        XaiResponsesClient client,
        string projectDir,
        Action<string>? onProgress = null)
    {
        _client = client;
        _projectDir = projectDir;
        _onProgress = onProgress;
    }

    public bool IsAvailable =>
        XaiResponsesClient.IsConfigured && !string.IsNullOrWhiteSpace(_projectDir);

    public string? FileId { get; private set; }

    public async Task EnsureUploadedAsync(string fountainText, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Fountain file session is not available.");
        if (string.IsNullOrWhiteSpace(fountainText))
            throw new InvalidOperationException("Fountain text is empty.");

        var sha = ProjectXaiArtifactFiles.Sha256Hex(fountainText);
        if (ProjectXaiArtifactFiles.TryGetReusable(_projectDir, _kind, sha, out var hit) && hit is not null)
        {
            FileId = hit.FileId;
            _onProgress?.Invoke($"Reusing {_kind} file_id={hit.FileId} (same SHA, no re-upload).");
            return;
        }

        _onProgress?.Invoke("Uploading screenplay to xAI Files…");
        var bytes = Encoding.UTF8.GetBytes(fountainText);
        var upload = await _client.UploadBookBytesAsync(bytes, "screenplay.stitch.fountain", ct: ct)
            .ConfigureAwait(false);
        FileId = upload.FileId;
        ProjectXaiArtifactFiles.Upsert(_projectDir, new ProjectXaiArtifactFiles.Entry
        {
            Kind = _kind,
            Sha256 = sha,
            FileId = upload.FileId,
            ExpiresAtUnix = upload.ExpiresAtUnixSeconds,
            Bytes = bytes.Length,
            Filename = "screenplay.stitch.fountain",
        });
        _onProgress?.Invoke($"Saved {_kind} file_id={upload.FileId}.");
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string instructionWithoutFountainBody,
        string model,
        double temperature = 0.2,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(FileId))
            throw new InvalidOperationException("xAI fountain file_id missing — call EnsureUploadedAsync first.");

        var result = await _client.CompleteWithFilesAndSystemAsync(
            model,
            new[] { FileId },
            systemPrompt,
            instructionWithoutFountainBody,
            ct,
            temperature).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.UsageJson))
            _onProgress?.Invoke("xAI usage: " + result.UsageJson);
        return result.OutputText;
    }
}

public sealed class FountainFileSessionFactory : IFountainFileSessionFactory
{
    private readonly XaiResponsesClient _xai;
    private readonly PageToMovieOptions _opts;

    public FountainFileSessionFactory(XaiResponsesClient xai, IOptions<PageToMovieOptions> opts)
    {
        _xai = xai;
        _opts = opts.Value;
    }

    public IFountainFileSession? TryCreate(string projectDir, string modelId)
    {
        if (_opts.UseFakes) return null;
        if (string.IsNullOrWhiteSpace(projectDir)) return null;
        if (!XaiResponsesClient.IsConfigured) return null;

        var entry = SupportedModelCatalog.Find(modelId);
        var id = (modelId ?? "").Trim().ToLowerInvariant();
        var looksXai =
            entry?.Provider == ModelProviderFamily.Xai ||
            id.Contains("grok", StringComparison.Ordinal) ||
            (entry?.ApiBase?.Contains("api.x.ai", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!looksXai) return null;

        return new XaiFountainFileSession(_xai, projectDir);
    }
}
