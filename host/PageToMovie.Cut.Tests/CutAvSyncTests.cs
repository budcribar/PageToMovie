using PageToMovie.Cut.Cut;
using PageToMovie.Cut.Services;
using Xunit;

namespace PageToMovie.Cut.Tests;

/// <summary>
/// Today's Annette <c>movie.mp4</c> / <c>cut.cache/picture.mp4</c> is the same
/// file as <c>movie (24).mp4</c> (25,703,504 bytes): V=91.750s / A=88.888s.
/// The gap is EOF only — last audio ~88.87s, then a frozen last frame
/// (London terrace) to 91.75s. Length is ~scenes 1–12, not the full film.
/// </summary>
public class CutAvSyncTests
{
    public const double Movie24VideoSec = 91.750000;
    public const double Movie24AudioSec = 88.888005;
    public const long Movie24Bytes = 25_703_504;

    [Fact]
    public void Annette_movie_24_eof_tail_is_rejected_and_trims_to_audio()
    {
        var gap = Movie24VideoSec - Movie24AudioSec;
        Assert.True(Movie24Bytes > 20_000_000); // full-length picture, not the 8.5s stub
        Assert.InRange(gap, 2.86, 2.87);
        Assert.False(CutComposeContract.AvDurationsMatch(Movie24VideoSec, Movie24AudioSec));
        Assert.True(CutComposeContract.ShouldTrimToShorterStream(Movie24VideoSec, Movie24AudioSec));
        Assert.Equal(Movie24AudioSec, CutComposeContract.AlignedDurationSec(Movie24VideoSec, Movie24AudioSec));
        Assert.NotEqual(Movie24VideoSec, CutComposeContract.AlignedDurationSec(Movie24VideoSec, Movie24AudioSec));
        Assert.False(CutComposeContract.AvDurationsMatch(Movie24VideoSec, 0));
        Assert.False(CutComposeContract.AvDurationsMatch(0, Movie24AudioSec));
        Assert.Equal(0, CutComposeContract.AlignedDurationSec(Movie24VideoSec, 0));
    }

    [Fact]
    public void Matching_streams_pass_one_aac_frame_and_fail_a_clip_gap()
    {
        Assert.True(CutComposeContract.AvDurationsMatch(91.75, 91.75));
        Assert.False(CutComposeContract.ShouldTrimToShorterStream(91.75, 91.75));
        Assert.True(CutComposeContract.AvDurationsMatch(
            CutComposeContract.AvDurationToleranceSec,
            CutComposeContract.AvDurationToleranceSec * 2));
        Assert.False(CutComposeContract.AvDurationsMatch(
            10, 10 + CutComposeContract.AvDurationToleranceSec + 0.001));
        // Extension takes often leak 80–157 ms of picture past the sound.
        Assert.False(CutComposeContract.AvDurationsMatch(5.04, 5.04 - 0.080));
        Assert.False(CutComposeContract.AvDurationsMatch(5.04, 5.04 - 0.157));
        Assert.Equal(1024d / 44100d, CutComposeContract.AvDurationToleranceSec, 9);
        Assert.Equal(5.04 - 0.157, CutComposeContract.AlignedDurationSec(5.04, 5.04 - 0.157));
    }

    [Fact]
    public void Operator_copy_and_align_filters_are_single_source()
    {
        Assert.Equal(
            "The movie's picture and sound do not match. Play or Make movie again.",
            CutComposeContract.AvMismatchError);
        Assert.Equal(
            CutComposeContract.AvMismatchError,
            CutComposeContract.OperatorComposeError(CutComposeContract.AvMismatchError, download: true));
        Assert.Contains("apad", CutComposeContract.PadAudioToVideoFilter, StringComparison.Ordinal);
        Assert.DoesNotContain("apad", CutComposeContract.ResampleAudioFilter, StringComparison.Ordinal);
        Assert.Contains("sample_rates=48000", CutComposeContract.ResampleAudioFilter, StringComparison.Ordinal);
        Assert.StartsWith("asetpts=PTS-STARTPTS", CutComposeContract.PadAudioToVideoFilter, StringComparison.Ordinal);
    }

