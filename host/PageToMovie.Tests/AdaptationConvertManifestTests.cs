using PageToMovie.Adaptation;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Core.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

public sealed class AdaptationConvertManifestTests
{
    private const string MaryBook = """
        Mary had a little lamb,
        Its fleece was white as snow.
        And everywhere that Mary went,
        The lamb was sure to go.
        """;

    [Fact]
    public async Task ConvertAsync_manifest_records_unlimited_and_adaptation_version()
    {
        var chat = new RecordingChatClient(_ => """
            Title: Mary

            FADE IN:

            EXT. COUNTRY LANE - DAY

            MARY walks with her lamb along a sunlit path.

            MARY
            Come along, little one.

            > FADE OUT.

            THE END

            ---VISION_META---
            {"visual_medium":"illustrated_picture_book","render_style_lock":"STYLE LOCK: soft watercolor","notes":"rhyme"}
            ---END_VISION_META---
            """);

        var result = await AdaptationService.ConvertAsync(
            new AdaptationRequest
            {
                BookText = MaryBook,
                Title = "Mary Had a Little Lamb",
                Author = "Nursery",
                ModelId = "grok-4.5",
                Temperature = 0.2,
                // no TargetRuntimeMinutes → unlimited
            },
            chat);

        Assert.NotNull(result.ConvertManifest);
        var m = result.ConvertManifest!;
        Assert.Equal("unlimited", m.RuntimeMode);
        Assert.Null(m.TargetRuntimeMinutes);
        Assert.False(string.IsNullOrWhiteSpace(m.AdaptationVersion));
        Assert.Equal(AdaptationVersion.Current, m.AdaptationVersion);
        Assert.Equal("grok-4.5", m.ModelId);
        Assert.False(string.IsNullOrWhiteSpace(m.PromptContentSha256));
        Assert.True(m.PromptContentSha256.Length >= 32);
        Assert.False(m.UsedHeuristicFallback);
        Assert.True(m.FountainChars > 0);
        Assert.False(string.IsNullOrWhiteSpace(m.CompletedUtc));
    }

    [Fact]
    public async Task ConvertAsync_manifest_records_explicit_target()
    {
        var chat = new RecordingChatClient(_ => """
            Title: Mary

            FADE IN:

            EXT. LANE - DAY

            MARY and the lamb.

            > FADE OUT.

            THE END
            """);

        var result = await AdaptationService.ConvertAsync(
            new AdaptationRequest
            {
                BookText = MaryBook,
                Title = "Mary",
                ModelId = "grok-4.5",
                TargetRuntimeMinutes = 5,
            },
            chat);

        Assert.NotNull(result.ConvertManifest);
        Assert.Equal(5, result.ConvertManifest!.TargetRuntimeMinutes);
        Assert.NotEqual("unlimited", result.ConvertManifest.RuntimeMode);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly Func<int, string> _fn;
        private int _n;
        public RecordingChatClient(Func<int, string> fn) => _fn = fn;
        public bool IsConfigured => true;
        public Task<string> CompleteAsync(
            string systemPrompt, string userPrompt, string model = "grok-4.5",
            double temperature = 0.2, CancellationToken ct = default,
            string? mode = null, string? reasoningEffort = null)
        {
            _n++;
            return Task.FromResult(_fn(_n));
        }
    }
}
