using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A clip's foley used to be requested twice in one prompt: <c>&lt;Sound&gt;</c>, baked into
/// <c>visual_prompt</c> by Stage 2, and <c>&lt;Foley&gt;</c>/<c>&lt;Score&gt;</c> inside the AUDIO
/// block that <see cref="ClipVideoPromptBuilder"/> builds from <c>audio_payload</c> at generation
/// time. Both describe the same sound, from the same screenplay cue.
/// </summary>
/// <remarks>
/// Unlike the matching <c>&lt;Speech&gt;</c> duplication, this one was audible. Two requests for
/// laughing and bleating against ONE request for the narration, and the foley won the opening
/// moment — the narrator started the line a word in. Measured directly in the provider playground:
/// an extend that spoke its line correctly dropped the line's first word the moment
/// <c>&lt;Sound&gt;</c> was added, and kept it with <c>&lt;Foley&gt;</c> alone.
///
/// <para>Nothing is lost by removing it: the screenplay's own <c>(SOUND: …)</c> cue is parsed at
/// Stage 1 into the beat's ambient/sfx, which is what <c>audio_payload</c> carries and what the
/// AUDIO block renders.</para>
/// </remarks>
public sealed class SoundDuplicationTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;

    public SoundDuplicationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-sound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* */ }
    }

    [Fact]
    public async Task Stage2_no_longer_bakes_a_sound_block_into_the_visual_prompt()
    {
        const string projectId = "Demo";
        await OfflineTestModelConfig.ApplyAsync(_store, projectId);
        ScreenplayService.SaveDraft(_store, projectId, """
            Title: Sound Check

            INT. SCHOOLROOM - DAY

            THE CHILDREN twist in their seats and point.

            (SOUND: children laughing and playing, a lamb bleats)

            NARRATOR (V.O.)
            It made the children laugh and play to see a lamb at school.
            """);
        var sign = ScreenplayService.SignOff(_store, projectId);
        Assert.True(sign.Ok, sign.Error);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync(projectId, resolution: "480p", scenes: "all");
        Assert.True(result.Ok);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result.OutPath!));
        var prompts = doc.RootElement.GetProperty("scenes").EnumerateArray()
            .SelectMany(s => s.GetProperty("veo_clips").EnumerateArray())
            .Select(c => c.TryGetProperty("visual_prompt", out var vp) ? vp.GetString() ?? "" : "")
            .ToList();

        Assert.NotEmpty(prompts);
        Assert.All(prompts, p =>
            Assert.DoesNotContain($"<{PromptFieldTags.Sound}>", p, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement ClipWith(string visualPrompt) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            clip_number = 2,
            visual_prompt = visualPrompt,
            characters_on_screen = new[] { "Character_Narrator" },
            audio_payload = new
            {
                speaker = "Character_Narrator",
                delivery = "voiceover_internal",
                dialogue = "It made the children laugh and play to see a lamb at school.",
                sfx = "clap, lamb bleat, laughter",
            },
        })).RootElement.Clone();

    private static ClipVideoPromptBuilder.PromptBuildResult Build(JsonElement clip) =>
        ClipVideoPromptBuilder.Build(
            clip,
            Path.GetTempPath(),
            new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character_Narrator"] = new()
                {
                    Key = "Character_Narrator", DisplayName = "Narrator",
                    Description = "warm voice", VoiceOnly = true,
                },
            });

    /// <summary>A plan built before the change still carries the block; it must not reach the model.</summary>
    [Fact]
    public void A_legacy_sound_block_is_stripped_on_the_way_to_the_model()
    {
        var built = Build(ClipWith(
            $"<{PromptFieldTags.Action}>THE CHILDREN twist and point.</{PromptFieldTags.Action}> " +
            $"<{PromptFieldTags.Sound}>children laughing and playing, a lamb bleats</{PromptFieldTags.Sound}> " +
            $"<{PromptFieldTags.Lighting}>Soft warm daylight.</{PromptFieldTags.Lighting}>"));

        Assert.DoesNotContain($"<{PromptFieldTags.Sound}>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        // The clip's own action and look are untouched — only the duplicated request is gone.
        Assert.Contains("twist and point", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Soft warm daylight", built.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sound itself is not lost — it reaches the model once, through the audio payload, which is
    /// the copy that carries the layer structure the model needs.
    /// </summary>
    [Fact]
    public void The_foley_still_reaches_the_model_exactly_once()
    {
        var built = Build(ClipWith(
            $"<{PromptFieldTags.Action}>THE CHILDREN twist and point.</{PromptFieldTags.Action}> " +
            $"<{PromptFieldTags.Sound}>children laughing and playing, a lamb bleats</{PromptFieldTags.Sound}>"));

        Assert.Contains("<Foley>", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lamb bleat", built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOf(built.Prompt, "lamb bleat"));
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }
}
