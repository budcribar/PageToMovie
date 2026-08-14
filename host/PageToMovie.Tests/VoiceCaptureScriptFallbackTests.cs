using System.Linq;
using PageToMovie.Core.Models;
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
}
