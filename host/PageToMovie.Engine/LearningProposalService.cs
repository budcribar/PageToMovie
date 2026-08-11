using System.Text;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>Admin-gated: propose prompt/rule text from recent fail events (chat).</summary>
public sealed class LearningProposalService
{
    private readonly ReviewEventStore _learning;
    private readonly IChatClient _chat;
    private readonly ILogger<LearningProposalService> _log;

    public LearningProposalService(
        ReviewEventStore learning,
        IChatClient chat,
        ILogger<LearningProposalService> log)
    {
        _learning = learning;
        _chat = chat;
        _log = log;
    }

    public async Task<ProposeLearningRulesResult> ProposeAsync(
        ProposeLearningRulesRequest req,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var n = Math.Clamp(req.LastNFails <= 0 ? 50 : req.LastNFails, 5, 200);
        // Scan full log then filter fails — Query(take:N) of mixed events can bury fails under passes
        var allEvents = await _learning.ReadAllAsync(ct).ConfigureAwait(false);
        var fails = allEvents
            .Where(e =>
                string.IsNullOrWhiteSpace(req.ProjectId) ||
                string.Equals(e.ProjectId, req.ProjectId, StringComparison.OrdinalIgnoreCase))
            .Where(e =>
                string.Equals(e.Type, "clip_fail", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(e.Type, "auto_review", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(e.Suggestion, "fail", StringComparison.OrdinalIgnoreCase)))
            .Where(e => string.IsNullOrWhiteSpace(req.Category) ||
                        string.Equals(e.Category, req.Category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Ts)
            .Take(n)
            .ToList();

        if (fails.Count == 0)
        {
            return new ProposeLearningRulesResult
            {
                Ok = false,
                Error = "No fail events found for the filters.",
                FailEventsUsed = 0,
            };
        }

        var cats = fails
            .Select(f => string.IsNullOrWhiteSpace(f.Category) ? "other" : f.Category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Recent film QC fails (newest first):");
        var i = 0;
        foreach (var f in fails)
        {
            i++;
            sb.AppendLine(
                $"{i}. [{f.Type}] project={f.ProjectId} S{f.Scene:D2}C{f.Clip:D2} " +
                $"cat={f.Category ?? "?"} note={Trim(f.Note, 50)}");
            if (!string.IsNullOrWhiteSpace(f.Before) || !string.IsNullOrWhiteSpace(f.After))
                sb.AppendLine($"   before/after present: beforeLen={f.Before?.Length ?? 0} afterLen={f.After?.Length ?? 0}");
        }

        var system =
            "You help improve a film generation pipeline. From QC fail notes, propose 3–7 concise " +
            "house rules for video prompt construction (and auto-review checks). " +
            "Output plain text bullet list only. No markdown fences. Each bullet one sentence. " +
            "Do not invent book-specific plot; keep rules general and actionable.";

        // Never invent a model id. Without an explicit model (or chat key), stay offline.
        var learningModel = (req.Model ?? "").Trim();
        if (!_chat.IsConfigured || string.IsNullOrWhiteSpace(learningModel))
        {
            var offline = string.Join("\n", cats.Select(c =>
                $"- Strengthen checks and gen guidance for category '{c}' based on {fails.Count} recent fails."));
            return new ProposeLearningRulesResult
            {
                Ok = true,
                Proposal = offline + "\n- Prefer continuity from previous clip tail; flag jumps as fail when clear.",
                FailEventsUsed = fails.Count,
                Categories = cats,
            };
        }

        try
        {
            learningModel = ProjectModelSelection.RequireExplicit(
                learningModel, ModelCapability.Chat, "Learning proposal");
            var proposal = await _chat.CompleteAsync(
                    system, sb.ToString(), model: learningModel, temperature: 0.3, ct,
                    mode: ChatCallModes.LearningPropose)
                .ConfigureAwait(false);
            return new ProposeLearningRulesResult
            {
                Ok = true,
                Proposal = proposal.Trim(),
                FailEventsUsed = fails.Count,
                Categories = cats,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Propose learning rules failed");
            return new ProposeLearningRulesResult
            {
                Ok = false,
                Error = ex.Message,
                FailEventsUsed = fails.Count,
                Categories = cats,
            };
        }
    }

    public async Task<ReviewComparisonInsightsDto> SynthesizePromptImprovementsAsync(
        string? projectId = null,
        CancellationToken ct = default)
    {
        var insights = await _learning.GetReviewComparisonAsync(projectId, ct).ConfigureAwait(false);
        var gaps = insights.Discrepancies
            .Where(d => d.DiscrepancyType != "AGREEMENT")
            .Take(30)
            .ToList();

        if (gaps.Count == 0)
        {
            insights.PromptImprovementProposal = "No discrepancies found between Human and AI reviews yet. As operators review clips, differences will be tracked here.";
            return insights;
        }

        // Admin path without an explicit project model — offline template only (no invented Grok id).
        insights.PromptImprovementProposal =
            "- [AI Too Permissive]: Require explicit verification of character wardrobe/costume lock across scene cuts.\n" +
            "- [AI Too Strict]: Allow subtle lighting shifts between angles if primary subject remains clear.\n" +
            "- [General]: Update auto-review prompt to weight action continuity higher than minor background rendering quirks.\n" +
            "- (Live AI synthesis requires an explicit Script & planning model from Settings.)";
        return insights;
    }

    // Token-accurate now (was raw character count) — see PromptTokenizer.
    private static string Trim(string? s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);
}
