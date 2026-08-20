using PageToMovie.Core.Utils;
using PageToMovie.Web.Components.Pages;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class ScenePlayGateTests
{
    [Fact]
    public void FormatClipLabel_is_scene_space_clip()
    {
        Assert.Equal("S02 C03", ScenePlayGate.FormatClipLabel(2, 3));
        Assert.Equal("S01 C01", ScenePlayGate.FormatClipLabel(1, 1));
    }

    [Fact]
    public void HasServerMp4_ignores_client_json_marker()
    {
        var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["scene_02_clip_03.mp4.client.json"] = 120,
            ["scene_02_clip_03.clip.json"] = 4000,
        };
        Assert.False(ScenePlayGate.HasServerMp4(index, 2, 3));
        Assert.Equal(new[] { 3 }, ScenePlayGate.MissingServerVideoClips(index, 2, new[] { 3 }));
    }

    [Fact]
    public void HasServerMp4_accepts_real_mp4()
    {
        var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["scene_02_clip_01.mp4"] = 50_000,
            ["scene_02_clip_02.mp4"] = 80_000,
            ["scene_02_clip_03.mp4"] = 90_000,
        };
        Assert.True(ScenePlayGate.HasServerMp4(index, 2, 1));
        Assert.Empty(ScenePlayGate.MissingServerVideoClips(index, 2, new[] { 1, 2, 3 }));
    }

    [Fact]
    public void DecideScenePlay_disabled_when_a_clip_is_missing()
    {
        var (canPlay, reason) = ScenePlayGate.DecideScenePlay(2, clipCount: 4, missingServerVideoClips: new[] { 3 });
        Assert.False(canPlay);
        Assert.Contains("S02 C03", reason, StringComparison.Ordinal);
        Assert.Contains("still missing", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecideScenePlay_enabled_when_all_clips_are_playable()
    {
        var (canPlay, reason) = ScenePlayGate.DecideScenePlay(2, clipCount: 4, missingServerVideoClips: Array.Empty<int>());
        Assert.True(canPlay);
        Assert.Null(reason);
    }

    [Fact]
    public void DecideScenePlay_local_blob_covers_missing_server_video()
    {
        var (canPlay, reason) = ScenePlayGate.DecideScenePlay(
            2, clipCount: 2, missingServerVideoClips: new[] { 2 }, hasLocalVideo: cn => cn == 2);
        Assert.True(canPlay);
        Assert.Null(reason);
    }

    [Fact]
    public void DecidePlaySelected_disabled_until_every_selected_scene_is_complete()
    {
        var selected = new[]
        {
            (Scene: 1, ClipCount: 2, MissingServerVideo: (IReadOnlyList<int>)Array.Empty<int>(), CompositeExists: false),
            (Scene: 2, ClipCount: 4, MissingServerVideo: (IReadOnlyList<int>)new[] { 3 }, CompositeExists: false),
        };
        var (canPlay, reason) = ScenePlayGate.DecidePlaySelected(selected);
        Assert.False(canPlay);
        Assert.Contains("S02 C03", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DecidePlaySelected_enabled_when_every_selected_scene_is_complete()
    {
        var selected = new[]
        {
            (Scene: 1, ClipCount: 2, MissingServerVideo: (IReadOnlyList<int>)Array.Empty<int>(), CompositeExists: false),
            (Scene: 2, ClipCount: 4, MissingServerVideo: (IReadOnlyList<int>)Array.Empty<int>(), CompositeExists: false),
        };
        var (canPlay, _) = ScenePlayGate.DecidePlaySelected(selected);
        Assert.True(canPlay);
    }

    [Fact]
    public void PerClipPlay_still_works_when_scene_has_a_hole()
    {
        var server = "http://localhost/api/projects/p/scenes/2/clips/1/video";
        var (src, error) = Review.ReviewPlayback.DecideClipPlay(
            new[] { server }, null, 2, 1, mediaFolderConnected: false);
        Assert.Equal(server, src);
        Assert.Null(error);

        var sceneGate = ScenePlayGate.DecideScenePlay(2, clipCount: 4, missingServerVideoClips: new[] { 3 });
        Assert.False(sceneGate.CanPlay);
    }

    [Fact]
    public void FormatPlayFailedError_names_every_missing_clip()
    {
        var msg = ScenePlayGate.FormatPlayFailedError("clips", new[] { "S02 C01", "S02 C03" });
        Assert.Contains("S02 C01", msg, StringComparison.Ordinal);
        Assert.Contains("S02 C03", msg, StringComparison.Ordinal);
        Assert.Contains("404", msg, StringComparison.Ordinal);
        Assert.Contains("Could not play the selected clips", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMissingClipPlayError_uses_spaced_clip_label()
    {
        var msg = ClientVideoStitchService.FormatMissingClipPlayError(new[] { "S01 C01" }, mediaFolderConnected: false);
        Assert.Contains("S01 C01", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("404", msg, StringComparison.Ordinal);
    }
}
