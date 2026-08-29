using System.Text.Json;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;

namespace PageToMovie.Engine.ModelBacked;

internal sealed record Stage2DirectiveInput(string System, string User, string Model, string Mode);
internal sealed record TextDirective(string Value);

internal sealed class Stage2DirectiveOperation(IChatClient chat, string name, string version)
    : IModelOperation<Stage2DirectiveInput, string>
{
    public string OperationName => name;
    public string PromptVersion => version;
    public async Task<ModelResponse<string>> ExecuteAsync(Stage2DirectiveInput input, ModelAttemptContext<string> context, CancellationToken ct)
    {
        var user = input.User;
        if (context.Kind == ModelAttemptKind.Correction)
            user += "\n\nCORRECT THE PREVIOUS RESPONSE. Return complete JSON only.\n" +
                    string.Join("\n", context.ValidationIssues.Select(i => $"- {i.Path ?? "$"}: {i.Message}")) +
                    "\nPrevious response:\n" + context.PreviousResponse;
        var raw = await chat.CompleteAsync(input.System, user, input.Model, 0, ct, input.Mode).ConfigureAwait(false);
        return new(raw, input.Model);
    }
}

internal sealed class JsonTextDirectiveParser(string property) : IModelResponseParser<string, TextDirective>
{
    public ModelParseResult<TextDirective> Parse(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClassifierJsonParser.StripFences(response));
            return doc.RootElement.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
                ? ModelParseResult<TextDirective>.Success(new(item.GetString()?.Trim() ?? ""))
                : ModelParseResult<TextDirective>.Failure(new ModelValidationIssue("missing_field", $"{property} is required.", "$." + property));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ModelParseResult<TextDirective>.Failure(new ModelValidationIssue("invalid_json", ex.Message));
        }
    }
}

internal sealed class TextDirectiveValidator(string property) : IModelResultValidator<TextDirective>
{
    public IReadOnlyList<ModelValidationIssue> Validate(TextDirective result) =>
        string.IsNullOrWhiteSpace(result.Value)
            ? [new("empty_field", $"{property} cannot be empty.", "$." + property)]
            : [];
}

internal sealed class JsonColorDirectiveParser : IModelResponseParser<string, ColorGradingDirective>
{
    public ModelParseResult<ColorGradingDirective> Parse(string response)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClassifierJsonParser.StripFences(response));
            var root = doc.RootElement;
            var stock = Get(root, "film_stock");
            var palette = Get(root, "color_palette");
            var prompt = ColorPaletteGradingClassifier.StripGradeLabel(Get(root, "grading_prompt"));
            if (string.IsNullOrWhiteSpace(prompt) && !string.IsNullOrWhiteSpace(stock))
                prompt = $"{stock}, {palette}".TrimEnd(',', ' ');
            return ModelParseResult<ColorGradingDirective>.Success(new(stock, palette, prompt));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ModelParseResult<ColorGradingDirective>.Failure(new ModelValidationIssue("invalid_json", ex.Message));
        }
    }
    private static string Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() ?? "" : "";
}

internal sealed class ColorDirectiveValidator : IModelResultValidator<ColorGradingDirective>
{
    public IReadOnlyList<ModelValidationIssue> Validate(ColorGradingDirective result)
    {
        var issues = new List<ModelValidationIssue>();
        if (string.IsNullOrWhiteSpace(result.FilmStock)) issues.Add(new("missing_field", "film_stock is required.", "$.film_stock"));
        if (string.IsNullOrWhiteSpace(result.ColorPalette)) issues.Add(new("missing_field", "color_palette is required.", "$.color_palette"));
        if (string.IsNullOrWhiteSpace(result.GradingPrompt)) issues.Add(new("missing_field", "grading_prompt is required.", "$.grading_prompt"));
        return issues;
    }
}

/// <summary>Generic over <typeparamref name="TInput"/> too (not just <typeparamref name="TResult"/>) so any
/// single-shot classifier can reuse it, not only the <see cref="Stage2DirectiveInput"/> ones it started with —
/// the body never touches <c>input</c>, it only formats <paramref name="unresolvedIssues"/> — see
/// <see cref="PortraitStyleGateOperation"/> for a non-chat (vision) reuse.</summary>
internal sealed class DirectiveTerminalFallback<TInput, TResult> : IDeterministicFallback<TInput, TResult> where TResult : class
{
    public TResult Create(TInput input, IReadOnlyList<ModelValidationIssue> unresolvedIssues) =>
        throw new InvalidOperationException(string.Join(" ", unresolvedIssues.Select(i => i.Message)));
}
