using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PageToMovie.Engine;

/// <summary>
/// Studio cut EDL + hash for one stitched WIP (<c>assets/movie_wip.film.json</c>).
/// Media bytes stay on the client; this JSON is project-git text provenance.
/// </summary>
public sealed class FilmBuildDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = FilmBuildService.SchemaVersion;

    [JsonPropertyName("film_id")]
    public string FilmId { get; set; } = "";

    [JsonPropertyName("created_at_utc")]
    public string CreatedAtUtc { get; set; } = "";

    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("studio")]
    public FilmBuildStudio Studio { get; set; } = new();

    [JsonPropertyName("timeline")]
    public FilmBuildTimeline Timeline { get; set; } = new();

    [JsonPropertyName("assembly")]
    public FilmBuildAssembly Assembly { get; set; } = new();

    [JsonPropertyName("provenance")]
    public FilmBuildProvenance Provenance { get; set; } = new();

    [JsonPropertyName("publish")]
    public FilmBuildPublish? Publish { get; set; }
}

/// <summary>Upload-time hash gate (Clipchamp / external edit detection).</summary>
public sealed class FilmBuildPublish
{
    public const string PathStudioIntact = "studio_intact";
    public const string PathExternalSameLength = "external_same_length";
    public const string PathExternalRestructured = "external_restructured";
    public const string PathUnknown = "unknown";

    /// <summary>studio_intact | external_same_length | external_restructured | unknown</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = PathUnknown;

    [JsonPropertyName("upload_sha256")]
    public string UploadSha256 { get; set; } = "";

    [JsonPropertyName("upload_duration_seconds")]
    public double? UploadDurationSeconds { get; set; }

    [JsonPropertyName("upload_byte_length")]
    public long? UploadByteLength { get; set; }

    [JsonPropertyName("studio_sha256")]
    public string? StudioSha256 { get; set; }

    [JsonPropertyName("studio_duration_seconds")]
    public double? StudioDurationSeconds { get; set; }

    [JsonPropertyName("duration_delta_seconds")]
    public double? DurationDeltaSeconds { get; set; }

    [JsonPropertyName("youtube_video_id")]
    public string? YoutubeVideoId { get; set; }

    [JsonPropertyName("youtube_url")]
    public string? YoutubeUrl { get; set; }

    [JsonPropertyName("recorded_at_utc")]
    public string RecordedAtUtc { get; set; } = "";

    /// <summary>Duration tolerance (seconds) for same-length external edit.</summary>
    public const double DurationEpsilonSeconds = 0.5;
}

public sealed class FilmBuildStudio
{
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "assets/movie_wip.mp4";

    [JsonPropertyName("byte_length")]
    public long? ByteLength { get; set; }
}

public sealed class FilmBuildTimeline
{
    [JsonPropertyName("total_seconds")]
    public double TotalSeconds { get; set; }

    [JsonPropertyName("segments")]
    public List<FilmBuildSegment> Segments { get; set; } = new();
}

public sealed class FilmBuildSegment
{
    [JsonPropertyName("i")]
    public int Index { get; set; }

    [JsonPropertyName("scene")]
    public int? Scene { get; set; }

    [JsonPropertyName("clip")]
    public int? Clip { get; set; }

    [JsonPropertyName("take")]
    public int? Take { get; set; }

    [JsonPropertyName("t_start")]
    public double TStart { get; set; }

    [JsonPropertyName("t_end")]
    public double TEnd { get; set; }

    [JsonPropertyName("src")]
    public string Src { get; set; } = "";

    [JsonPropertyName("src_sha256")]
    public string? SrcSha256 { get; set; }

    [JsonPropertyName("sidecar")]
    public string? Sidecar { get; set; }
}

public sealed class FilmBuildAssembly
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "ffmpeg";

    [JsonPropertyName("where")]
    public string Where { get; set; } = "client";
}

public sealed class FilmBuildProvenance
{
    [JsonPropertyName("app_repo")]
    public string AppRepo { get; set; } = "budcribar/PageToMovie";

    [JsonPropertyName("adaptation_version")]
    public string? AdaptationVersion { get; set; }

