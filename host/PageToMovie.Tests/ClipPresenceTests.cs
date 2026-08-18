using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The server keeps no MP4 after generation, so "does this clip exist" must count a provider-hosted
/// clip (sidecar with source_url / file_id) alongside a server file or a local-folder marker —
/// otherwise a generated-but-not-yet-saved clip is regenerated (and paid for) as "missing".
/// </summary>
public class ClipPresenceTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ptm_presence_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Provider_hosted_clip_counts_as_present()
    {
        var dir = TempDir();
        var mp4 = Path.Combine(dir, "scene_01_clip_02.mp4");
        Assert.False(FilmJobService.ClipPresentOnServerOrClient(mp4));
        File.WriteAllText(Path.Combine(dir, "scene_01_clip_02.clip.json"),
            """{"scene":1,"clip":2,"source_url":"https://vidgen.x.ai/xai-vidgen-bucket/x.mp4","source_file_id":"file_abc"}""");
        Assert.True(FilmJobService.ClipPresentOnServerOrClient(mp4));
        Assert.True(FilmJobService.SidecarHasProviderSource(mp4));
    }

    [Fact]
    public void Sidecar_without_source_is_not_presence()
    {
        var dir = TempDir();
        var mp4 = Path.Combine(dir, "scene_01_clip_01.mp4");
        File.WriteAllText(Path.Combine(dir, "scene_01_clip_01.clip.json"), """{"scene":1,"clip":1,"source_url":""}""");
        Assert.False(FilmJobService.ClipPresentOnServerOrClient(mp4));
        File.WriteAllText(mp4 + ".client.json", "{}");
        Assert.True(FilmJobService.ClipPresentOnServerOrClient(mp4));
    }
}
