using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class AdminLearning
{

    internal bool _busy;
    internal string? _error;
    internal string? _message;
    private string _projectFilter { get; set; } = "";
    internal List<ProjectInfo> _projects = new();
    internal LearningInsightsDto? _insights;
    internal ReviewComparisonInsightsDto? _comparison;
    internal List<ReviewLearningEvent> _events = new();
    private int _proposeN { get; set; } = 50;
    internal string? _proposal;
    private ProposalChecklistDocument? _checklist;
    private bool _showDoneItems;
    private string _rulesProject { get; set; } = "";
    internal ProjectRulesDocument? _rules;

    private static bool IsChecklistDone(ProposalChecklistItem item) =>
        string.Equals(item.Disposition, "accepted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.Disposition, "rejected", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human title/label for the filter list; id stays the option value.</summary>
    private static string ProjectOptionLabel(ProjectInfo p)
    {
        var label = p.Label ?? p.Title ?? p.Id;
        if (string.IsNullOrWhiteSpace(label) || string.Equals(label, p.Id, StringComparison.Ordinal))
            return p.Id;
        return $"{label} ({p.Id})";
    }

    private int DoneCount =>
        _checklist?.Items.Count(IsChecklistDone) ?? 0;

    private int PendingVisibleCount =>
        _checklist?.Items.Count(i => !IsChecklistDone(i)) ?? 0;

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); } catch { /* session hydrate is best-effort on first paint */ }
        if (!Session.IsAdmin) return;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            await LoadProjectsAsync();
            var pid = string.IsNullOrWhiteSpace(_projectFilter) ? null : _projectFilter.Trim();
            _insights = await Api.GetLearningInsightsAsync(pid);
            _comparison = await Api.GetReviewComparisonAsync(pid);
            _events = (await Api.GetLearningEventsAsync(pid, take: 80)).ToList();
            _checklist = await Api.GetProposalChecklistAsync();
            if (!string.IsNullOrWhiteSpace(_rulesProject))
                _rules = await Api.GetProjectRulesAsync(_rulesProject.Trim());
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task LoadProjectsAsync()
    {
        try
        {
            var projs = await Api.GetProjectsAsync();
            _projects = (projs?.Projects ?? new List<ProjectInfo>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .OrderBy(p => p.Label ?? p.Title ?? p.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Keep any previously loaded list; insights/events still refresh.
        }
    }

    private async Task SynthesizePromptImprovementsAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            var pid = string.IsNullOrWhiteSpace(_projectFilter) ? null : _projectFilter.Trim();
            _comparison = await Api.SynthesizePromptImprovementsAsync(pid);
            _message = "Prompt improvement recommendations synthesized from human vs AI review discrepancies.";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task LoadChecklistAsync()
    {
        _busy = true;
        _error = null;
        try
        {
            _checklist = await Api.GetProposalChecklistAsync();
            _message = _checklist is null
                ? "No checklist"
                : $"Checklist · {_checklist.Items.Count(i => i.Reviewed)}/{_checklist.Items.Count} reviewed";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task ToggleProposalAsync(ProposalChecklistItem item, bool reviewed)
    {
        _busy = true;
        _error = null;
        try
        {
            _checklist = await Api.ToggleProposalChecklistItemAsync(
                item.Id, reviewed, item.Disposition, item.Note);
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task SetDispositionAsync(ProposalChecklistItem item, string? disposition)
    {
        _busy = true;
        _error = null;
        try
        {
            // Mark reviewed when choosing a disposition
            var reviewed = item.Reviewed || !string.IsNullOrWhiteSpace(disposition);
            _checklist = await Api.ToggleProposalChecklistItemAsync(
                item.Id, reviewed, disposition, item.Note);
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task ProposeAsync()
    {
        _busy = true;
        _error = null;
        _proposal = null;
        try
        {
            var r = await Api.ProposeLearningRulesAsync(new ProposeLearningRulesRequest
            {
                LastNFails = _proposeN,
                ProjectId = string.IsNullOrWhiteSpace(_projectFilter) ? null : _projectFilter.Trim(),
            });
            if (r is null || !r.Ok)
                _error = r?.Error ?? "Propose failed";
            else
            {
                _proposal = r.Proposal + $"\n\n({r.FailEventsUsed} fails · cats: {string.Join(", ", r.Categories)})";
                // Server ingests bullets into checklist; reload to show checkboxes
                _checklist = await Api.GetProposalChecklistAsync();
                _message = "Proposal ingested into checklist — check off as you review";
            }
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task LoadRulesAsync()
    {
        if (string.IsNullOrWhiteSpace(_rulesProject)) return;
        _busy = true;
        _error = null;
        try { _rules = await Api.GetProjectRulesAsync(_rulesProject.Trim()); }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task SuggestRulesAsync()
    {
        if (string.IsNullOrWhiteSpace(_rulesProject)) return;
        _busy = true;
        _error = null;
        try
        {
            _rules = await Api.SuggestProjectRulesAsync(_rulesProject.Trim());
            _message = "Suggestions updated from fail events";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task ApproveRuleAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(_rulesProject)) return;
        _busy = true;
        try
        {
            _rules = await Api.ApproveProjectRuleAsync(_rulesProject.Trim(), id);
            _message = "Rule approved — injected into gen/auto-review for this project";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }

    private async Task RejectRuleAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(_rulesProject)) return;
        _busy = true;
        try { _rules = await Api.RejectProjectRuleAsync(_rulesProject.Trim(), id); }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; }
    }
}