    [JsonPropertyName("prompt_content_sha256")]
    public string? PromptContentSha256 { get; set; }

    [JsonPropertyName("runtime_mode")]
    public string? RuntimeMode { get; set; }

    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    [JsonPropertyName("stage1_manifest")]
    public string Stage1ManifestPath { get; set; } = ProjectStage1ConvertManifest.RelativePath;
}

/// <summary>Create / persist / load film builds.</summary>
public static class FilmBuildService
{
    public const string SchemaVersion = "film_build.v1";
    public const string RelativePath = "assets/movie_wip.film.json";

    public static string GetPath(string projectDir) =>
        Path.Combine(projectDir, "assets", "movie_wip.film.json");

    public static string NewFilmId(string projectId)
    {
        var slug = (projectId ?? "project").Replace('/', '_').Replace('\\', '_');
        if (slug.Length > 40) slug = slug[..40];
        var shortId = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"film_{slug}_{DateTime.UtcNow:yyyyMMddHHmmss}_{shortId}";
    }

    public static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static FilmBuildDocument Create(
        string projectId,
        string studioSha256,
        double durationSeconds,
        IReadOnlyList<FilmBuildSegment>? segments = null,
        long? byteLength = null,
        string assemblyWhere = "client",
        string studioPath = "assets/movie_wip.mp4")
    {
        var segs = segments?.ToList() ?? new List<FilmBuildSegment>();
        var total = durationSeconds;
        if (total <= 0 && segs.Count > 0)
            total = segs.Max(s => s.TEnd);

        var doc = new FilmBuildDocument
        {
            FilmId = NewFilmId(projectId),
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            ProjectId = projectId,
            Studio = new FilmBuildStudio
            {
                Sha256 = studioSha256 ?? "",
                DurationSeconds = total,
                Path = studioPath,
                ByteLength = byteLength,
            },
            Timeline = new FilmBuildTimeline
            {
                TotalSeconds = total,
                Segments = segs,
            },
            Assembly = new FilmBuildAssembly
            {
                Tool = "ffmpeg",
                Where = assemblyWhere,
            },
        };
        return doc;
    }

    /// <summary>Attach Stage‑1 pins from convert manifest when present.</summary>
    public static void AttachStage1Provenance(string projectDir, FilmBuildDocument doc)
    {
        var m = ProjectStage1ConvertManifest.TryRead(projectDir);
        if (m is null) return;
        doc.Provenance.AdaptationVersion = m.AdaptationVersion;
        doc.Provenance.PromptContentSha256 = m.PromptContentSha256;
        doc.Provenance.RuntimeMode = m.RuntimeMode;
        doc.Provenance.ModelId = m.ModelId;
    }

