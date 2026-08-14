using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Conversion;
using PageToMovie.Core.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Fountain → Fountain enrichment via <see cref="AdaptationService.EmbellishAsync"/> with a fake IChatClient.
/// The automated pass must preserve scene structure; on drift the original is kept.
/// </summary>
public sealed class AdaptationEmbellishTests
{
    [Fact]
    public async Task Embellish_preserving_scene_count_is_applied()
    {
        var input = Fountain(scenes: 3, tag: "sparse");
        var enriched = Fountain(scenes: 3, tag: "lantern-lit, breath fogging the cold air");
        var chat = new FakeChat(_ => enriched);

        var result = await AdaptationService.EmbellishAsync(
            input, "illustrated_picture_book", new ChatCall(chat, "grok-4.5"), bookText: "Once upon a cold night...");

        Assert.True(result.Ok);
        Assert.True(result.StructurePreserved);
        Assert.Equal(3, result.SceneCountBefore);
        Assert.Equal(3, result.SceneCountAfter);
        Assert.Contains("lantern-lit", result.Fountain, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Embellish_changing_scene_count_keeps_original()
    {
        var input = Fountain(scenes: 3, tag: "sparse");
        var added = Fountain(scenes: 4, tag: "over-enriched"); // model wrongly invented a scene
        var chat = new FakeChat(_ => added);

        var result = await AdaptationService.EmbellishAsync(input, "photoreal_live_action", new ChatCall(chat));

        Assert.False(result.Ok);
        Assert.False(result.StructurePreserved);
        Assert.Equal(3, result.SceneCountBefore);
        Assert.Equal(4, result.SceneCountAfter);
        Assert.Equal(input, result.Fountain);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
    }

    [Fact]
    public async Task Embellish_includes_book_text_in_prompt_when_provided()
    {
        var input = Fountain(scenes: 2, tag: "sparse");
        string? seenUser = null;
        var chat = new FakeChat(u => { seenUser = u; return Fountain(scenes: 2, tag: "enriched"); });

        await AdaptationService.EmbellishAsync(
            input, "auto", new ChatCall(chat), bookText: "MAGIC_BOOK_MARKER text of the source");

        Assert.NotNull(seenUser);
        Assert.Contains("MAGIC_BOOK_MARKER", seenUser!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Embellish_long_script_goes_per_scene_and_survives_whole_script_collapse()
    {
        var input = Fountain(scenes: 8, tag: "sparse");
        var chat = new FakeChat(u =>
        {
            var n = BookToFountainConverter.CountSceneHeadings(u);
            // Whole-script collapse (what Odyssey did: 28 → 15). Per-scene user has 1 heading.
            if (n >= 8) return Fountain(scenes: 3, tag: "collapsed");
            return Fountain(scenes: Math.Max(1, n), tag: "lantern-lit");
        });

        var result = await AdaptationService.EmbellishAsync(input, "auto", new ChatCall(chat));

        Assert.True(result.Ok);
        Assert.True(result.StructurePreserved);
        Assert.Equal(8, result.SceneCountBefore);
        Assert.Equal(8, result.SceneCountAfter);
        Assert.Equal(8, chat.Calls);
        Assert.Contains("lantern-lit", result.Fountain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("collapsed", result.Fountain, StringComparison.OrdinalIgnoreCase);
    }

    private static string Fountain(int scenes, string tag)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Test");
        sb.AppendLine("Author: Unit");
        sb.AppendLine();
        for (var i = 1; i <= scenes; i++)
        {
            sb.AppendLine(i % 2 == 0 ? $"EXT. PLACE {i} - DAY" : $"INT. ROOM {i} - NIGHT");
            sb.AppendLine();
            sb.AppendLine(new string('w', 50) + $" — {tag}, scene {i} action and description.");
            sb.AppendLine();
            sb.AppendLine("MARY");
            sb.AppendLine($"Come along, little lamb — line {i} with enough dialogue for the gate.");
            sb.AppendLine();
        }
        sb.AppendLine("FADE OUT.");
        sb.AppendLine();
        sb.AppendLine("THE END");
        return sb.ToString();
    }

    private sealed class FakeChat : IChatClient
    {
        private readonly Func<string, string> _responseForUser;
        public FakeChat(Func<string, string> responseForUser) => _responseForUser = responseForUser;

        public int Calls { get; private set; }
        public bool IsConfigured => true;

        public Task<string> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            string model = "grok-4.5",
            double temperature = 0.2,
            CancellationToken ct = default,
            string? mode = null,
            string? reasoningEffort = null)
        {
            Calls++;
            return Task.FromResult(_responseForUser(userPrompt));
        }
    }
}
