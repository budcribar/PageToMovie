using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class DepthOfFieldClassifierTests
{
    private sealed class MockChatClient : IChatClient
    {
        public bool IsConfigured => true;
        public string ResponseToReturn { get; set; } = "";

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task ClassifySceneDepthOfFieldAsync_ParsesDofDirectivesCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "dof": [
                {
                  "beat_id": "b1",
                  "aperture": "f/1.4 shallow depth of field, creamy soft bokeh",
                  "focal_plane": "Foreground: tin lantern latch",
                  "rack_focus": "Rack focus from foreground lantern latch at t=0s to Old Man's eyes at t=2s"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyDepthOfFieldWithChat = true });
        var classifier = new DepthOfFieldClassifier(mockChat, opts, NullLogger<DepthOfFieldClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. OLD MAN'S BEDCHAMBER - NIGHT"
        };

        var beats = new List<Dictionary<string, object?>>
        {
            new() { ["beat_id"] = "b1", ["visual_event"] = "A thin ray of light falls upon the vulture eye" }
        };

        var dof = await classifier.ClassifySceneDepthOfFieldAsync(scene, beats);

        Assert.NotNull(dof);
        Assert.Equal("f/1.4", dof!["b1"].Aperture);
        Assert.Equal("Foreground: tin lantern latch", dof["b1"].FocalPlane);
        Assert.Contains("Rack focus from foreground", dof["b1"].RackFocus);
    }

    [Fact]
    public async Task ClassifySceneDepthOfFieldAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyDepthOfFieldWithChat = false });
        var classifier = new DepthOfFieldClassifier(mockChat, opts, NullLogger<DepthOfFieldClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };
        var beats = new List<Dictionary<string, object?>> { new() { ["beat_id"] = "b1" } };

        var dof = await classifier.ClassifySceneDepthOfFieldAsync(scene, beats);

        Assert.Null(dof);
    }

    [Fact]
    public void SystemPrompt_aperture_is_fstop_only()
    {
        var prompt = DepthOfFieldClassifier.SystemPrompt();
        Assert.Contains("f-stop only", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not describe depth of field or bokeh", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creamy", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shallow depth of field", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("f/1.4 shallow depth of field, creamy soft bokeh", "f/1.4")]
    [InlineData("f / 2.8 moderate depth of field", "f/2.8")]
    [InlineData("f/8", "f/8")]
    [InlineData("creamy bokeh only", "")]
    public void SanitizeAperture_keeps_fstop_drops_dof_prose(string raw, string expected) =>
        Assert.Equal(expected, DepthOfFieldClassifier.SanitizeAperture(raw));
}
