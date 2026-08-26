using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A clip's spoken line reached the video model twice: once as the AUDIO block that
/// <see cref="ClipVideoPromptBuilder"/> builds from <c>audio_payload</c> at generation time — the
/// copy that actually drives what is said — and again as a <c>&lt;Speech&gt;</c> block Stage 2
/// baked into <c>visual_prompt</c>. That cost prompt budget on prompts already being compressed,
/// and gave the clip editor two boxes for one fact, only one of which changed anything.
///
/// <para>Stage 2 now emits no <c>&lt;Speech&gt;</c>. Plans built before that still carry one, so
/// the generation path strips it rather than leaving old projects paying for the duplicate.</para>
/// </summary>
public class SpeechDuplicationTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectStore _store;

    public SpeechDuplicationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fs-speech-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "projects", "Demo"));
        _store = new ProjectStore(Options.Create(new PageToMovieOptions { WorkspaceRoot = _root }));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* ignore */ }
        GC.SuppressFinalize(this);
    }

    private const string SpokenLine = "It made the children laugh and play.";

    private static readonly string SpeechTagOpen = $"<{PromptFieldTags.Speech}>";

    /// <summary>
    /// A planned clip carries its line in <c>audio_payload</c> and nowhere else. The delivery is
    /// still planned — how the line is spoken is a plan decision; the words are not the plan's to
    /// repeat.
    /// </summary>
    [Fact]
    public async Task Stage2_plans_the_line_into_audio_payload_only()
    {
        const string fountain = """
            Title: Speech Duplication

            INT. SCHOOLROOM - DAY

            MARY stands by the door.

            MARY
            It made the children laugh and play.

            NARRATOR (V.O.)
            And so the lamb went home again.
            """;
        await OfflineTestModelConfig.ApplyAsync(_store, "Demo");
        ScreenplayService.SaveDraft(_store, "Demo", fountain);
        Assert.True(ScreenplayService.SignOff(_store, "Demo").Ok);

        var planner = new Stage2PlannerService(_store, NullLogger<Stage2PlannerService>.Instance);
        var result = await planner.PlanAsync("Demo", resolution: "480p", scenes: "all");
        Assert.True(result.Ok);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(result.OutPath!));
        var spoken = 0;
        foreach (var scene in doc.RootElement.GetProperty("scenes").EnumerateArray())
        {
            foreach (var clip in scene.GetProperty("veo_clips").EnumerateArray())
            {
                var visual = clip.GetProperty("visual_prompt").GetString() ?? "";
                Assert.DoesNotContain(SpeechTagOpen, visual, StringComparison.OrdinalIgnoreCase);

                if (!clip.TryGetProperty("audio_payload", out var audio)
                    || !audio.TryGetProperty("dialogue", out var dlgEl)
                    || dlgEl.GetString() is not { Length: > 0 } dialogue)
                    continue;
                spoken++;
                // The words live in audio_payload. The visual prompt does not echo them, nor the
                // "lip-syncs" / "OFF-CAMERA VOICEOVER" framing that used to introduce them.
                Assert.DoesNotContain(dialogue.TrimEnd('.'), visual, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("lip-syncs", visual, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("OFF-CAMERA VOICEOVER", visual, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.True(spoken >= 2, $"expected both spoken beats to be planned, saw {spoken}");
    }

    /// <summary>A current plan: the line reaches the model exactly once, in the AUDIO block.</summary>
    [Fact]
    public void Built_prompt_says_the_line_once()
    {
        var built = Build(TaggedVisualPrompt(withLegacySpeech: false));

        Assert.Equal(1, Occurrences(built.Prompt, SpokenLine));
        Assert.Contains(SpokenLine, built.AudioBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan that predates the change still generates sanely: its baked-in
    /// <c>&lt;Speech&gt;</c> is dropped on the way to the model, so the line is still said once,
    /// and the rest of the prompt survives intact. Existing projects need no migration.
    /// </summary>
    [Fact]
    public void Legacy_plan_with_a_baked_in_speech_block_still_says_the_line_once()
    {
        var built = Build(TaggedVisualPrompt(withLegacySpeech: true));

        Assert.Equal(1, Occurrences(built.Prompt, SpokenLine));
        Assert.DoesNotContain(SpeechTagOpen, built.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFF-CAMERA VOICEOVER Character_Mary says", built.Prompt, StringComparison.Ordinal);
        // Everything around the dropped block is still there — this is a strip, not a truncation.
        Assert.Contains("MARY stands by the door", built.Prompt, StringComparison.Ordinal);
        Assert.Contains("INT. SCHOOLROOM - DAY", built.Prompt, StringComparison.Ordinal);
        // The fixture's legacy <Sound> block goes too, for the same reason and by the same strip:
        // audio_payload already asks for that sound once. See SoundDuplicationTests.
        Assert.DoesNotContain("wooden desks scrape", built.Prompt, StringComparison.Ordinal);
    }

    /// <summary>The strip is the tag's, not the words' — an unrelated quote in the action stays.</summary>
    [Fact]
    public void Stripping_the_legacy_block_leaves_the_rest_of_the_action_alone()
    {
        var clean = ClipVideoPromptBuilder.SanitizeActionText(
            $"<{PromptFieldTags.Action}>Character_Mary reads the slate aloud</{PromptFieldTags.Action}> " +
            $"{SpeechTagOpen}OFF-CAMERA VOICEOVER Character_Mary says \"{SpokenLine}\"</{PromptFieldTags.Speech}> " +
            $"<{PromptFieldTags.Lighting}>Soft warm daylight</{PromptFieldTags.Lighting}>",
            new[] { "Character_Mary" });

        Assert.DoesNotContain(SpeechTagOpen, clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SpokenLine, clean, StringComparison.Ordinal);
        Assert.Contains("reads the slate aloud", clean, StringComparison.Ordinal);
        Assert.Contains("<Lighting>Soft warm daylight</Lighting>", clean, StringComparison.Ordinal);
    }

    private static string TaggedVisualPrompt(bool withLegacySpeech)
    {
        var speech = withLegacySpeech
            ? $"{SpeechTagOpen}OFF-CAMERA VOICEOVER Character_Mary says \"{SpokenLine}\"</{PromptFieldTags.Speech}> "
            : "";
        return $"<{PromptFieldTags.Setting}>INT. SCHOOLROOM - DAY</{PromptFieldTags.Setting}> " +
               $"<{PromptFieldTags.Action}>MARY stands by the door, Character_Mary waits</{PromptFieldTags.Action}> " +
               $"<{PromptFieldTags.Sound}>wooden desks scrape</{PromptFieldTags.Sound}> " +
               speech +
               $"<{PromptFieldTags.Lighting}>Soft warm daylight</{PromptFieldTags.Lighting}>";
    }

    private static ClipVideoPromptBuilder.PromptBuildResult Build(string visualPrompt)
    {
        var clip = JsonSerializer.SerializeToDocument(new
        {
            clip_number = 1,
            visual_prompt = visualPrompt,
            characters_on_screen = new[] { "Character_Mary" },
            primary_subject = "Character_Mary",
            audio_payload = new
            {
                speaker = "Character_Mary",
                dialogue = SpokenLine,
                delivery = "voiceover_internal",
            },
        }).RootElement;

        return ClipVideoPromptBuilder.Build(
            clip, "proj", new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>());
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
