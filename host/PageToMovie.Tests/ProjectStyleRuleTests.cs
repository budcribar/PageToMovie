using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public class ProjectStyleRuleTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private readonly ProjectRulesService _rules;

    public ProjectStyleRuleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-style-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        _rules = new ProjectRulesService(
            _store,
            new ReviewEventStore(_store, NullLogger<ReviewEventStore>.Instance),
            NullLogger<ProjectRulesService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public async Task EnsureStyleRule_writes_active_style_from_render_lock()
    {
        var changed = await _rules.EnsureStyleRuleFromRenderLockAsync(
            "Demo",
            "STYLE LOCK: stylized 3D picture-book CG; not photoreal live-action",
            approvedBy: "cast_extract");
        Assert.True(changed);
        var doc = await _rules.LoadAsync("Demo");
        var style = Assert.Single(doc.Active, r => r.Category == "style");
        Assert.Equal(ProjectRulesService.StyleRuleId, style.Id);
        Assert.Contains("picture-book", style.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("style", await _rules.GetActiveRulesBlockAsync("Demo"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureStyleRule_does_not_overwrite_user_style()
    {
        var doc = await _rules.LoadAsync("Demo");
        doc.Active.Add(new ProjectRule
        {
            Id = "user_style",
            Category = "style",
            Text = "User approved: keep watercolor look",
            ApprovedBy = "admin",
            ApprovedAt = DateTimeOffset.UtcNow,
        });
        await _rules.SaveAsync("Demo", doc);

        var changed = await _rules.EnsureStyleRuleFromRenderLockAsync(
            "Demo",
            "STYLE LOCK: photoreal 1840s",
            approvedBy: "cast_extract");
        Assert.False(changed);
        var again = await _rules.LoadAsync("Demo");
        Assert.Contains(again.Active, r => r.Text!.Contains("watercolor"));
        Assert.DoesNotContain(again.Active, r => r.Text!.Contains("photoreal 1840s"));
    }

    [Fact]
    public async Task EnsurePerformanceRule_writes_from_book_inferred_lock()
    {
        var changed = await _rules.EnsurePerformanceRuleFromLockAsync(
            "Demo",
            "first-person confessional often addresses implied listener when speaking",
            approvedBy: "cast_extract");
        Assert.True(changed);
        var doc = await _rules.LoadAsync("Demo");
        var perf = Assert.Single(doc.Active, r => r.Id == ProjectRulesService.PerformanceRuleId);
        Assert.Contains("confessional", perf.Text, StringComparison.OrdinalIgnoreCase);
        // Generate reads vision_meta — the film lock is not a second house-rule writer.
        var block = await _rules.GetActiveRulesBlockAsync("Demo");
        Assert.DoesNotContain("PERFORMANCE LOCK", block, StringComparison.OrdinalIgnoreCase);
    }
}
