using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.ScreenplayEditor.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Deleting a clip only removed a blueprint row, so the next replan of that scene planned the beat
/// again — Mary19 scene 3 lost the same clip twice for exactly that reason, and the replan that
/// repaired its beat map was what brought the clip back. These cover the write-back that makes the
/// edit stick: the paragraph goes too, and the screenplay stays approved so nothing is owed.
/// </summary>
public sealed class ScreenplayClipWriteBackTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private readonly ScreenplayClipWriteBackService _writeBack;
    private const string ProjectId = "Demo";

    private const string Fountain = """
        Title: Write Back

        EXT. SCHOOLHOUSE - DAY

        THE LAMB stands in the yard, lingering near the step.

        THE CHILDREN
        What makes the lamb love Mary so?

        MARY's hand rests on the snow-white fleece.

        TEACHER
        Oh, Mary loves the lamb, you know.

        INT. SCHOOLROOM - DAY

        Dust hangs above the ink desks.
        """;

    public ScreenplayClipWriteBackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_wb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId));
        _store = new ProjectStore(Options.Create(
            new PageToMovieOptions { WorkspaceRoot = _root, EnableReadCaches = false }));
        _writeBack = new ScreenplayClipWriteBackService(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* */ }
    }

    /// <summary>Approves the screenplay and builds a real shot plan from it.</summary>
    private async Task PlanAsync()
    {
        await OfflineTestModelConfig.ApplyAsync(_store, ProjectId);
        ScreenplayService.SaveDraft(_store, ProjectId, Fountain);
        var sign = ScreenplayService.SignOff(_store, ProjectId);
        Assert.True(sign.Ok, sign.Error);
        var planner = new Stage2PlannerService(
            _store, Microsoft.Extensions.Logging.Abstractions.NullLogger<Stage2PlannerService>.Instance);
        var plan = await planner.PlanAsync(ProjectId, resolution: "480p", scenes: "all");
        Assert.True(plan.Ok);
    }

    private string Screenplay() => ScreenplayService.Get(_store, ProjectId).Text;

    private static int ActionParagraphs(string fountain, int sceneNumber) =>
        FountainFormatter.Parse(fountain).Scenes
            .First(s => s.SceneNumber == sceneNumber)
            .Beats.Count(b => b.Type == BeatType.Action);

    /// <summary>
    /// Asserted on the text rather than a count: Stage 2 coalesces a silent action beat into the
    /// spoken one that follows it, so the first clip legitimately covers two screenplay lines and
    /// both have to go. Counting paragraphs would encode a 1:1 that does not exist.
    /// </summary>
    [Fact]
    public async Task Deleting_a_clip_removes_its_lines_from_the_screenplay()
    {
        await PlanAsync();
        Assert.Contains("lingering near the step", Screenplay(), StringComparison.OrdinalIgnoreCase);
        var clips = _store.ReadSceneClipBeatIds(ProjectId, 1);
        Assert.NotEmpty(clips);

        var result = _writeBack.RemoveBeatsForClips(ProjectId, 1, new[] { clips[0].ClipNumber });

        Assert.True(result.Applied, result.Error);
        Assert.True(result.Removed >= 1);
        Assert.DoesNotContain("lingering near the step", Screenplay(), StringComparison.OrdinalIgnoreCase);
        // The clips it did not cover are untouched.
        Assert.Contains("Mary loves the lamb", Screenplay(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole point: with the line gone, replanning the scene cannot bring the clip back.
    /// </summary>
    [Fact]
    public async Task A_deleted_line_does_not_come_back_when_the_scene_is_replanned()
    {
        await PlanAsync();
        var before = _store.ReadSceneClipBeatIds(ProjectId, 1).Count;
        var clip = _store.ReadSceneClipBeatIds(ProjectId, 1)[0].ClipNumber;

        Assert.True(_writeBack.RemoveBeatsForClips(ProjectId, 1, new[] { clip }).Applied);
        _store.DeleteClip(ProjectId, 1, clip);

        var planner = new Stage2PlannerService(
            _store, Microsoft.Extensions.Logging.Abstractions.NullLogger<Stage2PlannerService>.Instance);
        Assert.True((await planner.PlanAsync(ProjectId, resolution: "480p", scenes: "1")).Ok);

        // Not a count — the replan re-derives the whole scene. What matters is that the deleted
        // line is not in it, which is exactly what a blueprint-only delete failed to achieve.
        var replanned = await _store.GetSceneDetailAsync(ProjectId, 1);
        Assert.DoesNotContain(
            replanned?.Clips ?? new List<ClipSummary>(),
            c => (c.VisualPrompt ?? "").Contains("lingering near the step", StringComparison.OrdinalIgnoreCase));
        Assert.True(before > 0);
    }

    /// <summary>
    /// The gate exists so a shot plan is never built from an unreviewed screenplay. A structured
    /// edit the user just performed is not that, and leaving the draft dirty would block the very
    /// replan this is meant to survive.
    /// </summary>
    [Fact]
    public async Task The_screenplay_stays_approved()
    {
        await PlanAsync();
        var clip = _store.ReadSceneClipBeatIds(ProjectId, 1)[0].ClipNumber;

        _writeBack.RemoveBeatsForClips(ProjectId, 1, new[] { clip });

        Assert.True(ScreenplayService.Get(_store, ProjectId).Status.Signed);
    }

    [Fact]
    public async Task Deleting_every_clip_in_a_scene_is_reported_as_a_scene_delete()
    {
        await PlanAsync();
        var all = _store.ReadSceneClipBeatIds(ProjectId, 2).Select(c => c.ClipNumber).ToList();

        var preview = _writeBack.PreviewDelete(ProjectId, 2, all);

        Assert.True(preview.EmptiesScene);
    }

    [Fact]
    public async Task Deleting_one_of_several_clips_is_not_a_scene_delete()
    {
        await PlanAsync();
        var first = _store.ReadSceneClipBeatIds(ProjectId, 1)[0].ClipNumber;

        var preview = _writeBack.PreviewDelete(ProjectId, 1, new[] { first });

        Assert.False(preview.EmptiesScene);
        Assert.True(preview.Paragraphs >= 1);
        Assert.Empty(preview.Unresolved);
    }

    [Fact]
    public async Task Removing_a_scene_pins_the_surviving_scene_numbers()
    {
        await PlanAsync();

        var result = _writeBack.RemoveScene(ProjectId, 1);

        Assert.True(result.Applied, result.Error);
        var model = FountainFormatter.Parse(Screenplay());
        Assert.All(model.Scenes, s => Assert.True(s.HasExplicitSceneNumber));
        // Scene 2 keeps its number rather than sliding down to 1, so the blueprint still agrees.
        Assert.Equal(new[] { 2 }, model.Scenes.Select(s => s.SceneNumber));
    }

    [Fact]
    public async Task An_added_clip_becomes_a_line_in_the_screenplay()
    {
        await PlanAsync();
        var before = ActionParagraphs(Screenplay(), 1);
        var fields = new ClipEditRequest
        {
            ProjectId = ProjectId,
            Scene = 1,
            Clip = 99,
            VisualPrompt = "THE TEACHER closes the register with a soft clap.",
        };

        var result = _writeBack.AddBeatForClip(ProjectId, 1, fields);

        Assert.True(result.Applied, result.Error);
        Assert.Equal(before + 1, ActionParagraphs(Screenplay(), 1));
        Assert.Contains("closes the register", Screenplay(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void With_no_screenplay_nothing_is_written_and_the_reason_is_given()
    {
        var result = _writeBack.RemoveBeatsForClips(ProjectId, 1, new[] { 1 });

        Assert.False(result.Applied);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
