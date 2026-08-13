using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Adaptation.Conversion;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ScreenplayIndexCutTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "CutDemo";

    public ScreenplayIndexCutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ptm-cut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId, "source"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Plan_keeps_all_on_natural_mode()
    {
        var index = BuildIndex();
        var plan = ScreenplayIndexCutter.Plan(index, targetMinutes: 8, runtimeMode: "natural");
        Assert.True(plan.KeepAll);
        Assert.Empty(plan.DroppedSequenceIds);
    }

    [Fact]
    public void Plan_keeps_opening_and_ending_drops_middle()
    {
        var index = BuildIndex();
        var plan = ScreenplayIndexCutter.Plan(index, targetMinutes: 8, runtimeMode: "reduced");
        Assert.False(plan.KeepAll);
        Assert.Contains("seq.open", plan.KeptSequenceIds);
        Assert.Contains("seq.end", plan.KeptSequenceIds);
        Assert.Contains("seq.middle", plan.DroppedSequenceIds);
    }

    [Fact]
    public void Apply_cuts_fountain_by_heading_and_leaves_title_page()
    {
        var index = BuildIndex();
        var plan = ScreenplayIndexCutter.Plan(index, 8, "reduced");
        var cut = ScreenplayIndexCutter.ApplyToFountain(FullFountain(), plan);
        Assert.False(string.IsNullOrWhiteSpace(cut));
        Assert.Contains("Title: Epic", cut);
        Assert.Contains("HALL 1", cut);
        Assert.Contains("BED 1", cut);
        Assert.DoesNotContain("CAVE 1", cut);
        Assert.True(BookToFountainConverter.LooksLikeGoodFountain(cut));
    }

    [Fact]
    public async Task TrimDraft_index_cut_does_not_touch_max_or_index()
    {
        var dir = _store.GetProjectDir(ProjectId);
        var max = FullFountain();
        ScreenplayService.SaveDraft(_store, ProjectId, max);
        ScreenplayService.WriteMaxBase(_store, ProjectId, max);
        await ProjectScreenplayIndex.WriteAsync(dir, BuildIndex());
        File.WriteAllText(Path.Combine(dir, "source", "book_full.txt"),
            string.Join(' ', Enumerable.Repeat("word", 8_000)));
        await FilmRuntime.SetTargetAsync(_store, ProjectId, 8);

        var chat = new NeverChat();
        var result = await ScreenplayService.TrimDraftAsync(_store, ProjectId, chat, model: "grok-4.6");
        Assert.True(result.Ok);
        Assert.True(result.Applied);
        Assert.Contains("sequences", result.Message ?? "", StringComparison.OrdinalIgnoreCase);

        var maxAfter = File.ReadAllText(ScreenplayService.GetMaxBasePath(_store, ProjectId));
        Assert.Equal(max.Replace("\r\n", "\n").Trim(), maxAfter.Replace("\r\n", "\n").Trim());
        Assert.True(File.Exists(ProjectScreenplayIndex.GetPath(dir)));
        var draft = ScreenplayService.Get(_store, ProjectId).Text;
        Assert.DoesNotContain("CAVE 1", draft);
        Assert.Contains("HALL 1", draft);
    }

    private static ScreenplayIndex BuildIndex() => new()
    {
        Acts =
        [
            new ScreenplayIndexAct
            {
                Id = "a1", Title = "A",
                Sequences =
                [
                    Seq("seq.open", "Open", "HALL", 2, 3),
                    Seq("seq.middle", "Middle", "CAVE", 2, 20),
                    Seq("seq.end", "End", "BED", 2, 3),
                ],
            },
        ],
    };

    private static ScreenplayIndexSequence Seq(string id, string title, string place, int cards, double minutesEach)
    {
        var scenes = new List<ScreenplayIndexCard>();
        for (var i = 1; i <= cards; i++)
        {
            scenes.Add(new ScreenplayIndexCard
            {
                Id = $"{id}.{i}",
                Order = i,
                Heading = $"INT. {place} {i} - DAY",
                LocationKey = "Loc_" + place,
                SpeakingCast = ["HERO"],
                Beat = $"{title} {i}",
                BookAnchorStart = "s",
                BookAnchorEnd = "e",
                ApproxMinutes = minutesEach,
            });
        }
        return new ScreenplayIndexSequence { Id = id, Title = title, Scenes = scenes };
    }

    private static string FullFountain()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Epic");
        sb.AppendLine("Author: H");
        sb.AppendLine();
        foreach (var place in new[] { "HALL", "CAVE", "BED" })
        {
            for (var i = 1; i <= 2; i++)
            {
                sb.AppendLine($"INT. {place} {i} - DAY");
                sb.AppendLine();
                sb.AppendLine("HERO");
                sb.AppendLine($"We are in {place} {i} with enough body text to look like a scene.");
                sb.AppendLine();
            }
        }
        sb.AppendLine("FADE OUT.");
        sb.AppendLine();
        sb.AppendLine("THE END");
        return sb.ToString();
    }

    private sealed class NeverChat : PageToMovie.Core.Abstractions.IChatClient
    {
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "",
            double temperature = 0.2, CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null) =>
            throw new InvalidOperationException("Index cut should not call the model.");
    }
}
