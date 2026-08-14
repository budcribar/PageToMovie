using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Stage‑1 via <see cref="AdaptationService.ConvertAsync"/> with a recorded IChatClient — no live API.
/// </summary>
public sealed class AdaptationFakeChatTests
{
    private const string MaryBook = """
        Mary had a little lamb,
        Its fleece was white as snow.
        And everywhere that Mary went,
        The lamb was sure to go.
        """;

    [Fact]
    public async Task ConvertAsync_with_recorded_fountain_returns_good_screenplay()
    {
        var chat = new RecordingChatClient(_ => GoodFountain(scenes: 3, withEnding: true));

        var result = await AdaptationService.ConvertAsync(
            new AdaptationRequest
            {
                BookText = MaryBook,
                Title = "Mary Had a Little Lamb",
                Author = "Nursery",
                TargetRuntimeMinutes = 3,
                ModelId = "grok-4.5",
                Temperature = 0.2,
            },
            new ChatCall(chat));

        Assert.False(string.IsNullOrWhiteSpace(result.Fountain));
        Assert.Contains("FADE", result.Fountain, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Runtime.TargetMinutes is >= 1 and <= 30);
        Assert.False(result.UsedHeuristicFallback);
        Assert.True(chat.Calls >= 1);
    }

    [Fact]
    public async Task ConvertAsync_strips_vision_meta_trailer_into_result()
    {
        var body = GoodFountain(scenes: 2, withEnding: true) + """

---VISION_META---
{"schema_version":"vision_meta.v1","visual_medium":"illustrated_picture_book","render_style_lock":"soft watercolor","performance_lock":"characters look at each other"}
---END_VISION_META---
""";
        var chat = new RecordingChatClient(_ => body);
        var result = await AdaptationService.ConvertAsync(
            new AdaptationRequest
            {
                BookText = MaryBook,
                Title = "Mary",
                ModelId = "grok-4.5",
            },
            new ChatCall(chat));

        Assert.DoesNotContain("VISION_META", result.Fountain, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.VisionMeta);
        Assert.False(string.IsNullOrWhiteSpace(result.VisionMeta!.RenderStyleLock));
    }

    [Fact]
    public async Task ConvertAsync_garbage_response_may_use_heuristic_fallback()
    {
        var chat = new RecordingChatClient(_ => "not a screenplay at all");
        var result = await AdaptationService.ConvertAsync(
            new AdaptationRequest
            {
                BookText = MaryBook,
                Title = "Mary",
                ModelId = "grok-4.5",
            },
            new ChatCall(chat));

        Assert.False(string.IsNullOrWhiteSpace(result.Fountain));
        // Converter should still produce something usable (heuristic or repaired).
        Assert.Contains("FADE", result.Fountain, StringComparison.OrdinalIgnoreCase);
    }

    private static string GoodFountain(int scenes, bool withEnding, int padBody = 60)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Test");
        sb.AppendLine("Author: Unit");
        sb.AppendLine();
        for (var i = 1; i <= scenes; i++)
        {
            sb.AppendLine(i % 2 == 0 ? $"EXT. PLACE {i} - DAY" : $"INT. ROOM {i} - NIGHT");
            sb.AppendLine();
            sb.AppendLine("NARRATOR");
            sb.AppendLine(new string('w', Math.Max(40, padBody)) + $" scene {i} action and description.");
            sb.AppendLine();
            sb.AppendLine("MARY");
            sb.AppendLine($"Come along, little lamb — line {i} with enough dialogue for the gate.");
            sb.AppendLine();
        }

        if (withEnding)
        {
            sb.AppendLine("FADE OUT.");
            sb.AppendLine();
            sb.AppendLine("THE END");
        }

        return sb.ToString();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly Func<int, string> _responseForCall;

        public RecordingChatClient(Func<int, string> responseForCall) =>
            _responseForCall = responseForCall;

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
            return Task.FromResult(_responseForCall(Calls));
        }
    }
}
