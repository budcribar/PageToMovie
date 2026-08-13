using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PageToMovie.Engine;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;
using PageToMovie.Engine.ModelExecution;
using Xunit;

namespace PageToMovie.Tests;

public sealed class MaryEndToEndLifecycleReplayTests
{
    [Fact]
    public async Task Complete_recorded_pipeline_is_stable_and_second_run_makes_zero_model_calls()
    {
        var fixture = LoadFixture();
        var cache = new Dictionary<string, ReplayArtifact>(StringComparer.Ordinal);
        var first = await ReplayAsync(fixture, Defaults(), cache);
        var second = await ReplayAsync(fixture, Defaults(), cache);

        Assert.True(first.ModelCalls > 0);
        Assert.Equal(0, second.ModelCalls);
        Assert.Equal(first.BookHash, second.BookHash);
        Assert.Equal(first.AggregateManifestHash, second.AggregateManifestHash);
        Assert.Equal(first.Operations.Select(x => x.DerivationHash), second.Operations.Select(x => x.DerivationHash));
        Assert.Equal(first.Operations.Select(x => x.OutputHash), second.Operations.Select(x => x.OutputHash));
        Assert.All(second.Operations, operation => Assert.True(operation.CacheHit));
        Assert.Contains(first.Operations, x => x.Name == "stage1" && x.Attempts == 2);
        Assert.Contains(first.Operations, x => x.Name == "stage2" && x.Attempts == 2);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("model")]
    [InlineData("temperature")]
    [InlineData("schema")]
    public async Task Derivation_change_invalidates_only_changed_operation_and_downstream(string dimension)
    {
        var fixture = LoadFixture();
        var cache = new Dictionary<string, ReplayArtifact>(StringComparer.Ordinal);
        var baseline = await ReplayAsync(fixture, Defaults(), cache);
        var changed = Defaults();
        changed["stage2"] = dimension switch
        {
            "prompt" => changed["stage2"] with { PromptVersion = "stage2-vNext" },
            "model" => changed["stage2"] with { Model = "catalog-model-next" },
            "temperature" => changed["stage2"] with { Temperature = 0.15 },
            _ => changed["stage2"] with { SchemaVersion = "2" },
        };

        var replay = await ReplayAsync(fixture, changed, cache);
        Assert.Equal(0, replay.Operations.Single(x => x.Name == "stage1").Attempts);
        Assert.Equal(0, replay.Operations.Single(x => x.Name == "cast").Attempts);
        Assert.False(replay.Operations.Single(x => x.Name == "stage2").CacheHit);
        // The changed Stage 2 derivation replayed to the same output hash, so downstream
        // content-addressed observations and judgments remain valid.
        Assert.True(replay.Operations.Single(x => x.Name == "multimodal_observation").CacheHit);
        Assert.True(replay.Operations.Single(x => x.Name == "multimodal_judgment").CacheHit);
        Assert.Equal(
            baseline.Operations.Single(x => x.Name == "stage1").OutputHash,
            replay.Operations.Single(x => x.Name == "stage1").OutputHash);
    }

    private static async Task<AggregateReplayManifest> ReplayAsync(
        ReplayFixture fixture,
        Dictionary<string, OperationConfig> configs,
        Dictionary<string, ReplayArtifact> cache)
    {
        var bookHash = Hash(fixture.Book);
        var upstream = bookHash;
        var operations = new List<ReplayOperationManifest>();
        foreach (var name in OperationOrder)
        {
            var config = configs[name];
            var derivation = Hash(JsonSerializer.Serialize(new
            {
                name, upstream, config.Model, config.PromptVersion,
                config.Temperature, config.SchemaVersion,
            }));
            if (cache.TryGetValue(derivation, out var cached))
            {
                operations.Add(new(name, derivation, cached.OutputHash, config.Model,
                    config.PromptVersion, config.Temperature, config.SchemaVersion, 0, true));
                upstream = cached.OutputHash;
                continue;
            }

            var responses = fixture.Operations[name];
            var pipeline = new ValidatedModelOperation<string, ReplayValue>(
                new ReplayModelOperation<string, string>(name, config.PromptVersion,
                    responses.Select(response => new ModelResponse<string>(response, config.Model))),
                new ReplayParser(),
                new ReplayValidator(name),
                new RejectFallback(),
                new ModelOperationOptions
                {
                    CorrectiveMaxAttempts = responses.Count - 1,
                    TransportMaxAttempts = 1,
                    BehaviorVersions = new Dictionary<string, string> { ["schema"] = config.SchemaVersion },
                });
            var result = await pipeline.ExecuteAsync(upstream);
            Assert.True(result.Success, result.Error);
            var outputHash = Hash(result.Value!.Json);
            cache[derivation] = new(outputHash);
            operations.Add(new(name, derivation, outputHash, config.Model,
                config.PromptVersion, config.Temperature, config.SchemaVersion,
                result.ModelCalls, false));
            upstream = outputHash;
        }

        var stableEnvelope = JsonSerializer.Serialize(new { schemaVersion = "1", bookHash, operations = operations.Select(x => x with { CacheHit = false, Attempts = 0 }) });
        return new(bookHash, operations, operations.Sum(x => x.Attempts), Hash(stableEnvelope));
    }

