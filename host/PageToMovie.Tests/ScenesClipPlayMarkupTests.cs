using Xunit;

namespace PageToMovie.Tests;

public class ScenesClipPlayMarkupTests
{
    [Fact]
    public void SceneHeaderPlay_stays_gated_on_scene_completeness()
    {
        var razor = ReadPage("ScenesSceneDetail.razor");
        Assert.Contains("data-testid=\"scene-header-play\"", razor, StringComparison.Ordinal);
        Assert.Contains("CanPlayOpenScene", razor, StringComparison.Ordinal);
        Assert.Contains("PlaySelectedClipsInSceneAsync", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipRowPlay_is_per_clip_and_not_scene_gated()
    {
        var razor = ReadPage("ScenesClipTable.razor");
        Assert.Contains("clip-row-play-{cn}", razor, StringComparison.Ordinal);
        Assert.Contains("CanPlayClip(ListState._detail.SceneNumber, cn)", razor, StringComparison.Ordinal);
        Assert.Contains("PlaySingleClipAsync(ListState._detail.SceneNumber, cn)", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("CanPlayOpenScene", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectorPlay_is_per_clip_and_not_scene_gated()
    {
        var razor = ReadPage("ScenesClipInspector.razor");
        Assert.Contains("data-testid=\"clip-inspector-play\"", razor, StringComparison.Ordinal);
        Assert.Contains("CanPlayClip(ListState._detail.SceneNumber, ClipForm._clip.ClipNumber)", razor, StringComparison.Ordinal);
        Assert.Contains("PlaySingleClipAsync(ListState._detail.SceneNumber, ClipForm._clip.ClipNumber)", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("CanPlayOpenScene", razor, StringComparison.Ordinal);
    }

    private static string ReadPage(string fileName)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            var candidate = Path.Combine(
                d.FullName, "host", "PageToMovie.Web", "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            d = d.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
