using Microsoft.JSInterop;
using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

/// <summary>
/// Annette export: cut.cache had only s01 + s17 + j01 + a stub picture.mp4
/// (first scene, then a frozen last-scene frame with digital silence).
/// Fingerprints still said the cut was complete. Play / Make movie must
/// not republish that stub.
/// </summary>
public class CutIncompleteMergeTests
{
    [Fact]
    public void Missing_scene_files_are_not_a_fresh_picture_or_movie()
    {
        var clips = FeatureClips(17);
        var plan = CutMergeCache.Build(clips, [], null, null);
        var saved = CutMergeCache.ManifestOf(plan);
        var presentScenes = new HashSet<int> { 1, 17 };
        var presentJoins = new HashSet<int> { 1 };

        var byFingerprint = CutMergeCache.Diff(plan, saved);
        Assert.True(byFingerprint.PictureFresh);
        Assert.True(byFingerprint.MovieFresh);
        Assert.Empty(byFingerprint.RebuildScenes);

        var dirty = CutMergeCache.Diff(plan, saved, presentScenes, presentJoins);
        Assert.False(dirty.PictureFresh);
        Assert.False(dirty.MovieFresh);
        Assert.False(dirty.RemixMusicOnly);
        Assert.True(dirty.MustStitch);
        Assert.Equal(Enumerable.Range(2, 15).ToArray(), dirty.RebuildScenes);
        Assert.False(CutMergeCache.CanReusePicture(dirty, "blob:stub-picture"));
        Assert.False(CutMergeCache.CanReuseMovie(dirty, "blob:stub-movie"));
        Assert.False(CutComposeContract.CanReuseExport("blob:stub-movie", dirty));
        Assert.True(CutComposeContract.MustStitch(dirty, "blob:stub-movie"));
    }

    [Fact]
    public void Complete_cache_still_reuses_picture_and_movie()
    {
        var clips = FeatureClips(4);
        var plan = CutMergeCache.Build(clips, [], null, null);
        var saved = CutMergeCache.ManifestOf(plan);
        var scenes = plan.Scenes.Select(s => s.Scene).ToHashSet();
        var joins = plan.Joins.Where(j => j.Encodes).Select(j => j.FromScene).ToHashSet();

        var fresh = CutMergeCache.Diff(plan, saved, scenes, joins);
        Assert.True(fresh.PictureFresh);
        Assert.True(fresh.MovieFresh);
        Assert.Empty(fresh.RebuildScenes);
        Assert.True(CutMergeCache.CanReusePicture(fresh, "blob:picture"));
        Assert.True(CutMergeCache.CanReuseMovie(fresh, "blob:movie"));
        Assert.True(CutMergeCache.HasEverySegment(plan, scenes, joins));
    }

    [Fact]
    public void Runtime_with_only_first_and_last_scene_drops_the_stub_picture()
    {
        var clips = FeatureClips(17);
        var plan = CutMergeCache.Build(clips, [], null, null);
        var runtime = new CutMergeRuntime();
        runtime.RememberPlan(plan);
        runtime.RememberScene(1, "blob:s01", plan.Scenes[0].Fingerprint);
        runtime.RememberScene(17, "blob:s17", plan.Scenes[^1].Fingerprint);
        runtime.PictureUrl = "blob:stub-picture";

        Assert.False(CutMergeCache.HasEverySegment(plan, runtime));
        CutMergeCache.RejectIncompletePicture(runtime, plan);
        Assert.Null(runtime.PictureUrl);
        Assert.True(string.IsNullOrWhiteSpace(runtime.Built.PictureFingerprint));
        Assert.True(string.IsNullOrWhiteSpace(runtime.Built.MovieFingerprint));

        var dirty = CutMergeCache.Diff(plan, runtime);
        Assert.False(dirty.PictureFresh);
        Assert.Contains(2, dirty.RebuildScenes);
        Assert.Contains(16, dirty.RebuildScenes);
        Assert.DoesNotContain(1, dirty.RebuildScenes);
        Assert.DoesNotContain(17, dirty.RebuildScenes);
        Assert.False(CutMergeCache.ShouldPersistPicture(false, "blob:stub-picture"));
        Assert.False(CutMergeCache.ShouldPersistPicture(true, null));
        Assert.True(CutMergeCache.ShouldPersistPicture(true, "blob:picture"));
    }

