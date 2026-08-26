using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A clip generated as a video-extend literally begins on its predecessor's last frame. Promote a
/// different take of that predecessor and the clip now opens on a frame the film no longer
/// contains — a visible jump at the join, and nothing was saying so. Promote wrote the pointer and
/// stopped; the staleness check looked at QA verdicts and blueprint mtime, neither of which knows
/// that a predecessor changed underneath.
/// </summary>
/// <remarks>
/// Deliberately computed from present state rather than flagged when a take is promoted. A flag
/// written at promote time would rot the other way round: promote a different take and back again,
/// and the flag would still claim a problem that had resolved itself. These cover that both ways.
/// </remarks>
public sealed class PredecessorChangedStalenessTests : IDisposable
{
    private readonly string _root;
    private readonly string _videoDir;
    private readonly ProjectStore _store;
    private const string ProjectId = "Demo";

    public PredecessorChangedStalenessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_pred_" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(_root, "projects", ProjectId);
        _videoDir = Path.Combine(proj, "assets", "video");
        Directory.CreateDirectory(_videoDir);
        File.WriteAllText(Path.Combine(proj, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(proj, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.grok.json","model_name":"grok-imagine-video"}""");
        File.WriteAllText(Path.Combine(proj, "blueprint.clips.grok.json"), """
            {
              "scenes": [
                {
                  "scene_number": 1,
                  "stage1_beat_map": ["sb_one", "sb_two"],
                  "veo_clips": [
                    { "clip_number": 1, "visual_prompt": "one", "stage1_beat_id": "sb_one",
                      "veo_continuation_source": "none" },
                    { "clip_number": 2, "visual_prompt": "two", "stage1_beat_id": "sb_two",
                      "veo_continuation_source": "extend_previous" }
                  ]
                }
              ]
            }
            """);
        _store = new ProjectStore(Options.Create(
            new PageToMovieOptions { WorkspaceRoot = _root, EnableReadCaches = false }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* */ }
    }

    private string P(string name) => Path.Combine(_videoDir, name);

    /// <summary>A take of a clip, with bytes so it counts as present.</summary>
    private void WriteTake(int clip, int take, int? extendedFromTake = null)
    {
        File.WriteAllBytes(P($"scene_01_clip_{clip:D2}_take_{take:D2}.mp4"), new byte[4096]);
        var extra = extendedFromTake is { } n ? $",\"extended_from_take\":{n}" : "";
        File.WriteAllText(
            P($"scene_01_clip_{clip:D2}_take_{take:D2}.clip.json"),
            $$"""{"schema_version":"clip_sidecar.v1","scene":1,"clip":{{clip}},"take":{{take}},"duration_seconds":5{{extra}}}""");
    }

    private void PointAt(int clip, int take) =>
        File.WriteAllText(P($"scene_01_clip_{clip:D2}.current.json"),
            $$"""{"scene":1,"clip":{{clip}},"take":{{take}}}""");

    private async Task<string?> StaleReasonForClip2()
    {
        var detail = await _store.GetSceneDetailAsync(ProjectId, 1);
        var clip = detail!.Clips.First(c => c.ClipNumber == 2);
        return clip.IsStale ? clip.StaleReason : null;
    }

    [Fact]
    public async Task A_clip_whose_predecessor_take_changed_is_stale()
    {
        WriteTake(clip: 1, take: 1);
        WriteTake(clip: 1, take: 2);
        WriteTake(clip: 2, take: 1, extendedFromTake: 1);
        PointAt(clip: 1, take: 2);   // a different take of the predecessor was promoted
        PointAt(clip: 2, take: 1);

        Assert.Equal("predecessor_changed", await StaleReasonForClip2());
    }

    [Fact]
    public async Task A_clip_still_on_the_take_it_continued_is_not_stale()
    {
        WriteTake(clip: 1, take: 1);
        WriteTake(clip: 2, take: 1, extendedFromTake: 1);
        PointAt(clip: 1, take: 1);
        PointAt(clip: 2, take: 1);

        Assert.Null(await StaleReasonForClip2());
    }

    /// <summary>
    /// The reason this is computed rather than flagged: undoing the promote must undo the warning.
    /// </summary>
    [Fact]
    public async Task Promoting_back_to_the_original_take_clears_it()
    {
        WriteTake(clip: 1, take: 1);
        WriteTake(clip: 1, take: 2);
        WriteTake(clip: 2, take: 1, extendedFromTake: 1);
        PointAt(clip: 2, take: 1);

        PointAt(clip: 1, take: 2);
        Assert.Equal("predecessor_changed", await StaleReasonForClip2());

        PointAt(clip: 1, take: 1);
        Assert.Null(await StaleReasonForClip2());
    }

    /// <summary>A clip generated before the take was recorded says nothing — unknown, not wrong.</summary>
    [Fact]
    public async Task A_clip_with_no_recorded_predecessor_take_is_silent()
    {
        WriteTake(clip: 1, take: 1);
        WriteTake(clip: 1, take: 2);
        WriteTake(clip: 2, take: 1);   // no extended_from_take
        PointAt(clip: 1, take: 2);
        PointAt(clip: 2, take: 1);

        Assert.Null(await StaleReasonForClip2());
    }

    /// <summary>The first clip of a scene continues nothing, so it can never go stale this way.</summary>
    [Fact]
    public async Task The_first_clip_of_a_scene_is_never_stale_for_this_reason()
    {
        WriteTake(clip: 1, take: 1, extendedFromTake: 9);
        PointAt(clip: 1, take: 1);

        var detail = await _store.GetSceneDetailAsync(ProjectId, 1);
        var first = detail!.Clips.First(c => c.ClipNumber == 1);
        Assert.NotEqual("predecessor_changed", first.StaleReason);
    }
}
