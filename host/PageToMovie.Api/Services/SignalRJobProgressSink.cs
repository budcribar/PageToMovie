using System.Collections.Concurrent;
using PageToMovie.Api.Hubs;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
// JobHubEvents lives in PageToMovie.Core.Models

namespace PageToMovie.Api.Services;

public sealed class SignalRJobProgressSink : IJobProgressSink
{
    private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMilliseconds(250);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastBroadcast = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHubContext<JobHub> _hub;
    private readonly HubGroupRegistry _groups;
    private readonly ILogger<SignalRJobProgressSink> _log;

    public SignalRJobProgressSink(
        IHubContext<JobHub> hub, HubGroupRegistry groups, ILogger<SignalRJobProgressSink> log)
    {
        _hub = hub;
        _groups = groups;
        _log = log;
    }

    public async Task OnJobUpdatedAsync(JobSnapshot snapshot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshot.JobId))
            return;

        var status = (snapshot.Status ?? "").ToLowerInvariant();
        var isTerminal = status is "done" or "failed" or "error";

        if (!isTerminal)
        {
            if (ShouldThrottleNonTerminal(snapshot.JobId))
                return;
        }
        else
        {
            _lastBroadcast.TryRemove(snapshot.JobId, out _);
        }

        // Multi-user: only job + owner groups (clients join user:{id} on connect).
        await _hub.Clients.Group($"job:{snapshot.JobId}")
            .SendAsync(JobHubEvents.JobUpdated, snapshot, ct);

        if (!string.IsNullOrWhiteSpace(snapshot.UserId))
        {
            await _hub.Clients.Group($"user:{snapshot.UserId}")
                .SendAsync(JobHubEvents.JobUpdated, snapshot, ct);
            WarnIfNobodyIsListening(snapshot, isTerminal);
        }
    }

    /// <summary>
    /// SendAsync to a group with no members succeeds and delivers nothing, so an undeliverable
    /// job looks exactly like a healthy one in the server log. Say it out loud instead.
    /// </summary>
    /// <remarks>
    /// Only the terminal update is worth warning about: a browser can legitimately be closed
    /// mid-run, but a finished job whose owner has no connection on this instance means the
    /// client never learned the clip exists. That is not cosmetic — ClientMediaFolderService
    /// saves generated media on JobUpdated and nowhere else, and the API host drops its own copy
    /// once ClientMediaUrl is published, so the bytes are simply lost.
    ///
    /// Two causes produce it. If <c>live groups</c> names other users but not this one, the
    /// browser's socket is on a different instance — SignalR here has no backplane, so a
    /// broadcast never crosses instances. If it names a group that looks like this user's under
    /// different casing or shape, the client joined under an id that does not match the one
    /// stamped on the job record (group matching is ordinal).
    /// </remarks>
    private void WarnIfNobodyIsListening(JobSnapshot snapshot, bool isTerminal)
    {
        if (!isTerminal || _groups.Count(snapshot.UserId) > 0)
            return;

        _groups.Note(
            "undelivered",
            $"job {snapshot.JobId} ({snapshot.Kind}) finished {snapshot.Status} for user:{snapshot.UserId}; " +
            $"clientMediaUrl={(string.IsNullOrWhiteSpace(snapshot.ClientMediaUrl) ? "no" : "yes")}");
        _log.LogWarning(
            "Job {JobId} ({Kind}) finished as {Status} but user:{UserId} has no live hub connection " +
            "on this instance, so the client was never told. Live groups: {Groups}. " +
            "ClientMediaUrl={HasMedia} — generated media is not saved without a JobUpdated.",
            snapshot.JobId, snapshot.Kind, snapshot.Status, snapshot.UserId, _groups.Describe(),
            !string.IsNullOrWhiteSpace(snapshot.ClientMediaUrl));
    }

    private bool ShouldThrottleNonTerminal(string jobId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastBroadcast.TryGetValue(jobId, out var last) && (now - last) < MinUpdateInterval)
        {
            // Throttled non-terminal update
            return true;
        }

        _lastBroadcast[jobId] = now;
        if (_lastBroadcast.Count > 1000)
            PruneStaleBroadcasts(now);

        return false;
    }

    private void PruneStaleBroadcasts(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-30);
        foreach (var kvp in _lastBroadcast)
            if (kvp.Value < cutoff)
                _lastBroadcast.TryRemove(kvp.Key, out _);
    }

    public async Task OnJobLogAsync(string message, CancellationToken ct = default)
    {
        // Progress text also arrives via JobUpdated.Message on user/job groups.
        // JobLog is optional detail; avoid Clients.All for multi-user isolation.
        await Task.CompletedTask;
        _ = message;
    }
}
