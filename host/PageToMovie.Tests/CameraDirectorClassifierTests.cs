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

    [Fact]
    public void SystemPrompt_owns_camera_not_dof()
    {
        var prompt = CameraDirectorClassifier.SystemPromptText;
        Assert.Contains("only writer of the clip <Camera> tag", prompt, StringComparison.Ordinal);
        Assert.Contains("SAME-SPEAKER RUNS", prompt, StringComparison.Ordinal);
        Assert.Contains("previous", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HONOR ACTION", prompt, StringComparison.Ordinal);
        Assert.Contains("Optics owns aperture", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("f/1.4", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("shallow depth of field", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do NOT name an f-stop", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifySceneCameraAsync_FeedsPreviousFramingOnSameSpeakerRun()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "directives": [
                { "beat_id": "b1", "shot_scale": "medium", "lens_spec": "35mm lens",
                  "camera_movement": "hold", "framing_prompt": "Medium shot, 35mm lens, hold" },
                { "beat_id": "b2", "shot_scale": "close_up", "lens_spec": "50mm lens",
                  "camera_movement": "hold", "framing_prompt": "Closer hold, 50mm lens" }
              ]
            }
            """
        };
        var opts = Options.Create(new PageToMovieOptions { ClassifyCameraDirectorWithChat = true });
        var classifier = new CameraDirectorClassifier(mockChat, opts, NullLogger<CameraDirectorClassifier>.Instance);

        var scene = new Dictionary<string, object?>
        {
            ["scene_number"] = 1,
            ["setting"] = "INT. ROOM - DAY",
            ["characters_on_screen"] = new List<object?> { "Character_Narrator" },
        };
        var beats = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["beat_id"] = "b1",
                ["speaker"] = "Character_Narrator",
                ["dialogue"] = "First line.",
                ["visual_event"] = "Character sits.",
                ["shot_scale_hint"] = "medium",
                ["lens_spec"] = "35mm lens",
                ["framing_prompt"] = "Medium shot, 35mm lens, hold",
            },
            new()
            {
                ["beat_id"] = "b2",
                ["speaker"] = "Character_Narrator",
                ["dialogue"] = "Second line.",
                ["visual_event"] = "Character faces the window.",
                ["blocking_notes"] = "camera behind, back to camera",
            },
        };

        var directives = await classifier.ClassifySceneCameraAsync(scene, beats);
        Assert.NotNull(directives);
        Assert.NotNull(mockChat.LastUserPrompt);
        Assert.Contains("Same-speaker run after beat 'b1'", mockChat.LastUserPrompt);
        Assert.Contains("Previous shot_scale: medium", mockChat.LastUserPrompt);
        Assert.Contains("Previous lens: 35mm lens", mockChat.LastUserPrompt);
        Assert.Contains("Previous Camera: Medium shot, 35mm lens, hold", mockChat.LastUserPrompt);
        Assert.Contains("do not invent OTS", mockChat.LastUserPrompt);
        Assert.Contains("Blocking: camera behind, back to camera", mockChat.LastUserPrompt);
    }

    [Fact]
    public async Task ClassifySceneCameraAsync_StripsDofFromFramingPrompt()
    {
        var mockChat = new MockChatClient
        {
            ResponseToReturn = """
            {
              "directives": [
                {
                  "beat_id": "b1",
                  "shot_scale": "close_up",
                  "lens_spec": "85mm f/1.4 portrait lens",
                  "camera_movement": "hold",
                  "framing_prompt": "Tight close-up, 85mm f/1.4 lens, shallow depth of field"
                }
              ]
            }
            """
        };
        var opts = Options.Create(new PageToMovieOptions { ClassifyCameraDirectorWithChat = true });
        var classifier = new CameraDirectorClassifier(mockChat, opts, NullLogger<CameraDirectorClassifier>.Instance);

        var directives = await classifier.ClassifySceneCameraAsync(
            new Dictionary<string, object?> { ["scene_number"] = 1, ["setting"] = "INT. ROOM" },
            new List<Dictionary<string, object?>> { new() { ["beat_id"] = "b1", ["dialogue"] = "Hi." } });

        Assert.NotNull(directives);
        Assert.DoesNotContain("f/1.4", directives!["b1"].FramingPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth of field", directives["b1"].FramingPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("f/1.4", directives["b1"].LensSpec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("85mm", directives["b1"].FramingPrompt, StringComparison.OrdinalIgnoreCase);
    }
}
