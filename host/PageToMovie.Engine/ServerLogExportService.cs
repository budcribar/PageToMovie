using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Packages all server diagnostic logs (job execution logs, project edit logs,
/// prompt history, sidecars, system state) into a zip bundle for server debugging.
/// </summary>
public sealed class ServerLogExportService
{
    private readonly ProjectStore _projects;
    private readonly FilmJobService _jobs;
    private readonly ILogger<ServerLogExportService> _logger;

    public ServerLogExportService(
        ProjectStore projects,
        FilmJobService jobs,
        ILogger<ServerLogExportService> logger)
    {
        _projects = projects;
        _jobs = jobs;
        _logger = logger;
    }

    public async Task<byte[]> ExportLogsZipAsync(CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. System Info
            var sysInfo = new
            {
                machineName = Environment.MachineName,
                osVersion = Environment.OSVersion.ToString(),
                is64Bit = Environment.Is64BitOperatingSystem,
                processorCount = Environment.ProcessorCount,
                activeProject = _projects.ActiveProjectId,
                exportTimeUtc = DateTimeOffset.UtcNow,
            };
            AddZipJsonEntry(zip, "system_info.json", sysInfo);

            // 2. Active & Recent Job Logs
            var activeJobs = _jobs.ListJobs(take: 100);
            AddZipJsonEntry(zip, "job_logs.json", activeJobs);

            // 3. Project Edit Logs, Sidecars & Prompt Files
            var projects = await _projects.ListProjectsAsync(ct).ConfigureAwait(false);
            foreach (var p in projects)
            {
                try
                {
                    var projDir = await _projects.GetProjectDirAsync(p.Id, ct).ConfigureAwait(false);

                    // edit_log.json
                    var editLogPath = Path.Combine(projDir, "edit_log.json");
                    if (File.Exists(editLogPath))
                    {
                        var content = await File.ReadAllBytesAsync(editLogPath, ct).ConfigureAwait(false);
                        AddZipFileEntry(zip, $"edit_logs/{p.Id}_edit_log.json", content);
                    }

                    // artifact_index.json
                    var artPath = Path.Combine(projDir, "artifact_index.json");
                    if (File.Exists(artPath))
                    {
                        var content = await File.ReadAllBytesAsync(artPath, ct).ConfigureAwait(false);
                        AddZipFileEntry(zip, $"artifact_index/{p.Id}_artifact_index.json", content);
                    }

                    // video prompts, meta & sidecars
                    var videoDir = Path.Combine(projDir, "assets", "video");
                    if (Directory.Exists(videoDir))
                    {
                        var promptFiles = Directory.GetFiles(videoDir, "*", SearchOption.TopDirectoryOnly)
                            .Where(f => f.EndsWith(".prompt.txt", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".clip.json", StringComparison.OrdinalIgnoreCase));

                        foreach (var pf in promptFiles)
                        {
                            var fn = Path.GetFileName(pf);
                            var content = await File.ReadAllBytesAsync(pf, ct).ConfigureAwait(false);
                            AddZipFileEntry(zip, $"prompts/{p.Id}/{fn}", content);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to collect logs for project {ProjectId}", p.Id);
                }
            }
        }

        return ms.ToArray();
    }

    private static void AddZipJsonEntry<T>(ZipArchive zip, string entryName, T obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(json);
    }

    private static void AddZipFileEntry(ZipArchive zip, string entryName, byte[] content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
