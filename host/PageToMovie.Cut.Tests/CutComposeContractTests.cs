using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutComposeContractTests
{
    [Fact]
    public void Preview_cache_reuses_url_until_cleared()
    {
        Assert.False(CutComposeContract.CanReusePreview(null));
        Assert.False(CutComposeContract.CanReusePreview(""));
        Assert.False(CutComposeContract.CanReusePreview("   "));
        Assert.True(CutComposeContract.CanReusePreview("blob:cut-preview"));
        var fresh = new CutMergeDiff(true, true, true, [], [], false, false);
        var dirty = new CutMergeDiff(false, false, true, [40], [39], true, false);
        Assert.True(CutComposeContract.CanReuseExport("blob:movie", fresh));
        Assert.False(CutComposeContract.MustStitch(fresh, "blob:movie"));
        Assert.False(CutComposeContract.CanReuseExport("blob:movie", dirty));
        Assert.True(CutComposeContract.MustStitch(dirty, "blob:movie"));
        Assert.True(CutComposeContract.MustStitch(fresh, null));
    }

    [Fact]
    public void Make_movie_waits_for_play_stitch_instead_of_aborting()
    {
        Assert.False(CutComposeContract.ExportAbortsInFlightPlay);
        Assert.True(CutComposeContract.ExportWaitsForInFlightPlay);
        Assert.False(CutComposeContract.ShouldCancelComposeOnExport);
    }

    [Fact]
    public void Browser_fs_error_is_rewritten_for_the_operator()
    {
        Assert.True(CutComposeContract.IsBrowserFsError("ErrnoError: FS error"));
        Assert.True(CutComposeContract.IsBrowserFsError("FS error"));
        Assert.True(CutComposeContract.IsBrowserFsError("ErrnoError"));
        Assert.False(CutComposeContract.IsBrowserFsError("Stopped."));
        Assert.False(CutComposeContract.IsBrowserFsError(null));
        Assert.Equal(
            CutComposeContract.BrowserWorkingFileError,
            CutComposeContract.OperatorComposeError("ErrnoError: FS error", download: true));
        Assert.Equal(
            CutComposeContract.BrowserWorkingFileError,
            CutComposeContract.OperatorComposeError("FS error trying to export", download: true));
        Assert.Equal("Stopped.", CutComposeContract.OperatorComposeError("Stopped.", download: true));
        Assert.Equal("Export failed.", CutComposeContract.OperatorComposeError(null, download: true));
        Assert.Equal("Play failed.", CutComposeContract.OperatorComposeError("  ", download: false));
    }

    [Fact]
    public void Cut_js_keeps_the_operator_fs_message()
    {
        var cutJs = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut", "wwwroot", "js", "cut.js"));
        var src = File.ReadAllText(cutJs);
        Assert.Contains(CutComposeContract.BrowserWorkingFileError, src, StringComparison.Ordinal);
        Assert.Contains("prepareExportAsync", src, StringComparison.Ordinal);
        Assert.Contains("drainComposeAsync", src, StringComparison.Ordinal);
        Assert.Contains("writeMemfs", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_clip_audio_stays_on_hard_cut_and_xfade()
    {
        Assert.True(CutComposeContract.KeepNativeClipAudio);
        Assert.False(CutComposeContract.PadCardSilence);
        Assert.Equal(CutComposeAudioJoin.KeepThroughConcat, CutComposeContract.AudioJoin(CutJoinKind.Cut));
        Assert.Equal(CutComposeAudioJoin.KeepThroughConcat, CutComposeContract.AudioJoin(CutJoinKind.CutToBlack));
        Assert.Equal(CutComposeAudioJoin.AcrossfadeOrHardCut, CutComposeContract.AudioJoin(CutJoinKind.Dissolve));
        Assert.Equal(CutComposeAudioJoin.AcrossfadeOrHardCut, CutComposeContract.AudioJoin(CutJoinKind.Dip));
        Assert.Equal(CutComposeAudioJoin.AcrossfadeOrHardCut, CutComposeContract.AudioJoin(CutJoinKind.FadeWhite));
    }

    [Fact]
    public void Cut_to_black_is_a_black_hold_not_a_scene_card()
    {
        Assert.Equal(0.4, CutComposeContract.CutToBlackHoldSeconds);
        Assert.True(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.CutToBlack));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.Dissolve));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.Dip));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.FadeWhite));
        Assert.False(CutComposeContract.JoinInsertsBlackHold(CutJoinKind.Cut));
        Assert.Equal(0.4, CutComposeContract.HoldSeconds(CutJoinKind.CutToBlack));
        Assert.Equal(0, CutComposeContract.HoldSeconds(CutJoinKind.Dissolve));
        Assert.False(CutComposeContract.JoinIsSceneCard(CutJoinKind.CutToBlack));
        Assert.False(CutComposeContract.JoinIsSceneCard(CutJoinKind.Dissolve));
    }

    [Fact]
    public void Scene_joins_wire_fade_white_then_dissolve_into_compose()
    {
        var s01 = NewClip(1, 1, 5.04);
        s01.JoinOverride = CutJoinKind.FadeWhite;
        var s02 = NewClip(2, 1, 20);
        s02.JoinOverride = CutJoinKind.Dissolve;
        var s03 = NewClip(3, 1, 40);
        var payload = CutComposeService.BuildExportPayload([s01, s02, s03]);

        Assert.Equal("fadewhite", payload[0].JoinOut);
        Assert.Equal("dissolve", payload[1].JoinOut);
        Assert.Equal("cut", payload[2].JoinOut);
        Assert.True(CutComposeContract.JoinIsXfade(CutJoinKind.FadeWhite));
        Assert.True(CutComposeContract.JoinIsXfade(CutJoinKind.Dissolve));
        Assert.False(CutComposeContract.JoinIsXfade(CutJoinKind.Cut));
        Assert.False(CutComposeContract.JoinIsXfade(CutJoinKind.CutToBlack));
        Assert.Equal(0.5, CutComposeContract.XfadeSecondsFor(5.04), 5);
        Assert.Equal(0.2, CutComposeContract.XfadeSecondsFor(0.4), 5);
        Assert.Equal(CutComposeContract.XfadeSeconds, CutComposeContract.XfadeSecondsFor(8), 5);
    }

    [Fact]
    public void Every_compose_export_path_is_wmp_safe_h264()
    {
        foreach (var path in CutFfmpegEncode.ComposeExportPaths)
        {
            var argv = CutFfmpegEncode.Argv(path);
            var expectAudio = path is not CutFfmpegEncodePath.Still
                and not CutFfmpegEncodePath.OverlaySilent
                and not CutFfmpegEncodePath.ConcatSilent;
            Assert.True(CutComposeContract.ExportArgvIsWmpSafe(argv, expectAudio), path.ToString());
            Assert.Contains("-pix_fmt", argv);
            Assert.Contains("yuv420p", argv);
            Assert.Contains("libx264", argv);
            Assert.Contains("main", argv);
            Assert.Contains("+faststart", argv);
            Assert.DoesNotContain("copy", argv);
        }

        var mix = CutFfmpegEncode.Argv(CutFfmpegEncodePath.Mix);
        Assert.True(CutComposeContract.MixKeepsVideoDuration(mix));
        Assert.DoesNotContain("-shortest", mix);
        Assert.Contains("-t", mix);
        Assert.True(CutComposeContract.MixMustNotShortenToMusic);
    }

    [Fact]
    public void Composed_duration_keeps_timeline_minus_xfades_not_music()
    {
        var s01a = NewClip(1, 1, 5.04);
        var s01b = NewClip(1, 2, 10.82);
        s01b.JoinOverride = CutJoinKind.Dissolve;
        var s02 = NewClip(2, 1, 25);
        s02.JoinOverride = CutJoinKind.Dissolve;
        var s03 = NewClip(3, 1, 30);
        s03.JoinOverride = CutJoinKind.Dissolve;
        var s04 = NewClip(4, 1, 33.81);
        var clips = new[] { s01a, s01b, s02, s03, s04 };
        var timeline = CutJitPlay.TotalSec(clips);
        var composed = CutComposeContract.ComposedDurationSec(clips);
        Assert.Equal(104.67, timeline, 5);
        Assert.True(composed < timeline);
        Assert.True(composed > 88);
        Assert.Equal(
            timeline
            - CutComposeContract.XfadeSecondsFor(10.82)
            - CutComposeContract.XfadeSecondsFor(25)
            - CutComposeContract.XfadeSecondsFor(30),
            composed,
            5);
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
