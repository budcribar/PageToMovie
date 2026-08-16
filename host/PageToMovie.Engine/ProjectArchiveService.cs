using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Admin full-project zip export / import for local debugging.
/// Zip layout: <c>{projectId}/…</c> (project.json at that folder root).
/// </summary>
public sealed class ProjectArchiveService
{
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;

    /// <summary>Compressed zip size cap (matches Kestrel multipart limit for admin import).</summary>
    public const long MaxZipBytes = 512L * 1024 * 1024;
    /// <summary>Max entries in a project zip (directories + files).</summary>
    public const int MaxZipEntries = 50_000;
    /// <summary>Max total uncompressed payload extracted from a zip.</summary>
    public const long MaxUncompressedTotalBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>Max size of any single extracted entry.</summary>
    public const long MaxSingleEntryUncompressedBytes = 512L * 1024 * 1024;

    private const string ProjectJsonFile = "project.json";
    private const string TitleKey = "title";

    private readonly ProjectStore _projects;
    private readonly ClipSidecarService? _sidecars;
    private readonly ProjectMigrationService? _migrations;
    private readonly ILogger<ProjectArchiveService> _log;

    public ProjectArchiveService(
        ProjectStore projects,
        ClipSidecarService? sidecars,
        ProjectMigrationService? migrations,
        ILogger<ProjectArchiveService>? log = null)
    {
        _projects = projects;
        _sidecars = sidecars;
        _migrations = migrations;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectArchiveService>.Instance;
    }

    public ProjectArchiveService(
        ProjectStore projects,
        ClipSidecarService sidecars,
        ILogger<ProjectArchiveService>? log)
        : this(projects, sidecars, null, log)
    {
    }

    public ProjectArchiveService(ProjectStore projects, ILogger<ProjectArchiveService>? log)
        : this(projects, null, null, log)
    {
    }

