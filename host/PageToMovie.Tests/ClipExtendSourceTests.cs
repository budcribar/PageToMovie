using System.Text.Json;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Video-extend chains the last combined provider file. Saved / AI-Edit slices are
/// screenplay-boundary tails capped by the VideoEdit catalog field — never a hardcoded
/// duration or model id.
/// </summary>
[Collection("catalog-serial")]
public class ClipExtendSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptm-extend-" + Guid.NewGuid().ToString("N"));

    public ClipExtendSourceTests()
    {
        SupportedModelCatalog.ReloadCatalog();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static double EditCap()
    {
        var cap = SupportedModelCatalog.VideoEditMaxInputDurationSeconds();
        Assert.True(cap is > 0, "VideoEdit catalog must publish maxEditInputDurationSeconds");
        return cap!.Value;
    }

    [Fact]
    public void Combined_predecessor_file_id_is_the_next_extend_input_even_when_over_the_edit_cap()
    {
        var cap = EditCap();
        var choice = ClipExtendSource.Select(
            predecessorFileId: "file_c1_c2",
            predecessorLeadInSeconds: 6.0,
            predecessorDurationSeconds: cap + 2.0,
            predecessorClipStopSeconds: null,
            markerFileId: "file_trimmed_marker",
            markerSeconds: 4.0,
            explicitLocalPath: "/tmp/local.mp4",
            explicitLocalDuration: 5.0,
            predecessorLocalPath: "/tmp/c2.mp4",
            predecessorLocalDuration: 5.0);

        Assert.Equal("file_c1_c2", choice.FileId);
        Assert.Null(choice.LocalPath);
        Assert.Equal(6.0 + cap + 2.0, choice.InputDurationSeconds);
        Assert.True(choice.InputDurationSeconds > cap);
    }

    [Fact]
    public void Standalone_C1_file_id_is_the_C2_extend_input()
    {
        var choice = ClipExtendSource.SelectFromPredecessor(
            new ClipProviderSource("https://vidgen.example/c1.mp4", "file_c1", LeadInSeconds: 0, DurationSeconds: 6.0));

        Assert.Equal("file_c1", choice.FileId);
        Assert.Equal(6.0, choice.InputDurationSeconds);
        Assert.Equal(6.0, ClipExtendSource.NewClipLeadInSeconds(choice.InputDurationSeconds));
    }

    [Fact]
    public void C3_from_combined_C2_records_lead_in_as_C1_plus_C2()
    {
        var c2 = new ClipProviderSource(
            "https://vidgen.example/c1c2.mp4",
            "file_c1_c2",
            LeadInSeconds: 6.0,
            DurationSeconds: 5.0,
            ClipStartSeconds: 6.0,
            ClipStopSeconds: 11.0);

        var choice = ClipExtendSource.SelectFromPredecessor(c2);
        Assert.Equal("file_c1_c2", choice.FileId);
        Assert.Equal(11.0, choice.InputDurationSeconds);
        Assert.Equal(11.0, ClipExtendSource.NewClipLeadInSeconds(choice.InputDurationSeconds));
    }

    [Fact]
    public void Combined_without_stop_uses_lead_in_plus_duration()
    {
        var input = ClipExtendSource.ProviderInputDurationSeconds(
            leadInSeconds: 6.0, durationSeconds: 5.0, clipStopSeconds: null);
        Assert.Equal(11.0, input);
        Assert.Equal(11.0, ClipExtendSource.NewClipLeadInSeconds(input));
    }

    [Fact]
    public void Select_does_not_fall_through_to_fresh_when_combined_and_over_edit_cap()
    {
        var cap = EditCap();
        var choice = ClipExtendSource.SelectFromPredecessor(
            new ClipProviderSource(null, "file_combined", LeadInSeconds: 5.0, DurationSeconds: cap + 4.0));

        Assert.True(choice.HasInput);
        Assert.Equal("file_combined", choice.FileId);
        Assert.NotNull(choice.InputDurationSeconds);
    }

    [Fact]
    public void Marker_is_only_used_when_predecessor_has_no_file_id()
    {
        var withFile = ClipExtendSource.Select(
            predecessorFileId: "file_keep",
            predecessorLeadInSeconds: 6.0,
            predecessorDurationSeconds: 5.0,
            predecessorClipStopSeconds: 11.0,
            markerFileId: "file_marker",
            markerSeconds: 4.0,
            explicitLocalPath: null,
            explicitLocalDuration: null,
            predecessorLocalPath: null,
            predecessorLocalDuration: null);
        Assert.Equal("file_keep", withFile.FileId);

        var noFile = ClipExtendSource.Select(
            predecessorFileId: null,
            predecessorLeadInSeconds: 0,
            predecessorDurationSeconds: 5.0,
            predecessorClipStopSeconds: null,
            markerFileId: "file_marker",
            markerSeconds: 4.0,
            explicitLocalPath: null,
            explicitLocalDuration: null,
            predecessorLocalPath: null,
            predecessorLocalDuration: null);
        Assert.Equal("file_marker", noFile.FileId);
        Assert.Equal(4.0, noFile.InputDurationSeconds);
    }

    [Fact]
    public void Screenplay_slice_under_edit_cap_is_kept()
    {
        var cap = EditCap();
        var slice = Math.Max(1.0, cap - 2.0);
        Assert.Equal(slice, ClipExtendSource.SavedSliceDurationSeconds(slice));
        Assert.Equal(slice, SupportedModelCatalog.CapToVideoEditInput(slice));
    }

    [Fact]
    public void Screenplay_slice_over_edit_cap_is_capped_from_catalog()
    {
        var cap = EditCap();
        Assert.Equal(cap, ClipExtendSource.SavedSliceDurationSeconds(cap + 3.2));
        Assert.Equal(cap, SupportedModelCatalog.CapToVideoEditInput(cap + 3.2));
    }

    [Fact]
    public void Combined_shorter_than_lead_in_must_not_be_saved()
    {
        Assert.Null(ClipExtendSource.SavedSliceDurationFromCombined(combinedSeconds: 5.0, leadInSeconds: 6.0));
        Assert.Null(ClipExtendSource.SavedSliceDurationFromCombined(combinedSeconds: 6.05, leadInSeconds: 6.0));
    }

    [Fact]
    public void Combined_tail_is_the_screenplay_slice_capped_by_catalog()
    {
        var cap = EditCap();
        Assert.Equal(5.0, ClipExtendSource.SavedSliceDurationFromCombined(11.0, 6.0));
        Assert.Equal(cap, ClipExtendSource.SavedSliceDurationFromCombined(6.0 + cap + 4.0, 6.0));
    }

    [Fact]
    public async Task Sidecar_on_disk_combined_predecessor_selects_file_id_and_C1_plus_C2_lead_in()
    {
        var projects = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        var sidecars = new ClipSidecarService(projects);
        var projectDir = Path.Combine(_root, "projects", "P");
        Directory.CreateDirectory(projectDir);

        var path = await sidecars.WriteSidecarAsync(
            projectDir, scene: 1, clip: 2, prompt: "c2", scriptText: "",
            model: "video", resolution: "480p", durationSeconds: 5.0, sha256: "", sizeBytes: 0,
            sourceUrl: "https://vidgen.example/c1c2.mp4", sourceProvider: "x",
            sourceFileId: "file_c1_c2",
            providerLeadInSeconds: 6.0,
            providerClipStartSeconds: 6.0,
            providerClipStopSeconds: 11.0);

        var src = ClipProviderSource.Read(path);
        Assert.NotNull(src);
        Assert.True(src!.IsCombined);
        var choice = ClipExtendSource.SelectFromPredecessor(src);
        Assert.Equal("file_c1_c2", choice.FileId);
        Assert.Equal(11.0, choice.InputDurationSeconds);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(6.0, doc.RootElement.GetProperty(ClipProviderSource.LeadInProperty).GetDouble());
        Assert.Equal(6.0, doc.RootElement.GetProperty(ClipProviderSource.ClipStartProperty).GetDouble());
        Assert.Equal(11.0, doc.RootElement.GetProperty(ClipProviderSource.ClipStopProperty).GetDouble());
    }

    [Fact]
    public void Clip_window_matches_lead_in_to_end()
    {
        var (start, stop) = ClipExtendSource.ClipWindowInProviderFile(6.0, 5.0);
        Assert.Equal(6.0, start);
        Assert.Equal(11.0, stop);
    }
}
