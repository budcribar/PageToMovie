using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Replanning one scene used to enrich the whole screenplay: the scene filter was applied after
/// <c>RunStage1EnrichmentsAsync</c>, so silent-beat, ambient/sfx, on-screen cast and extend/cut all
/// ran over every beat in the film to plan a single scene. Mary19 logged "extend vs hard-cut for 22
/// beat(s)" while planning one 3-beat scene — and each of those beats now carries a much larger
/// payload since the staging fix, so the waste got more expensive rather than less.
/// </summary>
public sealed class Stage2PartialReplanScopeTests : IDisposable
{
    private readonly string _root;

    public Stage2PartialReplanScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-s2-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
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

    /// <summary>Records every user prompt so we can see which beats were actually sent.</summary>
    private sealed class RecordingChat : IChatClient
    {
        public List<string> Prompts { get; } = new();
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "grok-4.5",
            double temperature = 0.2, CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            lock (Prompts)
                Prompts.Add(userPrompt);
            return Task.FromResult("");
        }
    }

    private const string Fountain = """
        Title: Scope Check

        INT. KITCHEN - DAY

        A kettle whistles on the hob in the empty kitchen.

        COOK
        Someone should take that off the heat.

        INT. GARDEN - DAY

        Rain beats down on the flattened marigolds.

        GARDENER
        Every year the same, and every year I forget the netting.

        INT. ATTIC - NIGHT

        Dust sheets cover the furniture in the sloped attic.

        CARETAKER
        Nothing up here has moved in a decade.
        """;

    [Fact]
    public async Task Replanning_one_scene_only_enriches_that_scene()
    {
        var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        const string projectId = "Demo";
        await OfflineTestModelConfig.ApplyAsync(store, projectId);
        ScreenplayService.SaveDraft(store, projectId, Fountain);
        var sign = ScreenplayService.SignOff(store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var chat = new RecordingChat();
        // One attempt, no backoff: the fake returns nothing, and this test is about what was ASKED,
        // not about what came back.
        var opts = Options.Create(new PageToMovieOptions
        {
            SilentBeatClassifyMaxAttempts = 1,
            SilentBeatClassifyBackoffBaseMs = 0,
        });

        var planner = new Stage2PlannerService(
            store,
            NullLogger<Stage2PlannerService>.Instance,
            extendCutClassifier: new ExtendCutClassifier(chat, opts, NullLogger<ExtendCutClassifier>.Instance));

        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "2");
        Assert.True(result.Ok);

        var asked = string.Join("\n", chat.Prompts);
        Assert.Contains("marigolds", asked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kettle", asked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dust sheets", asked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A full replan still sees everything — the scoping must not narrow "all".</summary>
    [Fact]
    public async Task Replanning_every_scene_still_enriches_every_scene()
    {
        var store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        const string projectId = "Demo";
        await OfflineTestModelConfig.ApplyAsync(store, projectId);
        ScreenplayService.SaveDraft(store, projectId, Fountain);
        var sign = ScreenplayService.SignOff(store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var chat = new RecordingChat();
        var opts = Options.Create(new PageToMovieOptions
        {
            SilentBeatClassifyMaxAttempts = 1,
            SilentBeatClassifyBackoffBaseMs = 0,
        });

        var planner = new Stage2PlannerService(
            store,
            NullLogger<Stage2PlannerService>.Instance,
            extendCutClassifier: new ExtendCutClassifier(chat, opts, NullLogger<ExtendCutClassifier>.Instance));

        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");
        Assert.True(result.Ok);

        var asked = string.Join("\n", chat.Prompts);
        Assert.Contains("kettle", asked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("marigolds", asked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dust sheets", asked, StringComparison.OrdinalIgnoreCase);
    }
}
