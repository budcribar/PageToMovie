using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

public sealed class CameraDirectorClassifierTests
{
    private sealed class MockChatClient : IChatClient
    {
        public bool IsConfigured => true;
        public string ResponseToReturn { get; set; } = "";
        public string? LastUserPrompt { get; private set; }

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            LastUserPrompt = userPrompt;
            return Task.FromResult(ResponseToReturn);
        }
    }

    [Fact]
    public async Task ClassifySceneCameraAsync_ParsesCameraDirectivesCorrectly()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "directives": [
                {
                  "beat_id": "b1",
                  "shot_scale": "wide",
                  "lens_spec": "24mm anamorphic lens",
                  "camera_movement": "locked tripod establishing shot",
                  "framing_prompt": "Wide establishing shot of room"
                },
                {
                  "beat_id": "b2",
                  "shot_scale": "close_up",
                  "lens_spec": "85mm f/1.4 portrait lens",
                  "camera_movement": "slow 10% dolly push-in",
                  "framing_prompt": "Tight close-up on face"
                }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyCameraDirectorWithChat = true });
        var classifier = new CameraDirectorClassifier(mockChat, opts, NullLogger<CameraDirectorClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. BARE ROOM - NIGHT"
        };

        var beats = new List<Dictionary<string, object?>>
        {
            new() { ["beat_id"] = "b1", ["visual_event"] = "Silence in room" },
            new() { ["beat_id"] = "b2", ["dialogue"] = "True!—nervous—" }
        };

        var directives = await classifier.ClassifySceneCameraAsync(scene, beats);

        Assert.NotNull(directives);
        Assert.Equal(ShotScale.Wide, directives!["b1"].ShotScale);
        Assert.Equal("24mm anamorphic lens", directives["b1"].LensSpec);
        Assert.Equal(ShotScale.CloseUp, directives["b2"].ShotScale);
        Assert.Equal("slow 10% dolly push-in", directives["b2"].CameraMovement);
    }

    [Fact]
    public async Task ClassifySceneCameraAsync_ReturnsNullWhenDisabled()
    {
        var mockChat = new MockChatClient();
        var opts = Options.Create(new PageToMovieOptions { ClassifyCameraDirectorWithChat = false });
        var classifier = new CameraDirectorClassifier(mockChat, opts, NullLogger<CameraDirectorClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1 };
        var beats = new List<Dictionary<string, object?>> { new() { ["beat_id"] = "b1" } };

        var directives = await classifier.ClassifySceneCameraAsync(scene, beats);

        Assert.Null(directives);
    }

    [Fact]
    public async Task ClassifySceneCameraAsync_RendersSecondSpeakerLineForCrossSpeakerBeat()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "directives": [
                { "beat_id": "b1", "shot_scale": "medium", "lens_spec": "35mm lens",
                  "camera_movement": "pan left from Character_Nick to Character_Sionna",
                  "framing_prompt": "Two-shot pan across the porch" }
              ]
            }
            """
        };

        var opts = Options.Create(new PageToMovieOptions { ClassifyCameraDirectorWithChat = true });
        var classifier = new CameraDirectorClassifier(mockChat, opts, NullLogger<CameraDirectorClassifier>.Instance);

        var scene = new Dictionary<string, object?> { ["scene_number"] = 1, ["setting"] = "EXT. PORCH - NIGHT" };
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = "b1",
                ["speaker"] = "Character_Nick",
                ["dialogue"] = "You coming or not?",
                ["secondary_speaker"] = "Character_Sionna",
                ["secondary_dialogue"] = "Give me a second.",
            }
        };

        var directives = await classifier.ClassifySceneCameraAsync(scene, beats);

        Assert.NotNull(directives);
        Assert.Contains("pan left from Character_Nick to Character_Sionna", directives!["b1"].CameraMovement);
        Assert.NotNull(mockChat.LastUserPrompt);
        Assert.Contains("Spoken (Character_Nick): \"You coming or not?\"", mockChat.LastUserPrompt);
        Assert.Contains("Then spoken (Character_Sionna): \"Give me a second.\"", mockChat.LastUserPrompt);
    }
}
