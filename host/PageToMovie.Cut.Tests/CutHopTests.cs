using PageToMovie.Cut.Cut;
using Xunit;

namespace PageToMovie.Cut.Tests;

public class CutHopTests
{
    [Fact]
    public void Seed_prefers_start_and_stop()
    {
        var hop = new CutHop(LeadInSeconds: 5, ClipStartSeconds: 5, ClipStopSeconds: 10, DurationSeconds: 5);
        var (inn, outt) = CutHop.SeedInOut(hop, fileDurationSec: 10);
        Assert.Equal(5, inn);
        Assert.Equal(10, outt);
    }

    [Fact]
    public void Seed_lead_in_plus_file_duration_when_stop_missing()
    {
        var hop = new CutHop(LeadInSeconds: 5, ClipStartSeconds: null, ClipStopSeconds: null, DurationSeconds: null);
        var (inn, outt) = CutHop.SeedInOut(hop, fileDurationSec: 10);
        Assert.Equal(5, inn);
        Assert.Equal(10, outt);
    }

    [Fact]
    public void Seed_lead_in_plus_sidecar_duration()
    {
        var hop = new CutHop(LeadInSeconds: 5, ClipStartSeconds: null, ClipStopSeconds: null, DurationSeconds: 5);
        var (inn, outt) = CutHop.SeedInOut(hop, fileDurationSec: 0);
        Assert.Equal(5, inn);
        Assert.Equal(10, outt);
    }

    [Fact]
    public void Seed_no_hop_is_full_file()
    {
        var (inn, outt) = CutHop.SeedInOut(CutHop.None, fileDurationSec: 8);
        Assert.Equal(0, inn);
        Assert.Equal(8, outt);
    }

    [Fact]
    public void Seed_sidecar_duration_when_file_duration_unknown()
    {
        var hop = new CutHop(0, null, null, DurationSeconds: 6);
        var (inn, outt) = CutHop.SeedInOut(hop, fileDurationSec: 0);
        Assert.Equal(0, inn);
        Assert.Equal(6, outt);
    }

    [Fact]
    public void Read_sidecar_fields()
    {
        var hop = CutHop.Read(
            """{"provider_lead_in_seconds":5,"provider_clip_start_seconds":5,"provider_clip_stop_seconds":10,"duration_seconds":5}""");
        Assert.True(hop.HasSlice);
        Assert.Equal(5, hop.LeadInSeconds);
        Assert.Equal(5, hop.ClipStartSeconds);
        Assert.Equal(10, hop.ClipStopSeconds);
        var (inn, outt) = CutHop.SeedInOut(hop, 10.2);
        Assert.Equal(5, inn);
        Assert.Equal(10, outt);
    }

    [Fact]
    public void FromFiles_seeds_take_sidecar_hop_not_file_zero()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_01_clip_02_take_03.mp4", "assets/video/scene_01_clip_02_take_03.mp4", 4000),
            new("scene_01_clip_02.current.json", "assets/video/scene_01_clip_02.current.json", 20,
                """{"take":3}"""),
            new("scene_01_clip_02_take_03.clip.json", "assets/video/scene_01_clip_02_take_03.clip.json", 120,
                """{"provider_lead_in_seconds":5,"provider_clip_start_seconds":5,"provider_clip_stop_seconds":10,"transition":"DISSOLVE TO:"}"""),
        ]);

        var clip = Assert.Single(clips);
        var take = Assert.Single(clip.Takes);
        Assert.Equal(3, take.Take);
        Assert.Equal(5, take.ProviderLeadInSeconds);
        Assert.Equal(5, take.ProviderClipStartSeconds);
        Assert.Equal(10, take.ProviderClipStopSeconds);
        Assert.Equal(5, take.MarkIn);
        Assert.Equal(10, take.MarkOut);
        Assert.Equal("DISSOLVE TO:", clip.FountainTransition);

        take.SetDuration(10);
        Assert.Equal(5, take.MarkIn);
        Assert.Equal(10, take.MarkOut);
        Assert.Equal(5, CutTimelineLayout.SlicedSeconds(clip));
    }

    [Fact]
    public void FromFiles_clip_sidecar_hop_is_fallback_for_take()
    {
        var clips = CutClipList.FromFiles(
        [
            new("scene_01_clip_01_take_01.mp4", "assets/video/scene_01_clip_01_take_01.mp4", 2000),
            new("scene_01_clip_01.current.json", "assets/video/scene_01_clip_01.current.json", 20,
                """{"take":1}"""),
            new("scene_01_clip_01.clip.json", "assets/video/scene_01_clip_01.clip.json", 80,
                """{"provider_lead_in_seconds":3.5}"""),
        ]);

        var take = Assert.Single(Assert.Single(clips).Takes);
        Assert.Equal(3.5, take.ProviderLeadInSeconds);
        take.SetDuration(9);
        Assert.Equal(3.5, take.MarkIn);
        Assert.Equal(9, take.MarkOut);
    }

    [Fact]
    public void SetDuration_without_hop_still_uses_full_file()
    {
        var take = new CutTake
        {
            Take = 1,
            FileName = "scene_01_clip_01_take_01.mp4",
            RelativePath = "assets/video/scene_01_clip_01_take_01.mp4",
        };
        take.SetDuration(8);
        Assert.Equal(0, take.MarkIn);
        Assert.Equal(8, take.MarkOut);
    }
}
