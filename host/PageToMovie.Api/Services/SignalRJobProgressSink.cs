using System.Collections.Concurrent;
using PageToMovie.Api.Hubs;
using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Microsoft.AspNetCore.SignalR;
// JobHubEvents lives in PageToMovie.Core.Models

namespace PageToMovie.Api.Services;

public sealed class SignalRJobProgressSink : IJobProgressSink
{
    private static readonly TimeSpan MinUpdateInterval = TimeSpan.FromMilliseconds(250);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastBroadcast = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHubContext<JobHub> _hub;

    public SignalRJobProgressSink(IHubContext<JobHub> hub) => _hub = hub;

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
            await _hub.Clients.Group($"user:{snapshot.UserId}")
                .SendAsync(JobHubEvents.JobUpdated, snapshot, ct);
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