    [Fact]
    public void Cut_js_trims_eof_tail_and_rejects_a_v_gap()
    {
        var cutJs = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "wwwroot", "js", "cut.js"));
        var src = File.ReadAllText(cutJs);
        var service = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Services", "CutComposeService.cs")));
        var editor = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Pages", "CutEditor.razor.cs")));
        var folder = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "PageToMovie.Cut.Components", "Services", "CutFolderService.cs")));

        Assert.Contains(CutComposeContract.PadAudioToVideoFilter, src, StringComparison.Ordinal);
        Assert.Contains(CutComposeContract.ResampleAudioFilter, src, StringComparison.Ordinal);
        Assert.Contains(CutComposeContract.AvMismatchError, src, StringComparison.Ordinal);
        Assert.Contains("function avDurationsMatch(videoSec, audioSec)", src, StringComparison.Ordinal);
        Assert.Contains("cut.probeAvDurations", src, StringComparison.Ordinal);
        Assert.Contains("async function alignAvIfNeededAsync", src, StringComparison.Ordinal);
        Assert.Contains("async function trimAvToDurationAsync", src, StringComparison.Ordinal);
        Assert.Contains("const keep = Math.min(probe.videoSec, probe.audioSec)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("async function padAudioToVideoAsync", src, StringComparison.Ordinal);
        Assert.Contains("let flat = await composeFlatClipsAndMixAsync", src, StringComparison.Ordinal);
        Assert.Contains("return alignAvIfNeededAsync(api, combined, onProgress)", src, StringComparison.Ordinal);
        Assert.Contains("acrossfade=d=\" + fade + \":c1=tri:c2=tri,apad[a]\"", src, StringComparison.Ordinal);
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=48000", src, StringComparison.Ordinal);
        Assert.Contains("Cut: concat normalize retry failed", src, StringComparison.Ordinal);
        Assert.Contains("[\"-shortest\"]", src, StringComparison.Ordinal);

        var still = src[src.IndexOf("async function stillVideoAsync", StringComparison.Ordinal)
            ..src.IndexOf("async function xfadeAsync", StringComparison.Ordinal)];
        Assert.Contains("\"-map\", \"0:v:0\", \"-map\", \"1:a:0\"", still, StringComparison.Ordinal);
        Assert.DoesNotContain("h264EncodeArgs(\"an\")", still, StringComparison.Ordinal);

        Assert.Contains("TryReuseMovieIfAvMatchAsync", service, StringComparison.Ordinal);
        Assert.Contains("MovieAvMatchesAsync", service, StringComparison.Ordinal);
        Assert.Contains("AvDurationsMatch(r.VideoSec, r.AudioSec)", service, StringComparison.Ordinal);
        Assert.Contains("PageToMovieCut.probeAvDurations", service, StringComparison.Ordinal);
        Assert.Contains("MovieAvMatchesAsync(compose.MoviePreviewUrl)", editor, StringComparison.Ordinal);
        Assert.Contains("MovieAvMatchesAsync(compose.Cache.PictureUrl)", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_result_with_movie_24_gap_fails_closed()
    {
        var r = new JsResult
        {
            Success = true,
            Url = "blob:movie-24",
            VideoSec = Movie24VideoSec,
            AudioSec = Movie24AudioSec,
        };
        Assert.False(CutComposeContract.AvDurationsMatch(r.VideoSec, r.AudioSec));
        Assert.Equal(Movie24AudioSec, CutComposeContract.AlignedDurationSec(r.VideoSec, r.AudioSec));
        Assert.Equal(CutComposeContract.AvMismatchError,
            CutComposeContract.OperatorComposeError(CutComposeContract.AvMismatchError, download: true));
        Assert.False(CutMergeCache.ShouldPersistPicture(
            segmentsComplete: true, pictureUrl: r.Url)
            && CutComposeContract.AvDurationsMatch(r.VideoSec, r.AudioSec));
    }
}
