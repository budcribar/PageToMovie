using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Video-extend input without server media: the browser trims the delta, the upload endpoint relays
/// it to xAI Files, and only a marker (file_id + seconds) stays on the server. The job must read the
/// marker before any on-disk source and use the delta's own duration as the lead-in to trim.
/// </summary>
public class ExtendSourceMarkerTests
{
    [Fact]
    public void Marker_name_is_per_clip_and_json()
    {
        Assert.Equal("_extend_src_s03c02.json", FilmJobService.ExtendSourceMarkerName(3, 2));
    }

    [Fact]
    public void Marker_parses_file_id_and_seconds()
    {
        var (fid, sec) = FilmJobService.TryReadExtendSourceMarker(
            """{"file_id":"file_38de2e8c-5a08-4656-ad68-e6532351e0ff","duration_seconds":4.5,"bytes":441560}""");
        Assert.Equal("file_38de2e8c-5a08-4656-ad68-e6532351e0ff", fid);
        Assert.Equal(4.5, sec);
    }

    [Fact]
    public void Malformed_or_empty_marker_yields_nothing()
    {
        Assert.Equal((null, null), FilmJobService.TryReadExtendSourceMarker("not json"));
        Assert.Equal((null, null), FilmJobService.TryReadExtendSourceMarker("""{"file_id":""}"""));
        var (fid, sec) = FilmJobService.TryReadExtendSourceMarker("""{"file_id":"file_x"}""");
        Assert.Equal("file_x", fid);
        Assert.Null(sec);
    }
}
