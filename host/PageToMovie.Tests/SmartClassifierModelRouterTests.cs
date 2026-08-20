using PageToMovie.Core.Models;
using PageToMovie.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PageToMovie.Tests;

// Reads SupportedModelCatalog.TaskRankings indirectly (via task-name ranking lookups) — must not
// run concurrently with tests that swap in a reduced synthetic catalog. See CatalogSerialCollection.
[Collection("catalog-serial")]
public class SmartClassifierModelRouterTests
{
    [Fact]
    public void ResolveOptimalModelForTask_HonorsUserExplicitOverride()
    {
        var router = new SmartClassifierModelRouter(NullLogger<SmartClassifierModelRouter>.Instance);
        const string overrideId = "operator-picked-model";
        var taskKey = FirstRankedTaskKey();
        var chosen = router.ResolveOptimalModelForTask(taskKey, userConfiguredModel: overrideId);

        Assert.Equal(overrideId, chosen);
    }

    [Fact]
    public void ResolveOptimalModelForTask_AutoUsesRankedModelWithKey_OrThrowsWhenNone()
    {
        var router = new SmartClassifierModelRouter(NullLogger<SmartClassifierModelRouter>.Instance);
        var taskKey = FirstRankedTaskKey();
        Assert.True(SupportedModelCatalog.TaskRankings.TryGetValue(taskKey, out var ranked));
        Assert.NotEmpty(ranked);

        var anyKeyed = ranked
            .Select(id => SupportedModelCatalog.Find(id))
            .Any(entry => entry is { Enabled: true } && HasRequiredKeys(entry));

        if (anyKeyed)
        {
            var chosen = router.ResolveOptimalModelForTask(taskKey, userConfiguredModel: "auto");
            Assert.Contains(chosen, ranked);
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(
            () => router.ResolveOptimalModelForTask(taskKey, userConfiguredModel: "auto"));
        Assert.Contains("no ranked catalog model has an available API key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstRankedTaskKey()
    {
        Assert.True(
            SupportedModelCatalog.TaskRankings.Count > 0,
            "models_catalog.json must define taskRankings for classifier routing.");
        return SupportedModelCatalog.TaskRankings.ContainsKey("beat_pacing")
            ? "beat_pacing"
            : SupportedModelCatalog.TaskRankings.Keys.First();
    }

    private static bool HasRequiredKeys(SupportedModelEntry entry) =>
        entry.RequiredEnvKeys.All(reqKey =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(reqKey)));
}
