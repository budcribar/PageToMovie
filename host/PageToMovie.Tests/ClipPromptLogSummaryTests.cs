using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// The generation submit log line printed its mode twice — "[Grok] Submit S02C2 … mode=video-extend
/// mode=video-extend chars=3 …" — because the caller logged the wire mode and PromptLogSummary led
/// with its own copy. The token now has one home: PromptLogDetails is the mode-less tail for callers
/// that log the mode themselves, PromptLogSummary prepends it for callers (prompt file header,
/// telemetry) that do not.
/// </summary>
public class ClipPromptLogSummaryTests
{
    private static ClipVideoPromptBuilder.PromptBuildResult Built(string mode) =>
        new()
        {
            Mode = mode,
            PromptLogDetails = "chars=3 onScreen=2 refs=0 loc=unlocked startFrame=no promptLen=3920",
        };

    [Fact]
    public void PromptLogDetails_carries_no_mode_token()
    {
        Assert.DoesNotContain("mode=", Built(ClipVideoPromptBuilder.ModeVideoExtend).PromptLogDetails);
    }

    [Fact]
    public void PromptLogSummary_states_the_mode_exactly_once()
    {
        var summary = Built(ClipVideoPromptBuilder.ModeVideoExtend).PromptLogSummary;

        Assert.Single(CommonRegex.Matches(summary, "mode="));
        Assert.StartsWith("mode=video-extend chars=3 ", summary);
    }

    [Fact]
    public void PromptLogSummary_is_empty_when_nothing_was_summarised()
    {
        Assert.Equal("", new ClipVideoPromptBuilder.PromptBuildResult().PromptLogSummary);
    }

    [Fact]
    public void WithPrompt_suffix_lands_on_both_details_and_summary()
    {
        var revised = Built("fresh").WithPrompt("rewritten prompt", " · pre-budget 4200→3920");

        Assert.EndsWith(" · pre-budget 4200→3920", revised.PromptLogDetails);
        Assert.EndsWith(" · pre-budget 4200→3920", revised.PromptLogSummary);
        Assert.Single(CommonRegex.Matches(revised.PromptLogSummary, "mode="));
        Assert.Equal("rewritten prompt", revised.Prompt);
    }

    [Fact]
    public void Submit_log_line_states_the_mode_exactly_once()
    {
        var src = EngineSourceLocator.ReadEngineSource("FilmJobService.cs");
        var start = src.IndexOf("$\"  [Grok] Submit S{ctx.Scene:D2}", StringComparison.Ordinal);
        Assert.True(start >= 0, "[Grok] Submit log line not found in FilmJobService.cs");

        var line = src[start..src.IndexOf(");", start, StringComparison.Ordinal)];

        Assert.Single(CommonRegex.Matches(line, "mode="));
        Assert.Contains("PromptLogDetails", line);
    }
}
