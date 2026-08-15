using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Xunit;

namespace PageToMovie.Tests.LiveApi;

/// <summary>
/// PAID: live clip video generation + prompt-build tests across all configured providers.
/// Excluded from default CI. Requires PAGETOMOVIE_LIVE_API_TESTS=1 and at least one
/// provider API key (XAI_API_KEY, GEMINI_API_KEY, ANTHROPIC_API_KEY).
///
/// Run all live API tests:
///   dotnet test --filter "Category=LiveApi"
///
/// Run a specific provider only:
///   dotnet test --filter "Category=LiveApi&FullyQualifiedName~Grok"
///   dotnet test --filter "Category=LiveApi&FullyQualifiedName~Gemini"
///   dotnet test --filter "Category=LiveApi&FullyQualifiedName~Claude"
/// </summary>
[Trait("Category", LiveApiGate.Category)]
public class ClipVideoGenerationLiveTests : IDisposable
{
    // ─── Provider matrices ───────────────────────────────────────────────────

    /// <summary>
    /// All video-capable providers. Each row: (label, videoModel, imageModel, envKey, supportsRefs).
    /// Claude/Anthropic intentionally absent — no video generation API.
    /// </summary>
    public static IEnumerable<object[]> VideoProviders()
    {
        yield return new object[]
        {
            "Grok_xAI", "grok-imagine-video", "grok-imagine-image-quality",
            SupportedModelCatalog.XaiApiKeyEnv, true,
        };
        yield return new object[]
        {
            "Gemini_Veo", "veo-3.1-generate-preview", "gemini-3-pro-image",
            SupportedModelCatalog.GoogleApiKeyEnv, false,
        };
    }

    /// <summary>
    /// All chat/planning providers. Each row: (label, model, envKey).
    /// </summary>
    public static IEnumerable<object[]> ChatProviders()
    {
        yield return new object[] { "Grok_xAI", "grok-4.5",        SupportedModelCatalog.XaiApiKeyEnv };
        yield return new object[] { "Claude",   "claude-sonnet-5", SupportedModelCatalog.AnthropicApiKeyEnv };
        yield return new object[] { "Gemini",   "gemini-2.5-flash",    SupportedModelCatalog.GoogleApiKeyEnv };
    }

    // ─── Setup / teardown ────────────────────────────────────────────────────

    private readonly string _tmpDir;

    public ClipVideoGenerationLiveTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "ptm-live-clipgen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* ignore */ }
    }

    // ─── Prompt-build tests (no video API spend) ─────────────────────────────

    /// <summary>
    /// Verifies the prompt built for an animal-only opener clip has STYLE LOCK and the
    /// Buster reference portrait is attached. Tests Bug 1 + Bug 2 in the gen-time path.
    /// No actual video generation — ClipVideoPromptBuilder.Build() only.
    /// </summary>
    [LiveApiTheory]
    [MemberData(nameof(VideoProviders))]
    public void PromptBuild_animal_only_clip_has_style_lock_and_buster_ref(
        string providerLabel, string videoModel, string imageModel, string envKey, bool supportsRefs)
    {
        _ = videoModel; _ = imageModel;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envKey)))
            return; // Provider key not set — skip gracefully

        var clipJson = """
            {
              "clip_number": 1,
              "veo_continuation_source": "none",
              "visual_prompt": "EXT. SUBURBAN BACKYARD - DAY. Character_Buster bounds across the grass.",
              "negative_prompt": "",
              "characters_on_screen": ["Character_Buster", "Character_Narrator"],
              "focus_keys": ["Character_Buster"],
              "primary_subject": "Character_Buster",
              "audio_payload": {
                "delivery": "voiceover_internal",
                "speaker": "Character_Narrator",
                "dialogue": "He's Buster the Noodle Head Dog.",
                "sfx": "", "ambient": ""
              },
              "duration_seconds": 7
            }
            """;

        var clip = JsonDocument.Parse(clipJson).RootElement;

        var projectDir = Path.Combine(_tmpDir, $"promptbuild-{providerLabel}");
        var charDir = Path.Combine(projectDir, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_buster_ref.png"), MinimalPng1x1());

        var chars = new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Narrator"] = new() { Key = "Character_Narrator", DisplayName = "Narrator", VoiceOnly = true },
            ["Character_Buster"]   = new() { Key = "Character_Buster",   DisplayName = "Buster",
                Description = "Small black-and-white dog.", VisualLock = "Always the same dog." },
        };

        var built = ClipVideoPromptBuilder.Build(
            clip, projectDir, chars,
            styleHead:
                "STYLE LOCK: stylized 3D animated children's picture-book CG " +
                "(same render family as animal hero) -- not photoreal, not live-action");

        // Bug 1: STYLE LOCK must be in the assembled prompt
        Assert.Contains("STYLE LOCK", built.Prompt, StringComparison.OrdinalIgnoreCase);

        // Bug 2: Character_Buster must be an on-screen key
        Assert.Contains("Character_Buster", built.OnScreenKeys, StringComparer.OrdinalIgnoreCase);

        if (supportsRefs)
        {
            Assert.NotEmpty(built.ReferenceImagePaths);
            Assert.True(built.RefsAttachedToApi,
                $"[{providerLabel}] Buster ref portrait should be attached to API payload.");
        }
    }

    // ─── Stage 2 planning smoke tests ────────────────────────────────────────

    /// <summary>
    /// Runs Stage 2 blueprint planning (pure heuristic — no AI chat call) with each
    /// provider's configuration and verifies STYLE LOCK appears and Buster is in cast.
    /// Kept cheap: no LLM call; validates the fixes in the planning code path.
    /// </summary>
    [LiveApiTheory]
    [MemberData(nameof(ChatProviders))]
    public async Task Stage2_plan_animal_scene_has_style_lock_and_buster_in_cast(
        string providerLabel, string planningModel, string envKey)
    {
        _ = planningModel;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envKey)))
            return;

        var workspace = Path.Combine(_tmpDir, $"stage2-{providerLabel}");
        Directory.CreateDirectory(workspace);

        const string fountain = """
            Title: Buster Live Plan Smoke

            EXT. SUBURBAN BACKYARD - DAY

            A small black-and-white dog BOUNDS across the grass.

            NARRATOR (V.O.)
            He's Buster the Noodle Head Dog.
            """;

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = workspace, UseFakes = false });
        var store = new ProjectStore(opts);
        const string projectId = "BusterLive";

        ScreenplayService.SaveDraft(store, projectId, fountain);
        Assert.True(ScreenplayService.SignOff(store, projectId).Ok);

        var sourceDir = Path.Combine(store.GetProjectDir(projectId), "source");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "cast_seeds.json"), """
            {
              "schema_version": "cast_seeds.v1",
              "character_seed_tokens": {
                "Character_Narrator": {
                  "canonical_given_name": "Narrator",
                  "display_name_policy": "never_on_screen",
                  "description": "Narrator (voice only; not on screen).",
                  "voice_profile": "Warm adult storyteller."
                },
                "Character_Buster": {
                  "canonical_given_name": "Buster",
                  "display_name_policy": "ok_anytime",
                  "description": "Small black-and-white dog.",
                  "visual_lock": "Always the same small dog.",
                  "species_kind": "animal"
                }
              }
            }
            """);

        // Pure heuristic planner — no AI enrichers, no API cost
        var planner = new Stage2PlannerService(store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");
        Assert.True(result.Ok, $"[{providerLabel}] PlanAsync failed");
        Assert.True(File.Exists(result.OutPath));

        var bp = await File.ReadAllTextAsync(result.OutPath!);
        using var doc = JsonDocument.Parse(bp);

        bool foundStyleLock = false, foundBuster = false;
        foreach (var scene in doc.RootElement.GetProperty("scenes").EnumerateArray())
        {
            foreach (var clip in scene.GetProperty("veo_clips").EnumerateArray())
            {
                var vp = clip.GetProperty("visual_prompt").GetString() ?? "";
                if (vp.Contains("STYLE LOCK", StringComparison.OrdinalIgnoreCase))
                    foundStyleLock = true;

                if (clip.TryGetProperty("characters_on_screen", out var cos))
                    foreach (var ch in cos.EnumerateArray())
                        if (string.Equals(ch.GetString(), "Character_Buster", StringComparison.OrdinalIgnoreCase))
                            foundBuster = true;
            }
        }

        Assert.True(foundStyleLock,
            $"[{providerLabel}] No clip had 'STYLE LOCK'. Animal-only scene must get style lock.");
        Assert.True(foundBuster,
            $"[{providerLabel}] Character_Buster not found in any clip's characters_on_screen.");
    }

    [LiveApiTheory]
    [MemberData(nameof(ChatProviders))]
    public async Task Live_TellTaleHeart_plan_includes_dialogue_pronunciation_hints(
        string providerLabel, string planningModel, string envKey)
    {
        _ = planningModel;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envKey)))
            return;

        var fountainPath = @"c:\Users\budcr\source\repos\gemini\PageToMovie\projects\TellTaleHeartV7\source\screenplay.fountain";
        if (!File.Exists(fountainPath))
        {
            fountainPath = @"c:\Users\budcr\source\repos\PageToMovie\projects\TellTaleHeartV7\source\screenplay.fountain";
        }
        if (!File.Exists(fountainPath)) return;

        var workspace = Path.Combine(_tmpDir, $"stage2-telltale-{providerLabel}");
        Directory.CreateDirectory(workspace);
        const string projectId = "TellTaleHeartLivePronTest";
        Directory.CreateDirectory(Path.Combine(workspace, "projects", projectId));

        var opts = Options.Create(new PageToMovieOptions { WorkspaceRoot = workspace, UseFakes = false });
        var store = new ProjectStore(opts);

        var text = await File.ReadAllTextAsync(fountainPath);
        ScreenplayService.SaveDraft(store, projectId, text);
        Assert.True(ScreenplayService.SignOff(store, projectId).Ok);

        var planner = new Stage2PlannerService(store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "16");
        Assert.True(result.Ok, $"[{providerLabel}] PlanAsync failed");

        var bpText = await File.ReadAllTextAsync(result.OutPath!);
        using var doc = JsonDocument.Parse(bpText);

        bool foundPronunciationHint = false;
        foreach (var sc in doc.RootElement.GetProperty("scenes").EnumerateArray())
        {
            if (!sc.TryGetProperty("veo_clips", out var clips) && !sc.TryGetProperty("clips", out clips)) continue;
            foreach (var clip in clips.EnumerateArray())
            {
                var built = ClipVideoPromptBuilder.Build(clip, store.GetProjectDir(projectId), new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>());
                if (built.Prompt.Contains("tear up the planks", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Contains("<Pronunciation>Pronounce 'tear' as /tɛr/ (rip or separate)", built.Prompt);
                    Assert.Contains("tare", built.Prompt);
                    foundPronunciationHint = true;
                }
            }
        }

        Assert.True(foundPronunciationHint, $"[{providerLabel}] Expected clip containing 'tear up the planks' with explicit pronunciation guide hint.");
    }

    // ─── Full video generation smoke tests ────────────────────────────────────

    /// <summary>
    /// Generates a real 3-second clip for the fixed Buster Scene 1 and verifies it
    /// produces a valid MP4. Tests each configured video provider.
    /// COST: ~$0.15 (Grok 480p 3s) to ~$1.20 (Veo 3.1 720p 3s) per run.
    /// </summary>
    [LiveApiTheory]
    [MemberData(nameof(VideoProviders))]
    public async Task GenerateClip_animal_scene1_produces_valid_mp4(
        string providerLabel, string videoModel, string imageModel, string envKey, bool supportsRefs)
    {
        _ = imageModel;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envKey)))
            return;

        var projectDir = Path.Combine(_tmpDir, $"clipgen-{providerLabel}");
        var charDir = Path.Combine(projectDir, "assets", "characters");
        Directory.CreateDirectory(charDir);
        File.WriteAllBytes(Path.Combine(charDir, "character_buster_ref.png"), MinimalPng1x1());

        var outDir = Path.Combine(projectDir, "assets", "video");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"buster_sc01_clip01_{providerLabel}.mp4");

        // Fixed prompt — includes STYLE LOCK (Bug 1) and correct cast (Bug 2)
        const string prompt =
            "STYLE LOCK: stylized 3D animated children's picture-book CG " +
            "(same render family as animal hero) -- not photoreal, not live-action.\n\n" +
            "CHARACTER VARIABLES:\n" +
            "Buster: Small black-and-white short-coated terrier mix. Bright dark eyes, floppy ears.\n\n" +
            "CAST COUNT: exactly 1 distinct on-screen character — Character_Buster. " +
            "Do not invent extra people, duplicate faces, or crowd extras not listed.\n\n" +
            "THIS CLIP:\n" +
            "Silent beat. Character_Buster bounds across the grass, leaping like a frog. " +
            "He skids, tumbles, pops up again, tail a blur. " +
            "Medium shot, 35mm lens, slow push-in. / 480p, 24fps";

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ptmOpts = Options.Create(new PageToMovieOptions());
        var ptmStore = new ProjectStore(ptmOpts);
        var telemetry = new ProjectTelemetryService(ptmStore, NullLogger<ProjectTelemetryService>.Instance);

        IVideoClient client = providerLabel switch
        {
            "Grok_xAI" => new GrokVideoClient(
                new HttpClient(), ptmOpts, telemetry,
                NullLogger<GrokVideoClient>.Instance),
            "Gemini_Veo" => new GeminiVideoClient(
                new HttpClient(), ptmOpts, telemetry,
                NullLogger<GeminiVideoClient>.Instance),
            _ => throw new ArgumentOutOfRangeException(nameof(providerLabel)),
        };

        string[] refs = supportsRefs
            ? [Path.Combine(charDir, "character_buster_ref.png")]
            : [];

        var requestId = await client.SubmitGenerationAsync(
            prompt, durationSeconds: 3, resolution: "480p",
            model: videoModel, ct: cts.Token,
            referenceImagePaths: refs);

        Assert.False(string.IsNullOrWhiteSpace(requestId),
            $"[{providerLabel}] SubmitGenerationAsync returned empty requestId.");

        var videoUrl = await client.PollForVideoUrlAsync(requestId, onProgress: null, cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(videoUrl),
            $"[{providerLabel}] PollForVideoUrlAsync returned empty URL.");

        // Download the resulting MP4
        using var http = new HttpClient();
        var bytes = await http.GetByteArrayAsync(videoUrl, cts.Token);
        await File.WriteAllBytesAsync(outPath, bytes, cts.Token);

        Assert.True(File.Exists(outPath),
            $"[{providerLabel}] Expected MP4 at {outPath} but file was not created.");
        Assert.True(bytes.Length > 10_000,
            $"[{providerLabel}] MP4 too small ({bytes.Length} bytes) — likely empty/errored.");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Minimal valid 1x1 white-pixel PNG for stub reference images.</summary>
    private static byte[] MinimalPng1x1() =>
    [
        0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,
        0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
        0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,
        0x08,0x02,0x00,0x00,0x00,0x90,0x77,0x53,
        0xDE,0x00,0x00,0x00,0x0C,0x49,0x44,0x41,
        0x54,0x08,0xD7,0x63,0xF8,0xFF,0xFF,0x3F,
        0x00,0x05,0xFE,0x02,0xFE,0xA7,0x35,0x81,
        0x84,0x00,0x00,0x00,0x00,0x49,0x45,0x4E,
        0x44,0xAE,0x42,0x60,0x82,
    ];
}
