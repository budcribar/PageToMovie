using PageToMovie.Adaptation;
using PageToMovie.Core.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Fountain → Fountain re-skin via <see cref="AdaptationService.ReskinAsync"/> with a fake IChatClient.
/// The automated pass must preserve scene structure; on drift the original is kept.
/// </summary>
public sealed class AdaptationReskinTests
{
    [Fact]
    public async Task Reskin_preserving_scene_count_is_applied()
    {
        var input = Fountain(scenes: 3, tag: "plain");
        var reskinned = Fountain(scenes: 3, tag: "watercolor picture-book");
        var chat = new FakeChat(_ => reskinned);

        var result = await AdaptationService.ReskinAsync(
            input, "illustrated_picture_book", new ChatCall(chat, "grok-4.5"));

        Assert.True(result.Ok);
        Assert.True(result.StructurePreserved);
        Assert.Equal(3, result.SceneCountBefore);
        Assert.Equal(3, result.SceneCountAfter);
        Assert.Contains("watercolor picture-book", result.Fountain, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Reskin_changing_scene_count_keeps_original()
    {
        var input = Fountain(scenes: 3, tag: "plain");
        var dropped = Fountain(scenes: 2, tag: "photoreal"); // model wrongly cut a scene
        var chat = new FakeChat(_ => dropped);

        var result = await AdaptationService.ReskinAsync(
            input, "photoreal_live_action", new ChatCall(chat));

        Assert.False(result.Ok);
        Assert.False(result.StructurePreserved);
        Assert.Equal(3, result.SceneCountBefore);
        Assert.Equal(2, result.SceneCountAfter);
        Assert.Equal(input, result.Fountain); // original preserved
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
    }

    [Fact]
    public async Task Reskin_strips_accidental_code_fence()
    {
        var input = Fountain(scenes: 2, tag: "plain");
        var fenced = "```fountain\n" + Fountain(scenes: 2, tag: "stylized 3D") + "\n```";
        var chat = new FakeChat(_ => fenced);

        var result = await AdaptationService.ReskinAsync(input, "stylized_3d_animated", new ChatCall(chat));

        Assert.True(result.Ok);
        Assert.DoesNotContain("```", result.Fountain);
        Assert.Contains("stylized 3D", result.Fountain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reskin_with_unconfigured_chat_keeps_original()
    {
        var input = Fountain(scenes: 2, tag: "plain");
        var chat = new FakeChat(_ => "unused") { Configured = false };

        var result = await AdaptationService.ReskinAsync(input, "photoreal_live_action", new ChatCall(chat));

        Assert.False(result.Ok);
        Assert.Equal(input, result.Fountain);
        Assert.Equal(0, chat.Calls);
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
            sb.AppendLine(new string('w', 50) + $" — {tag} look, scene {i} action and description.");
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
        private readonly Func<int, string> _responseForCall;
        public FakeChat(Func<int, string> responseForCall) => _responseForCall = responseForCall;

        public int Calls { get; private set; }
        public bool Configured { get; init; } = true;
        public bool IsConfigured => Configured;

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
