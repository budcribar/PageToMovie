using System.Text.Json;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Engine;
using PageToMovie.Engine.ModelBacked;
using PageToMovie.Fountain;
using Xunit;

namespace PageToMovie.Tests;

public class AspectRatioPipelineTests
{
    [Theory]
    [InlineData("illustrated_picture_book", "4:3")]
    [InlineData("picture_book", "4:3")]
    [InlineData("picture-book", "4:3")]
    [InlineData("childrens_book", "4:3")]
    [InlineData("illustrated", "4:3")]
    [InlineData("photoreal_live_action", "16:9")]
    [InlineData("live_action", "16:9")]
    [InlineData("photoreal", "16:9")]
    [InlineData("stylized_3d_animated", "16:9")]
    [InlineData("3d", "16:9")]
    [InlineData(null, "16:9")]
    [InlineData("", "16:9")]
    public void VisualMediumStyles_DefaultAspectRatioFor_resolves_expected_ratio(string? medium, string expectedRatio)
    {
        var actual = VisualMediumStyles.DefaultAspectRatioFor(medium);
        Assert.Equal(expectedRatio, actual);
    }

    [Theory]
    [InlineData("illustrated_picture_book", VisualMediumStyles.LabelIllustrated)]
    [InlineData("picture_book", VisualMediumStyles.LabelIllustrated)]
    [InlineData("photoreal_live_action", VisualMediumStyles.LabelPhotoreal)]
    [InlineData("stylized_3d_animated", VisualMediumStyles.LabelStylized3d)]
    [InlineData("other", VisualMediumStyles.LabelOther)]
    [InlineData("auto", VisualMediumStyles.LabelAuto)]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void VisualMediumStyles_DisplayLabel_matches_look_copy(string? medium, string expected)
    {
        Assert.Equal(expected, VisualMediumStyles.DisplayLabel(medium));
    }

    [Fact]
    public void ProjectVisionMeta_DefaultAspectRatio_matches_VisualMediumStyles()
    {
        Assert.Equal("4:3", ProjectVisionMeta.DefaultAspectRatio(ProjectVisionMeta.MediumIllustrated));
        Assert.Equal("16:9", ProjectVisionMeta.DefaultAspectRatio(ProjectVisionMeta.MediumPhotoreal));
    }

    [Fact]
    public void Stage1Normalizer_sets_4x3_for_illustrated_picture_book()
    {
        var gpv = new Dictionary<string, object?>
        {
            ["visual_medium"] = "illustrated_picture_book",
        };
        var stage1 = new Dictionary<string, object?>
        {
            ["movie_title"] = "Mary Had a Little Lamb",
            ["global_production_variables"] = gpv,
            ["scenes"] = new List<object?>(),
        };

        var normalized = Stage1Normalizer.Normalize(stage1);
        var normGpv = Assert.IsType<Dictionary<string, object?>>(normalized["global_production_variables"]);
        Assert.Equal("4:3", normGpv["target_aspect_ratio"]);
    }

    [Fact]
    public void Stage1Normalizer_preserves_explicit_aspect_ratio()
    {
        var gpv = new Dictionary<string, object?>
        {
            ["visual_medium"] = "illustrated_picture_book",
            ["target_aspect_ratio"] = "1:1",
        };
        var stage1 = new Dictionary<string, object?>
        {
            ["movie_title"] = "Square Picture Book",
            ["global_production_variables"] = gpv,
            ["scenes"] = new List<object?>(),
        };

        var normalized = Stage1Normalizer.Normalize(stage1);
        var normGpv = Assert.IsType<Dictionary<string, object?>>(normalized["global_production_variables"]);
        Assert.Equal("1:1", normGpv["target_aspect_ratio"]);
    }

    [Fact]
    public void FountainStage1Importer_BuildStage1_applies_medium_aspect_ratio()
    {
        var fountain = """
            Title: The Little Lamb
            Author: Test Author

            EXT. MEADOW - DAY
            Mary walks with the lamb.
            """;
        var parsed = FountainParser.Parse(fountain);

        var docIllustrated = FountainStage1Importer.BuildStage1(parsed, visualMedium: "illustrated_picture_book");
        var gpvIll = Assert.IsType<Dictionary<string, object?>>(docIllustrated["global_production_variables"]);
        Assert.Equal("4:3", gpvIll["target_aspect_ratio"]);

        var docPhotoreal = FountainStage1Importer.BuildStage1(parsed, visualMedium: "photoreal_live_action");
        var gpvPhoto = Assert.IsType<Dictionary<string, object?>>(docPhotoreal["global_production_variables"]);
        Assert.Equal("16:9", gpvPhoto["target_aspect_ratio"]);
    }

    [Theory]
    [InlineData("Notes: Medium = illustratedpicturebook", "4:3")]
    [InlineData("Notes: Medium = illustrated_picture_book", "4:3")]
    [InlineData("Medium: illustrated_picture_book", "4:3")]
    [InlineData("Visual Medium: picture_book", "4:3")]
    [InlineData("Style: picturebook", "4:3")]
    [InlineData("Notes: Medium = photoreal", "16:9")]
    [InlineData("", "16:9")]
    public void FountainStage1Importer_BuildStage1_resolves_aspect_ratio_from_fountain_metadata_when_omitted(string headerLine, string expectedRatio)
    {
        var fountain = $"""
            Title: The Story
            Author: Test Author
            {headerLine}

            EXT. MEADOW - DAY
            Mary walks with the lamb.
            """;
        var parsed = FountainParser.Parse(fountain);

        // Called without explicit visualMedium argument — must infer from Fountain metadata
        var doc = FountainStage1Importer.BuildStage1(parsed);
        var gpv = Assert.IsType<Dictionary<string, object?>>(doc["global_production_variables"]);
        Assert.Equal(expectedRatio, gpv["target_aspect_ratio"]);
    }

    [Fact]
    public void CameraDirectorClassifier_SystemPrompt_contains_headroom_and_framing_rules()
    {
        var prompt = CameraDirectorClassifier.SystemPromptText;
        Assert.Contains("Universal Headroom", prompt);
        Assert.Contains("Avoid Edge-Crowding", prompt);
        Assert.Contains("Multi-Height Grounding", prompt);
    }

    [Fact]
    public void ShotPlanRefiningClassifier_SystemPrompt_contains_headroom_rules()
    {
        var prompt = ShotPlanRefiningClassifier.SystemPrompt();
        Assert.Contains("Framing & Headroom", prompt);
        Assert.Contains("vertical headroom", prompt);
    }
}
