using Microsoft.Extensions.Logging.Abstractions;
using PageToMovie.Engine.ModelBacked;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ClassifierDirectiveHelpersTests
{
    [Fact]
    public void ParseKeyedArray_StripsFencesAndMapsItems()
    {
        var raw = """
            ```json
            { "dof": [ { "beat_id": "b1", "aperture": "f/1.4", "focal_plane": "eyes", "rack_focus": "static" } ] }
            ```
            """;

        var map = ClassifierDirectiveJson.ParseKeyedArray(
            raw, "dof",
            item => ClassifierDirectiveJson.MapThreeStringFields(
                item, "aperture", "focal_plane", "rack_focus",
                static (a, fp, rf) => (a, fp, rf)),
            NullLogger.Instance, "depth of field");

        Assert.NotNull(map);
        Assert.Equal(("f/1.4", "eyes", "static"), map!["b1"]);
    }

    [Fact]
    public void ParseKeyedArray_ReturnsNullWhenPropertyMissingOrNotArray()
    {
        var log = NullLogger.Instance;
        Assert.Null(ClassifierDirectiveJson.ParseKeyedArray("{\"other\":[]}", "dof", _ => ("b", 1), log, "x"));
        Assert.Null(ClassifierDirectiveJson.ParseKeyedArray("{\"dof\":\"nope\"}", "dof", _ => ("b", 1), log, "x"));
        Assert.Null(ClassifierDirectiveJson.ParseKeyedArray("{\"dof\":[]}", "dof", _ => ("b", 1), log, "x"));
        Assert.Null(ClassifierDirectiveJson.ParseKeyedArray("not-json", "dof", _ => ("b", 1), log, "x"));
    }

    [Fact]
    public void MapThreeStringFields_SkipsMissingOrBlankBeatId()
    {
        var raw = """
            { "dof": [
                { "aperture": "f/2" },
                { "beat_id": "  ", "aperture": "f/4" },
                { "beat_id": "b2", "aperture": "f/8", "focal_plane": "door", "rack_focus": "hold" }
            ] }
            """;

        var map = ClassifierDirectiveJson.ParseKeyedArray(
            raw, "dof",
            item => ClassifierDirectiveJson.MapThreeStringFields(
                item, "aperture", "focal_plane", "rack_focus",
                static (a, fp, rf) => (a, fp, rf)),
            NullLogger.Instance, "x");

        Assert.NotNull(map);
        Assert.Single(map!);
        Assert.Equal(("f/8", "door", "hold"), map["b2"]);
    }

    [Fact]
    public void BuildSceneUserPrompt_OmitsBlankStyleLock_AndOptionallySampleBeats()
    {
        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 2,
            ["setting"] = "INT. ROOM - NIGHT",
            ["render_style_lock"] = "  ",
            ["story_beats"] = new List<object?>
            {
                new Dictionary<string, object?> { ["visual_event"] = "A candle flickers" }
            }
        };

        var withoutBeats = ClassifierPromptParts.BuildSceneUserPrompt(scene, "RENDER STYLE LOCK", includeSampleBeats: false);
        Assert.Contains("SCENE 2: INT. ROOM - NIGHT", withoutBeats);
        Assert.DoesNotContain("RENDER STYLE LOCK", withoutBeats);
        Assert.DoesNotContain("SAMPLE BEATS", withoutBeats);

        scene["render_style_lock"] = "Period gothic";
        var withBeats = ClassifierPromptParts.BuildSceneUserPrompt(scene, "RENDER STYLE LOCK", includeSampleBeats: true);
        Assert.Contains("RENDER STYLE LOCK: Period gothic", withBeats);
        Assert.Contains("SAMPLE BEATS:", withBeats);
        Assert.Contains("A candle flickers", withBeats);
    }
}
