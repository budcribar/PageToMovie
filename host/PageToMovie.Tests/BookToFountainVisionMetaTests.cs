using PageToMovie.Engine;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;
using Xunit;

namespace PageToMovie.Tests;

public sealed class BookToFountainVisionMetaTests
{
    [Fact]
    public void SplitVisionMetaTrailer_StripsAndParses()
    {
        var raw = """
Title: Test

FADE IN:

INT. ROOM - DAY

Something happens.

FADE OUT.

---VISION_META---
{"visual_medium":"photoreal_live_action","render_style_lock":"STYLE LOCK: photoreal gothic","notes":"literary short"}
---END_VISION_META---
""";
        var (fountain, vision) = PageToMovie.Engine.ProjectVisionMeta.SplitVisionMetaTrailer(raw);
        Assert.DoesNotContain("VISION_META", fountain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FADE IN", fountain, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(vision);
        Assert.Equal(ProjectVisionMeta.MediumPhotoreal, vision!.VisualMedium);
        Assert.Contains("photoreal", vision.RenderStyleLock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitVisionMetaTrailer_NoTrailer()
    {
        var (fountain, vision) = PageToMovie.Engine.ProjectVisionMeta.SplitVisionMetaTrailer("FADE IN:\n\nINT. A - DAY\n");
        Assert.Null(vision);
        Assert.Contains("FADE IN", fountain);
    }
}
