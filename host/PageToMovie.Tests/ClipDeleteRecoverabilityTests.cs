using PageToMovie.Core.Utils;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Deleting a clip used to unlink its assembled video. The blueprint is versioned in the project's
/// git repo but <c>*.mp4</c> is gitignored, so Scene History → revert could bring the clip row back
/// and never its bytes: the row pointed at nothing and the clip read as never generated. Delete now
/// parks the media in the same <c>assets/video/.trash/</c> the take versions use, so the row and the
/// video come back together.
/// </summary>
public sealed class ClipDeleteRecoverabilityTests : IDisposable
{
    private readonly string _root;
    private readonly string _dir;
    private readonly string _videoDir;
    private readonly ProjectStore _store;

    private const string Blueprint = """
        {
          "scenes": [
            {
              "scene_number": 1,
              "stage1_beat_map": ["sb_one", "sb_two"],
              "veo_clips": [
                { "clip_number": 1, "visual_prompt": "one", "stage1_beat_id": "sb_one" },
                { "clip_number": 2, "visual_prompt": "two", "stage1_beat_id": "sb_two" }
              ]
            }
          ]
        }
        """;

    public ClipDeleteRecoverabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs_cliprec_" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "projects", "Demo");
        _videoDir = Path.Combine(_dir, "assets", "video");
        Directory.CreateDirectory(_videoDir);
        File.WriteAllText(Path.Combine(_dir, "project.json"), """{"id":"Demo"}""");
        File.WriteAllText(Path.Combine(_dir, "pipeline_config.json"),
            """{"blueprint_file":"blueprint.clips.grok.json","model_name":"grok-imagine-video"}""");
        File.WriteAllText(Path.Combine(_dir, "blueprint.clips.grok.json"), Blueprint);
        _store = new ProjectStore(Options.Create(
            new PageToMovieOptions { WorkspaceRoot = _root, EnableReadCaches = false }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* */ }
    }

    private string Video(string name) => Path.Combine(_videoDir, name);
    private string Trash(string name) => Path.Combine(_videoDir, ".trash", name);

    private void WriteClipMedia(int clip)
    {
        // A clip is a take plus the pointer naming it — the shape generate writes.
        File.WriteAllBytes(Video(ClipTakeNaming.TakeMp4FileName(1, clip, 1)), new byte[2048]);
        File.WriteAllText(
            Video(ClipTakeNaming.TakeSidecarFileName(1, clip, 1)), """{"source_file_id":"file_abc"}""");
        ClipSidecarService.WriteCurrentTake(_videoDir, 1, clip, 1);
    }

    [Fact]
    public void Deleting_a_clip_parks_its_video_in_the_trash_rather_than_unlinking_it()
    {
        WriteClipMedia(2);

        Assert.True(_store.DeleteClip("Demo", scene: 1, clip: 2));

        Assert.False(File.Exists(Video(ClipTakeNaming.TakeMp4FileName(1, 2, 1))));
        Assert.True(File.Exists(Trash(ClipTakeNaming.TakeMp4FileName(1, 2, 1))));
    }

    /// <summary>
    /// The sidecar is where <c>source_file_id</c> lives — the provider copy of the same video. It
    /// travels with the bytes so a restore brings back a clip that can still stream.
    /// </summary>
    [Fact]
    public void The_sidecar_travels_with_the_video()
    {
        WriteClipMedia(2);

        _store.DeleteClip("Demo", scene: 1, clip: 2);

        Assert.True(File.Exists(Trash(ClipTakeNaming.TakeSidecarFileName(1, 2, 1))));
        Assert.Contains("file_abc", File.ReadAllText(Trash(ClipTakeNaming.TakeSidecarFileName(1, 2, 1))));
    }

    [Fact]
    public void Deleting_a_clip_still_reports_that_it_had_a_video()
    {
        WriteClipMedia(2);
        Assert.True(_store.DeleteClip("Demo", scene: 1, clip: 2));

        // …and a clip with no media at all is still a blueprint-only delete, not an error.
        Assert.True(_store.DeleteClip("Demo", scene: 1, clip: 1));
    }

    /// <summary>
    /// Verification state is deleted, not trashed: Add-clip reuses max(existing) + 1, so a stale QA
    /// verdict left behind would attach itself to whatever new clip lands on this number.
    /// </summary>
    [Fact]
    public void Verification_state_is_not_kept()
    {
        WriteClipMedia(2);
        var verification = ClipDialogueVerificationService.BuildVerificationPath(_dir, 1, 2);
        Directory.CreateDirectory(Path.GetDirectoryName(verification)!);
        File.WriteAllText(verification, "{}");

        _store.DeleteClip("Demo", scene: 1, clip: 2);

        Assert.False(File.Exists(verification));
        Assert.False(File.Exists(Trash(Path.GetFileName(verification))));
    }

    [Fact]
    public void Deleting_a_clip_parks_every_take_and_drops_the_pointer()
    {
        // A clip is its takes. Trashing only scene_SS_clip_CC.mp4 left them all on disk, so the
        // deleted clip resolved again the moment anything asked for it.
        File.WriteAllBytes(Video(ClipTakeNaming.TakeMp4FileName(1, 2, 1)), new byte[2048]);
        File.WriteAllBytes(Video(ClipTakeNaming.TakeMp4FileName(1, 2, 2)), new byte[2048]);
        File.WriteAllText(Video(ClipTakeNaming.TakeSidecarFileName(1, 2, 2)), """{"source_file_id":"file_abc"}""");
        ClipSidecarService.WriteCurrentTake(_videoDir, 1, 2, 2);

        Assert.True(_store.DeleteClip("Demo", scene: 1, clip: 2));

        Assert.Null(ClipSidecarService.ResolveClipMediaPath(_videoDir, 1, 2));
        Assert.False(File.Exists(Video(ClipTakeNaming.TakeMp4FileName(1, 2, 1))));
        Assert.False(File.Exists(Video(ClipTakeNaming.TakeMp4FileName(1, 2, 2))));
        Assert.False(File.Exists(Video(ClipTakeNaming.CurrentTakePointerFileName(1, 2))));

        Assert.True(File.Exists(Trash(ClipTakeNaming.TakeMp4FileName(1, 2, 1))));
        Assert.True(File.Exists(Trash(ClipTakeNaming.TakeMp4FileName(1, 2, 2))));
        Assert.True(File.Exists(Trash(ClipTakeNaming.TakeSidecarFileName(1, 2, 2))));
    }
}
