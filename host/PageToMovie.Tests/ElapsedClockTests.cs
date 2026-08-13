using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

public sealed class ElapsedClockTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(9, "9s")]
    [InlineData(61, "1m 01s")]
    [InlineData(125, "2m 05s")]
    [InlineData(3661, "1h 1m")]
    public void Format_uses_short_labels(int seconds, string expected)
        => Assert.Equal(expected, ElapsedClock.Format(TimeSpan.FromSeconds(seconds)));
}

public sealed class Stage1OperatorProgressTests
{
    [Theory]
    [InlineData("Adapting chunk 3/20…", "Writing screenplay — part 3 of 20")]
    [InlineData("Still working — Chunk 12/20 (4m 10s)", "Writing screenplay — part 12 of 20")]
    [InlineData("Checking 58 possible name-spelling drift group(s)…", "Smoothing names…")]
    [InlineData("Checking 12 possible duplicate location name(s)…", "Smoothing place names…")]
    [InlineData("Checking narration continuity (split V.O. / verse)…", "Smoothing narration…")]
    [InlineData("Merge pass — unifying full-novel screenplay…", "Combining screenplay parts…")]
    [InlineData("Still writing screenplay… (15m 00s — single pass can take several minutes)", "Writing the full screenplay…")]
    public void Operator_message_is_plain_language(string engine, string expected)
    {
        var snap = new JobSnapshot
        {
            Status = "running",
            Kind = "stage1",
            Message = engine,
        };
        Assert.Equal(expected, AdaptationPageBase.AdaptationJobs.OperatorJobRunningMessage(snap));
    }

    [Fact]
    public void Long_adapt_hint_only_while_running_stage1()
    {
        var snap = new JobSnapshot { Status = "running", Kind = "stage1" };
        Assert.Contains("20–60 minutes", AdaptationPageBase.AdaptationStepUi.LongAdaptHint(snap, running: true));
        Assert.Null(AdaptationPageBase.AdaptationStepUi.LongAdaptHint(snap, running: false));
        Assert.Null(AdaptationPageBase.AdaptationStepUi.LongAdaptHint(
            new JobSnapshot { Status = "running", Kind = "stage2" }, running: true));
    }
}
