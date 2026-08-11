using PageToMovie.Core.Models;
using System.Text.Json;
using PageToMovie.Adaptation.Contracts;

namespace PageToMovie.Engine;

/// <summary>
/// Builds a learning package from a project after publish (or on demand).
/// Primary write: <c>artifacts/learning_packages/{id}/</c> under the project (git-friendly text).
/// Optional lab mirror under workspace <c>evals/learning_packages/</c> when configured.
/// </summary>
public static class LearningPackageService
{
    public const string SchemaVersion = "learning_package.v1";

    public static string PackagesRoot(string projectDir) =>
        Path.Combine(projectDir, "artifacts", "learning_packages");

    public static async Task<LearningPackageResult> CreateFromProjectAsync(
        ProjectStore store,
        string projectId,
        string? workspaceRoot = null,
        string? outcome = null,
        IReadOnlyList<string>? failureTags = null,
        CancellationToken ct = default)
    {
        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var film = await FilmBuildService.TryReadAsync(projectDir, ct).ConfigureAwait(false);
        var stage1 = await ProjectStage1ConvertManifest.TryReadAsync(projectDir, ct).ConfigureAwait(false);
        var report = await ProjectAdaptationReport.TryReadAsync(projectDir, ct).ConfigureAwait(false);
        var yt = await TryReadYoutubeAsync(projectDir, ct).ConfigureAwait(false);

        var packageId = "lp_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "_" +
                        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))
                            .ToLowerInvariant();

        var publishPath = film?.Publish?.Path ?? FilmBuildPublish.PathUnknown;
        var tags = new List<string>(failureTags ?? Array.Empty<string>());
        if (string.Equals(publishPath, FilmBuildPublish.PathExternalSameLength, StringComparison.Ordinal))
            tags.Add("external_edit_same_length");
        if (string.Equals(publishPath, FilmBuildPublish.PathExternalRestructured, StringComparison.Ordinal))
            tags.Add("external_edit_restructured");
        if (stage1?.UsedHeuristicFallback == true)
            tags.Add("stage1_heuristic_fallback");
        if (report?.Issues.Count > 0)
            tags.Add("adaptation_report_issues");

        var package = new
        {
            schema_version = SchemaVersion,
            package_id = packageId,
            created_at_utc = DateTime.UtcNow.ToString("o"),
            project_id = projectId,
            outcome = outcome ?? (film?.Publish is null ? "pre_publish" : "published"),
            film_id = film?.FilmId,
            publish_path = publishPath,
            youtube_video_id = film?.Publish?.YoutubeVideoId ?? yt?.VideoId,
            youtube_url = film?.Publish?.YoutubeUrl ?? yt?.Url,
            studio_sha256 = film?.Studio.Sha256,
            upload_sha256 = film?.Publish?.UploadSha256,
            failure_tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
            stage1 = stage1 is null ? null : new
            {
                adaptation_version = stage1.AdaptationVersion,
                prompt_content_sha256 = stage1.PromptContentSha256,
                runtime_mode = stage1.RuntimeMode,
                model_id = stage1.ModelId,
                natural_runtime_minutes = stage1.NaturalRuntimeMinutes,
                target_runtime_minutes = stage1.TargetRuntimeMinutes,
                vision_meta_status = stage1.VisionMetaStatus,
                adaptation_report_status = stage1.AdaptationReportStatus,
            },
            adaptation_report_summary = report is null ? null : new
            {
                source_complete = report.SourceComplete,
                metrics = report.Metrics,
                issue_count = report.Issues.Count,
                issue_types = report.Issues.Select(i => i.Type).Distinct().ToList(),
            },
            film_timeline_segments = film?.Timeline.Segments.Count ?? 0,
            paths = new
            {
                film_build = FilmBuildService.RelativePath,
                stage1_manifest = ProjectStage1ConvertManifest.RelativePath,
                adaptation_report = ProjectAdaptationReport.RelativePath,
            },
        };

        var dir = Path.Combine(PackagesRoot(projectDir), packageId);
        Directory.CreateDirectory(dir);
        var packagePath = Path.Combine(dir, "package.json");
        await File.WriteAllTextAsync(packagePath, JsonSerializer.Serialize(package, JsonDefaults.Indented) + "\n", ct).ConfigureAwait(false);

        // Trajectory: lightweight stage markers we can infer from artifacts on disk
        var trajectoryPath = Path.Combine(dir, "trajectory.jsonl");
        using (var tw = new StreamWriter(trajectoryPath))
        {
            void Line(string op, object detail) =>
                tw.WriteLine(JsonSerializer.Serialize(new { op, at = DateTime.UtcNow.ToString("o"), detail }));

            if (stage1 is not null)
                Line("stage1_convert", new { stage1.AdaptationVersion, stage1.RuntimeMode, stage1.ModelId });
            if (report is not null)
                Line("adaptation_report", new { report.SourceComplete, issues = report.Issues.Count });
            if (film is not null)
                Line("film_stitched", new { film.FilmId, film.Studio.Sha256, segments = film.Timeline.Segments.Count });
            if (film?.Publish is not null)
                Line("film_published", new { film.Publish.Path, film.Publish.YoutubeVideoId });
        }

        // Optional lab mirror under app workspace evals/
        string? labPath = null;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            try
            {
                var labDir = Path.Combine(workspaceRoot, "evals", "learning_packages", packageId);
                Directory.CreateDirectory(labDir);
                File.Copy(packagePath, Path.Combine(labDir, "package.json"), overwrite: true);
                File.Copy(trajectoryPath, Path.Combine(labDir, "trajectory.jsonl"), overwrite: true);
                labPath = Path.Combine("evals", "learning_packages", packageId);
            }
            catch
            {
                /* non-fatal */
            }
        }

        try
        {
            store.TriggerAutoGitCommit(projectId, $"ptm:stage=learning_package id={packageId}");
        }
        catch { /* non-fatal */ }

        return new LearningPackageResult
        {
            PackageId = packageId,
            ProjectRelativePath = Path.Combine("artifacts", "learning_packages", packageId).Replace('\\', '/'),
            LabRelativePath = labPath,
            PublishPath = publishPath,
            FilmId = film?.FilmId,
        };
    }

    private static async Task<YouTubeUploadInfo?> TryReadYoutubeAsync(string projectDir, CancellationToken ct = default)
    {
        var path = Path.Combine(projectDir, "assets", "youtube_upload.json");
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<YouTubeUploadInfo>(
                text,
                JsonDefaults.IndentedCaseInsensitive);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class LearningPackageResult
{
    public string PackageId { get; init; } = "";
    public string ProjectRelativePath { get; init; } = "";
    public string? LabRelativePath { get; init; }
    public string? PublishPath { get; init; }
    public string? FilmId { get; init; }
}
