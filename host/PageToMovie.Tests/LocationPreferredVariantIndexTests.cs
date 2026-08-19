using System.Text;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The locked set plate is a byte-copy of the chosen variant and nothing records which one, so
/// after a reload the Locations tile grid showed every look unlocked (tile #1 was the plate).
/// ListLocations now derives PreferredVariantIndex by matching the ref bytes to a variant.
/// </summary>
public sealed class LocationPreferredVariantIndexTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;
    private const string ProjectId = "LocPref";

    public LocationPreferredVariantIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ptm-locpref-" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(_root, "projects", ProjectId);
        Directory.CreateDirectory(Path.Combine(projDir, "source"));
        File.WriteAllText(Path.Combine(projDir, "source", "cast_seeds.json"),
            """{"schema_version":"cast_seeds.v1","character_seed_tokens":{},"location_seed_tokens":{"Location_Schoolroom":{"display_name":"Schoolroom","description":"one-room schoolhouse interior"}}}""");
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
    }

    private static byte[] Png(string tag) => Encoding.ASCII.GetBytes("\x89PNG-fake-" + tag + new string('x', 200));

    [Fact]
    public void Locked_plate_reports_the_variant_it_was_taken_from()
    {
        var dir = _store.GetLocationAssetsDir(ProjectId);
        Directory.CreateDirectory(dir);
        for (var i = 1; i <= 3; i++)
            File.WriteAllBytes(Path.Combine(dir, ProjectStore.LocationVariantFileName("Location_Schoolroom", i)), Png("v" + i));

        // Lock look #2 the way LockVariantAsync does: copy its bytes into the ref.
        _store.LockLocationRefFromBytes(ProjectId, "Location_Schoolroom",
            File.ReadAllBytes(Path.Combine(dir, ProjectStore.LocationVariantFileName("Location_Schoolroom", 2))));

        var row = Assert.Single(_store.ListLocations(ProjectId), r => r.Key.Equals("Location_Schoolroom", StringComparison.OrdinalIgnoreCase));
        Assert.True(row.Locked);
        Assert.Equal(3, row.Variants.Count);
        Assert.Equal(2, row.PreferredVariantIndex);
    }

    [Fact]
    public void Uploaded_plate_that_matches_no_variant_has_no_index()
    {
        var dir = _store.GetLocationAssetsDir(ProjectId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, ProjectStore.LocationVariantFileName("Location_Schoolroom", 1)), Png("v1"));
        _store.LockLocationRefFromBytes(ProjectId, "Location_Schoolroom", Png("uploaded"));

        var row = Assert.Single(_store.ListLocations(ProjectId), r => r.Key.Equals("Location_Schoolroom", StringComparison.OrdinalIgnoreCase));
        Assert.True(row.Locked);
        Assert.Null(row.PreferredVariantIndex);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
