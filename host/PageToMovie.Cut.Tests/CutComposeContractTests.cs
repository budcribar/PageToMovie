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
        Assert.True(CutComposeContract.IsBrowserFsError("RuntimeError: memory access out of bounds"));
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
            "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var src = File.ReadAllText(cutJs);
        Assert.Contains(CutComposeContract.BrowserWorkingFileError, src, StringComparison.Ordinal);
        Assert.Contains("prepareExportAsync", src, StringComparison.Ordinal);
        Assert.Contains("drainComposeAsync", src, StringComparison.Ordinal);
        Assert.Contains("writeMemfs", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Cut_js_releases_transient_compose_blobs()
    {
        var cutJs = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var src = File.ReadAllText(cutJs);
        Assert.Contains("function releaseTempUrl(url)", src, StringComparison.Ordinal);
        Assert.Contains("releaseTempUrl(beforeOverlay)", src, StringComparison.Ordinal);
        Assert.Contains("releaseTempUrl(previous)", src, StringComparison.Ordinal);
        Assert.Contains("releaseTempUrl(placed.url)", src, StringComparison.Ordinal);
        Assert.Contains("transientBodies.forEach", src, StringComparison.Ordinal);
        Assert.Contains("const hasInlineCards = slice.some", src, StringComparison.Ordinal);
        Assert.Contains("combined = await concatPinned(api, pieces, onProgress)", src, StringComparison.Ordinal);
        Assert.Contains("if (pieces.length > 1)", src, StringComparison.Ordinal);
        Assert.Contains("await resetFfmpegWorker(api);", src, StringComparison.Ordinal);
        Assert.Contains("function execChecked(ffmpeg, args, label)", src, StringComparison.Ordinal);
        Assert.Contains("fps=30,settb=AVTB,setpts=PTS-STARTPTS", src, StringComparison.Ordinal);
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", src, StringComparison.Ordinal);
        Assert.Contains("\"-t\", String(outputSec)", src, StringComparison.Ordinal);
        Assert.Contains("const hardCut = await concatPinned(api, [leftTail.url, rightHead.url], onProgress)", src, StringComparison.Ordinal);
        var ensureJoin = src[src.IndexOf("async function ensureJoinUrlAsync", StringComparison.Ordinal)
            ..src.IndexOf("async function stitchScenesAsync", StringComparison.Ordinal)];
        Assert.Contains("async function ensureJoinUrlAsync(api, join", ensureJoin, StringComparison.Ordinal);
        Assert.Contains("join.url = hardCut.url", src, StringComparison.Ordinal);
        Assert.Contains("actualSec + 0.25 < expectedSec", src, StringComparison.Ordinal);
        Assert.Contains("const appended = await xfadeAsync", src, StringComparison.Ordinal);
        Assert.Contains("const video = await concatVideoRemuxAsync(api, pieces, onProgress)", src, StringComparison.Ordinal);
        Assert.Contains("const repaired = await mixMovieAudioAsync", src, StringComparison.Ordinal);
        Assert.Contains("tpad=start_mode=add:start_duration=", src, StringComparison.Ordinal);
        Assert.Contains(":color=black:stop_mode=clone:stop_duration=", src, StringComparison.Ordinal);
        Assert.Contains("const outputSec = Math.max(pictureEndSec, musicSec)", src, StringComparison.Ordinal);
        Assert.Contains("extendPicture ? h264EncodeArgs(\"aac\") : audioRemuxArgs()", src, StringComparison.Ordinal);
        Assert.DoesNotContain("videoEncodeArgs", src, StringComparison.Ordinal);
        Assert.Contains("cut.validateVideoUrl", src, StringComparison.Ordinal);
        Assert.Contains("cut.validateAudioUrl", src, StringComparison.Ordinal);
        Assert.Contains("media.onloadeddata", src, StringComparison.Ordinal);
        var videoRemux = src[src.IndexOf("async function concatVideoRemuxOnce", StringComparison.Ordinal)
            ..src.IndexOf("async function concatVideoRemuxAsync", StringComparison.Ordinal)];
        Assert.Contains("\"-map\", \"0:v:0\", \"-c:v\", \"copy\", \"-an\"", videoRemux, StringComparison.Ordinal);
        var trim = src[src.IndexOf("function buildTrimArgs", StringComparison.Ordinal)
            ..src.IndexOf("function xfadeName", StringComparison.Ordinal)];
        Assert.True(
            trim.IndexOf("args.push(\"-i\", inName)", StringComparison.Ordinal)
            < trim.IndexOf("args.push(\"-ss\", String(start))", StringComparison.Ordinal),
            "Accurate trim seeking must put -ss after -i so short transition tails keep video.");
        Assert.Contains("await measuredSceneSecondsAsync(base, scene.url, scene.seconds)", src, StringComparison.Ordinal);
        Assert.Contains("await measuredSceneSecondsAsync(api, built.url, scene.seconds)", src, StringComparison.Ordinal);
        Assert.Contains("api.probeDurationAsync(url)", src, StringComparison.Ordinal);
        Assert.Contains("args.push(\"-map\", \"0:v:0\", \"-map\", \"0:a:0\")", src, StringComparison.Ordinal);
        Assert.Contains("args.push(\"-map\", \"0:v:0\", \"-map\", \"1:a:0\", \"-shortest\")", src, StringComparison.Ordinal);
        Assert.Contains("format=yuv420p,setpts=PTS-STARTPTS", trim, StringComparison.Ordinal);
        Assert.Contains("\"-af\", \"asetpts=PTS-STARTPTS\"", trim, StringComparison.Ordinal);
        var concat = src[src.IndexOf("async function concatEncodeOnce", StringComparison.Ordinal)
            ..src.IndexOf("async function concatEncodeAsync", StringComparison.Ordinal)];
        Assert.Contains("list.push(\"duration \" + durations[i])", concat, StringComparison.Ordinal);
        Assert.Contains("setpts=PTS-STARTPTS", concat, StringComparison.Ordinal);
        Assert.Contains("asetpts=PTS-STARTPTS", concat, StringComparison.Ordinal);
        Assert.Contains("outputSec += seconds", concat, StringComparison.Ordinal);
        Assert.Contains("[\"-t\", String(outputSec)]", concat, StringComparison.Ordinal);
        Assert.Contains("\"-fflags\", \"+genpts\", \"-f\", \"concat\"", concat, StringComparison.Ordinal);
        Assert.Contains("end - start < 0.1", src, StringComparison.Ordinal);
        var windows = src[src.IndexOf("async function prepareWindowsAsync", StringComparison.Ordinal)
            ..src.IndexOf("async function deleteMemfs", StringComparison.Ordinal)];
        Assert.DoesNotContain("urls.push(c.url)", windows, StringComparison.Ordinal);
        Assert.Contains("audioFilters.push(\"atrim=duration=\"", src, StringComparison.Ordinal);
        Assert.Contains("amix=inputs=2:duration=longest", src, StringComparison.Ordinal);
        Assert.DoesNotContain("amix=inputs=2:duration=first", src, StringComparison.Ordinal);
        var musicPlace = src[src.IndexOf("async function placeMusicAsync", StringComparison.Ordinal)
            ..src.IndexOf("function mixFiltersOf", StringComparison.Ordinal)];
        Assert.DoesNotContain("args.push(\"-t\"", musicPlace, StringComparison.Ordinal);
    }

    [Fact]
    public void Cut_js_has_configurable_scene_pool_with_single_worker_fallback()
    {
        var cutJs = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var src = File.ReadAllText(cutJs);

        Assert.Contains("FFMPEG_WORKER_MIN = 1", src, StringComparison.Ordinal);
        Assert.Contains("FFMPEG_WORKER_MAX = 4", src, StringComparison.Ordinal);
        Assert.Contains("ffmpegWorkers", src, StringComparison.Ordinal);
        Assert.Contains("prepareScenesWithPoolAsync", src, StringComparison.Ordinal);
        Assert.Contains("Promise.allSettled(runs)", src, StringComparison.Ordinal);
        Assert.Contains("fellBackToOne", src, StringComparison.Ordinal);
        Assert.Contains("retrying safely with 1 worker", src, StringComparison.Ordinal);
        Assert.Contains("forceFresh: queryFlag(\"ffmpegFresh\")", src, StringComparison.Ordinal);
        Assert.Contains("getLastComposeMetrics", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Cut_js_has_configurable_stitch_pool_with_independent_fallback_and_metrics()
    {
        var cutJs = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var src = File.ReadAllText(cutJs);

        Assert.Contains("ffmpegStitchWorkers", src, StringComparison.Ordinal);
        Assert.Contains("setFfmpegStitchWorkerCount", src, StringComparison.Ordinal);
        Assert.Contains("prepareStitchPiecesWithPoolAsync", src, StringComparison.Ordinal);
        Assert.Contains("stitchFellBackToOne", src, StringComparison.Ordinal);
        Assert.Contains("Parallel transition render failed", src, StringComparison.Ordinal);
        Assert.Contains("stitchPrepareMs", src, StringComparison.Ordinal);
        Assert.Contains("concatMs", src, StringComparison.Ordinal);
        Assert.Contains("musicPrepareMs", src, StringComparison.Ordinal);
        Assert.Contains("mixMs", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Cut_js_has_verified_combined_concat_mix_with_two_pass_fallback()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var src = File.ReadAllText(Path.Combine(root, "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var service = File.ReadAllText(Path.Combine(root, "PageToMovie.Cut.Components", "Services", "CutComposeService.cs"));

        Assert.Contains("ffmpegCombined", src, StringComparison.Ordinal);
        Assert.Contains("concatAndMixOnce", src, StringComparison.Ordinal);
        Assert.Contains("filters.withVo", src, StringComparison.Ordinal);
        Assert.Contains("filters.musicOnly", src, StringComparison.Ordinal);
        Assert.Contains("Combined movie ended early", src, StringComparison.Ordinal);
        Assert.Contains("cut.validateVideoUrl(combined.url)", src, StringComparison.Ordinal);
        Assert.Contains("cut.validateAudioUrl(combined.url)", src, StringComparison.Ordinal);
        Assert.Contains("combinedValidated", src, StringComparison.Ordinal);
        Assert.Contains("combinedFellBack", src, StringComparison.Ordinal);
        Assert.Contains("retrying proven export path", src, StringComparison.Ordinal);
        Assert.Contains("ffmpegCopyFinal", src, StringComparison.Ordinal);
        Assert.Contains("renderBoundaryHoldAsync", src, StringComparison.Ordinal);
        Assert.Contains("copyOutput.push.apply(copyOutput, audioRemuxArgs())", src, StringComparison.Ordinal);
        Assert.Contains("streamCopyFailed", src, StringComparison.Ordinal);
        Assert.Contains("finalCopyFellBack", src, StringComparison.Ordinal);
        Assert.Contains("Fast final pass failed", src, StringComparison.Ordinal);
        Assert.Contains("pictureReusable: pictureReusable", src, StringComparison.Ordinal);
        Assert.Contains("r.PictureReusable is false", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Cut_js_has_flat_clip_pipeline_with_global_pool_and_scene_fallback()
    {
        var cutJs = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var src = File.ReadAllText(cutJs);

        Assert.Contains("ffmpegFlat", src, StringComparison.Ordinal);
        Assert.Contains("ffmpegClipWorkers", src, StringComparison.Ordinal);
        Assert.Contains("prepareFlatClipsWithPoolAsync", src, StringComparison.Ordinal);
        Assert.Contains("flatJoinsOf", src, StringComparison.Ordinal);
        Assert.Contains("composeFlatClipsAndMixAsync", src, StringComparison.Ordinal);
        Assert.Contains("flatFellBack", src, StringComparison.Ordinal);
        Assert.Contains("retrying proven scene pipeline", src, StringComparison.Ordinal);
        Assert.Contains("pictureReusable: false", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_stitch_api_reuses_cut_pool_cache_and_combined_mix_from_web_service()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var cutJs = File.ReadAllText(Path.Combine(
            host, "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var stitchService = File.ReadAllText(Path.Combine(
            host, "PageToMovie.Web", "Services", "ClientVideoStitchService.cs"));

        Assert.Contains("cut.concatVideosOptimizedAsync", cutJs, StringComparison.Ordinal);
        Assert.Contains("cut.concatAndMixVideosOptimizedAsync", cutJs, StringComparison.Ordinal);
        Assert.Contains("prepareFlatClipsWithPoolAsync", cutJs, StringComparison.Ordinal);
        Assert.Contains("parallel-normalize-copy", cutJs, StringComparison.Ordinal);
        Assert.Contains("parallel-normalize-combined-mix", cutJs, StringComparison.Ordinal);
        Assert.Contains("_sharedStitchCache", cutJs, StringComparison.Ordinal);
        Assert.Contains("getLastSharedStitchMetrics", cutJs, StringComparison.Ordinal);
        Assert.Contains("PageToMovieCut.concatVideosOptimizedAsync", stitchService, StringComparison.Ordinal);
        Assert.Contains("PageToMovieCut.concatAndMixVideosOptimizedAsync", stitchService, StringComparison.Ordinal);
        Assert.Contains("PageToMovieFfmpeg.concatVideosAsync", stitchService, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_cut_rcl_is_referenced_by_both_hosts_and_web_bridges_active_project_media()
    {
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var rcl = Path.Combine(host, "PageToMovie.Cut.Components");
        var standalone = Path.Combine(host, "PageToMovie.Cut");
        var web = Path.Combine(host, "PageToMovie.Web");

        Assert.True(File.Exists(Path.Combine(rcl, "PageToMovie.Cut.Components.csproj")));
        Assert.True(File.Exists(Path.Combine(rcl, "Pages", "CutEditor.razor")));
        Assert.True(File.Exists(Path.Combine(rcl, "wwwroot", "js", "cut.js")));
        Assert.True(File.Exists(Path.Combine(rcl, "wwwroot", "css", "cut.css")));
        Assert.Contains("PageToMovie.Cut.Components.csproj",
            File.ReadAllText(Path.Combine(standalone, "PageToMovie.Cut.csproj")), StringComparison.Ordinal);
        Assert.Contains("PageToMovie.Cut.Components.csproj",
            File.ReadAllText(Path.Combine(web, "PageToMovie.Web.csproj")), StringComparison.Ordinal);
        Assert.Contains("<CutEditor />",
            File.ReadAllText(Path.Combine(standalone, "Pages", "Home.razor")), StringComparison.Ordinal);

        var webPage = File.ReadAllText(Path.Combine(web, "Components", "Pages", "Cut.razor"));
        Assert.Contains("@page \"/cut\"", webPage, StringComparison.Ordinal);
        Assert.Contains("HostProjectPrefix=\"@ActiveProject.ProjectId\"", webPage, StringComparison.Ordinal);
        Assert.Contains("AutoAttachHostFolder=\"true\"", webPage, StringComparison.Ordinal);
        Assert.Contains("resolveProjectDirectoryForCutAsync",
            File.ReadAllText(Path.Combine(web, "wwwroot", "js", "pagetomovie-media.js")), StringComparison.Ordinal);
        Assert.Contains("attachHostMediaFolderAsync",
            File.ReadAllText(Path.Combine(rcl, "wwwroot", "js", "cut.js")), StringComparison.Ordinal);

        var css = File.ReadAllText(Path.Combine(rcl, "wwwroot", "css", "cut.css"));
        Assert.Contains(".cut-editor", css, StringComparison.Ordinal);
        Assert.DoesNotContain("html, body", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_binds_selected_title_id_to_the_timeline()
    {
        var home = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Pages", "CutEditor.razor"));
        var src = File.ReadAllText(home);
        Assert.Contains("SelectedTextId=\"@_selectedTextId\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedTextId=\"_selectedTextId\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_accepts_audio_only_mp4_and_waits_for_composed_music()
    {
        var pages = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Pages"));
        var markup = File.ReadAllText(Path.Combine(pages, "CutEditor.razor"));
        var code = File.ReadAllText(Path.Combine(pages, "CutEditor.razor.cs"));

        Assert.Contains("video/mp4", markup, StringComparison.Ordinal);
        Assert.Contains(".mp4", markup, StringComparison.Ordinal);
        Assert.Contains("RequiresComposedMusic", code, StringComparison.Ordinal);
        Assert.Contains("Compose.Music.HasFile", code, StringComparison.Ordinal);
        Assert.Contains("RequiresComposedMusic && !Compose.HasCachedMoviePreview", code, StringComparison.Ordinal);
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
            if (path == CutFfmpegEncodePath.Mix)
            {
                Assert.True(CutComposeContract.MixArgvIsSafe(argv));
                Assert.Contains("copy", argv);
                Assert.DoesNotContain("libx264", argv);
                continue;
            }
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
        Assert.True(CutComposeContract.MixCopiesNormalizedPicture);
        Assert.True(CutComposeContract.MixArgvIsSafe(mix));
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
