using PageToMovie.Core.Abstractions;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Engine-level draft-edit seams: the D0 full-length base (screenplay.max.fountain) and the trim seam,
/// including the D5 guarantee that editing/trimming an approved screenplay re-gates Cast (ReadyForShots).
/// </summary>
public sealed class ScreenplayDraftEditTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "Demo";

    public ScreenplayDraftEditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-draftedit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* ignore */ }
    }

    [Fact]
    public void MaxBase_roundtrips_and_is_not_a_draft_recovery_candidate()
    {
        // Base present but canonical draft missing → recovery must NOT adopt the base.
        var source = Path.Combine(_store.GetProjectDir(ProjectId), "source");
        Directory.CreateDirectory(source);
        ScreenplayService.WriteMaxBase(_store, ProjectId, Fountain(6));

        Assert.True(File.Exists(ScreenplayService.GetMaxBasePath(_store, ProjectId)));
        Assert.False(File.Exists(ScreenplayService.GetDraftPath(_store, ProjectId)));

        // EnsureCanonicalDraft (invoked by Get) must not promote the base into the canonical draft.
        var doc = ScreenplayService.Get(_store, ProjectId);
        Assert.False(doc.Status.DraftExists);
        Assert.True(string.IsNullOrEmpty(doc.Text));
    }

    [Fact]
    public void HasMaxBase_reflects_base_presence_for_fork_reuse()
    {
        Assert.False(ScreenplayService.HasMaxBase(_store, ProjectId));
        ScreenplayService.WriteMaxBase(_store, ProjectId, Fountain(4));
        Assert.True(ScreenplayService.HasMaxBase(_store, ProjectId));
    }

    [Fact]
    public async Task TrimDraft_seeds_base_replaces_draft_and_regates_cast()
    {
        var source = Path.Combine(_store.GetProjectDir(ProjectId), "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "book_full.txt"),
            "A story. " + string.Concat(Enumerable.Repeat("word ", 400)));

        // Approve a full-length (6-scene) screenplay → Cast is unlocked.
        ScreenplayService.SaveDraft(_store, ProjectId, Fountain(6));
        var sign = ScreenplayService.SignOff(_store, ProjectId);
        Assert.True(sign.Status.ReadyForShots);

        // Trim to a shorter (3-scene) version.
        var chat = new StubChat(Fountain(3));
        var result = await ScreenplayService.TrimDraftAsync(_store, ProjectId, new ChatCall(chat, "grok-4.5"));

        Assert.True(result.Ok);
        Assert.True(result.Applied);

        // Base was seeded from the pre-trim full draft and stays full-length.
        var basePath = ScreenplayService.GetMaxBasePath(_store, ProjectId);
        Assert.True(File.Exists(basePath));
        Assert.Contains("PLACE 6", File.ReadAllText(basePath));

        // Working draft is the trimmed version, and approval is re-gated (D5): Cast blocked until re-approve.
        var status = ScreenplayService.Get(_store, ProjectId).Status;
        Assert.Equal(3, status.SceneHeadingCount);
        Assert.True(status.Dirty);
        Assert.False(status.ReadyForShots);
    }

    [Fact]
    public async Task EmbellishDraft_updates_base_so_fit_length_derives_from_enriched()
    {
        Directory.CreateDirectory(Path.Combine(_store.GetProjectDir(ProjectId), "source"));

        // Simulate generation: both the working draft and the full-length base are the lean version.
        ScreenplayService.SaveDraft(_store, ProjectId, Fountain(4, "PLAIN"));
        ScreenplayService.WriteMaxBase(_store, ProjectId, Fountain(4, "PLAIN"));

        // Enrich (same scene count) — the base must become the enriched version so a later Fit length
        // trims from the enriched screenplay rather than discarding the enrichment.
        var chat = new StubChat(Fountain(4, "ENRICHED"));
        var r = await ScreenplayService.EmbellishDraftAsync(_store, ProjectId, "auto", chat, model: "grok-4.5");

        Assert.True(r.Applied);
        var baseText = File.ReadAllText(ScreenplayService.GetMaxBasePath(_store, ProjectId));
        Assert.Contains("ENRICHED", baseText);
        Assert.DoesNotContain("PLAIN", baseText);
    }

    private static string Fountain(int scenes, string marker = "action")
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Test");
        sb.AppendLine("Author: Unit");
        sb.AppendLine();
        for (var i = 1; i <= scenes; i++)
        {
            sb.AppendLine(i % 2 == 0 ? $"EXT. PLACE {i} - DAY" : $"INT. ROOM {i} - NIGHT");
            sb.AppendLine();
            sb.AppendLine(new string('w', 50) + $" scene {i} {marker} and description.");
            sb.AppendLine();
            sb.AppendLine("MARY");
            sb.AppendLine($"Come along, little lamb — line {i} with enough dialogue for the gate.");
            sb.AppendLine();
        }
        sb.AppendLine("FADE OUT.");
        sb.AppendLine();
        sb.AppendLine("THE END");
        return sb.ToString();
    }

    private sealed class StubChat : IChatClient
    {
        private readonly string _response;
        public StubChat(string response) => _response = response;
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "grok-4.5",
            double temperature = 0.2, CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null) =>
            Task.FromResult(_response);
    }
}