    /// <summary>
    /// Build a zip of the entire project directory. Caller must dispose the stream
    /// (FileStream with DeleteOnClose).
    /// </summary>
    public async Task<ProjectExportResult> ExportAsync(string projectId, CancellationToken ct = default)
    {
        // ASP.NET keeps a single route segment's %2F encoded rather than decoding it (see
        // ProjectStore.NormalizeProjectId's own doc comment) — a composite "owner/slug" id
        // arrives here as e.g. "budcribar%2FTellTaleHeartV7" unless normalized. Without this,
        // both the download filename and every entry path inside the zip end up with a literal
        // "%2F" baked into them as text instead of the intended nested owner/slug structure.
        var id = ProjectStore.NormalizeProjectId((projectId ?? "").Trim());
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Project id required");

        var projectDir = await _projects.GetProjectDirAsync(id, ct).ConfigureAwait(false);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        // Filesystem-safe download filename — id may contain a real "/" (owner/slug), which
        // can't appear in an actual filename, so it's replaced here only; the zip's internal
        // entry paths below use the real id with its "/" intact to form genuine subfolders.
        var fileNameSafeId = id.Replace('/', '_');
        var fileName = $"PageToMovie_{fileNameSafeId}_{stamp}.zip";
        var tempPath = Path.Combine(Path.GetTempPath(), $"ptm-export-{Guid.NewGuid():N}.zip");

        await TryMigrateOrConvertClipsAsync(projectDir, id, ct).ConfigureAwait(false);

        try
        {
            await Task.Run(async () =>
            {
                await WriteZipContentsAsync(id, projectDir, tempPath, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            var length = new FileInfo(tempPath).Length;
            _log.LogInformation("Exported project {ProjectId} → {Bytes} bytes", id, length);

            // Open for reading; delete when stream is disposed
            var read = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);

            return new ProjectExportResult
            {
                Stream = read,
                FileName = fileName,
                ContentType = "application/zip",
                ProjectId = id,
                ByteLength = length,
            };
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private async Task TryMigrateOrConvertClipsAsync(string projectDir, string id, CancellationToken ct)
    {
        if (_migrations is not null)
        {
            try
            {
                await _migrations.MigrateIfNeededAsync(projectDir, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Export: schema migration failed for {ProjectId}", id);
            }
        }
        else if (_sidecars is not null)
        {
            try
            {
                await _sidecars.ConvertProjectClipsToNewFormatAsync(projectDir, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Export: clip format conversion failed for {ProjectId}", id);
            }
        }
    }

    private static async Task WriteZipContentsAsync(
        string id, string projectDir, string tempPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true);
        // Manifest for importers. Export must not modify a project's working files.
        var projectSchema = await ProjectFormatVersions.TryReadProjectSchemaVersionAsync(projectDir, ct).ConfigureAwait(false)
                            ?? ProjectFormatVersions.ProjectSchemaVersion;
        var share = ProjectScreenplayShare.Inspect(projectDir);
        var metaEntry = zip.CreateEntry($"{id}/_export_meta.json", CompressionLevel.Fastest);
        await using (var metaStream = await metaEntry.OpenAsync(ct).ConfigureAwait(false))
        using (var w = new StreamWriter(metaStream, Encoding.UTF8))
        {
            await w.WriteAsync(JsonSerializer.Serialize(
                ProjectFormatVersions.BuildExportMeta(
                    id, projectSchema,
                    hasScreenplayMax: share.HasMax,
                    hasScreenplayIndex: share.HasIndex,
                    indexSceneCards: share.SceneCards),
                JsonOpts));
        }

        // ZipArchiveMode.Create does not support GetEntry — track names ourselves.
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{id}/_export_meta.json",
        };

        foreach (var file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(projectDir, file);
            var name = Path.GetFileName(file);
            if (ShouldSkipExportFile(rel, name))
                continue;

            // Portable zip entry names (no ':' etc.) so Windows extract works even if
            // something on the Linux host still uses an OS-allowed-but-not-Windows name.
            var relNorm = rel.Replace('\\', '/');
            var safeRel = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeRelativePath(relNorm);
            if (string.IsNullOrEmpty(safeRel))
                continue;
            var entryName = $"{id}/{safeRel}";
            // Avoid duplicate entries if two disk paths collapse to the same safe name.
            if (!seenEntries.Add(entryName))
                continue;

            var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
            await using (var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            await using (var entryStream = await entry.OpenAsync(ct).ConfigureAwait(false))
            {
                await sourceStream.CopyToAsync(entryStream, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldSkipExportFile(string rel, string name)
    {
        if (string.IsNullOrEmpty(rel) || rel.StartsWith("..", StringComparison.Ordinal))
            return true;
        // Skip OS junk. Also skip a stray on-disk "_export_meta.json" — it's an
        // export-generated manifest (written fresh, above, for THIS export), never
        // real project content; a leftover copy (e.g. from an older re-slug rename,
        // before ImportAsync started deleting it) would otherwise re-enter the zip as
        // a second, colliding entry with a stale projectId that wins on extraction.
        if (name is "Thumbs.db" or ".DS_Store" or "_export_meta.json")
            return true;

        // Ephemeral collab locks — not project content; filenames used colons
        // (loc:Hall.json) that break Windows Explorer extract (0x80070057).
        var relNorm = rel.Replace('\\', '/');
        if (relNorm.StartsWith("leases/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relNorm, "leases", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>
    /// Import a project zip. Supports:
    /// <list type="bullet">
    /// <item>Entries under <c>{id}/…</c> with project.json</item>
    /// <item>Entries with project.json at zip root</item>
    /// </list>
    /// </summary>
    public async Task<ProjectImportResult> ImportAsync(
        Stream zipStream,
        string? preferredId = null,
        bool overwrite = false,
        string? targetUserId = null,
        string? forceOwnerUserId = null,
        CancellationToken ct = default)
    {
        if (zipStream is null || !zipStream.CanRead)
            throw new InvalidOperationException("Zip stream required");

        var tempZip = Path.Combine(Path.GetTempPath(), $"ptm-import-{Guid.NewGuid():N}.zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), $"ptm-import-dir-{Guid.NewGuid():N}");

        try
        {
            await using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await StreamCopy.CopyWithSizeCapAsync(zipStream, fs, MaxZipBytes, ct, "Zip file").ConfigureAwait(false);
            }

            Directory.CreateDirectory(tempExtract);
            ExtractZipSafely(tempZip, tempExtract, ct);

            var contentRoot = FindProjectContentRoot(tempExtract)
                ?? throw new InvalidOperationException(
                    "Zip does not look like a PageToMovie project (no project.json found).");

            var idFromMeta = await TryReadProjectIdAsync(contentRoot, ct).ConfigureAwait(false);
            var (id, dest, ownerId) = await PrepareImportDestinationAsync(
                contentRoot, preferredId, idFromMeta, overwrite, targetUserId, forceOwnerUserId, ct)
                .ConfigureAwait(false);

            // Copy extracted content into projects/{id}
            CopyDirectory(contentRoot, dest);

            return await FinishImportedProjectAsync(contentRoot, dest, id, ownerId, overwrite, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            CleanupImportTemps(tempZip, tempExtract);
        }
    }

    private async Task<(string Id, string Dest, string? TargetUserId)> PrepareImportDestinationAsync(
        string contentRoot,
        string? preferredId,
        string? idFromMeta,
        bool overwrite,
        string? targetUserId,
        string? forceOwnerUserId,
        CancellationToken ct)
    {
        var idFromFolder = Path.GetFileName(contentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var rawId = ResolveImportRawId(preferredId, idFromMeta, idFromFolder, forceOwnerUserId, ref targetUserId);

        // Preserves an "owner/slug" split (SanitizeProjectIdPublic alone would collapse the "/"
        // into "_", landing the import at a flat projects/{owner}_{slug}/ instead of the
        // namespaced projects/{owner}/{slug}/ layout the rest of the app expects).
        var id = ProjectStore.SanitizeComposeProjectIdPublic(rawId);
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("Could not derive a safe project id from the zip.");

        var projectsRoot = Path.GetFullPath(Path.Combine(_projects.WorkspaceRoot, "projects"));
        Directory.CreateDirectory(projectsRoot);
        var dest = Path.GetFullPath(Path.Combine(projectsRoot, id));

        if (!dest.StartsWith(projectsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid project destination path.");

        if (Directory.Exists(dest))
        {
            if (!overwrite)
                throw new InvalidOperationException(
                    $"Project already exists: {id}. Enable overwrite or choose another id.");
            await _projects.DeleteProjectAsync(id, ct).ConfigureAwait(false);
        }

        return (id, dest, targetUserId);
    }

    private static string ResolveImportRawId(
        string? preferredId,
        string? idFromMeta,
        string idFromFolder,
        string? forceOwnerUserId,
        ref string? targetUserId)
    {
        string rawId;
        if (!string.IsNullOrWhiteSpace(preferredId))
            rawId = preferredId.Trim();
        else if (!string.IsNullOrWhiteSpace(idFromMeta))
            rawId = idFromMeta;
        else
            rawId = idFromFolder;

        // User-mode / rename import: land the project in the importer's own namespace, taking only
        // the slug (last path segment) from the zip's id and prefixing the forced owner. Stops one
        // user from importing into another's namespace, and re-slug rename from keeping the old owner.
        if (string.IsNullOrWhiteSpace(forceOwnerUserId))
            return rawId;

        var basis = rawId.Replace('\\', '/').Trim('/');
        var lastSlash = basis.LastIndexOf('/');
        var slug = lastSlash >= 0 ? basis[(lastSlash + 1)..] : basis;
        // Stamp ownerUserId to match the namespace only when the caller gave no owner. A caller
        // that knows the real user id (re-slug rename) passes it as targetUserId — the folder
        // segment ("budcribargmail_com") is not the user id ("budcribar@gmail.com") and would
        // fail the ownership check on activate.
        if (string.IsNullOrWhiteSpace(targetUserId))
            targetUserId = forceOwnerUserId.Trim();
        return $"{forceOwnerUserId.Trim()}/{slug}";
    }

    private async Task<ProjectImportResult> FinishImportedProjectAsync(
        string contentRoot,
        string dest,
        string id,
        string? targetUserId,
        bool overwrite,
        CancellationToken ct)
    {
        var exportMeta = await ProjectFormatVersions.TryReadExportMetaAsync(contentRoot, ct).ConfigureAwait(false)
                         ?? await ProjectFormatVersions.TryReadExportMetaAsync(dest, ct).ConfigureAwait(false);
        var schemaBefore = await ProjectFormatVersions.TryReadProjectSchemaVersionAsync(dest, ct).ConfigureAwait(false)
                           ?? exportMeta?.ProjectSchemaVersion
                           ?? "v0";

        // _export_meta.json is a manifest ABOUT an export, generated fresh by ExportAsync every
        // time — never real project content. Leaving the copy from this zip on disk would freeze
        // this project's next export with the *previous* project's id forever (re-slug rename's
        // export → import → delete-old goes through here, and re-exporting later would re-zip
        // this stale file as a second, colliding "_export_meta.json" entry that wins over the
        // freshly-generated correct one on extraction).
        try { File.Delete(Path.Combine(dest, "_export_meta.json")); } catch { /* best effort */ }

        // Ensure project.json id and optional ownerUserId match
        await EnsureProjectJsonIdAsync(dest, id, targetUserId, ct).ConfigureAwait(false);

        var migrated = await TryMigrateImportedProjectAsync(dest, id, ct).ConfigureAwait(false);
        var schemaAfter = await ProjectFormatVersions.TryReadProjectSchemaVersionAsync(dest, ct).ConfigureAwait(false)
                          ?? ProjectFormatVersions.ProjectSchemaVersion;

        _projects.InvalidateReadCaches(null);
        var info = await _projects.ActivateAsync(id, ct).ConfigureAwait(false);

        _log.LogInformation(
            "Imported project {ProjectId} from zip (overwrite={Overwrite}, exportFmt={ExportFmt}, schema {Before}→{After}, migrated={Migrated})",
            id, overwrite, exportMeta?.ExportFormatVersion, schemaBefore, schemaAfter, migrated);

        return new ProjectImportResult
        {
            Ok = true,
            ProjectId = id,
            Project = info,
            Message = BuildImportSuccessMessage(id, overwrite, migrated, schemaBefore, schemaAfter, exportMeta?.ExportFormatVersion),
            ExportFormatVersion = exportMeta?.ExportFormatVersion,
            ProjectSchemaVersionBefore = schemaBefore,
            ProjectSchemaVersionAfter = schemaAfter,
            Migrated = migrated,
        };
    }

    private async Task<bool> TryMigrateImportedProjectAsync(string dest, string id, CancellationToken ct)
    {
        if (_migrations is null)
            return false;
        try
        {
            return await _migrations.MigrateIfNeededAsync(dest, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Import: schema migration failed for {ProjectId}", id);
            return false;
        }
    }

    private static string BuildImportSuccessMessage(
        string id,
        bool overwrite,
        bool migrated,
        string schemaBefore,
        string schemaAfter,
        int? exportFormatVersion)
    {
        var msg = overwrite
            ? $"Imported and replaced project “{id}”"
            : $"Imported project “{id}”";
        if (migrated)
            msg += $" · converted project schema {schemaBefore} → {schemaAfter}";
        else if (!string.Equals(schemaBefore, schemaAfter, StringComparison.OrdinalIgnoreCase))
            msg += $" · schema {schemaAfter}";
        if (exportFormatVersion is int efv)
            msg += $" · export format v{efv}";
        return msg;
    }

    private static void CleanupImportTemps(string tempZip, string tempExtract)
    {
        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* ignore */ }
        try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>
    /// Rename a project by re-slugging its folder/id: export → import under the new
    /// "{owner}/{newSlug}" id → set the display title → delete the old project → activate the new.
    /// Reuses the export/import machinery so the id is remapped everywhere consistently instead of
    /// hand-patching the media registry, active pointer, per-project git, etc. When the new slug
    /// equals the current one this degrades to a display-title-only rename (no folder move).
    /// </summary>
    public async Task<ProjectRenameResult> RenameViaReimportAsync(
        string oldId,
        string newName,
        bool force = false,
        CancellationToken ct = default)
    {
        var old = ProjectStore.NormalizeProjectId((oldId ?? "").Trim());
        if (string.IsNullOrEmpty(old))
            throw new InvalidOperationException("Project id required");
        var title = (newName ?? "").Trim();
        if (title.Length == 0)
            throw new InvalidOperationException("New project name is required.");
        if (title.Length > 80) title = title[..80].Trim();

        // Preserve the project's existing owner namespace (an admin renaming another user's project
        // must not move it into the admin's namespace) — derive it from the old id, not the caller.
        var owner = old.Contains('/', StringComparison.Ordinal)
            ? ProjectStore.SanitizeUserSegment(old[..old.IndexOf('/', StringComparison.Ordinal)])
            : "";
        var newSlug = ProjectStore.SanitizeProjectIdPublic(title);
        if (newSlug.Length == 0)
            throw new InvalidOperationException("Project name has no usable characters.");

        var oldSlug = old.Contains('/', StringComparison.Ordinal)
            ? old[(old.LastIndexOf('/') + 1)..]
            : old;

        // Same slug → nothing to move; just update the display name in place.
        if (string.Equals(newSlug, oldSlug, StringComparison.OrdinalIgnoreCase))
        {
            await _projects.RenameProjectAsync(old, title, ct).ConfigureAwait(false);
            return new ProjectRenameResult
            {
                Ok = true,
                OldId = old,
                NewId = old,
                ReSlugged = false,
                Message = $"Renamed to “{title}” (display name; folder unchanged).",
            };
        }

        // Clips whose bytes live only in the browser (offloaded) don't travel in the export. Once
        // sidecars carry a provider source_url + just-in-time download exists they re-fetch on access,
        // but until then surface the count so the caller can decide.
        var offloaded = CountOffloadedMedia(await _projects.GetProjectDirAsync(old, ct).ConfigureAwait(false));

        // export → import(new id, forced owner) → title → delete old → activate new.
        // Keep the real owner user id on the moved project (the namespace segment is derived
        // from it but is not it); otherwise the renamed project fails the ownership check.
        var oldInfo = await _projects.GetProjectAsync(old, ct).ConfigureAwait(false);
        string? ownerUserId = null;
        if (!string.IsNullOrWhiteSpace(oldInfo?.OwnerUserId))
        {
            ownerUserId = oldInfo.OwnerUserId.Trim();
        }
        else if (!string.IsNullOrEmpty(owner))
        {
            ownerUserId = owner;
        }
        await using var exp = await ExportAsync(old, ct).ConfigureAwait(false);
        var import = await ImportAsync(
            exp.Stream,
            preferredId: string.IsNullOrEmpty(owner) ? newSlug : $"{owner}/{newSlug}",
            overwrite: false,
            targetUserId: ownerUserId,
            forceOwnerUserId: string.IsNullOrEmpty(owner) ? null : owner,
            ct: ct).ConfigureAwait(false);
        if (!import.Ok)
            throw new InvalidOperationException(import.Error ?? "Re-import failed during rename.");

        await _projects.RenameProjectAsync(import.ProjectId, title, ct).ConfigureAwait(false);
        await _projects.DeleteProjectAsync(old, ct).ConfigureAwait(false);
        var info = await _projects.ActivateAsync(import.ProjectId, ct).ConfigureAwait(false);

        var msg = $"Renamed to “{title}” (folder {oldSlug} → {newSlug}).";
        if (offloaded > 0)
            msg += $" {offloaded} clip(s) stored only in the browser will re-download on access once their source links are available.";
        return new ProjectRenameResult
        {
            Ok = true,
            OldId = old,
            NewId = import.ProjectId,
            ReSlugged = true,
            OffloadedClipCount = offloaded,
            Project = info,
            Message = msg,
        };
    }

    /// <summary>Count client-offloaded media markers (<c>*.client.json</c>) under a project's assets.</summary>
    private static int CountOffloadedMedia(string projectDir)
    {
        var assets = Path.Combine(projectDir, "assets");
        if (!Directory.Exists(assets)) return 0;
        try
        {
            return Directory.EnumerateFiles(assets, "*.client.json", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Extract zip with entry-count / total-size / path-traversal guards (zip-bomb mitigation).
    /// </summary>
    internal static void ExtractZipSafely(string zipPath, string destDir, CancellationToken ct = default)
    {
        destDir = Path.GetFullPath(destDir);
        Directory.CreateDirectory(destDir);

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxZipEntries)
        {
            throw new InvalidOperationException(
                $"Zip has too many entries ({archive.Entries.Count:N0}; max {MaxZipEntries:N0}).");
        }

        long totalUncompressed = 0;
        var entryCount = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaxZipEntries)
            {
                throw new InvalidOperationException(
                    $"Zip has too many entries (max {MaxZipEntries:N0}).");
            }

            ExtractOneZipEntry(entry, destDir, ref totalUncompressed);
        }
    }

    private static void ExtractOneZipEntry(ZipArchiveEntry entry, string destDir, ref long totalUncompressed)
    {
        // Directory entries end with / or \
        var rawName = entry.FullName.Replace('\\', '/');
        // Portable extract: map loc:Foo.json → loc_Foo.json so Linux-authored zips
        // with colon lease names still extract on Windows (and on our Linux import host
        // when running under a Windows-style invalid-char policy).
        var name = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeRelativePath(rawName);
        if (string.IsNullOrWhiteSpace(rawName) || rawName.EndsWith('/'))
        {
            ExtractZipDirectoryEntry(destDir, name);
            return;
        }

        if (string.IsNullOrWhiteSpace(name) || IsLeaseZipEntry(name))
            return;

        ExtractZipFileEntry(entry, destDir, name, ref totalUncompressed);
    }

    private static void ExtractZipDirectoryEntry(string destDir, string name)
    {
        // Ensure directory exists (still count toward bomb limits via empty path only).
        if (string.IsNullOrWhiteSpace(name))
            return;
        var dirPath = Path.GetFullPath(Path.Combine(destDir, name));
        EnsureUnderRoot(destDir, dirPath);
        Directory.CreateDirectory(dirPath);
    }

    /// <summary>Ephemeral leases are not needed on import; skip if present in old zips.</summary>
    private static bool IsLeaseZipEntry(string name) =>
        name.Contains("/leases/", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("leases/", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("/leases", StringComparison.OrdinalIgnoreCase);

    private static void ExtractZipFileEntry(
        ZipArchiveEntry entry, string destDir, string name, ref long totalUncompressed)
    {
        if (entry.Length < 0 || entry.Length > MaxSingleEntryUncompressedBytes)
        {
            throw new InvalidOperationException(
                $"Zip entry too large: {entry.FullName} ({entry.Length:N0} bytes; max {MaxSingleEntryUncompressedBytes:N0}).");
        }

        totalUncompressed += entry.Length;
        if (totalUncompressed > MaxUncompressedTotalBytes)
        {
            throw new InvalidOperationException(
                $"Zip uncompressed size exceeds limit ({MaxUncompressedTotalBytes:N0} bytes).");
        }

        var destPath = Path.GetFullPath(Path.Combine(destDir, name));
        EnsureUnderRoot(destDir, destPath);

        var parent = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        entry.ExtractToFile(destPath, overwrite: true);
        VerifyExtractedEntrySize(entry, destPath);
    }

    private static void VerifyExtractedEntrySize(ZipArchiveEntry entry, string destPath)
    {
        // Defense in depth: measure actual written size (some archives lie in headers).
        var written = new FileInfo(destPath).Length;
        if (written <= MaxSingleEntryUncompressedBytes)
            return;
        try { File.Delete(destPath); } catch { /* ignore */ }
        throw new InvalidOperationException(
            $"Extracted entry exceeded size cap: {entry.FullName}");
    }

    private static void EnsureUnderRoot(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Zip entry path escapes destination directory.");
        }
    }

    private static string? FindProjectContentRoot(string extractRoot)
    {
        var direct = Path.Combine(extractRoot, ProjectJsonFile);
        if (File.Exists(direct))
            return extractRoot;

        // Single top-level folder with project.json
        var nestedDir = Directory.GetDirectories(extractRoot)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, ProjectJsonFile)));
        if (nestedDir is not null)
            return nestedDir;

        // Nested: projects/MyId/project.json
        var nested = Directory.GetFiles(extractRoot, ProjectJsonFile, SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
        if (nested is not null)
            return Path.GetDirectoryName(nested);

        return null;
    }

    private static async Task<string?> TryReadProjectIdAsync(string contentRoot, CancellationToken ct)
    {
        var path = Path.Combine(contentRoot, ProjectJsonFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("id", out var idEl))
                return idEl.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private static async Task EnsureProjectJsonIdAsync(string dest, string id, string? targetUserId, CancellationToken ct)
    {
        var path = Path.Combine(dest, ProjectJsonFile);
        Dictionary<string, object?> meta;
        if (File.Exists(path))
        {
            try
            {
                meta = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                           await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), JsonOpts)
                       ?? new Dictionary<string, object?>();
            }
            catch
            {
                meta = new Dictionary<string, object?>();
            }
        }
        else
        {
            meta = new Dictionary<string, object?>
            {
                [TitleKey] = id,
                ["blueprint_file"] = "blueprint.clips.grok.json",
                ["scenes_file"] = "scenes.json",
                ["config_file"] = "pipeline_config.json",
                ["state_file"] = "pipeline_state.json",
            };
        }

        meta["id"] = id;
        if (!meta.ContainsKey(TitleKey) || meta[TitleKey] is null || string.IsNullOrWhiteSpace(meta[TitleKey]?.ToString()))
            meta[TitleKey] = id;

        if (!string.IsNullOrWhiteSpace(targetUserId))
        {
            meta["ownerUserId"] = targetUserId.Trim();
            meta["owner_user_id"] = targetUserId.Trim();
        }

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(meta, JsonOpts) + "\n",
            ct).ConfigureAwait(false);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            if (rel.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe path in archive: {rel}");
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destDir);
            File.Copy(file, target, overwrite: true);
        }
    }

}

public sealed class ProjectExportResult : IAsyncDisposable, IDisposable
{
    public required Stream Stream { get; init; }
    public required string FileName { get; init; }
    public string ContentType { get; init; } = "application/zip";
    public string ProjectId { get; init; } = "";
    public long ByteLength { get; init; }

    public void Dispose() => Stream.Dispose();

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public sealed class ProjectRenameResult
{
    public bool Ok { get; init; }
    public string OldId { get; init; } = "";
    public string NewId { get; init; } = "";
    /// <summary>True when the folder/id actually moved; false for a display-name-only change.</summary>
    public bool ReSlugged { get; init; }
    public int OffloadedClipCount { get; init; }
    public ProjectInfo? Project { get; init; }
    public string? Message { get; init; }
}

public sealed class ProjectImportResult
{
    public bool Ok { get; init; }
    public string ProjectId { get; init; } = "";
    public ProjectInfo? Project { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    /// <summary>From _export_meta.json when present.</summary>
    public int? ExportFormatVersion { get; init; }
    public string? ProjectSchemaVersionBefore { get; init; }
    public string? ProjectSchemaVersionAfter { get; init; }
    public bool Migrated { get; init; }
}
