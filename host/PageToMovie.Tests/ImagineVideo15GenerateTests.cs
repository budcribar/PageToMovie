using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Imagine Video 1.5 generate-role: reference_audios + AUDIO_n, voice persist,
/// generate 3 vs extend 1 speakers, 1080p vs 720p R2V cap, no 1.5 extensions,
/// and skip TTS overlay when the take wire model writes native audio.
/// No live xAI calls.
/// </summary>
[Collection("catalog-serial")]
public sealed class ImagineVideo15GenerateTests : IDisposable
{
    private const string GenerateId = "grok-imagine-video-1.5";
    private const string ExtendId = "grok-imagine-video";
    private const string VirtualId = "imagine-video-1.5-extend";
    private const string ProjectId = "Imagine15Test";

    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly string _root;
    private readonly ProjectStore _store;
    private readonly ProjectTelemetryService _telemetry;

    public ImagineVideo15GenerateTests()
    {
        SupportedModelCatalog.ReloadCatalog();
        _root = Path.Combine(Path.GetTempPath(), "fs-imagine15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", ProjectId));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
        _telemetry = new ProjectTelemetryService(_store, NullLogger<ProjectTelemetryService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    [Fact]
    public void Catalog_1_5_is_r2v_capped_and_flat_priced()
    {
        var m = SupportedModelCatalog.Find(GenerateId, ModelCapability.Video);
        Assert.NotNull(m);
        Assert.False(m!.SupportsVideoContinue);
        Assert.Equal(3, m.MaxSpeakersPerClip);
        Assert.Equal("720p", m.MaxResolutionWithReferences);
        Assert.Equal(0.08, m.VideoCostPerSecondByResolution!["480p"]);
        Assert.Equal(0.08, m.VideoCostPerSecondByResolution["720p"]);
        Assert.Equal(0.08, m.VideoCostPerSecondByResolution["1080p"]);

        var bundle = SupportedModelCatalog.Find(VirtualId, ModelCapability.Video);
        Assert.NotNull(bundle);
        Assert.True(bundle!.Virtual);
        Assert.NotEqual(3, bundle.MaxSpeakersPerClip);
        Assert.Empty(SupportedModelCatalog.GenerateRolePresetVoices(ExtendId));
        Assert.Equal(28, SupportedModelCatalog.GenerateRolePresetVoices(VirtualId).Count);
    }

    [Fact]
    public void Roles_never_wire_1_5_to_extensions()
    {
        var roles = SupportedModelCatalog.ResolveVideoRoles(VirtualId);
        Assert.Equal(GenerateId, roles.WireModelId(isExtendHop: false));
        Assert.Equal(ExtendId, roles.WireModelId(isExtendHop: true));
        Assert.False(roles.Generate.SupportsVideoContinue);
        Assert.True(roles.Extend!.SupportsVideoContinue);
    }

    [Fact]
    public void VideoReferenceResolution_caps_1080p_only_when_refs_or_voices_attached()
    {
        var model = SupportedModelCatalog.Find(GenerateId, ModelCapability.Video);
        Assert.Equal("1080p", VideoReferenceResolution.Cap("1080p", model, false, false));
        Assert.Equal("720p", VideoReferenceResolution.Cap("1080p", model, true, false));
        Assert.Equal("720p", VideoReferenceResolution.Cap("1080p", model, false, true));
        Assert.Equal("720p", VideoReferenceResolution.Cap("720p", model, true, true));
        Assert.Equal("480p", VideoReferenceResolution.Cap("480p", model, true, true));
    }

    [Fact]
    public void ImagineVoicePicker_scores_gender_age_and_temperament()
    {
        var roster = SupportedModelCatalog.Find(GenerateId, ModelCapability.Video)!.PresetVoices!;
        Assert.Equal("orion", ImagineVoicePicker.Pick(
            roster, new ImagineVoicePicker.VoiceHints("male", "elderly", "weathered storyteller", "Old man", null)));
        Assert.Equal("carina", ImagineVoicePicker.Pick(
            roster, new ImagineVoicePicker.VoiceHints("female", "youthful", "cheerful girl", null, "bright")));
        Assert.Equal("eve", ImagineVoicePicker.NormalizeVoiceId(roster, "EVE"));
        Assert.Null(ImagineVoicePicker.NormalizeVoiceId(roster, "not-a-voice"));
    }

    [Fact]
    public void ImagineVoiceAssignment_picks_once_then_honors_override()
    {
        var roster = ImagineVoiceAssignment.RosterForProjectVideo(GenerateId);
        var first = ImagineVoiceAssignment.Ensure(
            _store, ProjectId, "Character_Hero", roster,
            new ImagineVoicePicker.VoiceHints("male", "elderly", "weathered wise narrator", "Hero", null));
        Assert.Equal("orion", first);
        Assert.Equal("orion", _store.ListCharacters(ProjectId).Single(c => c.Key == "Character_Hero").ImagineVoiceId);

        var again = ImagineVoiceAssignment.Ensure(
            _store, ProjectId, "Character_Hero", roster,
            new ImagineVoicePicker.VoiceHints("female", "youthful", "cheerful", null, null),
            existingVoiceId: first);
        Assert.Equal("orion", again);

        _store.UpdateCharacterSeedText(ProjectId, "Character_Hero", imagineVoiceId: "eve");
        Assert.Equal("eve", _store.ListCharacters(ProjectId).Single(c => c.Key == "Character_Hero").ImagineVoiceId);

        var saved = ImagineVoiceAssignment.Ensure(
            _store, ProjectId, "Character_Hero", roster,
            new ImagineVoicePicker.VoiceHints("male", "elderly", null, null, null),
            existingVoiceId: "eve");
        Assert.Equal("eve", saved);
    }

    [Fact]
    public void Prompt_adds_AUDIO_tags_and_omits_VoiceLock_when_preset_attached()
    {
        using var doc = JsonDocument.Parse("""
            {
              "clip_number": 1,
              "visual_prompt": "INT. HALL.",
              "characters_on_screen": ["Character_Eve", "Character_Rex", "Character_Orion"],
              "audio_payload": {
                "speaker": "Character_Eve",
                "dialogue": "We begin.",
                "delivery": "spoken_on_camera",
                "secondary_speaker": "Character_Rex",
                "secondary_dialogue": "We answer.",
                "tertiary_speaker": "Character_Orion",
                "tertiary_dialogue": "We close."
              }
            }
            """);
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Eve"] = new()
            {
                Key = "Character_Eve", DisplayName = "Eve", ImagineVoiceId = "eve",
                VoiceProfile = "polished storyteller",
            },
            ["Character_Rex"] = new()
            {
                Key = "Character_Rex", DisplayName = "Rex", ImagineVoiceId = "rex",
                VoiceProfile = "deep commander",
            },
            ["Character_Orion"] = new()
            {
                Key = "Character_Orion", DisplayName = "Orion", ImagineVoiceId = "orion",
            },
        };

        var built = ClipVideoPromptBuilder.Build(
            doc.RootElement, Path.GetTempPath(), profiles, videoModel: GenerateId);

        Assert.Equal(new[] { "eve", "rex", "orion" }, built.ReferenceAudioVoiceIds);
        Assert.Contains("<AUDIO_0>", built.Prompt);
        Assert.Contains("<AUDIO_1>", built.Prompt);
        Assert.Contains("<AUDIO_2>", built.Prompt);
        Assert.Contains("Character_Eve <AUDIO_0>", built.Prompt);
        Assert.Contains("Character_Rex <AUDIO_1>", built.Prompt);
        Assert.Contains("Character_Orion <AUDIO_2>", built.Prompt);
        Assert.DoesNotContain("<VoiceLock>", built.Prompt);
    }

