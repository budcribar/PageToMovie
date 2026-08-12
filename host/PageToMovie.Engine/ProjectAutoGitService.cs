using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;

namespace PageToMovie.Engine;

/// <summary>
/// Background service that debounces project file mutations and automatically commits &amp; pushes to Git/GitHub.
/// Runs non-blocking on background threads without interrupting operator UI workflows.
/// </summary>
public sealed class ProjectAutoGitService
{
    private readonly ProjectGitRepositoryService _gitRepo;
    private readonly ILogger<ProjectAutoGitService> _log;
    private readonly ConcurrentDictionary<string, (string Message, string Author, DateTime ScheduledAt)> _pendingQueue = new(StringComparer.OrdinalIgnoreCase);

    public ProjectAutoGitService(
        ProjectGitRepositoryService gitRepo,
        IOptions<PageToMovieOptions> opts,
        ILogger<ProjectAutoGitService>? log = null)
    {
        _gitRepo = gitRepo;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectAutoGitService>.Instance;
    }

    /// <summary>
    /// Schedule an auto-commit &amp; push for a project. Debounced by 4 seconds.
    /// </summary>
    public void QueueCommitAndPush(string projectDir, string projectId, string message, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(projectId)) return;
        if (!Directory.Exists(projectDir)) return;

        var who = string.IsNullOrWhiteSpace(author) ? "PageToMovie" : author.Trim();
        var key = $"{projectDir}|{projectId}";
        var scheduledAt = DateTime.UtcNow.AddSeconds(4);

        _pendingQueue[key] = (message, who, scheduledAt);

        _ = Task.Run(async () =>
        {
            await Task.Delay(4200).ConfigureAwait(false);
            await ProcessQueueItemAsync(key).ConfigureAwait(false);
        });
    }

    private async Task ProcessQueueItemAsync(string key)
    {
        if (!_pendingQueue.TryRemove(key, out var item)) return;

        var parts = key.Split('|', 2);
        if (parts.Length < 2) return;
        var projectPath = parts[0];
        var projectId = parts[1];

        if (!Directory.Exists(projectPath)) return;

        // Guard: never nest a project git inside the app repo (local sample projects).
        if (!ProjectGitRepositoryService.TryEnsureRepository(projectPath, out var skip))
        {
            _log.LogDebug("Auto-Git skipped for {Id}: {Reason}", projectId, skip);
            return;
        }

        try
        {
            var commitResult = await _gitRepo.CommitProjectStateAsync(projectPath, item.Author, item.Message).ConfigureAwait(false);
            _log.LogInformation("Auto-Git commit for {Id} @ {Hash}: {Msg}", projectId, commitResult.CommitHash, item.Message);

            // Push to remote if Git configuration is active
            var pushResult = await _gitRepo.PushProjectAsync(projectPath, projectId).ConfigureAwait(false);
            if (pushResult.Success)
            {
                _log.LogInformation("Auto-Git pushed {Id} to GitHub: {Msg}", projectId, pushResult.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-Git commit/push failed for {Id}", projectId);
        }
    }
}
