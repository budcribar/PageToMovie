using PageToMovie.Core.Models;
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
    public void SceneMediaPresenceIndex_buckets_take_files_and_markers_once()
    {
        var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["scene_02_clip_01_take_03.mp4"] = 50_000,
            ["scene_02_clip_02_take_04.mp4.client.json"] = 120,
            ["scene_02_clip_03_take_02.clip.json"] = 400,
            ["unrelated.json"] = 99,
        };

        var presence = new SceneMediaPresenceIndex(index);
        Assert.True(presence.HasServerMp4(2, 1));
        Assert.False(presence.HasServerMp4(2, 2));
        Assert.True(presence.IsPresent(2, 1));
        Assert.True(presence.IsPresent(2, 2));
        Assert.True(presence.IsPresent(2, 3));
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

        var present = ScenePlayGate.DecideOneClipPlay(2, 1, hasServerVideo: true);
        Assert.True(present.CanPlay);
        Assert.Null(present.DisabledReason);
    }

    [Fact]
    public void DecideOneClipPlay_enabled_for_present_clip_when_sibling_is_missing()
    {
        var hole = ScenePlayGate.DecideScenePlay(2, clipCount: 4, missingServerVideoClips: new[] { 3 });
        Assert.False(hole.CanPlay);
        Assert.Contains("S02 C03", hole.DisabledReason, StringComparison.Ordinal);

        var clip1 = ScenePlayGate.DecideOneClipPlay(2, 1, hasServerVideo: true);
        var clip2 = ScenePlayGate.DecideOneClipPlay(2, 2, hasServerVideo: false, hasLocalVideo: true);
        Assert.True(clip1.CanPlay);
        Assert.True(clip2.CanPlay);
    }

    [Fact]
    public void DecideOneClipPlay_disabled_for_sidecar_only_clip()
    {
        Assert.False(ScenePlayGate.IsClipPlayable(sizeBytes: 120));
        var (canPlay, reason) = ScenePlayGate.DecideOneClipPlay(2, 3, hasServerVideo: false);
        Assert.False(canPlay);
        Assert.Contains("S02 C03", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IsClipPlayable_unknown_size_does_not_disable()
    {
        Assert.True(ScenePlayGate.IsClipPlayable(sizeBytes: 0));
        Assert.True(ScenePlayGate.IsClipPlayable(sizeBytes: 0, hasLocalVideo: true));
        Assert.True(ScenePlayGate.IsClipPlayable(sizeBytes: 120, hasLocalVideo: true));
        Assert.False(ScenePlayGate.IsClipPlayable(sizeBytes: 120));
    }

    [Fact]
    public void LocalClipPlayableCache_collects_missing_server_and_unknown_size_clips()
    {
        var scenes = new[]
        {
            new SceneSummary { SceneNumber = 1, ClipsMissingServerVideo = [2] },
            new SceneSummary { SceneNumber = 2, ClipsMissingServerVideo = [] },
        };
        var detail = new SceneDetail
        {
            SceneNumber = 1,
            Clips =
            [
                new ClipSummary { ClipNumber = 1, SizeBytes = 80_000 },
                new ClipSummary { ClipNumber = 2, SizeBytes = 0 },
            ],
        };

        var needed = LocalClipPlayableCache.CollectNeeded(scenes, detail);
        Assert.Contains((1, 2), needed);
        Assert.DoesNotContain((1, 1), needed);
        Assert.DoesNotContain((2, 1), needed);
    }

    [Fact]
    public void SceneMediaPresenceIndex_current_take_pointer_is_present_not_server_mp4()
    {
        var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["scene_01_clip_02.current.json"] = 20,
            ["scene_01_clip_02_take_01.clip.json"] = 400,
        };

        var presence = new SceneMediaPresenceIndex(index);
        Assert.True(presence.IsPresent(1, 2));
        Assert.False(presence.HasServerMp4(1, 2));
    }

    [Fact]
    public void IsClipPlayableFromSceneMissingList_does_not_require_the_rest_of_the_scene()
    {
        var missing = (IReadOnlyList<int>)new[] { 3 };
        Assert.True(ScenePlayGate.IsClipPlayableFromSceneMissingList(1, missing));
        Assert.True(ScenePlayGate.IsClipPlayableFromSceneMissingList(2, missing));
        Assert.False(ScenePlayGate.IsClipPlayableFromSceneMissingList(3, missing));
        Assert.True(ScenePlayGate.IsClipPlayableFromSceneMissingList(3, missing, hasLocalVideo: true));
    }

    [Fact]
    public void DecideScenePlay_enabled_when_every_clip_exists()
    {
        var (canPlay, reason) = ScenePlayGate.DecideScenePlay(
            2, clipCount: 4, missingServerVideoClips: Array.Empty<int>());
        Assert.True(canPlay);
        Assert.Null(reason);
        Assert.True(ScenePlayGate.DecideOneClipPlay(2, 3, hasServerVideo: true).CanPlay);
    }

    [Fact]
    public void DecideScenePlay_disabled_while_media_is_syncing_even_if_clips_look_complete()
    {
        var (canPlay, reason) = ScenePlayGate.DecideScenePlay(
            2,
            clipCount: 4,
            missingServerVideoClips: Array.Empty<int>(),
            mediaSyncing: true,
            mediaSyncReason: ScenePlayGate.MediaStillDownloadingReason(3, 68, "Downloading clips (3/68)"));
        Assert.False(canPlay);
        Assert.Contains("Downloading", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3/68", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void After_sync_DecideScenePlay_follows_completeness()
    {
        var complete = ScenePlayGate.DecideScenePlay(
            2, clipCount: 4, missingServerVideoClips: Array.Empty<int>(), mediaSyncing: false);
        Assert.True(complete.CanPlay);
        Assert.Null(complete.DisabledReason);

        var hole = ScenePlayGate.DecideScenePlay(
            2, clipCount: 4, missingServerVideoClips: new[] { 3 }, mediaSyncing: false);
        Assert.False(hole.CanPlay);
        Assert.Contains("S02 C03", hole.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void DecidePlaySelected_disabled_while_media_is_syncing()
    {
        var selected = new[]
        {
            (Scene: 1, ClipCount: 2, MissingServerVideo: (IReadOnlyList<int>)Array.Empty<int>(), CompositeExists: false),
        };
        var (canPlay, reason) = ScenePlayGate.DecidePlaySelected(
            selected,
            mediaSyncing: true,
            mediaSyncReason: ScenePlayGate.MediaStillDownloadingReason());
        Assert.False(canPlay);
        Assert.Contains("still downloading", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locally_present_clip_can_still_play_while_media_is_syncing()
    {
        var scene = ScenePlayGate.DecideScenePlay(
            2, clipCount: 4, missingServerVideoClips: new[] { 3 }, mediaSyncing: true);
        Assert.False(scene.CanPlay);

        var localClip = ScenePlayGate.DecideOneClipPlay(2, 1, hasServerVideo: false, hasLocalVideo: true);
        var serverClip = ScenePlayGate.DecideOneClipPlay(2, 2, hasServerVideo: true);
        Assert.True(localClip.CanPlay);
        Assert.True(serverClip.CanPlay);
    }

    [Fact]
    public void MediaStillDownloadingReason_prefers_last_status_then_counts()
    {
        Assert.Equal("Checking files…", ScenePlayGate.MediaStillDownloadingReason(1, 10, "Checking files…"));
        Assert.Equal("Media is still downloading (2/5)", ScenePlayGate.MediaStillDownloadingReason(2, 5));
        Assert.Equal("Media is still downloading", ScenePlayGate.MediaStillDownloadingReason());
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
