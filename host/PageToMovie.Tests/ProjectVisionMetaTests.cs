using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ProjectVisionMetaTests
{
    [Theory]
    [InlineData("photoreal_live_action", false)]
    [InlineData("illustrated_picture_book", true)]
    [InlineData("stylized_3d_animated", true)]
    public void PrefersIllustrated_FromEnum(string medium, bool illustrated)
    {
        Assert.Equal(illustrated, ProjectVisionMeta.PrefersIllustrated(medium));
    }

    [Fact]
    public void ParseModelJson_Photoreal()
    {
        var doc = ProjectVisionMeta.ParseModelJson(
            """{"visual_medium":"photoreal_live_action","render_style_lock":"STYLE LOCK: photoreal gothic","notes":"Poe"}""");
        Assert.NotNull(doc);
        Assert.Equal(ProjectVisionMeta.MediumPhotoreal, doc!.VisualMedium);
        Assert.Contains("photoreal", doc.RenderStyleLock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryRead_book_kind_only_is_not_a_decided_medium()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-extract-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(dir, "source"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "source", "extract_meta.json"),
                """{"schema_version":"extract_meta.v1","book_kind":"short","pages":12}""");
            Assert.Null(ProjectVisionMeta.TryGetDecided(dir));
            var ex = Assert.Throws<InvalidOperationException>(() => ProjectVisionMeta.RequireDecided(dir));
            // Says what is missing and where the operator fixes it — choosing the look is free,
            // and the message used to send them back through a paid book/screenplay run.
            Assert.Contains("no look yet", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("screenplay", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("regen", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_WriteRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptm-vision-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(dir, "source"));
        try
        {
            ProjectVisionMeta.Write(dir, new ProjectVisionMeta.Document
            {
                VisualMedium = ProjectVisionMeta.MediumIllustrated,
                DecidedBy = "adaptation",
            });
            var read = ProjectVisionMeta.TryRead(dir);
            Assert.NotNull(read);
            Assert.Equal(ProjectVisionMeta.MediumIllustrated, read!.VisualMedium);
            Assert.False(string.IsNullOrWhiteSpace(read.RenderStyleLock));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
