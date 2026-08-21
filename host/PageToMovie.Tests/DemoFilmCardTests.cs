using PageToMovie.Adaptation.Contracts;
using PageToMovie.Api;
using PageToMovie.Engine;
using PageToMovie.Web.Components.Pages;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public class DemoFilmCardTests
{
    [Fact]
    public void AboutText_uses_stored_look_not_cinematic_description()
    {
        var film = new DemoListItem
        {
            Title = "Mary Had a Little Lamb",
            ProjectId = "budcribar/Mary19",
            Description = "A cinematic short film adaptation of “Mary Had a Little Lamb”.",
            Look = VisualMediumStyles.LabelIllustrated,
            VisualMedium = VisualMediumStyles.MediumIllustrated,
        };

        var about = Demo_FilmCard.AboutText(film);

        Assert.Equal(VisualMediumStyles.LabelIllustrated, about);
        Assert.DoesNotContain("cinematic", about, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mary", about, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutText_falls_back_to_visual_medium_token_when_label_missing()
    {
        var film = new DemoListItem
        {
            Title = "Any Story",
            Description = "A cinematic short film adaptation of “Any Story”.",
            VisualMedium = VisualMediumStyles.MediumPhotoreal,
        };

        Assert.Equal(VisualMediumStyles.MediumPhotoreal, Demo_FilmCard.AboutText(film));
    }

    [Fact]
    public void AboutText_short_fallback_when_look_missing_does_not_invent_illustrated()
    {
        var film = new DemoListItem
        {
            Title = "Any Story",
            Description = "A cinematic short film adaptation of “Any Story”.",
        };

        var about = Demo_FilmCard.AboutText(film);

        Assert.Equal("Short film.", about);
        Assert.DoesNotContain("picture", about, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("illustrated", about, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cinematic", about, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDemoLook_reads_illustrated_label_from_project_store()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_demo_look_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(ApiEndpointHelpers.ResolveDemoLook(root).Look);

            ProjectVisionMeta.SetAdaptationMediumPreference(root, ProjectVisionMeta.MediumIllustrated);
            var (look, medium) = ApiEndpointHelpers.ResolveDemoLook(root);

            Assert.Equal(VisualMediumStyles.LabelIllustrated, look);
            Assert.Equal(ProjectVisionMeta.MediumIllustrated, medium);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveDemoLook_auto_is_not_a_look()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptm_demo_look_auto_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ProjectVisionMeta.SetAdaptationMediumPreference(root, ProjectVisionMeta.MediumAuto);
            var (look, medium) = ApiEndpointHelpers.ResolveDemoLook(root);
            Assert.Null(look);
            Assert.Null(medium);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