    public static async Task WriteAsync(string projectDir, FilmBuildDocument doc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var path = GetPath(projectDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(doc, JsonDefaults.Indented);
        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
    }

    public static async Task<FilmBuildDocument?> TryReadAsync(string projectDir, CancellationToken ct = default)
    {
        var path = GetPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FilmBuildDocument>(
                text,
                JsonDefaults.IndentedCaseInsensitive);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Register a studio cut: write film build, auto-commit trajectory.
    /// </summary>
    public static async Task<FilmBuildDocument> RegisterAsync(
        ProjectStore store,
        string projectId,
        string studioSha256,
        double durationSeconds,
        IReadOnlyList<FilmBuildSegment>? segments = null,
        long? byteLength = null,
        string assemblyWhere = "client",
        CancellationToken ct = default)
    {
        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var doc = Create(projectId, studioSha256, durationSeconds, segments, byteLength, assemblyWhere);
        AttachStage1Provenance(projectDir, doc);
        await WriteAsync(projectDir, doc, ct).ConfigureAwait(false);
        try
        {
            store.TriggerAutoGitCommit(projectId, ProjectStageCommits.FilmStitched(doc.FilmId));
        }
        catch
        {
            /* non-fatal */
        }
        return doc;
    }

    /// <summary>Hash on-disk WIP bytes and register a minimal film build (no timeline).</summary>
    public static async Task<FilmBuildDocument?> RegisterFromWipFileAsync(
        ProjectStore store,
        string projectId,
        string? wipRelativePath = null,
        CancellationToken ct = default)
    {
        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var rel = string.IsNullOrWhiteSpace(wipRelativePath) ? "assets/movie_wip.mp4" : wipRelativePath!;
        var full = Path.Combine(projectDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return null;
        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        if (bytes.Length == 0) return null;
        return await RegisterAsync(
            store,
            projectId,
            HashBytes(bytes),
            durationSeconds: 0,
            segments: null,
            byteLength: bytes.Length,
            assemblyWhere: "server",
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hash-gate the exact bytes about to upload vs <see cref="FilmBuildStudio.Sha256"/>.
    /// Writes/updates <c>film_build.publish</c> and returns the path classification.
    /// </summary>
    public static async Task<FilmBuildPublish> ApplyUploadHashGateAsync(
        ProjectStore store,
        string projectId,
        byte[] uploadBytes,
        double? uploadDurationSeconds = null,
        string? youtubeVideoId = null,
        string? youtubeUrl = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uploadBytes);
        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var uploadSha = HashBytes(uploadBytes);
        var doc = await TryReadAsync(projectDir, ct).ConfigureAwait(false);
        if (doc is null)
        {
            // No prior stitch record — create minimal film_build from upload bytes alone.
            doc = Create(
                projectId,
                studioSha256: uploadSha,
                durationSeconds: uploadDurationSeconds ?? 0,
                segments: null,
                byteLength: uploadBytes.Length,
                assemblyWhere: "upload");
            AttachStage1Provenance(projectDir, doc);
        }

        var studioSha = (doc.Studio.Sha256 ?? "").Trim().ToLowerInvariant();
        var path = ClassifyPublishPath(
            studioSha,
            uploadSha,
            doc.Studio.DurationSeconds,
            uploadDurationSeconds);

        var publish = new FilmBuildPublish
        {
            Path = path,
            UploadSha256 = uploadSha,
            UploadDurationSeconds = uploadDurationSeconds,
            UploadByteLength = uploadBytes.Length,
            StudioSha256 = string.IsNullOrWhiteSpace(studioSha) ? null : studioSha,
            StudioDurationSeconds = doc.Studio.DurationSeconds > 0 ? doc.Studio.DurationSeconds : null,
            DurationDeltaSeconds = uploadDurationSeconds is > 0 && doc.Studio.DurationSeconds > 0
                ? Math.Abs(uploadDurationSeconds.Value - doc.Studio.DurationSeconds)
                : null,
            YoutubeVideoId = youtubeVideoId,
            YoutubeUrl = youtubeUrl,
            RecordedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        doc.Publish = publish;
        await WriteAsync(projectDir, doc, ct).ConfigureAwait(false);

        try
        {
            store.TriggerAutoGitCommit(
                projectId,
                $"ptm:stage=film_published path={path}" +
                (string.IsNullOrWhiteSpace(youtubeVideoId) ? "" : $" youtube={youtubeVideoId}"));
        }
        catch { /* non-fatal */ }

        return publish;
    }

    public static string ClassifyPublishPath(
        string? studioSha256,
        string uploadSha256,
        double studioDurationSeconds,
        double? uploadDurationSeconds)
    {
        var studio = (studioSha256 ?? "").Trim().ToLowerInvariant();
        var upload = (uploadSha256 ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(upload))
            return FilmBuildPublish.PathUnknown;
        if (!string.IsNullOrWhiteSpace(studio) &&
            string.Equals(studio, upload, StringComparison.OrdinalIgnoreCase))
            return FilmBuildPublish.PathStudioIntact;

        if (uploadDurationSeconds is > 0 && studioDurationSeconds > 0)
        {
            var delta = Math.Abs(uploadDurationSeconds.Value - studioDurationSeconds);
            if (delta <= FilmBuildPublish.DurationEpsilonSeconds)
                return FilmBuildPublish.PathExternalSameLength;
            return FilmBuildPublish.PathExternalRestructured;
        }

        // Hash differs, no reliable duration → treat as restructured (conservative).
        if (!string.IsNullOrWhiteSpace(studio))
            return FilmBuildPublish.PathExternalRestructured;
        return FilmBuildPublish.PathUnknown;
    }
}
