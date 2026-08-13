using System.Text.Json;

namespace PageToMovie.Engine;

/// <summary>
/// Canonical format versions for on-disk projects and export zips.
/// When you change layout or semantics, bump the right number and add a step in
/// <see cref="ProjectMigrationService"/> so import/export can convert older data.
/// </summary>
public static class ProjectFormatVersions
{
    /// <summary>
    /// Version of the project folder contract (project.json, fountain, cast, clips, …).
    /// Stored as <c>schema_version</c> in project.json. Keep in sync with
    /// <see cref="ProjectMigrationService.CurrentSchemaVersion"/>.
    /// </summary>
    public const string ProjectSchemaVersion = ProjectMigrationService.CurrentSchemaVersion;

    /// <summary>
    /// Version of the <b>export zip package</b> (entry layout, _export_meta.json fields,
    /// client-media merge). Independent of project schema — bump when the zip shape changes.
    /// </summary>
    /// <remarks>
    /// 1 = server folder only, meta schema string PageToMovie.project_export.v1<br/>
    /// 2 = server folder + optional client media merge; structured exportFormatVersion field
    /// </remarks>
    public const int ExportFormatVersion = 2;

    /// <summary>Oldest project schema we still auto-migrate from.</summary>
    public const string MinSupportedProjectSchemaVersion = "v0";

    /// <summary>Stable product id for the export package family.</summary>
    public const string ExportPackageId = "PageToMovie.project_export";

    public static object BuildExportMeta(
        string projectId,
        string? projectSchemaVersion,
        bool clientMediaMerged = false,
        int? clientMediaFilesAdded = null,
        string? clientMediaListError = null,
        bool hasScreenplayMax = false,
        bool hasScreenplayIndex = false,
        int indexSceneCards = 0) => new
    {
        package = ExportPackageId,
        schema = $"{ExportPackageId}.v{ExportFormatVersion}",
        exportFormatVersion = ExportFormatVersion,
        projectSchemaVersion = string.IsNullOrWhiteSpace(projectSchemaVersion)
            ? ProjectSchemaVersion
            : projectSchemaVersion.Trim(),
        minSupportedProjectSchemaVersion = MinSupportedProjectSchemaVersion,
        projectId,
        exportedAtUtc = DateTime.UtcNow.ToString("o"),
        clientMediaMerged,
        clientMediaFilesAdded,
        clientMediaListError,
        hasScreenplayMax,
        hasScreenplayIndex,
        indexSceneCards,
        note =
            "Server project folder including screenplay.max and screenplay.index when present. " +
            "Browser may merge local media (MP4/MP3). " +
            "On import, ProjectMigrationService upgrades projectSchemaVersion.",
    };

    /// <summary>Read project.json schema_version if present.</summary>
    public static async Task<string?> TryReadProjectSchemaVersionAsync(string projectDir, CancellationToken ct = default)
    {
        try
        {
            var path = Path.Combine(projectDir, "project.json");
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("schema_version", out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString();
            // camelCase variant
            if (doc.RootElement.TryGetProperty("schemaVersion", out var v2) &&
                v2.ValueKind == JsonValueKind.String)
                return v2.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>Parse _export_meta.json from an extracted project root (optional).</summary>
    public static async Task<ExportPackageMeta?> TryReadExportMetaAsync(string contentRoot, CancellationToken ct = default)
    {
        try
        {
            var path = Path.Combine(contentRoot, "_export_meta.json");
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ExportPackageMeta>(
                json,
                JsonDefaults.IndentedCaseInsensitive);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>DTO for _export_meta.json inside project zips.</summary>
public sealed class ExportPackageMeta
{
    public string? Package { get; set; }
    public string? Schema { get; set; }
    public int? ExportFormatVersion { get; set; }
    public string? ProjectSchemaVersion { get; set; }
    public string? MinSupportedProjectSchemaVersion { get; set; }
    public string? ProjectId { get; set; }
    public string? ExportedAtUtc { get; set; }
    public bool? ClientMediaMerged { get; set; }
    public int? ClientMediaFilesAdded { get; set; }
    public string? Note { get; set; }
}
