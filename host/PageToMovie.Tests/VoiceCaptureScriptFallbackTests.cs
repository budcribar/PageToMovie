using System.Linq;
using PageToMovie.Core.Models;
using PageToMovie.Web.Components.Shared;
using PageToMovie.Web.Services;
using Xunit;

namespace PageToMovie.Tests;

public sealed class VoiceCaptureScriptFallbackTests
{
    [Fact]
    public void EstimatePhraseDurationSec_clamps()
    {
        Assert.Equal(2.0, ClientVoiceCaptureService.EstimatePhraseDurationSec("Hi"));
        Assert.Equal(8.0, ClientVoiceCaptureService.EstimatePhraseDurationSec(string.Join(' ', Enumerable.Repeat("word", 40))));
        Assert.InRange(ClientVoiceCaptureService.EstimatePhraseDurationSec("Oh, Mary loves the lamb, you know."), 2.0, 8.0);
    }

    [Fact]
    public void AddScriptFallback_marks_teacher_lines_confident()
    {
        var phrases = new VoiceCapturePhrases { ProjectId = "p", CharKey = "Character_Teacher" };
        var scenes = new List<EngineApiClient.NarratorSceneLinesDto>
        {
            new()
            {
                Scene = 1,
                HasOtherSpeakers = true,
                Lines = new() { "Oh, Mary loves the lamb, you know.", "" },
            },
        };
        ClientVoiceCaptureService.AddScriptFallbackPhrases(phrases, scenes);
        Assert.Single(phrases.Phrases);
        Assert.True(phrases.Phrases[0].Confident);
        Assert.Equal("Oh, Mary loves the lamb, you know.", phrases.Phrases[0].Text);
        Assert.True(phrases.Phrases[0].DurationSec >= 2);
    }

    [Fact]
    public void AutoStopDelayMs_is_phrase_length_plus_200ms()
    {
        Assert.Equal(3200, VoiceCaptureStep.AutoStopDelayMs(3.0));
        Assert.Equal(VoiceCaptureStep.AutoStopTailMs + 2000, VoiceCaptureStep.AutoStopDelayMs(2.0));
        Assert.InRange(VoiceCaptureStep.AutoStopDelayMs(double.NaN), 400, 30_000);
        Assert.Equal(400, VoiceCaptureStep.AutoStopDelayMs(0));
        Assert.Equal(30_000, VoiceCaptureStep.AutoStopDelayMs(120));
    }

    [Theory]
    [InlineData(0.9, "52,199,89")]
    [InlineData(0.6, "255,204,0")]
    [InlineData(0.2, "255,59,48")]
    public void WordStyleFor_colors_green_yellow_red(double match, string rgb)
    {
        var style = VoiceCaptureStep.WordStyleFor(new[] { match }, 0, recording: false);
        Assert.Contains(rgb, style);
        Assert.Equal("", VoiceCaptureStep.WordStyleFor(new[] { match }, 0, recording: true));
        Assert.Equal("", VoiceCaptureStep.WordStyleFor(null, 0, recording: false));
    }
}
