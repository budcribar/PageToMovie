using PageToMovie.Engine.ModelBacked;
using PageToMovie.Engine.ModelExecution;
using Xunit;

namespace PageToMovie.Tests;

public sealed class CastLifecycleReplayTests
{
    [Fact]
    public async Task Extraction_replay_corrects_invalid_primary_response()
    {
        var input = new CastModelInput("system", "user", OfflineTestModelConfig.Required("chat"));
        var valid = """
            {
              "schema_version":"cast_seeds.v1",
              "character_seed_tokens":{
                "Character_Mary":{
                  "canonical_given_name":"Mary",
                  "description":"A young child in a plain blue dress.",
                  "visual_lock":"Young child, blue dress, brown hair.",
                  "display_name_policy":"ok_anytime",
                  "species_kind":"human",
                  "source_image_pages":[1]
                }
              }
            }
            """;
        var pipeline = new ValidatedModelOperation<CastModelInput, Dictionary<string, object?>>(
            new ReplayModelOperation<CastModelInput, string>(
                "cast_extraction", "1",
                [new ModelResponse<string>("{}", input.Model), new ModelResponse<string>(valid, input.Model)]),
            new CastJsonObjectParser(),
            new CastExtractionValidator(),
            new TerminalCastFallback(),
            new ModelOperationOptions { CorrectiveMaxAttempts = 1 });

        var result = await pipeline.ExecuteAsync(input);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ModelResultSource.CorrectiveResponse, result.Source);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Contains(result.Attempts[0].ValidationIssues, issue => issue.Code == "invalid_schema");
        Assert.Contains("Character_Mary", CastExtractionValidator.FindSeeds(result.Value!)!.Keys);
    }

    [Fact]
    public async Task Extraction_replay_fails_terminally_when_required_model_data_stays_missing()
    {
        var input = new CastModelInput("system", "user", OfflineTestModelConfig.Required("chat"));
        var pipeline = new ValidatedModelOperation<CastModelInput, Dictionary<string, object?>>(
            new ReplayModelOperation<CastModelInput, string>(
                "cast_extraction", "1",
                [new ModelResponse<string>("not json", input.Model), new ModelResponse<string>("{}", input.Model)]),
            new CastJsonObjectParser(),
            new CastExtractionValidator(),
            new TerminalCastFallback(),
            new ModelOperationOptions { CorrectiveMaxAttempts = 1 });

        var result = await pipeline.ExecuteAsync(input);

        Assert.False(result.Success);
        Assert.Equal(ModelResultSource.Failed, result.Source);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Contains(result.ValidationIssues, issue => issue.Code == "fallback_failed");
    }

    [Fact]
    public async Task Literalize_replay_rejects_invented_members_and_keeps_closed_input_cast()
    {
        var original = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Character_Mary"] = new Dictionary<string, object?>
            {
                ["description"] = "A young child.",
                ["visual_lock"] = "Young child in a blue dress.",
            },
        };
        const string invented = """
            {"character_seed_tokens":{
              "Character_Mary":{"description":"A young child.","visual_lock":"Young child in a blue dress."},
              "Character_Shepherd":{"description":"An adult.","visual_lock":"Adult shepherd."}
            }}
            """;
        var input = new CastModelInput("system", "user", OfflineTestModelConfig.Required("chat"));
        var pipeline = new ValidatedModelOperation<CastModelInput, Dictionary<string, object?>>(
            new ReplayModelOperation<CastModelInput, string>(
                "cast_visual_literalize", "1",
                [new ModelResponse<string>(invented, input.Model), new ModelResponse<string>(invented, input.Model)]),
            new CastJsonObjectParser(),
            new LiteralizedCastValidator(original.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)),
            new OriginalCastFallback(original),
            new ModelOperationOptions { CorrectiveMaxAttempts = 1 });

        var result = await pipeline.ExecuteAsync(input);

        Assert.True(result.Success);
        Assert.Equal(ModelResultSource.DeterministicFallback, result.Source);
        Assert.Same(original, result.Value);
        Assert.Single(result.Value!);
        Assert.DoesNotContain("Character_Shepherd", result.Value!.Keys);
        Assert.All(result.Attempts, attempt =>
            Assert.Contains(attempt.ValidationIssues, issue => issue.Code == "invented_character"));
    }
}
