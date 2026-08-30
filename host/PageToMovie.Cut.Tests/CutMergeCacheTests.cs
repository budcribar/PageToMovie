using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutMergeCacheTests
{
    [Fact]
    public void Dirty_one_scene_rebuilds_only_that_segment()
    {
        var clips = FeatureClips(52);
        var titles = new List<CutTextClip>
        {
            new() { Text = "Hi", StartSec = SceneStart(40) + 2, Seconds = 2 },
        };
        var plan = CutMergeCache.Build(clips, titles, null, null);
        var saved = CutMergeCache.ManifestOf(plan);
        Assert.Equal(52, plan.Scenes.Count);
        Assert.Equal(51, plan.Joins.Count);

        titles[0].Text = "Bye";
        var dirty = CutMergeCache.Diff(CutMergeCache.Build(clips, titles, null, null), saved);

        Assert.Equal(new[] { 40 }, dirty.RebuildScenes);
        Assert.Empty(dirty.RebuildJoins);
        Assert.False(dirty.MovieFresh);
        Assert.False(dirty.PictureFresh);
        Assert.True(dirty.MustStitch);
        Assert.False(dirty.RemixMusicOnly);
        Assert.True(CutComposeContract.MustStitch(dirty, "blob:movie"));
    }

    [Fact]
    public void Unchanged_film_does_not_stitch_on_play_or_export()
    {
        var clips = FeatureClips(52);
        var titles = new[] { new CutTextClip { Text = "Hi", StartSec = 2, Seconds = 2 } };
        var music = ScoreAt(12, 1, 8);
        var plan = CutMergeCache.Build(clips, titles, "score.mp3", music);
        var saved = CutMergeCache.ManifestOf(plan);
        var fresh = CutMergeCache.Diff(plan, saved);

        Assert.True(fresh.MovieFresh);
        Assert.True(fresh.PictureFresh);
        Assert.True(fresh.MusicFresh);
        Assert.Empty(fresh.RebuildScenes);
        Assert.Empty(fresh.RebuildJoins);
        Assert.False(fresh.MustStitch);
        Assert.True(CutComposeContract.CanReuseExport("blob:movie", fresh));
        Assert.False(CutComposeContract.MustStitch(fresh, "blob:movie"));
        Assert.True(CutPlayMerge.IsFreshMerge(plan.MovieFingerprint, clips, titles, "score.mp3", music));
    }

    [Fact]
    public void Join_look_or_adjacent_edge_rebuilds_the_join_not_clean_scenes()
    {
        var clips = FeatureClips(52);
        var plan = CutMergeCache.Build(clips, [], null, null);
        var saved = CutMergeCache.ManifestOf(plan);

        clips[38].JoinOverride = CutJoinKind.FadeWhite;
        var look = CutMergeCache.Diff(CutMergeCache.Build(clips, [], null, null), saved);
        Assert.Empty(look.RebuildScenes);
        Assert.Equal(new[] { 39 }, look.RebuildJoins);

        clips[38].JoinOverride = null;
        clips[39].ApplyInOut(0.4, 8);
        var edge = CutMergeCache.Diff(CutMergeCache.Build(clips, [], null, null), saved);
        Assert.Equal(new[] { 40 }, edge.RebuildScenes);
        Assert.Equal(new[] { 39 }, edge.RebuildJoins);
        Assert.DoesNotContain(1, edge.RebuildScenes);
        Assert.DoesNotContain(52, edge.RebuildScenes);
    }

    [Fact]
    public void Muting_a_clip_rebuilds_its_scene_and_the_joins_that_carry_its_audio()
    {
        var clips = FeatureClips(52);
        var plan = CutMergeCache.Build(clips, [], null, null);
        var saved = CutMergeCache.ManifestOf(plan);

        clips[39].Muted = true;
        var dirty = CutMergeCache.Diff(CutMergeCache.Build(clips, [], null, null), saved);

        // A cached scene piece and a cross-fade both carry the clip's own audio, so silencing it
        // has to invalidate them — otherwise Make movie hands back the old sound.
        Assert.Equal(new[] { 40 }, dirty.RebuildScenes);
        Assert.Equal(new[] { 39, 40 }, dirty.RebuildJoins);
        Assert.False(dirty.MovieFresh);
        Assert.False(dirty.PictureFresh);
    }

    [Fact]
    public void Moving_the_score_remixes_cached_picture_and_does_not_rebuild_scenes()
    {
        var clips = FeatureClips(8);
        var music = ScoreAt(0, 0, 20);
        var plan = CutMergeCache.Build(clips, [], "score.mp3", music);
        var saved = CutMergeCache.ManifestOf(plan);

        music.SetStart(18);
        var moved = CutMergeCache.Diff(CutMergeCache.Build(clips, [], "score.mp3", music), saved);

        Assert.True(moved.PictureFresh);
        Assert.False(moved.MusicFresh);
        Assert.True(moved.RemixMusicOnly);
        Assert.Empty(moved.RebuildScenes);
        Assert.Empty(moved.RebuildJoins);
        Assert.True(moved.MustStitch);
        Assert.False(CutComposeContract.CanReuseExport("blob:movie", moved));
    }

    [Fact]
    public void Reload_reuses_cached_urls_until_a_scene_fingerprint_changes()
    {
        var clips = FeatureClips(6);
        var titles = new List<CutTextClip>
        {
            new() { Text = "Open", StartSec = SceneStart(5) + 1, Seconds = 2 },
        };
        var plan = CutMergeCache.Build(clips, titles, null, null);
        var cache = new CutMergeRuntime();
        cache.RememberPlan(plan);
        foreach (var scene in plan.Scenes)
            cache.RememberScene(scene.Scene, "blob:s" + scene.Scene, scene.Fingerprint);

        titles[0].Text = "Later";
        var next = CutMergeCache.Build(clips, titles, null, null);
        var diff = CutMergeCache.Diff(next, cache.Built);
        Assert.Equal(new[] { 5 }, diff.RebuildScenes);

        foreach (var scene in next.Scenes)
        {
            var url = cache.SceneUrlIfFresh(scene.Scene, scene.Fingerprint);
            if (scene.Scene == 5)
                Assert.Null(url);
            else
                Assert.Equal("blob:s" + scene.Scene, url);
        }
    }

    [Fact]
    public void Persist_manifest_round_trips_in_cut_project_json()
    {
        var clips = FeatureClips(3);
        var plan = CutMergeCache.Build(clips, [], "score.mp3", ScoreAt(4, 0, 10));
        var json = CutProjectFile.Serialize(
            clips, "score.mp3", movieFingerprint: plan.MovieFingerprint, mergeCache: CutMergeCache.ManifestOf(plan));
        Assert.Contains("mergeCache", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cut.cache/s01.mp4", json, StringComparison.Ordinal);

        var reload = FeatureClips(3);
        Assert.True(CutProjectFile.TryApply(
            reload, json, out _, out _, out var movieFp, out _, out var cache));
        Assert.Equal(plan.MovieFingerprint, movieFp);
        Assert.Equal(plan.PictureFingerprint, cache.PictureFingerprint);
        Assert.Equal(3, cache.Scenes.Count);
        var again = CutMergeCache.Diff(CutMergeCache.Build(reload, [], "score.mp3", ScoreAt(4, 0, 10)), cache);
        Assert.True(again.MovieFresh);
        Assert.Empty(again.RebuildScenes);
    }

    [Fact]
    public void Cache_file_names_are_not_takes()
    {
        Assert.True(CutMergeCache.TryParseSceneFile("cut.cache/s40.mp4", out var scene));
        Assert.Equal(40, scene);
        Assert.True(CutMergeCache.TryParseJoinFile("cut.cache/j39.mp4", out var from));
        Assert.Equal(39, from);
        Assert.True(CutMergeCache.IsPictureFileName("cut.cache/picture.mp4"));
        Assert.False(CutClipNaming.IsUsableClipMp4("cut.cache/s40.mp4"));
        Assert.False(CutPlayMerge.IsMovieFileName("cut.cache/picture.mp4"));
    }

    private static CutClip[] FeatureClips(int scenes)
    {
        var clips = new CutClip[scenes];
        for (var i = 0; i < scenes; i++)
            clips[i] = NewClip(i + 1, 1, 8);
        return clips;
    }

    private static double SceneStart(int scene) => (scene - 1) * 8;

    private static CutMusic ScoreAt(double start, double inn, double outt)
    {
        var music = new CutMusic { FileName = "score.mp3" };
        music.SetDuration(30);
        music.SetStart(start);
        music.ApplyInOut(inn, outt);
        return music;
    }

    private static CutClip NewClip(int scene, int clip, double duration)
    {
        var c = new CutClip { Scene = scene, Clip = clip };
        c.Takes.Add(new CutTake
        {
            Take = 1,
            FileName = $"scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
            RelativePath = $"assets/video/scene_{scene:D2}_clip_{clip:D2}_take_01.mp4",
        });
        c.ActiveTakeNumber = 1;
        c.SeedSelection();
        c.SetDuration(duration);
        return c;
    }
}
