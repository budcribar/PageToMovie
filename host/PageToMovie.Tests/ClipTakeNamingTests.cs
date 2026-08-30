using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

public class ClipTakeNamingTests
{
    [Theory]
    [InlineData("scene_03_clip_07_take_01.clip.json", 1)]
    [InlineData("scene_03_clip_07_take_04.mp4", 4)]
    [InlineData("scene_03_clip_07_take_01_20260820_172934.clip.json", 1)]
    [InlineData("scene_03_clip_07.mp4", 0)]
    [InlineData("scene_01_clip_01_100.mp4", 0)]
    public void ParseTakeNumber_reads_filename_including_timestamped_leftovers(string fileName, int expected)
    {
        Assert.Equal(expected, ClipTakeNaming.ParseTakeNumber(fileName));
    }

    [Fact]
    public void ResolveTakeNumber_prefers_filename_over_sidecar_field()
    {
        Assert.Equal(4, ClipTakeNaming.ResolveTakeNumber("scene_01_clip_02_take_04.clip.json", sidecarTake: 1));
        Assert.Equal(3, ClipTakeNaming.ResolveTakeNumber("scene_01_clip_02.mp4", sidecarTake: 3));
        Assert.Equal(0, ClipTakeNaming.ResolveTakeNumber("scene_01_clip_02.mp4", sidecarTake: 0));
    }

    [Fact]
    public void Timestamped_leftover_is_not_a_stable_take_name()
    {
        Assert.True(ClipTakeNaming.IsStableTakeName("scene_03_clip_07_take_01.clip.json"));
        Assert.False(ClipTakeNaming.IsStableTakeName("scene_03_clip_07_take_01_20260820_172934.clip.json"));
        Assert.True(ClipTakeNaming.IsTimestampedTakeName("scene_03_clip_07_take_01_20260820_172934.clip.json"));
        Assert.True(ClipTakeNaming.IsCanonicalClipName("scene_03_clip_07.mp4"));
        Assert.False(ClipTakeNaming.IsCanonicalClipName("scene_03_clip_07_take_01.mp4"));
    }

    [Fact]
    public void AssignUniqueTakeNumbers_splits_two_take_01_files()
    {
        var a = 1;
        var b = 1;
        ClipTakeNaming.AssignUniqueTakeNumbers(new (int, Action<int>)[]
        {
            (1, n => a = n),
            (1, n => b = n),
        });
        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public void JobMediaSaveKey_uses_job_and_take_not_canonical_path()
    {
        var first = ClipTakeNaming.JobMediaSaveKey("P", "job-1", "assets/video/scene_01_clip_01.mp4", 1);
        var second = ClipTakeNaming.JobMediaSaveKey("P", "job-2", "assets/video/scene_01_clip_01.mp4", 2);
        Assert.NotEqual(first, second);
        Assert.Contains("take-01", first);
        Assert.Contains("take-02", second);
    }

    [Fact]
    public void JobMediaSaveKey_same_job_different_takes_are_distinct()
    {
        var first = ClipTakeNaming.JobMediaSaveKey("P", "job-1", ClipTakeNaming.TakeRelativePath(1, 1, 2), 2);
        var second = ClipTakeNaming.JobMediaSaveKey("P", "job-1", ClipTakeNaming.TakeRelativePath(1, 1, 3), 3);
        Assert.Equal("P|job-1|take-02", first);
        Assert.Equal("P|job-1|take-03", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ShouldKeepLocalSidecar_when_local_is_larger()
    {
        Assert.True(ClipTakeNaming.ShouldKeepLocalSidecar(5170, 4933));
        Assert.False(ClipTakeNaming.ShouldKeepLocalSidecar(4933, 5170));
        Assert.False(ClipTakeNaming.ShouldKeepLocalSidecar(4933, 4933));
        Assert.False(ClipTakeNaming.ShouldKeepLocalSidecar(0, 4933));
    }

    [Fact]
    public void Take_paths_have_no_timestamp()
    {
        Assert.Equal("scene_03_clip_07_take_05.mp4", ClipTakeNaming.TakeMp4FileName(3, 7, 5));
        Assert.Equal("scene_03_clip_07_take_05.clip.json", ClipTakeNaming.TakeSidecarFileName(3, 7, 5));
        Assert.Equal("assets/video/scene_03_clip_07_take_05.mp4", ClipTakeNaming.TakeRelativePath(3, 7, 5));
        Assert.DoesNotContain("2026", ClipTakeNaming.TakeMp4FileName(3, 7, 5));
    }

    [Fact]
    public void CurrentTakePath_comes_from_pointer_take_number()
    {
        Assert.Equal("assets/video/scene_01_clip_02_take_03.mp4", ClipTakeNaming.CurrentTakePath(1, 2, 3));
        Assert.Null(ClipTakeNaming.CurrentTakePath(1, 2, 0));
        Assert.Equal(2, ClipTakeNaming.ParseCurrentTakePointer("""{"scene":1,"clip":2,"take":2}"""));
        Assert.Equal(0, ClipTakeNaming.ParseCurrentTakePointer("""{"scene":1,"clip":2}"""));
        Assert.Equal("assets/video/scene_01_clip_02.current.json", ClipTakeNaming.CurrentTakePointerRelativePath(1, 2));
    }

    /// <summary>
    /// A take sidecar carries its own "take" field, so one reaching the pointer parser reads as a
    /// perfectly valid pointer and silently overrides the promoted take with the sidecar's own.
    /// That is how selecting a take marked it active but played a different one.
    /// </summary>
    [Fact]
    public void Clip_sidecar_is_not_accepted_as_a_current_take_pointer()
    {
        const string sidecar = """
            {"schema_version":"clip_sidecar.v1","project_id":"Demo","scene":1,"clip":2,"take":7,
             "script_text":"","visual_prompt":"","model":"m","resolution":"720p"}
            """;
        Assert.Equal(0, ClipTakeNaming.ParseCurrentTakePointer(sidecar));
        Assert.Equal(7, ClipTakeNaming.ParseCurrentTakePointer("""{"scene":1,"clip":2,"take":7}"""));
    }

    /// <summary>
    /// The pointer is always named exactly, so the media-folder prefix search must not answer a
    /// <c>.current.json</c> lookup — the take sidecars share its prefix and its .json extension.
    /// </summary>
    [Fact]
    public void Media_js_never_prefix_falls_back_for_the_current_take_pointer()
    {
        var js = ReadWebJs("pagetomovie-media.js");
        var guard = js.IndexOf(@"/\.current\.json$/i.test(fileName)", StringComparison.Ordinal);
        var fallback = js.IndexOf("_bestPrefixFileHandleAsync(dir, fileName)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "current-take pointer guard is missing from _resolveFileHandleAsync");
        Assert.True(guard < fallback, "the guard must run before the prefix fallback");
    }

    private static string ReadWebJs(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "PageToMovie.Web", "wwwroot", "js", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {fileName} above {AppContext.BaseDirectory}");
    }
}