    private static IReadOnlyList<ModelValidationIssue> Validate(string name, string json)
    {
        if (name == "stage1")
        {
            var split = PageToMovie.Engine.BookToFountainConverter.SplitVisionMetaTrailer(json);
            var issues = new List<ModelValidationIssue>();
            if (!AdaptationFountain.LooksLikeGoodFountain(split.Fountain)) issues.Add(new("invalid_fountain", "Fountain is invalid."));
            if (split.Vision is null) issues.Add(new("missing_vision_meta", "VISION_META is required."));
            return issues;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return name switch
            {
                "cast" when !root.TryGetProperty("Character_Mary", out _) || !root.TryGetProperty("Character_Lamb", out _)
                    => [new("cast_membership", "Mary and Lamb are required.")],
                "stage2" when !root.TryGetProperty("scene_1", out _) || !root.TryGetProperty("scene_2", out _)
                    => [new("missing_scene", "Both source scenes are required.")],
                "multimodal_observation" when !root.TryGetProperty("frames", out _) || !root.TryGetProperty("unavailable", out _)
                    => [new("missing_observation", "Frames and availability are required.")],
                "multimodal_judgment" when !root.TryGetProperty("verdict", out _) || !root.TryGetProperty("evidence_frame_ids", out _)
                    => [new("missing_judgment", "Verdict and evidence identities are required.")],
                _ => Array.Empty<ModelValidationIssue>(),
            };
        }
        catch (JsonException ex) { return [new("invalid_json", ex.Message)]; }
    }

    private static ReplayFixture LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LifecycleReplay", "mary_lifecycle.json");
        if (!File.Exists(path))
            path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "LifecycleReplay", "mary_lifecycle.json"));
        return JsonSerializer.Deserialize<ReplayFixture>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static Dictionary<string, OperationConfig> Defaults() => OperationOrder.ToDictionary(
        name => name,
        name => new OperationConfig("catalog-model", name + "-v1", 0, "1"),
        StringComparer.Ordinal);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static readonly string[] OperationOrder = ["stage1", "cast", "stage2", "multimodal_observation", "multimodal_judgment"];

    private sealed record ReplayFixture(string Book, Dictionary<string, List<string>> Operations);
    private sealed record ReplayValue(string Json);
    private sealed record ReplayArtifact(string OutputHash);
    private sealed record OperationConfig(string Model, string PromptVersion, double Temperature, string SchemaVersion);
    private sealed record AggregateReplayManifest(string BookHash, IReadOnlyList<ReplayOperationManifest> Operations, int ModelCalls, string AggregateManifestHash);
    private sealed record ReplayOperationManifest(string Name, string DerivationHash, string OutputHash, string Model, string PromptVersion, double Temperature, string SchemaVersion, int Attempts, bool CacheHit);
    private sealed class ReplayParser : IModelResponseParser<string, ReplayValue> { public ModelParseResult<ReplayValue> Parse(string response) => ModelParseResult<ReplayValue>.Success(new(response)); }
    private sealed class ReplayValidator(string name) : IModelResultValidator<ReplayValue> { public IReadOnlyList<ModelValidationIssue> Validate(ReplayValue result) => MaryEndToEndLifecycleReplayTests.Validate(name, result.Json); }
    private sealed class RejectFallback : IDeterministicFallback<string, ReplayValue> { public ReplayValue Create(string input, IReadOnlyList<ModelValidationIssue> unresolvedIssues) => throw new InvalidOperationException("Recorded replay exhausted before validation passed."); }
}
