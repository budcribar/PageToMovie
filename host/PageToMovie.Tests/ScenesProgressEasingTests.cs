using PageToMovie.Web.Components.Pages;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// A shot plan reports once per scene and each scene is roughly nine classifier calls, so a purely
/// stepped bar sits frozen at its floor for minutes and reads as "nothing is happening".
/// </summary>
public class ScenesProgressEasingTests
{
    private static Scenes.ScenesGeneration NewGen() => new(new Scenes());

    [Fact]
    public void First_sight_of_a_step_returns_the_stepped_value()
    {
        var gen = NewGen();
        Assert.Equal(5, gen.EaseAcrossCurrentStep(5, index: 0, total: 29));
    }

    [Fact]
    public void Staying_on_one_step_creeps_upward_but_never_past_the_next_step()
    {
        const int total = 29;
        const int index = 10;
        var stepped = (int)System.Math.Round(100.0 * index / total);      // 34
        var next = (int)System.Math.Round(100.0 * (index + 1) / total);   // 38

        var gen = NewGen();
        Assert.Equal(stepped, gen.EaseAcrossCurrentStep(stepped, index, total)); // starts the clock
        Thread.Sleep(80);
        var eased = gen.EaseAcrossCurrentStep(stepped, index, total);

        Assert.InRange(eased, stepped, next);
    }

    /// <summary>
    /// ComputeProgressPercent clamps to a 5% floor, so on a long plan the first couple of steps
    /// are already below it and there is nothing to ease toward. The bar genuinely cannot move
    /// until real progress passes 5% — worth knowing before chasing "it is stuck at the start".
    /// </summary>
    [Fact]
    public void Early_steps_below_the_clamp_floor_have_nothing_to_ease_toward()
    {
        var gen = NewGen();
        // 1/29 rounds to 3%, under the 5% floor the shared helper clamps to.
        Assert.Equal(5, gen.EaseAcrossCurrentStep(5, index: 0, total: 29));
        Thread.Sleep(80);
        Assert.Equal(5, gen.EaseAcrossCurrentStep(5, index: 0, total: 29));
    }

    [Fact]
    public void Unknown_total_is_left_to_the_shared_soft_crawl()
    {
        var gen = NewGen();
        Assert.Equal(12, gen.EaseAcrossCurrentStep(12, index: 0, total: 0));
    }

    /// <summary>A step whose next value is no higher has nothing to ease toward.</summary>
    [Fact]
    public void Last_step_does_not_ease()
    {
        var gen = NewGen();
        Assert.Equal(92, gen.EaseAcrossCurrentStep(92, index: 29, total: 29));
        Assert.Equal(92, gen.EaseAcrossCurrentStep(92, index: 29, total: 29));
    }
}
