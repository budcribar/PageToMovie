using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Mary19 (2026-08-19): after the register endpoint deleted synced sidecars it left
/// "…clip.json.client.json" markers behind; the scene list counted those as "clip present", so
/// S01 showed 4/4 with nothing playable and the sidecar self-heal (keyed off "not present") never ran.
/// </summary>
public class ClipOnDiskMarkerTests
{
    [Fact]
    public void Marker_for_a_synced_sidecar_does_not_make_the_clip_present()
    {
        var idx = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["scene_01_clip_02_take_01.clip.json.client.json"] = 120,
        };
        Assert.False(ProjectStore.ClipOnDisk(idx, 1, 2));
    }

    [Fact]
    public void Video_marker_sidecar_or_mp4_make_the_clip_present()
    {
        Assert.True(ProjectStore.ClipOnDisk(new(StringComparer.OrdinalIgnoreCase) { ["scene_01_clip_02.mp4.client.json"] = 120 }, 1, 2));
        Assert.True(ProjectStore.ClipOnDisk(new(StringComparer.OrdinalIgnoreCase) { ["scene_01_clip_02_take_01.clip.json"] = 4000 }, 1, 2));
        Assert.True(ProjectStore.ClipOnDisk(new(StringComparer.OrdinalIgnoreCase) { ["scene_01_clip_02.mp4"] = 500_000 }, 1, 2));
    }
}
