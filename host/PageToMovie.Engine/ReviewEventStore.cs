using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Host-level append-only review/learning events (<c>{WorkspaceRoot}/_learning/review_events.jsonl</c>).
/// Project edit log remains the per-project audit; this store powers admin insights across films.
/// </summary>
public sealed class ReviewEventStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const string ClipFailType = "clip_fail";

    private readonly ProjectStore _projects;
    private readonly ILogger<ReviewEventStore> _log;

    public ReviewEventStore(ProjectStore projects, ILogger<ReviewEventStore> log)
    {
        _projects = projects;
        _log = log;
    }

    public string LearningDir =>
        Path.Combine(_projects.WorkspaceRoot, "_learning");

    public string EventsPath =>
        Path.Combine(LearningDir, "review_events.jsonl");

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<ReviewLearningEvent> AppendAsync(ReviewLearningEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (string.IsNullOrWhiteSpace(ev.Id))
            ev.Id = Guid.NewGuid().ToString("N")[..12];
        if (ev.Ts == default)
            ev.Ts = DateTimeOffset.UtcNow;
        ev.Note ??= "";
        ev.Type ??= "";
        ev.ProjectId ??= "";

        try
        {
            await JsonlStore.AppendAsync(EventsPath, ev, JsonOpts, _writeGate, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to append learning event {Type} for {Project}",
                ev.Type, ev.ProjectId);
        }

        return ev;
    }

    public Task<ReviewLearningEvent> AppendFromEditLogAsync(
        string projectId,
        EditLogEntry entry,
        string? userId = null,
        string? category = null,
        string? suggestion = null,
        string? confidence = null,
        string? continuity = null,
        int? suggestionCount = null,
        string? field = null,
        string? jobId = null,
        string? outcome = null,
        CancellationToken ct = default)
    {
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(entry.Ts) &&
            DateTimeOffset.TryParse(entry.Ts, out var parsed))
            ts = parsed.ToUniversalTime();

        return AppendAsync(new ReviewLearningEvent
        {
            Id = entry.Id,
            Ts = ts,
            ProjectId = projectId,
            UserId = userId,
            Type = entry.Type,
            Scene = entry.Scene,
            Clip = entry.Clip,
            Character = entry.Character,
            Note = entry.UserNote,
            ActionTaken = entry.ActionTaken,
            Before = entry.Before,
            After = entry.After,
            LearningLayer = entry.LearningLayer,
            Category = category,
            Suggestion = suggestion,
            Confidence = confidence,
            Continuity = continuity,
            SuggestionCount = suggestionCount,
            Field = field,
            JobId = jobId,
            Outcome = outcome,
        }, ct);
    }

    /// <summary>
    /// Apply the optional projectId/type/category/from/to filters, order newest-first, and take
    /// at most <paramref name="take"/>. Shared by <see cref="QueryAsync"/>.
    /// </summary>
    private static List<ReviewLearningEvent> FilterOrderTake(
        IEnumerable<ReviewLearningEvent> all,
        string? projectId,
        string? type,
        string? category,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int take)
    {
        IEnumerable<ReviewLearningEvent> q = all;
        if (!string.IsNullOrWhiteSpace(projectId))
            q = q.Where(e => string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(type))
            q = q.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));
        if (from is { } f)
            q = q.Where(e => e.Ts >= f);
        if (to is { } t)
            q = q.Where(e => e.Ts <= t);
        return q.OrderByDescending(e => e.Ts).Take(take).ToList();
    }

    public async Task<LearningInsightsDto> BuildInsightsAsync(
        string? projectId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int recentTake = 40,
        CancellationToken ct = default)
    {
        var events = await QueryAsync(projectId, from: from, to: to, take: 5000, ct: ct).ConfigureAwait(false);
        var dto = new LearningInsightsDto
        {
            EventCount = events.Count,
            From = from ?? events.LastOrDefault()?.Ts,
            To = to ?? events.FirstOrDefault()?.Ts,
            Recent = events.Take(Math.Clamp(recentTake <= 0 ? 40 : recentTake, 1, 200)).ToList(),
        };

        foreach (var e in events)
        {
            Bump(dto.ByType, e.Type);
            if (!string.IsNullOrWhiteSpace(e.Category))
                Bump(dto.ByCategory, e.Category!);

            if (string.Equals(e.Type, ClipFailType, StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase)))
            {
                dto.HumanFail += string.Equals(e.Type, ClipFailType, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                var cat = string.IsNullOrWhiteSpace(e.Category) ? "other" : e.Category!;
                if (string.Equals(e.Type, ClipFailType, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase))
                    Bump(dto.FailByCategory, cat);
            }

            if (string.Equals(e.Type, "clip_pass", StringComparison.OrdinalIgnoreCase))
                dto.HumanPass++;
            if (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase))
                dto.AutoReview++;
            if (string.Equals(e.Type, "auto_review_apply", StringComparison.OrdinalIgnoreCase))
                dto.ApplyCount++;
            if (string.Equals(e.Type, "regen_after_review", StringComparison.OrdinalIgnoreCase))
                dto.RegenCount++;
        }

        // auto_review fails counted above only when suggestion=fail; ensure human fail tally correct
        dto.HumanFail = events.Count(e =>
            string.Equals(e.Type, ClipFailType, StringComparison.OrdinalIgnoreCase));

        return dto;
    }

    public async Task<IReadOnlyList<ReviewLearningEvent>> ReadAllAsync(CancellationToken ct = default)
    {
        var path = EventsPath;
        if (!File.Exists(path))
            return Array.Empty<ReviewLearningEvent>();

        var list = new List<ReviewLearningEvent>();
        try
        {
            string[] lines;
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
            ParseEventLines(lines, list);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed reading learning events");
        }

        return list;
    }

    /// <summary>
    /// Deserialize one JSON event per non-blank line into <paramref name="list"/>, silently
    /// skipping any malformed line. Shared by <see cref="ReadAllAsync"/>.
    /// </summary>
    private static void ParseEventLines(string[] lines, List<ReviewLearningEvent> list)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var ev = JsonSerializer.Deserialize<ReviewLearningEvent>(line, JsonOpts);
                if (ev is not null) list.Add(ev);
            }
            catch
            {
                /* skip bad line */
            }
        }
    }

    public async Task<IReadOnlyList<ReviewLearningEvent>> QueryAsync(
        string? projectId = null,
        string? type = null,
        string? category = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int take = 200,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 5000);
        var all = await ReadAllAsync(ct).ConfigureAwait(false);
        return FilterOrderTake(all, projectId, type, category, from, to, take);
    }

    private static void Bump(Dictionary<string, int> map, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) key = "unknown";
        map.TryGetValue(key, out var n);
        map[key] = n + 1;
    }

    public async Task<ReviewComparisonInsightsDto> GetReviewComparisonAsync(string? projectId = null, CancellationToken ct = default)
    {
        var events = await ReadAllAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            events = events.Where(e => string.Equals(e.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var items = new List<HumanVsAiComparisonItem>();
        var groups = events
            .Where(e => e.Scene.HasValue && e.Clip.HasValue)
            .GroupBy(e => (e.ProjectId, Scene: e.Scene!.Value, Clip: e.Clip!.Value));

        foreach (var g in groups)
        {
            var humanEv = g.FirstOrDefault(e => string.Equals(e.Type, "clip_pass", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(e.Type, ClipFailType, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(e.Type, "scene_approve", StringComparison.OrdinalIgnoreCase));
            var aiEv = g.FirstOrDefault(e => string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(e.Type, "auto_review_apply", StringComparison.OrdinalIgnoreCase));

            if (humanEv is not null && aiEv is not null)
            {
                var humanPass = string.Equals(humanEv.Type, "clip_pass", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(humanEv.Type, "scene_approve", StringComparison.OrdinalIgnoreCase);
                var aiPass = string.Equals(aiEv.Suggestion, "pass", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(aiEv.Outcome, "pass", StringComparison.OrdinalIgnoreCase) ||
                             (!string.IsNullOrEmpty(aiEv.Note) && aiEv.Note.Contains("Pass", StringComparison.OrdinalIgnoreCase));

                var discType = (humanPass, aiPass) switch
                {
                    (false, true) => "AI_TOO_PERMISSIVE", // Human Fail, AI Pass
                    (true, false) => "AI_TOO_STRICT",     // Human Pass, AI Fail
                    _ => "AGREEMENT"
                };

                items.Add(new HumanVsAiComparisonItem
                {
                    ProjectId = g.Key.ProjectId,
                    SceneNumber = g.Key.Scene,
                    ClipNumber = g.Key.Clip,
                    HumanVerdict = humanPass ? "pass" : "fail",
                    Note = humanEv.Note ?? "",
                    AiVerdict = aiPass ? "pass" : "fail",
                    AiScore = int.TryParse(aiEv.Confidence, out var sc) ? sc : (aiPass ? 8 : 4),
                    AiReasoning = aiEv.Note ?? "",
                    DiscrepancyType = discType,
                    Ts = humanEv.Ts > aiEv.Ts ? humanEv.Ts : aiEv.Ts,
                });
            }
        }

        var total = items.Count;
        var agree = items.Count(x => x.DiscrepancyType == "AGREEMENT");
        var permissive = items.Count(x => x.DiscrepancyType == "AI_TOO_PERMISSIVE");
        var strict = items.Count(x => x.DiscrepancyType == "AI_TOO_STRICT");
        var pct = total > 0 ? Math.Round((agree * 100.0) / total, 1) : 100.0;

        return new ReviewComparisonInsightsDto
        {
            Ok = true,
            TotalCompared = total,
            AgreementCount = agree,
            AiTooPermissiveCount = permissive,
            AiTooStrictCount = strict,
            AgreementPercentage = pct,
            Discrepancies = items.OrderByDescending(x => x.Ts).ToList(),
        };
    }
}
