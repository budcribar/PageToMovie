using PageToMovie.Adaptation;
using PageToMovie.Core.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Fountain → Fountain trim via <see cref="AdaptationService.TrimAsync"/> with a fake IChatClient.
/// Trimming may shrink the scene count but must never expand it; the output must be valid Fountain.
/// </summary>
public sealed class AdaptationTrimTests
{
    [Fact]
    public async Task Trim_reducing_scene_count_is_applied()
    {
        var input = Fountain(scenes: 6);
        var trimmed = Fountain(scenes: 3);
        var chat = new FakeChat(_ => trimmed);

        var result = await AdaptationService.TrimAsync(input, targetMinutes: 2, naturalMinutes: 5, new ChatCall(chat));

        Assert.True(result.Ok);
        Assert.Equal(6, result.SceneCountBefore);
        Assert.Equal(3, result.SceneCountAfter);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Trim_condense_only_same_scene_count_is_allowed()
    {
        var input = Fountain(scenes: 3);
        var condensed = Fountain(scenes: 3); // same scenes, condensed within
        var chat = new FakeChat(_ => condensed);

        var result = await AdaptationService.TrimAsync(input, targetMinutes: 3, naturalMinutes: 4, new ChatCall(chat));

        Assert.True(result.Ok);
        Assert.Equal(3, result.SceneCountAfter);
    }

    [Fact]
    public async Task Trim_that_expands_scene_count_keeps_original()
    {
        var input = Fountain(scenes: 3);
        var expanded = Fountain(scenes: 5); // model wrongly added scenes
        var chat = new FakeChat(_ => expanded);

        var result = await AdaptationService.TrimAsync(input, targetMinutes: 1, naturalMinutes: 4, new ChatCall(chat));

        Assert.False(result.Ok);
        Assert.False(result.StructurePreserved);
        Assert.Equal(3, result.SceneCountBefore);
        Assert.Equal(5, result.SceneCountAfter);
        Assert.Equal(input, result.Fountain);
    }

    [Fact]
    public async Task Trim_passes_target_minutes_into_prompt()
    {
        var input = Fountain(scenes: 4);
        string? seenSystem = null;
        var chat = new FakeChat((sys) => { seenSystem = sys; return Fountain(scenes: 2); });

        await AdaptationService.TrimAsync(input, targetMinutes: 7, naturalMinutes: 20, new ChatCall(chat));

        Assert.NotNull(seenSystem);
        Assert.Contains("7", seenSystem!, StringComparison.Ordinal);
        Assert.Contains("20", seenSystem!, StringComparison.Ordinal);
    }

    private static string Fountain(int scenes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Title: Test");
        sb.AppendLine("Author: Unit");
        sb.AppendLine();
        for (var i = 1; i <= scenes; i++)
        {
            sb.AppendLine(i % 2 == 0 ? $"EXT. PLACE {i} - DAY" : $"INT. ROOM {i} - NIGHT");
            sb.AppendLine();
            sb.AppendLine(new string('w', 50) + $" scene {i} action and description.");
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
        private readonly Func<string, string> _responseForSystem;
        public FakeChat(Func<string, string> responseForSystem) => _responseForSystem = responseForSystem;

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
            return Task.FromResult(_responseForSystem(systemPrompt));
        }
    }
}
