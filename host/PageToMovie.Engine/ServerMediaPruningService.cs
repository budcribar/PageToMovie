using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine
{
    /// <summary>
    /// Background hosted service that prunes server-cached MP4/media binaries once the client has
    /// confirmed a synced copy via <see cref="MediaRegistryService"/> and the file has aged past
    /// <see cref="MediaPruningOptions.MaxFileAgeHours"/>, or on emergency disk-usage overflow.
    /// Off by default (<see cref="MediaPruningOptions.Enabled"/>) — this deletes files, and the server
    /// never re-creates lost generated video, so it must never touch a file the registry hasn't
    /// confirmed the client already has a copy of.
    /// </summary>
    public sealed class ServerMediaPruningService : BackgroundService
    {
        private static readonly string[] MediaExtensions = { ".mp4", ".webm", ".mov", ".wav", ".avi" };

        private readonly ILogger<ServerMediaPruningService> _logger;
        private readonly ProjectStore _projects;
        private readonly MediaRegistryService _registry;
        private readonly MediaPruningOptions _opts;

        public ServerMediaPruningService(
            ILogger<ServerMediaPruningService> logger,
            ProjectStore projects,
            MediaRegistryService registry,
            IOptions<PageToMovieOptions> opts)
        {
            _logger = logger;
            _projects = projects;
            _registry = registry;
            _opts = opts.Value.MediaPruning;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_opts.Enabled)
            {
                _logger.LogInformation(
                    "ServerMediaPruningService disabled (PageToMovie:MediaPruning:Enabled=false).");
                return;
            }

            var checkInterval = TimeSpan.FromMinutes(Math.Max(1, _opts.CheckIntervalMinutes));
            var maxAge = TimeSpan.FromHours(Math.Max(1, _opts.MaxFileAgeHours));
            _logger.LogInformation(
                "ServerMediaPruningService started. Checking every {Interval}, max age {MaxAge}.",
                checkInterval, maxAge);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await PerformPruningAsync(maxAge, _opts.MaxDiskUsagePercent, stoppingToken)
                        .ConfigureAwait(false);
                    if (deleted > 0)
                        _logger.LogInformation("Server media pruning pass deleted {Count} synced file(s).", deleted);
                }
                catch (OperationCanceledException)
                {
                    // shutting down
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during server media pruning execution.");
                }

                try
                {
                    await Task.Delay(checkInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // shutting down
                }
            }
        }

        /// <summary>
        /// Prunes media files that are both (a) older than <paramref name="maxAge"/> and (b) already
        /// confirmed synced to a client via <see cref="MediaRegistryService"/>, plus an emergency
        /// disk-pressure pass (still synced-only, oldest first) if usage still exceeds
        /// <paramref name="maxDiskPercent"/> afterward. Files with no registry record are never
        /// touched — the registry not knowing about a file means it may be the only copy in existence.
        /// </summary>
        public async Task<int> PerformPruningAsync(
            TimeSpan maxAge, double maxDiskPercent, CancellationToken ct = default)
        {
            var projectsRoot = Path.Combine(_projects.WorkspaceRoot, "projects");
            if (!Directory.Exists(projectsRoot))
                return 0;

            var candidates = await CollectSyncedMediaFilesAsync(projectsRoot, ct).ConfigureAwait(false);
            var deletedCount = 0;

            // Pass 0: aggressive prune — synced files only, but past a short grace period rather
            // than the full age window, since the client has already confirmed a local copy.
            var aggressiveCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _opts.AggressivePruneGraceMinutes));
            deletedCount += PruneOlderThan(candidates, aggressiveCutoff);

            // Pass 1: age-based prune (synced files only) — mostly redundant with Pass 0 above
            // (aggressiveCutoff is always >= cutoff), kept as a harmless fallback.
            deletedCount += PruneOlderThan(candidates, DateTime.UtcNow - maxAge);

            // Pass 2: emergency disk-pressure prune (synced files only), oldest first.
            deletedCount += EmergencyPruneForDisk(candidates, projectsRoot, maxDiskPercent);

            return deletedCount;
        }

        private int PruneOlderThan(List<MediaCandidate> candidates, DateTime cutoff)
        {
            var deleted = 0;
            foreach (var c in candidates.Where(c => c.LastWriteTimeUtc < cutoff && TryDelete(c)).ToList())
            {
                deleted++;
                candidates.Remove(c);
            }
            return deleted;
        }

        private int EmergencyPruneForDisk(List<MediaCandidate> candidates, string projectsRoot, double maxDiskPercent)
        {
            try
            {
                var driveRoot = Path.GetPathRoot(Path.GetFullPath(projectsRoot));
                if (string.IsNullOrEmpty(driveRoot)) return 0;
                var drive = new DriveInfo(driveRoot);
                if (!drive.IsReady || drive.TotalSize <= 0) return 0;
                if (DriveUsedPercent(drive) <= maxDiskPercent) return 0;

                _logger.LogWarning(
                    "Disk usage ({UsedPercent:F1}%) exceeds max threshold ({MaxPercent:F1}%). " +
                    "Emergency-pruning synced media oldest-first.",
                    DriveUsedPercent(drive), maxDiskPercent);
                return DeleteOldestUntilUnderQuota(candidates, drive, maxDiskPercent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disk usage check skipped during pruning.");
                return 0;
            }
        }

        private int DeleteOldestUntilUnderQuota(
            List<MediaCandidate> candidates, DriveInfo drive, double maxDiskPercent)
        {
            var deleted = 0;
            foreach (var c in candidates.OrderBy(c => c.LastWriteTimeUtc).ToList())
            {
                if (DriveUsedPercent(drive) <= maxDiskPercent) break;
                if (TryDelete(c)) deleted++;
            }
            return deleted;
        }

        private static double DriveUsedPercent(DriveInfo drive) =>
            (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100.0;

        private sealed record MediaCandidate(string ProjectId, string RelativePath, string FullPath, DateTime LastWriteTimeUtc);

        /// <summary>Only files the client has confirmed syncing (present in <see cref="MediaRegistryService"/>).</summary>
        private async Task<List<MediaCandidate>> CollectSyncedMediaFilesAsync(string projectsRoot, CancellationToken ct)
        {
            var result = new List<MediaCandidate>();

            foreach (var projectDir in Directory.GetDirectories(projectsRoot))
            {
                ct.ThrowIfCancellationRequested();
                await CollectProjectSyncedMediaAsync(projectDir, result, ct).ConfigureAwait(false);
            }

            return result;
        }

        private async Task CollectProjectSyncedMediaAsync(
            string projectDir, List<MediaCandidate> result, CancellationToken ct)
        {
            var projectId = Path.GetFileName(projectDir);
            if (string.IsNullOrWhiteSpace(projectId)) return;

            string[] files;
            try
            {
                files = Directory.GetFiles(projectDir, "*", SearchOption.AllDirectories)
                    .Where(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate project media under {Dir} during pruning.", projectDir);
                return;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await TryAddSyncedMediaFileAsync(projectId, projectDir, file, result, ct).ConfigureAwait(false);
            }
        }

        private async Task TryAddSyncedMediaFileAsync(
            string projectId, string projectDir, string file, List<MediaCandidate> result, CancellationToken ct)
        {
            var relativePath = Path.GetRelativePath(projectDir, file).Replace('\\', '/');

            if (ClipForkFallback.IsProtectedFromPrune(file))
                return;

            MediaObjectDto? registered;
            try
            {
                registered = await _registry.TryGetAsync(projectId, relativePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex, "Media registry lookup failed for {Project}/{Path}; treating as unsynced.",
                    projectId, relativePath);
                return; // fail closed: never delete when sync status can't be confirmed
            }

            if (registered is null)
                return; // no client confirmation — may be the only surviving copy; keep it

            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(file); }
            catch { return; }

            result.Add(new MediaCandidate(projectId, relativePath, file, lastWrite));
        }

        private bool TryDelete(MediaCandidate c)
        {
            try
            {
                File.Delete(c.FullPath);
                _logger.LogInformation(
                    "Pruned synced server media file: {Project}/{Path} (age {AgeHours:F1}h)",
                    c.ProjectId, c.RelativePath, (DateTime.UtcNow - c.LastWriteTimeUtc).TotalHours);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete pruned media file {FilePath}", c.FullPath);
                return false;
            }
        }
    }
}