    [Fact]
    public void Play_and_make_movie_do_not_reuse_an_annette_stub()
    {
        var clips = FeatureClips(17);
        var compose = new CutComposeService(new UnusedJs());
        var plan = CutMergeCache.Build(clips, [], compose.AudioFileName, compose.Music);
        compose.Cache.RememberPlan(plan);
        compose.Cache.RememberScene(1, "blob:s01", plan.Scenes[0].Fingerprint);
        compose.Cache.RememberScene(17, "blob:s17", plan.Scenes[^1].Fingerprint);
        compose.Cache.PictureUrl = "blob:stub-picture";
        compose.AttachExistingMerge("blob:stub-movie", clips.Length);

        var reused = compose.TryReuseMovie(clips, [], (_, _) => { }, onPrefix: null);
        Assert.False(reused);
        Assert.Null(compose.Cache.PictureUrl);

        var payload = compose.BuildComposePlan(clips, []);
        Assert.Null(payload.ReuseMovieUrl);
        Assert.Null(payload.ReusePictureUrl);
        Assert.Equal(17, payload.Scenes.Count);
        Assert.Equal("blob:s01", payload.Scenes[0].Url);
        Assert.Equal("blob:s17", payload.Scenes[^1].Url);
        Assert.All(payload.Scenes.Skip(1).Take(15), scene => Assert.True(string.IsNullOrWhiteSpace(scene.Url)));
        Assert.True(compose.LastDiff.MustStitch);
        Assert.Equal(Enumerable.Range(2, 15).ToArray(), compose.LastDiff.RebuildScenes);
    }

    [Fact]
    public void Happy_path_plan_still_reuses_a_complete_picture()
    {
        var clips = FeatureClips(3);
        var compose = new CutComposeService(new UnusedJs());
        var plan = CutMergeCache.Build(clips, [], compose.AudioFileName, compose.Music);
        compose.Cache.RememberPlan(plan);
        foreach (var scene in plan.Scenes)
            compose.Cache.RememberScene(scene.Scene, "blob:s" + scene.Scene, scene.Fingerprint);
        foreach (var join in plan.Joins.Where(j => j.Encodes))
            compose.Cache.RememberJoin(join.FromScene, "blob:j" + join.FromScene, join.Fingerprint);
        compose.Cache.PictureUrl = "blob:picture";
        compose.AttachExistingMerge("blob:movie", clips.Length);

        Assert.True(compose.TryReuseMovie(clips, [], (_, _) => { }, onPrefix: null));
        var payload = compose.BuildComposePlan(clips, []);
        Assert.Equal("blob:movie", payload.ReuseMovieUrl);
        Assert.Equal("blob:picture", payload.ReusePictureUrl);
        Assert.All(payload.Scenes, scene => Assert.False(string.IsNullOrWhiteSpace(scene.Url)));
        Assert.False(compose.LastDiff.MustStitch);
    }

    [Fact]
    public void Incomplete_merge_error_is_the_operator_copy()
    {
        Assert.Equal(
            "The movie is missing scenes. Play or Make movie again so every scene is included.",
            CutComposeContract.IncompleteMergeError);
        Assert.Equal(
            CutComposeContract.IncompleteMergeError,
            CutComposeContract.OperatorComposeError(CutComposeContract.IncompleteMergeError, download: true));
    }

    [Fact]
    public void Persist_and_folder_attach_refuse_an_incomplete_picture()
    {
        var host = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));
        var editor = File.ReadAllText(Path.Combine(
            host, "PageToMovie.Cut.Components", "Pages", "CutEditor.razor.cs"));
        var folder = File.ReadAllText(Path.Combine(
            host, "PageToMovie.Cut.Components", "Services", "CutFolderService.cs"));
        var compose = File.ReadAllText(Path.Combine(
            host, "PageToMovie.Cut.Components", "Services", "CutComposeService.cs"));

        Assert.Contains("HasEverySegment(compose.CurrentPlan, compose.Cache)", editor, StringComparison.Ordinal);
        Assert.Contains("RejectIncompletePicture(compose.Cache, compose.Cache.Built.Scenes)", folder, StringComparison.Ordinal);
        Assert.Contains("ShouldPersistPicture(", folder, StringComparison.Ordinal);
        Assert.Contains("HasEverySegment(CurrentPlan, Cache)", compose, StringComparison.Ordinal);
        Assert.Contains("IncompleteMergeError", compose, StringComparison.Ordinal);
    }

    private static CutClip[] FeatureClips(int scenes)
    {
        var clips = new CutClip[scenes];
        for (var i = 0; i < scenes; i++)
            clips[i] = NewClip(i + 1, 1, 8);
        return clips;
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

    private sealed class UnusedJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException(identifier);
    }
}
