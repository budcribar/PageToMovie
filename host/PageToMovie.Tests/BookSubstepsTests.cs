using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Book sub-step completion markers (Look / Enrich / Fit length) persisted in pipeline_state.json so the
/// Book sub-strip can show which optional passes have been run on the current screenplay. A user who steps
/// away and comes back should be able to tell at a glance that they already embellished / set a length.
/// </summary>
public class BookSubstepsTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "BookSubsteps";

    public BookSubstepsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-book-substeps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId));
        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = _root });
        _store = new ProjectStore(opts);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void No_markers_by_default()
    {
        var s = _store.ReadBookSubsteps(ProjectId);
        Assert.False(s.LookDone);
        Assert.False(s.EnrichDone);
        Assert.False(s.FitLengthDone);
        Assert.Null(s.FitLengthTargetMinutes);
    }

    [Fact]
    public void Marks_each_substep_independently_and_records_fit_length_target()
    {
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.Enrich);
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.FitLength, targetMinutes: 8);

        var s = _store.ReadBookSubsteps(ProjectId);
        Assert.True(s.EnrichDone);
        Assert.True(s.FitLengthDone);
        Assert.False(s.LookDone); // never marked
        Assert.NotNull(s.FitLengthTargetMinutes);
        Assert.Equal(8d, s.FitLengthTargetMinutes!.Value);
    }

    [Fact]
    public void Marking_is_idempotent_and_preserves_other_substeps()
    {
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.Look);
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.Enrich);
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.Look); // again

        var s = _store.ReadBookSubsteps(ProjectId);
        Assert.True(s.LookDone);
        Assert.True(s.EnrichDone);
    }

    [Fact]
    public void Unknown_stage_is_ignored()
    {
        _store.MarkBookSubstepDone(ProjectId, "bogus");
        var s = _store.ReadBookSubsteps(ProjectId);
        Assert.False(s.LookDone);
        Assert.False(s.EnrichDone);
        Assert.False(s.FitLengthDone);
    }

    [Fact]
    public void Clear_resets_all_markers()
    {
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.Look);
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.FitLength, 5);
        _store.ClearBookSubsteps(ProjectId);

        var s = _store.ReadBookSubsteps(ProjectId);
        Assert.False(s.LookDone);
        Assert.False(s.FitLengthDone);
        Assert.Null(s.FitLengthTargetMinutes);
    }

    [Fact]
    public async Task AdaptationStatus_surfaces_substep_markers()
    {
        _store.MarkBookSubstepDone(ProjectId, ProjectStore.BookSubstepKeys.Enrich);
        var status = await _store.GetAdaptationStatusAsync(ProjectId);
        Assert.True(status.BookSubsteps.EnrichDone);
        Assert.False(status.BookSubsteps.LookDone);
    }
}
