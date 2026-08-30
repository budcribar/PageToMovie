using System.Text.Json;
using PageToMovie.Core.Models;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Per-project house rules (<c>project_rules.json</c>) + pending suggestions from repeated fail categories.
/// </summary>
public sealed class ProjectRulesService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Minimum fails in one category before auto-suggest.</summary>
    public const int DefaultMinFailsForSuggest = 3;

    private const string CategoryOther = "other";
    private const string CategoryStyle = "style";
    private const string CategoryPerformance = "performance";
    private const string ApproverCastExtract = "cast_extract";
    private const string ApproverSystem = "system";

    private readonly ProjectStore _projects;
    private readonly ReviewEventStore _learning;
    private readonly ILogger<ProjectRulesService> _log;

    public ProjectRulesService(
        ProjectStore projects,
        ReviewEventStore learning,
        ILogger<ProjectRulesService> log)
    {
        _projects = projects;
        _learning = learning;
        _log = log;
    }

    public async Task<string> RulesPathAsync(string projectId, CancellationToken ct = default) =>
        Path.Combine(await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false), "project_rules.json");

    public string RulesPath(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "project_rules.json");

    public async Task<ProjectRulesDocument> LoadAsync(string projectId, CancellationToken ct = default)
    {
        var path = await RulesPathAsync(projectId, ct).ConfigureAwait(false);
        if (!File.Exists(path))
            return new ProjectRulesDocument();
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ProjectRulesDocument>(text, JsonOpts)
                   ?? new ProjectRulesDocument();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed loading project rules for {Project}", projectId);
            return new ProjectRulesDocument();
        }
    }

    public async Task SaveAsync(string projectId, ProjectRulesDocument doc, CancellationToken ct = default)
    {
        var path = await RulesPathAsync(projectId, ct).ConfigureAwait(false);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc, JsonOpts) + "\n", ct).ConfigureAwait(false);
    }

    /// <summary>Stable id for auto style rule written from cast extract / render_style_lock.</summary>
    public const string StyleRuleId = "style_from_cast";
    /// <summary>Stable id for auto performance/address rule from cast extract.</summary>
    public const string PerformanceRuleId = "performance_from_cast";

    /// <summary>Active rules as text block for prompt injection.</summary>
    public async Task<string> GetActiveRulesBlockAsync(string projectId, CancellationToken ct = default)
    {
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        var lines = doc.Active
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Where(r => !IsFilmPerformanceLockRule(r))
            .Select(r => $"- [{(string.IsNullOrWhiteSpace(r.Category) ? CategoryOther : r.Category.Trim())}] {r.Text.Trim()}")
            .ToList();

        // Film-level STYLE LOCK is ProjectVisionMeta — do not inject cast_seeds render_style_lock.
        // Film-level PERFORMANCE LOCK is vision_meta — do not inject cast_seeds performance_lock.

        if (lines.Count == 0) return "";
        return "PROJECT HOUSE RULES (approved):\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// Upsert style rule from cast extract <c>render_style_lock</c> (derived from Fountain SoT).
    /// Does not overwrite a user-approved style rule (different id / non-system approver).
    /// </summary>
    public Task<bool> EnsureStyleRuleFromRenderLockAsync(
        string projectId,
        string? renderStyleLock,
        string approvedBy = ApproverCastExtract,
        CancellationToken ct = default) =>
        EnsureSystemRuleFromLockAsync(
            projectId, renderStyleLock, NormalizeStyleRuleText,
            StyleRuleId, CategoryStyle, approvedBy, ct);

    public static string NormalizeStyleRuleText(string? renderStyleLock) =>
        NormalizeLockRuleText(
            renderStyleLock,
            maxTokens: 150,
            prefixWhenMissing: "Hold this film’s render medium consistently: ",
            "STYLE", "picture", "photoreal", "live-action", "CGI", "animated");

    /// <summary>
    /// Upsert performance/address convention from cast extract (book-inferred, not a fixed eye recipe).
    /// </summary>
    public Task<bool> EnsurePerformanceRuleFromLockAsync(
        string projectId,
        string? performanceLock,
        string approvedBy = ApproverCastExtract,
        CancellationToken ct = default) =>
        EnsureSystemRuleFromLockAsync(
            projectId, performanceLock, NormalizePerformanceRuleText,
            PerformanceRuleId, CategoryPerformance, approvedBy, ct);

    public static string NormalizePerformanceRuleText(string? performanceLock) =>
        NormalizeLockRuleText(
            performanceLock,
            maxTokens: 175,
            prefixWhenMissing: "PERFORMANCE LOCK: ",
            "PERFORMANCE", "address", "viewer", "camera", "confessional", "observ");

    /// <summary>
    /// Film-level address lock written from cast extract. Generate reads
    /// <c>vision_meta.performance_lock</c> instead — do not inject this as a second writer.
    /// </summary>
    private static bool IsFilmPerformanceLockRule(ProjectRule r) =>
        string.Equals(r.Id, PerformanceRuleId, StringComparison.OrdinalIgnoreCase) ||
        (r.Text?.TrimStart().StartsWith("PERFORMANCE LOCK", StringComparison.OrdinalIgnoreCase) ?? false);

    private async Task<bool> EnsureSystemRuleFromLockAsync(
        string projectId,
        string? rawLock,
        Func<string?, string> normalize,
        string ruleId,
        string category,
        string approvedBy,
        CancellationToken ct)
    {
        var text = normalize(rawLock);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        var systemOwned = doc.Active.FirstOrDefault(r =>
            string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (systemOwned is not null)
        {
            if (string.Equals(systemOwned.Text?.Trim(), text, StringComparison.OrdinalIgnoreCase))
                return false;
            systemOwned.Text = text;
            systemOwned.Category = category;
            systemOwned.ApprovedAt = DateTimeOffset.UtcNow;
            systemOwned.ApprovedBy = approvedBy;
            await SaveAsync(projectId, doc, ct).ConfigureAwait(false);
            return true;
        }

        // User already has an active rule they approved — leave it
        var userOwned = doc.Active.FirstOrDefault(r =>
            string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.Id, ruleId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.ApprovedBy, ApproverCastExtract, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.ApprovedBy, ApproverSystem, StringComparison.OrdinalIgnoreCase));
        if (userOwned is not null)
            return false;

        doc.Active.RemoveAll(r =>
            string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(r.ApprovedBy, ApproverCastExtract, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(r.ApprovedBy, ApproverSystem, StringComparison.OrdinalIgnoreCase)));

        doc.Active.Add(new ProjectRule
        {
            Id = ruleId,
            Text = text,
            Category = category,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = approvedBy,
            SourceFailCount = 0,
        });
        await SaveAsync(projectId, doc, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Token-accurate now (was raw character count) — see PromptTokenizer. This text is
    /// stored and re-injected into many future prompts, so an accurate budget matters more
    /// here than in a one-shot classifier field.
    /// </summary>
    private static string NormalizeLockRuleText(
        string? raw, int maxTokens, string prefixWhenMissing, params string[] keywords)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return "";
        if (!keywords.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase)))
            t = prefixWhenMissing + t;
        return PromptTokenizer.TruncateToTokens(t, maxTokens);
    }

    /// <summary>
    /// Scan host learning events for this project; add pending suggestions for hot fail categories.
    /// Does not auto-activate.
    /// </summary>
    public async Task<ProjectRulesDocument> SuggestFromFailsAsync(
        string projectId,
        int minFails = DefaultMinFailsForSuggest,
        CancellationToken ct = default)
    {
        minFails = Math.Max(2, minFails);
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        var events = await _learning.QueryAsync(projectId: projectId, take: 2000, ct: ct).ConfigureAwait(false);
        var fails = events
            .Where(e =>
                string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var byCat = fails
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Category) ? CategoryOther : e.Category.Trim().ToLowerInvariant())
            .Select(g => new
            {
                Category = g.Key,
                Count = g.Count(),
                Notes = g.Select(x => x.Note)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Take(5)
                    .ToList(),
            })
            .Where(x => x.Count >= minFails)
            .ToList();

        var activeTexts = new HashSet<string>(
            doc.Active.Select(a => (a.Text ?? "").Trim()).Where(t => t.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var pendingTexts = new HashSet<string>(
            doc.Pending.Select(p => (p.Text ?? "").Trim()).Where(t => t.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var activeCategories = new HashSet<string>(
            doc.Active.Select(a => (a.Category ?? CategoryOther).Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var g in byCat)
        {
            var text = BuildRuleText(g.Category, g.Notes);
            if (activeTexts.Contains(text) || pendingTexts.Contains(text))
                continue;
            // Skip if this category already has an active or pending rule
            if (activeCategories.Contains(g.Category) ||
                doc.Pending.Any(p => string.Equals(p.Category, g.Category, StringComparison.OrdinalIgnoreCase)))
                continue;

            doc.Pending.Add(new ProjectRuleSuggestion
            {
                Id = Guid.NewGuid().ToString("N")[..10],
                Category = g.Category,
                FailCount = g.Count,
                Text = text,
                Rationale = $"Seen {g.Count} fails tagged {g.Category}.",
                SuggestedAt = DateTimeOffset.UtcNow,
            });
            pendingTexts.Add(text);
        }

        await SaveAsync(projectId, doc, ct).ConfigureAwait(false);
        return doc;
    }

    public async Task<ProjectRulesDocument> ApproveAsync(
        string projectId,
        string suggestionId,
        string? textOverride,
        string? approvedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
            throw new ArgumentException("suggestionId required", nameof(suggestionId));
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        var sug = doc.Pending.FirstOrDefault(p =>
            string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown suggestion: {suggestionId}");

        var text = !string.IsNullOrWhiteSpace(textOverride)
            ? textOverride.Trim()
            : (sug.Text ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("Rule text cannot be empty.");
        doc.Pending.RemoveAll(p => string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        doc.Active.Add(new ProjectRule
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Text = text,
            Category = string.IsNullOrWhiteSpace(sug.Category) ? CategoryOther : sug.Category.Trim(),
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = approvedBy,
            SourceFailCount = sug.FailCount,
        });
        await SaveAsync(projectId, doc, ct).ConfigureAwait(false);
        return doc;
    }

    public async Task<ProjectRulesDocument> RejectAsync(string projectId, string suggestionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
            throw new ArgumentException("suggestionId required", nameof(suggestionId));
        var doc = await LoadAsync(projectId, ct).ConfigureAwait(false);
        doc.Pending.RemoveAll(p => string.Equals(p.Id, suggestionId, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(projectId, doc, ct).ConfigureAwait(false);
        return doc;
    }

    private static string BuildRuleText(string category, List<string> notes)
    {
        var sample = notes.FirstOrDefault(n => n is { Length: > 8 });
        return category switch
        {
            "wrong_voice" => "Keep each character's voice consistent with their voice_profile (gender, pitch, age).",
            "wrong_look" => "Match locked character appearance and visual_lock on every clip; no identity drift.",
            "wrong_style" or CategoryStyle =>
                "Hold the project render medium on every clip (picture-book CG vs photoreal, etc.); no medium drift mid-film.",
            "continuity" => "When continuing from previous clip, match wardrobe, place, and pose from the last frames.",
            "silent" => "Dialogue clips must have clear audible speech and lip sync for the speaker.",
            "framing" => "Follow planned framing/action in visual_prompt; avoid empty holds and wrong shots.",
            _ => string.IsNullOrWhiteSpace(sample)
                ? $"Address repeated review fails in category '{category}'."
                : $"Address '{category}' issues (e.g. {Trim(sample, 30)}).",
        };
    }

    // Token-accurate now (was raw character count) — see PromptTokenizer. Stored rule text,
    // re-injected into many future prompts, so an accurate budget matters here.
    private static string Trim(string s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);
}
