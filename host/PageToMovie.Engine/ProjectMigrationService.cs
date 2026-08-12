using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Manages schema versioning for PageToMovie projects and automatically executes
/// sequential migration steps (e.g. v0 → v1 clip naming & sidecars, v1 → v2 prompt label tags)
/// on import, export, and load.
/// Keep <see cref="CurrentSchemaVersion"/> aligned with
/// <see cref="ProjectFormatVersions.ProjectSchemaVersion"/>. Export zip shape is versioned
/// separately via <see cref="ProjectFormatVersions.ExportFormatVersion"/>.
/// </summary>
public sealed class ProjectMigrationService
{
    /// <summary>Latest on-disk project schema. Bump when adding a migration step below.</summary>
    public const string CurrentSchemaVersion = "v2";

    private readonly ClipSidecarService _sidecars;
    private readonly ILogger<ProjectMigrationService> _log;

    public ProjectMigrationService(
        ClipSidecarService sidecars,
        ILogger<ProjectMigrationService>? log = null)
    {
        _sidecars = sidecars;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectMigrationService>.Instance;
    }

    /// <summary>
    /// Check project schema version and execute necessary migrations up to CurrentSchemaVersion.
    /// </summary>
    public async Task<bool> MigrateIfNeededAsync(string projectDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            return false;

        var projectJsonPath = Path.Combine(projectDir, "project.json");
        var currentVersion = "v0";
        Dictionary<string, object?>? projectDict = null;

        if (File.Exists(projectJsonPath))
        {
            try
            {
                var text = await File.ReadAllTextAsync(projectJsonPath, ct).ConfigureAwait(false);
                projectDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(text, JsonDefaults.IndentedCaseInsensitive);
                if (projectDict is not null && projectDict.TryGetValue("schema_version", out var vObj) && vObj is JsonElement el)
                {
                    currentVersion = el.GetString() ?? "v0";
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed reading project.json schema_version at {Path}", projectJsonPath);
            }
        }

        if (string.Equals(currentVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            // Already on latest version, still ensure missing sidecars exist
            await _sidecars.EnsureAllSidecarsExistAsync(projectDir, ct).ConfigureAwait(false);
            return false;
        }

        _log.LogInformation("Migrating project at {Dir} from schema {OldVersion} → {NewVersion}", projectDir, currentVersion, CurrentSchemaVersion);

        // Step 0 → 1: Convert clip naming convention and write .clip.json sidecars
        if (currentVersion is "v0" or "unversioned" or "")
        {
            await _sidecars.ConvertProjectClipsToNewFormatAsync(projectDir, ct).ConfigureAwait(false);
            currentVersion = "v1";
        }

        // Step 1 → 2: Camera directive:/Performance:/Optics: plain-text labels baked into each
        // clip's visual_prompt at Stage2 planning time -> explicit <Camera>/<Performance>/<Optics>
        // tags (ClipVideoPromptBuilder/Stage2PlannerService switched formats so a compression
        // regex can no longer mistake a label for prose that happens to contain the same words).
        if (currentVersion == "v1")
        {
            await MigrateVisualPromptLabelsToTagsAsync(projectDir, ct).ConfigureAwait(false);
            currentVersion = "v2";
        }

        // Update project.json with new schema version
        projectDict ??= new Dictionary<string, object?>();
        projectDict["schema_version"] = CurrentSchemaVersion;
        projectDict["migrated_at_utc"] = DateTime.UtcNow.ToString("o");

        try
        {
            var updatedJson = JsonSerializer.Serialize(projectDict, JsonDefaults.IndentedCaseInsensitive);
            await File.WriteAllTextAsync(projectJsonPath, updatedJson + "\n", ct).ConfigureAwait(false);
            _log.LogInformation("Successfully updated project.json to schema {Version} for {Dir}", CurrentSchemaVersion, projectDir);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed updating project.json schema version at {Path}", projectJsonPath);
        }

        return true;
    }

    /// <summary>
    /// Rewrite Camera directive:/Performance:/Optics: labels to &lt;Camera&gt;/&lt;Performance&gt;/
    /// &lt;Optics&gt; tags inside every clip's visual_prompt across this project's
    /// blueprint.clips*.json files. Best-effort per file — a parse/write failure on one file
    /// doesn't block the rest of the project's migration.
    /// </summary>
    private async Task MigrateVisualPromptLabelsToTagsAsync(string projectDir, CancellationToken ct)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectDir, "blueprint.clips*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed listing blueprint files for prompt-tag migration at {Dir}", projectDir);
            return;
        }

        foreach (var file in files)
        {
            if (file.Contains(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                if (node is null) continue;
                if (MigrateVisualPromptNode(node))
                {
                    await File.WriteAllTextAsync(file, node.ToJsonString(JsonDefaults.IndentedCaseInsensitive), ct).ConfigureAwait(false);
                    _log.LogInformation("Migrated visual_prompt labels to tags in {File}", file);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipped prompt-tag migration for {File}", file);
            }
        }
    }

    private static bool MigrateVisualPromptNode(JsonNode? node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (string.Equals(key, "visual_prompt", StringComparison.OrdinalIgnoreCase) &&
                        value is JsonValue jv && jv.TryGetValue<string>(out var s) &&
                        !string.IsNullOrEmpty(s))
                    {
                        var migrated = MigrateVisualPromptLabelText(s);
                        if (!string.Equals(migrated, s, StringComparison.Ordinal))
                        {
                            obj[key] = migrated;
                            changed = true;
                        }
                    }
                    else if (MigrateVisualPromptNode(value))
                    {
                        changed = true;
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    if (MigrateVisualPromptNode(item)) changed = true;
                break;
        }
        return changed;
    }

    /// <summary>
    /// Color grading: is deliberately left untouched — it's partly embedded in
    /// ColorPaletteGradingClassifier's own AI prompt template (an example output format shown to
    /// the model), not purely deterministic C# string building like the other three, so converting
    /// it means editing what the model is shown rather than a safe mechanical label rename.
    /// </summary>
    private static string MigrateVisualPromptLabelText(string text)
    {
        text = CommonRegex.Replace(
            text, @"Camera directive:\s*(.+?)(?=\s+Performance:|\s+Optics:|\s+Color grading:|$)",
            m => PromptTags.Wrap("Camera", m.Groups[1].Value), RegexOptions.Singleline);
        text = CommonRegex.Replace(
            text, @"Performance:\s*(.+?)(?=\s+Optics:|\s+Color grading:|$)",
            m => PromptTags.Wrap("Performance", m.Groups[1].Value), RegexOptions.Singleline);
        text = CommonRegex.Replace(
            text, @"Optics:\s*(.+?)(?=\s+Color grading:|$)",
            m => PromptTags.Wrap("Optics", m.Groups[1].Value), RegexOptions.Singleline);
        return text;
    }
}
