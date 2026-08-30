using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutJoinAudioTests
{
    [Fact]
    public void Persist_round_trips_jcut_and_lcut_on_the_join()
    {
        var left = NewClip(1, 1, 8);
        var right = NewClip(2, 1, 8);
        left.JoinOverride = CutJoinKind.Cut;
        left.JoinAudio = CutJoinAudio.JCut(1);

        var json = CutProjectFile.Serialize([left, right], null);
        Assert.Contains("\"joinAudio\": \"jcut\"", json, StringComparison.Ordinal);
        Assert.Contains("\"joinAudioSec\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"joinOut\": \"cut\"", json, StringComparison.Ordinal);

        var reloadLeft = NewClip(1, 1, 8);
        var reloadRight = NewClip(2, 1, 8);
        Assert.True(CutProjectFile.TryApply([reloadLeft, reloadRight], json, out _));
        Assert.Equal(CutJoinKind.Cut, reloadLeft.JoinOverride);
        Assert.Equal(CutJoinAudioKind.JCut, reloadLeft.JoinAudio.Kind);
        Assert.Equal(1, reloadLeft.JoinAudio.Seconds);
        Assert.Equal(CutJoinAudioKind.None, reloadRight.JoinAudio.Kind);

        reloadLeft.JoinAudio = CutJoinAudio.LCut(0.5);
        var again = CutProjectFile.Serialize([reloadLeft, reloadRight], null);
        var check = NewClip(1, 1, 8);
        Assert.True(CutProjectFile.TryApply([check, NewClip(2, 1, 8)], again, out _));
        Assert.Equal(CutJoinAudioKind.LCut, check.JoinAudio.Kind);
        Assert.Equal(0.5, check.JoinAudio.Seconds);
    }

    [Theory]
    [InlineData(0.75, 8, 8, 0.75)]
    [InlineData(1, 8, 8, 1)]
    [InlineData(10, 8, 8, 4)]
    [InlineData(0.75, 0.6, 8, 0.25)]
    [InlineData(0.75, 0.2, 8, 0)]
    public void Clamp_never_exceeds_the_shorter_adjacent_clip(
        double requested, double leftSec, double rightSec, double expected)
    {
        var clamped = CutJoinAudio.ClampSeconds(requested, leftSec, rightSec);
        Assert.Equal(expected, clamped, 5);
        if (expected < CutJoinAudio.MinSeconds)
            Assert.False(CutJoinAudio.JCut(requested).Clamped(leftSec, rightSec).IsActive);
    }

    [Fact]
    public void Play_compose_offsets_audio_and_leaves_the_picture_cut()
    {
        var left = NewClip(1, 1, 8);
        var right = NewClip(2, 1, 6);
        left.JoinOverride = CutJoinKind.Cut;

        Assert.Equal(CutComposeAudioJoin.KeepThroughConcat, CutComposeContract.AudioJoin(left.JoinToNext(right)));
        Assert.False(CutComposeContract.JoinIsXfade(left.JoinToNext(right)));

        left.JoinAudio = CutJoinAudio.JCut();
        var j = CutComposeContract.ResolveJoinAudio(left, right);
        Assert.True(j.IsActive);
        Assert.Equal(0.75, j.Seconds);
        Assert.Equal(CutComposeAudioJoin.IncomingLeads, CutComposeContract.AudioJoin(left.JoinToNext(right), j));
        Assert.False(CutComposeContract.JoinIsXfade(left.JoinToNext(right)));
        Assert.True(CutComposeContract.JoinEncodes(left.JoinToNext(right), j));

        left.JoinAudio = CutJoinAudio.LCut(1);
        var l = CutComposeContract.ResolveJoinAudio(left, right);
        Assert.Equal(CutJoinAudioKind.LCut, l.Kind);
        Assert.Equal(1, l.Seconds);
        Assert.Equal(CutComposeAudioJoin.OutgoingHangs, CutComposeContract.AudioJoin(left.JoinToNext(right), l));
        Assert.Equal(CutJitPlay.TotalSec([left, right]), CutComposeContract.ComposedDurationSec([left, right]));
    }

    [Fact]
    public void Fingerprint_and_merge_cache_rebuild_only_the_changed_join()
    {
        var clips = new[]
        {
            NewClip(1, 1, 8),
            NewClip(2, 1, 8),
            NewClip(3, 1, 8),
        };
        var before = CutMergeCache.Build(clips, [], null, null);
        var saved = CutMergeCache.ManifestOf(before);
        var movie = CutPlayMerge.Fingerprint(clips, [], null);

        clips[0].JoinAudio = CutJoinAudio.JCut(0.75);
        var after = CutMergeCache.Build(clips, [], null, null);
        var dirty = CutMergeCache.Diff(after, saved);

        Assert.NotEqual(movie, CutPlayMerge.Fingerprint(clips, [], null));
        Assert.Empty(dirty.RebuildScenes);
        Assert.Equal(new[] { 1 }, dirty.RebuildJoins);
        Assert.DoesNotContain(2, dirty.RebuildJoins);
        Assert.True(after.Joins[0].Encodes);
        Assert.Equal("jcut", after.Joins[0].AudioKind);
        Assert.Equal(0.75, after.Joins[0].AudioSec);
        Assert.False(after.Joins[1].Encodes);
        Assert.False(dirty.MovieFresh);
        Assert.False(CutComposeContract.CanReuseExport("blob:movie", dirty));

        clips[0].JoinAudio = CutJoinAudio.LCut(1);
        var flipped = CutMergeCache.Diff(CutMergeCache.Build(clips, [], null, null), CutMergeCache.ManifestOf(after));
        Assert.Empty(flipped.RebuildScenes);
        Assert.Equal(new[] { 1 }, flipped.RebuildJoins);
    }

    [Fact]
    public void Export_payload_and_tick_label_keep_jcut_off_the_wipe_list()
    {
        var left = NewClip(1, 1, 8);
        var right = NewClip(2, 1, 8);
        left.JoinOverride = CutJoinKind.Cut;
        left.JoinAudio = CutJoinAudio.JCut(0.75);
        var payload = CutComposeService.BuildExportPayload([left, right]);

        Assert.Equal("cut", payload[0].JoinOut);
        Assert.Equal("jcut", payload[0].JoinAudio);
        Assert.Equal(0.75, payload[0].JoinAudioSec);
        Assert.Equal("", payload[1].JoinAudio);
        Assert.DoesNotContain("jcut", CutTimelineLayout.EditableJoins.Select(CutTransitionMap.WireName));
        Assert.DoesNotContain("lcut", CutTimelineLayout.EditableJoins.Select(CutTransitionMap.WireName));
        Assert.Equal("J-cut", CutTransitionMap.TickLabel(CutJoinKind.Cut, left.JoinAudio));
        Assert.Equal("Dissolve · L-cut", CutTransitionMap.TickLabel(CutJoinKind.Dissolve, CutJoinAudio.LCut()));
        Assert.False(CutTimelineLayout.ShowsJoinTick(CutJoinKind.Cut));

        var layout = CutTimelineLayout.Build([left, right], 10);
        Assert.Equal(CutJoinKind.Cut, Assert.Single(layout.Joins).Kind);
    }

    [Fact]
    public void Hard_cut_with_jcut_waits_for_the_stitched_join()
    {
        var a = NewClip(1, 1, 4);
        var b = NewClip(2, 1, 4);
        a.FountainTransition = "CUT TO:";
        var clips = new[] { a, b };

        Assert.True(CutJitPlay.IsHardPlayJoin(clips, 0));
        a.JoinAudio = CutJoinAudio.JCut();
        Assert.False(CutJitPlay.IsHardPlayJoin(clips, 0));
        Assert.Equal(4, CutJitPlay.NativeReachableThrough(clips));
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
