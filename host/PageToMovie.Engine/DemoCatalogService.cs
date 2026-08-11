using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Demo gallery under <c>{WorkspaceRoot}/_demos/{id}/</c> (meta.json + optional staged movie.mp4).
/// <b>YouTube is the source of truth for playback</b>: the public wall lists demos that have a
/// <see cref="DemoEntry.YoutubeId"/> (after upload). Local movie.mp4 is only a staging file and
/// is deleted once YouTube accepts the upload. No admin content-approval queue — YouTube
/// (and the user's own publish) gates what appears.
/// </summary>
public sealed class DemoCatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public const long MaxUploadBytes = 512L * 1024 * 1024;
    public const long MinUploadBytes = 1024;
    /// <summary>Max demos a user may submit per rolling 24h window.</summary>
    public const int MaxPublishesPerUserPerDay = 20;
    /// <summary>Legacy cap (no longer used for admin review queues).</summary>
    public const int MaxPendingPerUser = 5;
    /// <summary>Max open report notes stored on one demo.</summary>
    public const int MaxReportNotes = 20;

    private readonly ProjectStore _projects;
    private readonly ILogger<DemoCatalogService> _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public DemoCatalogService(ProjectStore projects, ILogger<DemoCatalogService> log)
    {
        _projects = projects;
        _log = log;
    }

    public string DemosDir => Path.Combine(_projects.WorkspaceRoot, "_demos");

    /// <summary>Clean up leftover staged temporary movie files under _demos to reclaim server volume space.</summary>
    public void CleanupStagedDemoMovies()
    {
        lock (_lock)
        {
            if (!Directory.Exists(DemosDir)) return;
            var subdirs = Directory.GetDirectories(DemosDir);
            foreach (var dir in subdirs)
            {
                var movieFile = Path.Combine(dir, "movie.mp4");
                if (File.Exists(movieFile))
                {
                    try
                    {
                        File.Delete(movieFile);
                        _log.LogInformation("Cleaned up temporary staged demo file {Path}", movieFile);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed to clean up demo movie {Path}", movieFile);
                    }
                }
            }
        }
    }

    public static class DemoStatuses
    {
        public const string Pending = "pending";
        public const string Public = "public";
        public const string Rejected = "rejected";
        public const string Removed = "removed";

        public static bool IsKnown(string? s) =>
            string.Equals(s, Pending, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, Public, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, Rejected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, Removed, StringComparison.OrdinalIgnoreCase);

        public static string Normalize(string? s) =>
            IsKnown(s) ? s!.Trim().ToLowerInvariant() : Public; // legacy metas without status → public
    }

    public sealed class DemoEntry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string? ProjectId { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public long SizeBytes { get; set; }
        public string? ContentType { get; set; }
        /// <summary>public | rejected | removed (pending legacy only)</summary>
        public string Status { get; set; } = DemoStatuses.Public;
        public bool AcceptedGuidelines { get; set; }
        public int ReportCount { get; set; }
        public List<string> ReportNotes { get; set; } = new();
        public string? ReviewedBy { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public string? ReviewNote { get; set; }

        // YouTube publish metadata (declared by the submitter; used at actual upload time,
        // whether that's immediate auto-approval or a later admin approval).
        public bool MadeForKids { get; set; }
        public bool IsAiSyntheticContent { get; set; } = true;
        public string PrivacyStatus { get; set; } = "public";
        public List<string>? Tags { get; set; }

        /// <summary>Studio-only taxonomy (e.g. storybook, horror). Not from YouTube — kept across channel sync.</summary>
        public string? Category { get; set; }

        /// <summary>none | uploading | done | failed. "done" means the video now lives on YouTube
        /// and <see cref="Id"/>'s local movie.mp4 has been deleted (server footprint goal).</summary>
        public string YoutubeUploadStatus { get; set; } = "none";
        public string? YoutubeId { get; set; }
        public string? YoutubeUrl { get; set; }
        public string? YoutubeUploadError { get; set; }
        public ulong? YoutubeLikeCount { get; set; }
        public ulong? YoutubeViewCount { get; set; }
        public DateTimeOffset? YoutubeStatsRefreshedAt { get; set; }
    }

    public void SetYouTubeStats(string id, ulong? likeCount, ulong? viewCount)
    {
        if (!IsValidId(id)) return;
        lock (_lock)
        {
            var e = ReadUnlocked(id);
            if (e is null) return;
            e.YoutubeLikeCount = likeCount;
            e.YoutubeViewCount = viewCount;
            e.YoutubeStatsRefreshedAt = DateTimeOffset.UtcNow;
            SaveUnlocked(e);
        }
    }

    public IReadOnlyList<DemoEntry> List(int take = 50, string? status = null)
    {
        take = Math.Clamp(take, 1, 200);
        lock (_lock)
        {
            return LoadAllUnlocked()
                .Where(e => status is null
                    || string.Equals(e.Status, status, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedAt)
                .Take(take)
                .ToList();
        }
    }

    /// <summary>
    /// Public gallery: demos that live on YouTube (source of truth for the film).
    /// Removed/rejected are excluded; pending-without-YouTube never appear.
    /// </summary>
    public IReadOnlyList<DemoEntry> ListPublic(int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        lock (_lock)
        {
            return LoadAllUnlocked()
                .Where(e =>
                    !string.Equals(e.Status, DemoStatuses.Removed, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(e.Status, DemoStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(e.YoutubeId))
                .OrderByDescending(e => e.CreatedAt)
                .Take(take)
                .ToList();
        }
    }

    public DemoEntry? TryGet(string id)
    {
        if (!IsValidId(id)) return null;
        lock (_lock)
            return ReadUnlocked(id);
    }

    public string? ResolveMoviePath(string id)
    {
        if (!IsValidId(id)) return null;
        var path = Path.Combine(DemosDir, id, "movie.mp4");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Most recently created public demo for this project by this creator (if any).
    /// Used for YouTube V2 replace when re-publishing an updated cut.
    /// </summary>
    public DemoEntry? FindPublicDemoForProject(string projectId, string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return null;
        lock (_lock)
        {
            return LoadAllUnlocked()
                .Where(e =>
                    string.Equals(e.Status, DemoStatuses.Public, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(createdBy)
                        || string.Equals(e.CreatedBy, createdBy, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Copy the project's WIP movie onto an existing demo as movie.mp4 (for V2 YouTube replace).
    /// Updates size/metadata; does not clear <see cref="DemoEntry.YoutubeId"/>.
    /// </summary>
    public DemoEntry AttachMovieFromWip(
        string demoId,
        string projectId,
        string? title = null,
        string? description = null,
        bool? madeForKids = null,
        bool? isAiSyntheticContent = null,
        string? privacyStatus = null,
        List<string>? tags = null)
    {
        var wip = _projects.ResolveWipMoviePath(projectId)
                  ?? throw new InvalidOperationException("WIP movie not found — build the cut first.");
        return AttachMovieFromFile(demoId, wip, title, description, madeForKids, isAiSyntheticContent, privacyStatus, tags);
    }

    /// <summary>Write a new movie.mp4 onto an existing demo (V2 replace source material).</summary>
    public DemoEntry AttachMovieFromFile(
        string demoId,
        string sourceMp4Path,
        string? title = null,
        string? description = null,
        bool? madeForKids = null,
        bool? isAiSyntheticContent = null,
        string? privacyStatus = null,
        List<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(sourceMp4Path) || !File.Exists(sourceMp4Path))
            throw new InvalidOperationException("Source movie not found");
        if (!LooksLikeMp4(sourceMp4Path))
            throw new InvalidOperationException("Source file is not a valid MP4.");

        lock (_lock)
        {
            var entry = ReadUnlocked(demoId)
                        ?? throw new InvalidOperationException("Demo not found");
            var dir = Path.Combine(DemosDir, entry.Id);
            Directory.CreateDirectory(dir);
            var moviePath = Path.Combine(dir, "movie.mp4");
            File.Copy(sourceMp4Path, moviePath, overwrite: true);
            var fi = new FileInfo(moviePath);
            if (fi.Length < MinUploadBytes)
                throw new InvalidOperationException("Movie file is too small");

            entry.SizeBytes = fi.Length;
            entry.ContentType = "video/mp4";
            if (!string.IsNullOrWhiteSpace(title))
                entry.Title = title.Trim();
            if (description is not null)
                entry.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            if (madeForKids is bool mfk)
                entry.MadeForKids = mfk;
            if (isAiSyntheticContent is bool ai)
                entry.IsAiSyntheticContent = ai;
            if (privacyStatus is "public" or "unlisted" or "private")
                entry.PrivacyStatus = privacyStatus;
            if (tags is not null)
                entry.Tags = tags.Count > 0 ? tags : null;
            // New bytes on disk — YouTube pointer is stale until V2 publish finishes.
            entry.YoutubeUploadStatus = string.IsNullOrWhiteSpace(entry.YoutubeId) ? "none" : "pending_replace";
            entry.YoutubeUploadError = null;

            SaveUnlocked(entry);
            _log.LogInformation(
                "Demo {Id} attached new movie ({Bytes} bytes) for YouTube replace; prior YoutubeId={Yt}",
                entry.Id, entry.SizeBytes, entry.YoutubeId);
            return entry;
        }
    }

    /// <summary>
    /// Stream a new movie onto an existing demo (same as <see cref="AttachMovieFromFile"/> for uploads).
    /// </summary>
    public async Task<DemoEntry> AttachMovieFromStreamAsync(
        string demoId,
        Stream content,
        string? title = null,
        string? description = null,
        bool? madeForKids = null,
        bool? isAiSyntheticContent = null,
        string? privacyStatus = null,
        List<string>? tags = null,
        CancellationToken ct = default)
    {
        if (content is null || !content.CanRead)
            throw new InvalidOperationException("Empty upload");

        var temp = Path.Combine(Path.GetTempPath(), "ptm_demo_replace_" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await using (var fs = new FileStream(
                             temp, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithSizeCapAsync(content, fs, MaxUploadBytes, ct).ConfigureAwait(false);
            }

            var fi = new FileInfo(temp);
            if (fi.Length < MinUploadBytes)
                throw new InvalidOperationException("Uploaded video is too small");
            if (!LooksLikeMp4(temp))
                throw new InvalidOperationException(
                    "Upload is not a valid MP4 (missing ftyp box). Only MP4 video is accepted.");

            return AttachMovieFromFile(demoId, temp, title, description, madeForKids, isAiSyntheticContent, privacyStatus, tags);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Whether anonymous/public viewers may stream this demo.
    /// Pending/rejected/removed are not world-readable.
    /// </summary>
    /// <summary>World-visible: not removed/rejected and has a YouTube id (gallery SoT).</summary>
    public bool IsPubliclyStreamable(DemoEntry? e) =>
        e is not null
        && !string.Equals(e.Status, DemoStatuses.Removed, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(e.Status, DemoStatuses.Rejected, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(e.YoutubeId);

    public bool CanUserViewVideo(DemoEntry e, string? userId, bool isAdmin)
    {
        if (IsPubliclyStreamable(e))
            return true;
        if (isAdmin)
            return true;
        if (!string.IsNullOrWhiteSpace(userId)
            && string.Equals(e.CreatedBy, userId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(e.Status, DemoStatuses.Removed, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>Enforce publish rate before accepting a new demo (no admin review queue).</summary>
    public void EnsureUserMayPublish(string? userId, bool isAdmin)
    {
        if (isAdmin)
            return;
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("Sign in required to publish a demo.");

        lock (_lock)
        {
            var since = DateTimeOffset.UtcNow.AddHours(-24);
            var mineToday = LoadAllUnlocked()
                .Where(e =>
                    string.Equals(e.CreatedBy, userId, StringComparison.OrdinalIgnoreCase)
                    && e.CreatedAt >= since)
                .Count();
            if (mineToday >= MaxPublishesPerUserPerDay)
            {
                throw new InvalidOperationException(
                    $"Publish limit reached ({MaxPublishesPerUserPerDay} demos per 24 hours). Try again later.");
            }
        }
    }

    public DemoEntry PublishFromWip(
        string projectId,
        string title,
        string? description,
        string? createdBy,
        bool acceptedGuidelines,
        bool madeForKids = false,
        bool isAiSyntheticContent = true,
        string privacyStatus = "public",
        List<string>? tags = null)
    {
        var wip = _projects.ResolveWipMoviePath(projectId)
                  ?? throw new InvalidOperationException("WIP movie not found — build the cut first.");
        return PublishFromFile(
            wip, title, description, projectId, createdBy, acceptedGuidelines,
            madeForKids, isAiSyntheticContent, privacyStatus, tags);
    }

    public async Task<DemoEntry> PublishFromStreamAsync(
        Stream content,
        string title,
        string? description,
        string? projectId,
        string? createdBy,
        bool acceptedGuidelines,
        bool madeForKids = false,
        bool isAiSyntheticContent = true,
        string privacyStatus = "public",
        List<string>? tags = null,
        CancellationToken ct = default)
    {
        if (content is null || !content.CanRead)
            throw new InvalidOperationException("Empty upload");
        if (!acceptedGuidelines)
            throw new InvalidOperationException("You must accept the gallery guidelines to publish.");

        var id = GenerateId();
        var dir = Path.Combine(DemosDir, id);
        Directory.CreateDirectory(dir);
        var moviePath = Path.Combine(dir, "movie.mp4");
        try
        {
            await using (var fs = new FileStream(
                             moviePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithSizeCapAsync(content, fs, MaxUploadBytes, ct).ConfigureAwait(false);
            }

            var fi = new FileInfo(moviePath);
            if (fi.Length < MinUploadBytes)
                throw new InvalidOperationException("Uploaded video is too small");
            if (!LooksLikeMp4(moviePath))
                throw new InvalidOperationException(
                    "Upload is not a valid MP4 (missing ftyp box). Only MP4 video is accepted.");

            var entry = NewPendingEntry(
                id, title, description, projectId, createdBy, fi.Length, acceptedGuidelines,
                madeForKids, isAiSyntheticContent, privacyStatus, tags);
            await WriteMetaAsync(dir, entry, ct).ConfigureAwait(false);

            _log.LogInformation(
                "Demo {Id} published (awaiting YouTube) ({Bytes} bytes) project={Project} by={User}",
                id, entry.SizeBytes, projectId, createdBy);
            return entry;
        }
        catch
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* ignore */ }
            throw;
        }
    }

    public async Task<DemoEntry> PublishFromFileAsync(
        string sourceMp4Path,
        string title,
        string? description,
        string? projectId,
        string? createdBy,
        bool acceptedGuidelines,
        bool madeForKids = false,
        bool isAiSyntheticContent = true,
        string privacyStatus = "public",
        List<string>? tags = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceMp4Path) || !File.Exists(sourceMp4Path))
            throw new InvalidOperationException("Source movie not found");
        if (!acceptedGuidelines)
            throw new InvalidOperationException("You must accept the gallery guidelines to publish.");

        var id = GenerateId();
        var dir = Path.Combine(DemosDir, id);
        Directory.CreateDirectory(dir);
        var moviePath = Path.Combine(dir, "movie.mp4");
        try
        {
            File.Copy(sourceMp4Path, moviePath, overwrite: false);
            var fi = new FileInfo(moviePath);
            if (fi.Length < MinUploadBytes)
                throw new InvalidOperationException("Movie file is too small");
            if (!LooksLikeMp4(moviePath))
                throw new InvalidOperationException("Source file is not a valid MP4.");

            var entry = NewPendingEntry(
                id, title, description, projectId, createdBy, fi.Length, acceptedGuidelines,
                madeForKids, isAiSyntheticContent, privacyStatus, tags);
            await File.WriteAllTextAsync(
                Path.Combine(dir, "meta.json"),
                JsonSerializer.Serialize(entry, JsonOpts) + "\n",
                ct).ConfigureAwait(false);

            _log.LogInformation(
                "Demo {Id} published from file (awaiting YouTube) ({Bytes} bytes) project={Project} by={User}",
                id, entry.SizeBytes, projectId, createdBy);
            return entry;
        }
        catch
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* ignore */ }
            throw;
        }
    }

    public DemoEntry PublishFromFile(
        string sourceMp4Path,
        string title,
        string? description,
        string? projectId,
        string? createdBy,
        bool acceptedGuidelines,
        bool madeForKids = false,
        bool isAiSyntheticContent = true,
        string privacyStatus = "public",
        List<string>? tags = null) =>
        PublishFromFileAsync(sourceMp4Path, title, description, projectId, createdBy, acceptedGuidelines, madeForKids, isAiSyntheticContent, privacyStatus, tags).GetAwaiter().GetResult();

    public DemoEntry? Report(string id, string? note, string? reporterUserId)
    {
        lock (_lock)
        {
            var entry = ReadUnlocked(id);
            if (entry is null)
                return null;
            if (!string.Equals(entry.Status, DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
                return entry; // ignore reports on non-public

            entry.ReportCount = Math.Max(0, entry.ReportCount) + 1;
            var line = $"{DateTimeOffset.UtcNow:o} by {reporterUserId ?? "anon"}: "
                       + (string.IsNullOrWhiteSpace(note) ? "(no note)" : note.Trim());
            entry.ReportNotes ??= new List<string>();
            entry.ReportNotes.Add(line);
            while (entry.ReportNotes.Count > MaxReportNotes)
                entry.ReportNotes.RemoveAt(0);

            // Auto-hide after enough community reports (no admin approval queue).
            if (entry.ReportCount >= 3
                && string.Equals(entry.Status, DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
            {
                entry.Status = DemoStatuses.Removed;
                entry.ReviewNote = $"Auto-removed after {entry.ReportCount} reports";
                _log.LogWarning("Demo {Id} auto-removed after {N} reports", id, entry.ReportCount);
            }

            SaveUnlocked(entry);
            return entry;
        }
    }

    public DemoEntry? SetStatus(
        string id,
        string status,
        string? reviewerUserId,
        string? reviewNote)
    {
        if (!DemoStatuses.IsKnown(status))
            throw new InvalidOperationException($"Unknown status: {status}");

        lock (_lock)
        {
            var entry = ReadUnlocked(id);
            if (entry is null)
                return null;

            entry.Status = status.Trim().ToLowerInvariant();
            entry.ReviewedBy = reviewerUserId;
            entry.ReviewedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(reviewNote))
                entry.ReviewNote = reviewNote.Trim();

            SaveUnlocked(entry);
            _log.LogInformation(
                "Demo {Id} → {Status} by {Reviewer}",
                id, entry.Status, reviewerUserId);
            return entry;
        }
    }

    /// <summary>Hard-delete every demo created by <paramref name="userId"/> (admin cascade).</summary>
    public int HardDeleteAllByUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return 0;
        List<string> ids;
        lock (_lock)
        {
            ids = LoadAllUnlocked()
                .Where(e => string.Equals(e.CreatedBy, userId, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Id)
                .ToList();
        }
        var n = 0;
        foreach (var id in ids)
        {
            if (Delete(id, requesterUserId: null, isAdmin: true))
                n++;
        }
        return n;
    }

    public bool Delete(string id, string? requesterUserId, bool isAdmin)
    {
        var entry = TryGet(id);
        if (entry is null) return false;
        if (!isAdmin &&
            !string.Equals(entry.CreatedBy, requesterUserId, StringComparison.OrdinalIgnoreCase))
            return false;

        // Soft-delete for non-admin owner: mark removed so admin has audit trail optional.
        // Hard-delete for admin always; owner can hard-delete their own pending/rejected.
        if (isAdmin || !string.Equals(entry.Status, DemoStatuses.Public, StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                var dir = Path.Combine(DemosDir, id);
                if (!Directory.Exists(dir)) return false;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    return true;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete demo {Id}", id);
                    return false;
                }
            }
        }

        // Public demos: owner request → removed (hidden), admin can hard-delete later.
        return SetStatus(id, DemoStatuses.Removed, requesterUserId, "Removed by publisher") is not null;
    }

    public static bool LooksLikeMp4(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 12)
                return false;
            Span<byte> header = stackalloc byte[12];
            var n = fs.Read(header);
            if (n < 12)
                return false;
            return header[4] == (byte)'f'
                   && header[5] == (byte)'t'
                   && header[6] == (byte)'y'
                   && header[7] == (byte)'p';
        }
        catch
        {
            return false;
        }
    }

    private DemoEntry NewPendingEntry(
        string id,
        string title,
        string? description,
        string? projectId,
        string? createdBy,
        long sizeBytes,
        bool acceptedGuidelines,
        bool madeForKids = false,
        bool isAiSyntheticContent = true,
        string privacyStatus = "public",
        List<string>? tags = null) =>
        new()
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? (projectId ?? "Demo") : title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            ProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim(),
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            SizeBytes = sizeBytes,
            ContentType = "video/mp4",
            // Public metadata immediately; gallery lists only after YouTube id is set.
            Status = DemoStatuses.Public,
            AcceptedGuidelines = acceptedGuidelines,
            MadeForKids = madeForKids,
            IsAiSyntheticContent = isAiSyntheticContent,
            PrivacyStatus = privacyStatus is "public" or "unlisted" or "private" ? privacyStatus : "public",
            Tags = tags is { Count: > 0 } ? tags : null,
        };

    /// <summary>
    /// Record a YouTube upload attempt/result. On success, deletes the local movie.mp4 (server
    /// footprint goal) — the entry stays valid without it once <see cref="DemoEntry.YoutubeId"/> is set.
    /// </summary>
    public DemoEntry? SetYouTubeUploadStatus(
        string id,
        string status,
        string? youtubeId = null,
        string? youtubeUrl = null,
        string? error = null)
    {
        lock (_lock)
        {
            var entry = ReadUnlocked(id);
            if (entry is null)
                return null;

            entry.YoutubeUploadStatus = status;
            entry.YoutubeUploadError = error;
            if (!string.IsNullOrWhiteSpace(youtubeId))
                entry.YoutubeId = youtubeId;
            if (!string.IsNullOrWhiteSpace(youtubeUrl))
                entry.YoutubeUrl = youtubeUrl;
            // YouTube success is the gate for the public wall — mark public, clear errors.
            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.YoutubeId))
            {
                entry.Status = DemoStatuses.Public;
                entry.YoutubeUploadError = null;
            }

            SaveUnlocked(entry);

            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var moviePath = Path.Combine(DemosDir, entry.Id, "movie.mp4");
                    if (File.Exists(moviePath))
                    {
                        File.Delete(moviePath);
                        _log.LogInformation(
                            "Demo {Id} moved to YouTube ({YoutubeId}); deleted local movie.mp4.",
                            entry.Id, youtubeId);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete local movie.mp4 for demo {Id} after YouTube upload.", entry.Id);
                }
            }

            return entry;
        }
    }

    /// <summary>
    /// Admin: put an existing YouTube video on the public gallery without uploading a local MP4.
    /// YouTube remains the source of truth for playback.
    /// </summary>
    public DemoEntry RegisterFromYouTube(
        string youtubeIdOrUrl,
        string title,
        string? description,
        string? createdBy,
        string? projectId = null,
        bool fromChannel = false)
    {
        var ytId = ExtractYouTubeVideoId(youtubeIdOrUrl)
            ?? throw new InvalidOperationException(
                "Could not parse a YouTube video id. Paste a watch URL, youtu.be link, or 11-char id.");
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Title is required.");

        lock (_lock)
        {
            // Match by YouTube id — channel is playback SoT; studio fields (category/tags/project) stay local.
            var existing = LoadAllUnlocked()
                .FirstOrDefault(e =>
                    string.Equals(e.YoutubeId, ytId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // Title/description always from YouTube when channel-syncing (or when provided).
                existing.Title = title.Trim();
                if (fromChannel || !string.IsNullOrWhiteSpace(description))
                    existing.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
                existing.Status = DemoStatuses.Public;
                existing.YoutubeUploadStatus = "done";
                existing.YoutubeUploadError = null;
                existing.YoutubeUrl = $"https://www.youtube.com/watch?v={ytId}";
                // ProjectId / Category / Tags / CreatedBy are studio-owned — only set if empty or explicit projectId
                if (!string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(existing.ProjectId))
                    existing.ProjectId = projectId.Trim();
                if (string.IsNullOrWhiteSpace(existing.CreatedBy) && !string.IsNullOrWhiteSpace(createdBy))
                    existing.CreatedBy = createdBy;
                SaveUnlocked(existing);
                _log.LogInformation("Demo {Id} matched YouTube {Yt} (title refreshed from channel)", existing.Id, ytId);
                return existing;
            }

            var id = GenerateId();
            var entry = NewPendingEntry(
                id,
                title.Trim(),
                description,
                projectId,
                createdBy,
                sizeBytes: 0,
                acceptedGuidelines: true);
            entry.YoutubeId = ytId;
            entry.YoutubeUrl = $"https://www.youtube.com/watch?v={ytId}";
            entry.YoutubeUploadStatus = "done";
            entry.Status = DemoStatuses.Public;
            SaveUnlocked(entry);
            _log.LogInformation(
                "Demo {Id} registered from YouTube {Yt} title={Title} by={User}",
                id, ytId, entry.Title, createdBy);
            return entry;
        }
    }

    /// <summary>
    /// After a channel sync: hide public demos whose YouTube id is no longer on the channel
    /// (stale manual entries / renamed ghosts). Studio category/tags kept if they return later.
    /// </summary>
    /// <summary>
    /// Undo accidental gallery wipe when sync listed 0 videos (channel-hidden only).
    /// </summary>
    public int RestoreChannelHiddenDemos()
    {
        var restored = 0;
        lock (_lock)
        {
            foreach (var e in LoadAllUnlocked())
            {
                if (!string.Equals(e.Status, DemoStatuses.Removed, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(e.YoutubeId))
                    continue;
                var note = e.ReviewNote ?? "";
                if (!note.Contains("not on connected", StringComparison.OrdinalIgnoreCase)
                    && !note.Contains("Hidden: video not on", StringComparison.OrdinalIgnoreCase))
                    continue;
                e.Status = DemoStatuses.Public;
                e.ReviewNote = null;
                e.YoutubeUploadStatus = "done";
                SaveUnlocked(e);
                restored++;
            }
        }
        if (restored > 0)
            _log.LogInformation("Restored {N} gallery demos previously hidden by empty channel sync", restored);
        return restored;
    }

    /// <param name="listIsAuthoritative">
    /// True only when YouTube returned a successful list (including empty). False on glitch/error —
    /// never hide in that case. A successful empty list means the channel has no videos; hide all
    /// gallery rows that pointed at channel ids no longer present.
    /// </param>
    public int HideDemosNotOnChannel(
        IReadOnlyCollection<string> channelYoutubeIds,
        bool listIsAuthoritative = true)
    {
        if (!listIsAuthoritative)
        {
            _log.LogWarning("Skip hide-not-on-channel: channel list was not authoritative (glitch/error)");
            return 0;
        }

        var set = new HashSet<string>(
            (channelYoutubeIds ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);
        // set.Count == 0 is valid: last video deleted from channel → hide remaining gallery rows.
        var hidden = 0;
        lock (_lock)
        {
            foreach (var e in LoadAllUnlocked())
            {
                if (string.IsNullOrWhiteSpace(e.YoutubeId))
                    continue;
                if (string.Equals(e.Status, DemoStatuses.Removed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Status, DemoStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (set.Contains(e.YoutubeId))
                    continue;
                e.Status = DemoStatuses.Removed;
                e.ReviewNote = "Hidden: video not on connected Page to Movie channel";
                SaveUnlocked(e);
                hidden++;
            }
        }
        if (hidden > 0)
            _log.LogInformation("Hid {N} gallery demos not present on YouTube channel", hidden);
        return hidden;
    }

    /// <summary>
    /// Upsert public gallery entries for every channel upload. Returns counts.
    /// Skips videos already removed/rejected intentionally? — re-public if still on channel.
    /// </summary>
    public (int Added, int Updated, int Total) SyncFromChannelUploads(
        IReadOnlyList<YouTubeAuthService.ChannelUploadVideo> videos,
        string? createdBy = "youtube-channel")
    {
        if (videos is null || videos.Count == 0)
            return (0, 0, 0);

        var added = 0;
        var updated = 0;
        foreach (var v in videos)
        {
            if (string.IsNullOrWhiteSpace(v.VideoId))
                continue;
            bool existed;
            lock (_lock)
            {
                existed = LoadAllUnlocked().Any(e =>
                    string.Equals(e.YoutubeId, v.VideoId, StringComparison.OrdinalIgnoreCase));
            }
            RegisterFromYouTube(
                v.VideoId,
                v.Title,
                v.Description,
                createdBy,
                fromChannel: true);
            if (existed) updated++;
            else added++;
        }
        return (added, updated, added + updated);
    }

    /// <summary>Parse watch / youtu.be / embed / raw 11-char ids.</summary>
    public static string? ExtractYouTubeVideoId(string? input) =>
        PageToMovie.Core.Util.YouTubeVideoId.Extract(input);


    /// <summary>
    /// One-time self-heal for legacy demo records that stored an <b>email</b> in
    /// <see cref="DemoEntry.CreatedBy"/>. CreatedBy is a stable ownership id (never an email under
    /// current rules) and doubles as the public byline handle, so an email there both mislabels the
    /// creator and can break ownership checks once ids were normalized away from the email.
    /// For each demo whose CreatedBy looks like an email, <paramref name="resolveCanonicalIdByEmail"/>
    /// maps it to that account's canonical id; a non-empty, non-email, changed result is written back.
    /// Idempotent: a CreatedBy without '@' is left untouched, and unresolved/unchanged values are
    /// skipped. Returns the number of records rewritten. Generic on purpose — any user's stale email
    /// record is healed, nothing story- or account-specific is hardcoded.
    /// </summary>
    public async Task<int> MigrateEmailCreatedByAsync(Func<string, string?> resolveCanonicalIdByEmail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolveCanonicalIdByEmail);
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(DemosDir)) return 0;
            var fixedCount = 0;
            foreach (var dir in Directory.EnumerateDirectories(DemosDir))
            {
                var metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    var json = await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false);
                    var entry = JsonSerializer.Deserialize<DemoEntry>(json, JsonOpts);
                    var oldBy = entry?.CreatedBy?.Trim();
                    if (entry is null || string.IsNullOrEmpty(oldBy) || !oldBy.Contains('@'))
                        continue;

                    var canonical = resolveCanonicalIdByEmail(oldBy)?.Trim();
                    if (string.IsNullOrEmpty(canonical) ||
                        canonical.Contains('@') ||
                        string.Equals(canonical, oldBy, StringComparison.OrdinalIgnoreCase))
                        continue; // unresolved, still an email, or unchanged — leave as-is

                    entry.CreatedBy = canonical;
                    await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(entry, JsonOpts) + "\n", ct).ConfigureAwait(false);
                    fixedCount++;
                    _log.LogInformation(
                        "Demo {Id}: migrated CreatedBy from an email to canonical id {New}",
                        Path.GetFileName(dir), canonical);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skip demo dir during CreatedBy migration {Dir}", dir);
                }
            }
            return fixedCount;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public int MigrateEmailCreatedBy(Func<string, string?> resolveCanonicalIdByEmail) =>
        MigrateEmailCreatedByAsync(resolveCanonicalIdByEmail).GetAwaiter().GetResult();

    private List<DemoEntry> LoadAllUnlocked()
    {
        if (!Directory.Exists(DemosDir))
            return new List<DemoEntry>();

        var list = new List<DemoEntry>();
        foreach (var dir in Directory.EnumerateDirectories(DemosDir))
        {
            try
            {
                var id = Path.GetFileName(dir);
                var entry = ReadUnlocked(id);
                if (entry is not null)
                    list.Add(entry);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skip bad demo dir {Dir}", dir);
            }
        }
        return list;
    }

    private async Task<DemoEntry?> ReadUnlockedAsync(string id, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return null;
        var metaPath = Path.Combine(DemosDir, id, "meta.json");
        var moviePath = Path.Combine(DemosDir, id, "movie.mp4");
        if (!File.Exists(metaPath))
            return null;
        try
        {
            var json = await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<DemoEntry>(json, JsonOpts);
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
                return null;
            entry.Status = DemoStatuses.Normalize(
                string.IsNullOrWhiteSpace(entry.Status) ? null : entry.Status);
            var hasLocalMovie = File.Exists(moviePath);
            // Valid without a local file only once it has moved to YouTube; otherwise the movie
            // is genuinely missing (corrupt/partial write) and the entry should not surface.
            if (!hasLocalMovie && string.IsNullOrWhiteSpace(entry.YoutubeId))
                return null;
            if (hasLocalMovie && entry.SizeBytes <= 0)
            {
                try { entry.SizeBytes = new FileInfo(moviePath).Length; }
                catch { /* ignore */ }
            }
            entry.ReportNotes ??= new List<string>();
            return entry;
        }
        catch
        {
            return null;
        }
    }

    private DemoEntry? ReadUnlocked(string id) =>
        ReadUnlockedAsync(id).GetAwaiter().GetResult();

    private async Task SaveUnlockedAsync(DemoEntry entry, CancellationToken ct = default)
    {
        var dir = Path.Combine(DemosDir, entry.Id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "meta.json"),
            JsonSerializer.Serialize(entry, JsonOpts) + "\n",
            ct).ConfigureAwait(false);
    }

    private void SaveUnlocked(DemoEntry entry) =>
        SaveUnlockedAsync(entry).GetAwaiter().GetResult();

    private static async Task WriteMetaAsync(string dir, DemoEntry entry, CancellationToken ct) =>
        await File.WriteAllTextAsync(
                Path.Combine(dir, "meta.json"),
                JsonSerializer.Serialize(entry, JsonOpts) + "\n",
                ct)
            .ConfigureAwait(false);

    private static async Task CopyWithSizeCapAsync(
        Stream source,
        Stream dest,
        long maxBytes,
        CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
            if (total > maxBytes)
                throw new InvalidOperationException(
                    $"Upload exceeds size limit ({maxBytes:N0} bytes).");
            await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length is >= 8 and <= 40
        && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static string GenerateId()
    {
        var bytes = RandomNumberGenerator.GetBytes(12);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
