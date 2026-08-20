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
        var chosen = router.ResolveOptimalModelForTask("beat_pacing", userConfiguredModel: "claude-sonnet-5");

        Assert.Equal("claude-sonnet-5", chosen);
    }

    [Fact]
    public void ResolveOptimalModelForTask_ReturnsRankedCandidateWhenKeysPresent()
    {
        var had = Environment.GetEnvironmentVariable("XAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", "test-key");
            var router = new SmartClassifierModelRouter(NullLogger<SmartClassifierModelRouter>.Instance);
            var chosen = router.ResolveOptimalModelForTask("beat_pacing", userConfiguredModel: "auto");

            Assert.Equal("grok-4.6", chosen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XAI_API_KEY", had);
        }
    }

    [Fact]
    public void ResolveOptimalModelForTask_AutoWithoutKeys_ThrowsRatherThanInventingAFallback()
    {
        string[] keys = ["XAI_API_KEY", "GEMINI_API_KEY", "ANTHROPIC_API_KEY"];
        var prev = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var k in keys)
                Environment.SetEnvironmentVariable(k, null);

            var router = new SmartClassifierModelRouter(NullLogger<SmartClassifierModelRouter>.Instance);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                router.ResolveOptimalModelForTask("beat_pacing", userConfiguredModel: "auto"));

            Assert.Contains("no ranked catalog model has an available API key", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var (k, v) in prev)
                Environment.SetEnvironmentVariable(k, v);
        }
    }
}