    [Fact]
    public void Prompt_keeps_VoiceLock_when_no_preset_and_skips_AUDIO_on_extend_hop()
    {
        using var doc = JsonDocument.Parse("""
            {
              "clip_number": 2,
              "visual_prompt": "INT. HALL.",
              "characters_on_screen": ["Character_Eve"],
              "audio_payload": {
                "speaker": "Character_Eve",
                "dialogue": "We begin.",
                "delivery": "spoken_on_camera"
              }
            }
            """);
        var profiles = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Eve"] = new()
            {
                Key = "Character_Eve", DisplayName = "Eve",
                VoiceProfile = "polished storyteller",
            },
        };

        var fresh = ClipVideoPromptBuilder.Build(
            doc.RootElement, Path.GetTempPath(), profiles, videoModel: GenerateId);
        Assert.Empty(fresh.ReferenceAudioVoiceIds);
        Assert.DoesNotContain("<AUDIO_0>", fresh.Prompt);
        Assert.Contains("<VoiceLock>", fresh.Prompt);

        var hop = ClipVideoPromptBuilder.Build(
            doc.RootElement, Path.GetTempPath(), profiles,
            videoModel: GenerateId,
            previousClipExtendFileId: "file-prev");
        Assert.Empty(hop.ReferenceAudioVoiceIds);
        Assert.DoesNotContain("<AUDIO_0>", hop.Prompt);
    }

    [Fact]
    public void NativeVideoAudioPolicy_skips_overlay_only_for_native_audio_wire_model()
    {
        Assert.True(NativeVideoAudioPolicy.ShouldSkipVoiceOverlay(GenerateId));
        Assert.False(NativeVideoAudioPolicy.ShouldSkipVoiceOverlay(ExtendId));
        Assert.False(NativeVideoAudioPolicy.ShouldSkipVoiceOverlay(VirtualId));
        Assert.False(NativeVideoAudioPolicy.ShouldSkipVoiceOverlay(null));
    }

    [Fact]
    public async Task Grok_submit_sends_reference_audios_and_caps_resolution()
    {
        var handler = new StubGrokVideoHandler();
        var client = BuildClient(handler);
        var had = Environment.GetEnvironmentVariable("XAI_API_KEY");
        Environment.SetEnvironmentVariable("XAI_API_KEY", "test-key");
        try
        {
            await client.SubmitGenerationAsync(
                "bare text", 6, "1080p", GenerateId, CancellationToken.None);
            Assert.Contains(handler.Paths, p => p.EndsWith("videos/generations", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.Paths, p => p.Contains("extensions", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("1080p", handler.LastBody!["resolution"]!.GetValue<string>());
            Assert.False(handler.LastBody.ContainsKey("reference_audios"));

            handler.Reset();
            await client.SubmitGenerationAsync(
                "voices", 6, "1080p", GenerateId, CancellationToken.None,
                referenceAudioVoiceIds: new[] { "eve", "rex", "orion", "iris" });
            Assert.Equal("720p", handler.LastBody!["resolution"]!.GetValue<string>());
            var voices = handler.LastBody["reference_audios"]!.AsArray();
            Assert.Equal(3, voices.Count);
            Assert.Equal("eve", voices[0]!["voice_id"]!.GetValue<string>());
            Assert.Equal("rex", voices[1]!["voice_id"]!.GetValue<string>());
            Assert.Equal("orion", voices[2]!["voice_id"]!.GetValue<string>());

            handler.Reset();
            var png = Path.Combine(_root, "ref.png");
            await File.WriteAllBytesAsync(png, Png1x1);
            await client.SubmitGenerationAsync(
                "refs", 6, "1080p", GenerateId, CancellationToken.None,
                referenceImagePaths: new[] { png });
            Assert.Equal("720p", handler.LastBody!["resolution"]!.GetValue<string>());
            Assert.True(handler.LastBody.ContainsKey("reference_images"));
            Assert.False(handler.LastBody.ContainsKey("image"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", had);
        }
    }

    [Fact]
    public async Task Grok_does_not_call_extensions_when_catalog_disallows_continue()
    {
        var handler = new StubGrokVideoHandler();
        var client = BuildClient(handler);
        var had = Environment.GetEnvironmentVariable("XAI_API_KEY");
        Environment.SetEnvironmentVariable("XAI_API_KEY", "test-key");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SubmitGenerationAsync(
                    "hop", 6, "720p", GenerateId, CancellationToken.None,
                    extendSourceFileId: "file-prev"));
            Assert.Contains("does not support video continue", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(handler.Paths);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", had);
        }
    }

    private GrokVideoClient BuildClient(StubGrokVideoHandler handler)
    {
        var http = new HttpClient(handler);
        var opts = Options.Create(new PageToMovieOptions { GrokTimeoutSeconds = 30, GrokPollSeconds = 0 });
        return new GrokVideoClient(http, opts, _telemetry, NullLogger<GrokVideoClient>.Instance);
    }

    private sealed class StubGrokVideoHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public JsonObject? LastBody { get; private set; }

        public void Reset()
        {
            Paths.Clear();
            LastBody = null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            Paths.Add(path);
            if (request.Content is not null)
            {
                var raw = await request.Content.ReadAsStringAsync(ct);
                LastBody = JsonNode.Parse(raw) as JsonObject;
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("videos/generations", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"request_id\":\"req-1\"}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"unexpected\"}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
