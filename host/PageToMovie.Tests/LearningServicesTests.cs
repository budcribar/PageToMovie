using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class LearningServicesTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _projects;
    private readonly ReviewEventStore _events;
    private readonly ProjectRulesService _rules;
    private readonly LearningProposalService _propose;

    public LearningServicesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_learn2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        File.WriteAllText(Path.Combine(_root, "projects", "Demo", "project.json"),
            """{"id":"Demo","label":"Demo"}""");
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root, EnableReadCaches = false });
        _projects = new ProjectStore(opts);
        _events = new ReviewEventStore(_projects, NullLogger<ReviewEventStore>.Instance);
        _rules = new ProjectRulesService(_projects, _events, NullLogger<ProjectRulesService>.Instance);
        _propose = new LearningProposalService(
            _events,
            new OfflineChat(),
            NullLogger<LearningProposalService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* temp */ }
    }

    [Fact]
    public void Clip_gen_and_auto_review_prompts_are_embedded()
    {
        var gen = PromptFiles.TryReadEmbedded("prompts/clip_gen_rules.txt");
        var ar = PromptFiles.TryReadEmbedded("prompts/clip_auto_review.txt");
        Assert.False(string.IsNullOrWhiteSpace(gen));
        Assert.Contains("HOUSE RULES", gen!, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(ar));
        Assert.Contains("CHECKLIST", ar!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IDENTITY", ar!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task P3_propose_from_fails_offline()
    {
        for (var i = 0; i < 5; i++)
        {
            await _events.AppendAsync(new ReviewLearningEvent
            {
                ProjectId = "Demo",
                Type = "clip_fail",
                Category = "continuity",
                Note = "Jump cut at join",
                Scene = 1,
                Clip = i + 1,
            });
        }

        var r = await _propose.ProposeAsync(new ProposeLearningRulesRequest { LastNFails = 10, ProjectId = "Demo" });
        Assert.True(r.Ok);
        Assert.True(r.FailEventsUsed >= 5);
        Assert.False(string.IsNullOrWhiteSpace(r.Proposal));
        Assert.Contains("continuity", r.Categories);
    }

    [Fact]
    public async Task P4_project_rules_suggest_approve()
    {
        for (var i = 0; i < 4; i++)
        {
            await _events.AppendAsync(new ReviewLearningEvent
            {
                ProjectId = "Demo",
                Type = "clip_fail",
                Category = "wrong_voice",
                Note = "Female voice on dad",
                Scene = 1,
                Clip = i + 1,
            });
        }

        var doc = await _rules.SuggestFromFailsAsync("Demo", minFails: 3);
        Assert.NotEmpty(doc.Pending);
        var sug = doc.Pending.First(p => p.Category == "wrong_voice");
        doc = await _rules.ApproveAsync("Demo", sug.Id, textOverride: null, approvedBy: "admin");
        Assert.DoesNotContain(doc.Pending, p => p.Id == sug.Id);
        Assert.Contains(doc.Active, a => a.Category == "wrong_voice");
        var block = await _rules.GetActiveRulesBlockAsync("Demo");
        Assert.Contains("voice", block, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class OfflineChat : IChatClient
    {
        public bool IsConfigured => false;
        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "grok-4.5",
            double temperature = 0.2, CancellationToken ct = default, string? mode = null, string? reasoningEffort = null) =>
            Task.FromResult("- offline rule");
    }
}
