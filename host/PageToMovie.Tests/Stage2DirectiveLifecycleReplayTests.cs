using PageToMovie.Engine.ModelBacked;
using PageToMovie.Engine.ModelExecution;
using Xunit;

namespace PageToMovie.Tests;

public sealed class Stage2DirectiveLifecycleReplayTests
{
    [Theory]
    [InlineData("lighting_token", "cinematic_lighting")]
    [InlineData("negative_tokens", "negative_prompt")]
    public async Task Text_directive_replay_corrects_missing_required_field(string property, string operation)
    {
        var input = new Stage2DirectiveInput("system", "user", OfflineTestModelConfig.Required("chat"), "test");
        var pipeline = new ValidatedModelOperation<Stage2DirectiveInput, TextDirective>(
            new ReplayModelOperation<Stage2DirectiveInput, string>(operation, "v1_product",
                [new("{}", input.Model), new($"{{\"{property}\":\"validated value\"}}", input.Model)]),
            new JsonTextDirectiveParser(property), new TextDirectiveValidator(property),
            new DirectiveTerminalFallback<Stage2DirectiveInput, TextDirective>(), new ModelOperationOptions { CorrectiveMaxAttempts = 1 });

        var result = await pipeline.ExecuteAsync(input);

        Assert.Equal(ModelResultSource.CorrectiveResponse, result.Source);
        Assert.Equal("validated value", result.Value!.Value);
        Assert.Equal(2, result.Attempts.Count);
    }

    [Fact]
    public async Task Color_directive_replay_corrects_partial_response()
    {
        var input = new Stage2DirectiveInput("system", "user", OfflineTestModelConfig.Required("chat"), "test");
        var valid = """{"film_stock":"fine grain","color_palette":"cool blue","grading_prompt":"Cool blue fine grain"}""";
        var pipeline = new ValidatedModelOperation<Stage2DirectiveInput, ColorGradingDirective>(
            new ReplayModelOperation<Stage2DirectiveInput, string>("color_palette_grading", "v1_product",
                [new("{\"film_stock\":\"fine grain\"}", input.Model), new(valid, input.Model)]),
            new JsonColorDirectiveParser(), new ColorDirectiveValidator(),
            new DirectiveTerminalFallback<Stage2DirectiveInput, ColorGradingDirective>(), new ModelOperationOptions { CorrectiveMaxAttempts = 1 });

        var result = await pipeline.ExecuteAsync(input);

        Assert.Equal(ModelResultSource.CorrectiveResponse, result.Source);
        Assert.Equal("cool blue", result.Value!.ColorPalette);
        Assert.Contains(result.Attempts[0].ValidationIssues, i => i.Path == "$.color_palette");
    }
}
