using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelBacked;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Visual medium is decided at book/screenplay and stored on ProjectVisionMeta.
/// Stage 2 / generate fail when it is missing — they do not invent photoreal or 3D CG.
/// </summary>
[Collection("catalog-serial")]
public sealed class VisualMediumSsotTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;

    public VisualMediumSsotTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-medium-ssot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void PersistAdaptationDecision_writes_medium_and_matching_lock()
    {
        var dir = _store.GetProjectDir("Demo");
        var persisted = ProjectVisionMeta.PersistAdaptationDecision(dir, new ProjectVisionMeta.Document
        {
            VisualMedium = ProjectVisionMeta.MediumIllustrated,
            DecidedBy = "adaptation",
        });

        Assert.NotNull(persisted);
        Assert.Equal(ProjectVisionMeta.MediumIllustrated, persisted!.VisualMedium);
        Assert.Equal(VisualMediumStyles.IllustratedStyleLock, persisted.RenderStyleLock);

        var read = ProjectVisionMeta.RequireDecided(dir);
        Assert.Equal(ProjectVisionMeta.MediumIllustrated, read.VisualMedium);
        Assert.Equal(VisualMediumStyles.IllustratedStyleLock, read.RenderStyleLock);
        Assert.True(File.Exists(ProjectVisionMeta.GetPath(dir)));
        var extract = File.ReadAllText(ProjectVisionMeta.GetExtractMetaPath(dir));
        Assert.Contains(ProjectVisionMeta.MediumIllustrated, extract, StringComparison.Ordinal);
        Assert.Contains("render_style_lock", extract, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistAdaptationDecision_does_not_invent_when_trailer_missing()
    {
        var dir = _store.GetProjectDir("Demo");
        Assert.Null(ProjectVisionMeta.PersistAdaptationDecision(dir, null));
        Assert.Null(ProjectVisionMeta.TryGetDecided(dir));
    }

    [Fact]
    public void RequireDecided_fails_when_vision_and_extract_have_no_medium()
    {
        var dir = _store.GetProjectDir("Demo");
        Directory.CreateDirectory(Path.Combine(dir, "source"));
        File.WriteAllText(Path.Combine(dir, "source", "extract_meta.json"),
            """{"schema_version":"extract_meta.v1","pages":3}""");

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectVisionMeta.RequireDecided(dir));
        Assert.Equal(ProjectVisionMeta.MissingMediumMessage, ex.Message);
    }

    [Fact]
    public async Task Stage2_uses_vision_meta_when_GPV_has_no_render_style_lock()
    {
        const string projectId = "Demo";
        await OfflineTestModelConfig.ApplyAsync(_store, projectId, writeDecidedVision: false);
        OfflineTestModelConfig.WriteDecidedVision(_store, projectId, ProjectVisionMeta.MediumIllustrated);
        ScreenplayService.SaveDraft(_store, projectId, """
            Title: Medium SSoT

            INT. ROOM - DAY

            HERO
            Hello.
            """);
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");
        Assert.True(result.Ok);
        Assert.True(File.Exists(result.OutPath));

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result.OutPath!));
        var scenes = doc.RootElement.GetProperty("scenes");
        Assert.True(scenes.GetArrayLength() >= 1);
        var scene = scenes[0];
        Assert.Equal(
            ProjectVisionMeta.MediumIllustrated,
            scene.GetProperty("visual_medium").GetString());
        Assert.Equal(
            VisualMediumStyles.IllustratedStyleLock,
            scene.GetProperty("render_style_lock").GetString());

        var prompt = scene.GetProperty("veo_clips")[0].GetProperty("visual_prompt").GetString() ?? "";
        Assert.Contains("picture-book", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "stylized 3D animated children's picture-book CG",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage2_fails_when_project_has_no_visual_medium()
    {
        const string projectId = "Demo";
        await OfflineTestModelConfig.ApplyAsync(_store, projectId, writeDecidedVision: false);
        ScreenplayService.SaveDraft(_store, projectId, """
            Title: Missing Medium

            INT. ROOM - DAY

            HERO
            Hello.
            """);
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planner.PlanAsync(projectId, resolution: "480p", scenes: "all"));
        Assert.Equal(ProjectVisionMeta.MissingMediumMessage, ex.Message);
    }

    [Fact]
    public async Task Stage2_fails_when_project_has_no_performance_lock()
    {
        const string projectId = "Demo";
        await OfflineTestModelConfig.ApplyAsync(_store, projectId, writeDecidedVision: false);
        ProjectVisionMeta.Write(_store.GetProjectDir(projectId), new ProjectVisionMeta.Document
        {
            VisualMedium = ProjectVisionMeta.MediumIllustrated,
            DecidedBy = "adaptation",
        });
        ScreenplayService.SaveDraft(_store, projectId, """
            Title: Missing Performance Lock

            INT. ROOM - DAY

            HERO
            Hello.
            """);
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var progress = new List<string>();
        var chat = new RecordingChat();
        var classifyOpts = Options.Create(new PageToMovieOptions
        {
            ClassifyExtendCutWithChat = true,
            SilentBeatClassifyMaxAttempts = 1,
            SilentBeatClassifyBackoffBaseMs = 0,
        });
        var planner = new Stage2PlannerService(
            _store,
            NullLogger<Stage2PlannerService>.Instance,
            silentBeatClassifier: new SilentBeatActionClassifier(
                chat, classifyOpts, NullLogger<SilentBeatActionClassifier>.Instance),
            ambientSfxClassifier: new AmbientSfxClassifier(
                chat, classifyOpts, NullLogger<AmbientSfxClassifier>.Instance),
            onScreenCastClassifier: new OnScreenCastClassifier(
                chat, classifyOpts, NullLogger<OnScreenCastClassifier>.Instance),
            extendCutClassifier: new ExtendCutClassifier(
                chat, classifyOpts, NullLogger<ExtendCutClassifier>.Instance),
            speciesKindClassifier: new SpeciesKindClassifier(
                chat, classifyOpts, NullLogger<SpeciesKindClassifier>.Instance));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planner.PlanAsync(projectId, resolution: "480p", scenes: "all", onProgress: progress.Add));
        Assert.Equal(ProjectVisionMeta.MissingPerformanceLockMessage, ex.Message);
        Assert.Contains("cast from the screenplay", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("book/screenplay", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan looks", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Fail-fast at job start: no classifier progress and no chat calls.
        Assert.Empty(chat.Prompts);
        Assert.DoesNotContain(progress, line =>
            line.Contains("Classifying", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Extend/cut", StringComparison.OrdinalIgnoreCase)
            || line.Contains("silent beat", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Loading screenplay", StringComparison.OrdinalIgnoreCase));
    }

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

    [Fact]
    public void ClipVideoPromptBuilder_does_not_invent_photoreal_or_3d_when_medium_missing()
    {
        var clip = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 1,
            visual_prompt = "INT. ROOM - DAY. Character_Hero stands.",
            characters_on_screen = new[] { "Character_Hero" },
            veo_continuation_source = "none",
            audio_payload = new { speaker = "", dialogue = "", delivery = "none" },
        })).RootElement.Clone();

        var built = ClipVideoPromptBuilder.Build(clip, Path.GetTempPath());
        Assert.DoesNotContain("stylized 3D animated children's picture-book CG", built.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(VisualMediumStyles.PhotorealStyleLock, built.Prompt, StringComparison.Ordinal);
    }
}
