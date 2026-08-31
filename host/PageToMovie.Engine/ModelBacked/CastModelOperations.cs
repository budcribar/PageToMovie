using System.Text.Json;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;

namespace PageToMovie.Engine.ModelBacked;

internal sealed record CastModelInput(string SystemPrompt, string UserPrompt, string Model);

internal sealed class CastChatOperation(
    IChatClient chat,
    string operationName,
    string promptVersion,
    string mode,
    double temperature) : IModelOperation<CastModelInput, string>
{
    public string OperationName => operationName;
    public string PromptVersion => promptVersion;

    public async Task<ModelResponse<string>> ExecuteAsync(
        CastModelInput input,
        ModelAttemptContext<string> context,
        CancellationToken ct)
    {
        var user = input.UserPrompt;
        if (context.Kind == ModelAttemptKind.Correction)
        {
            user += $"""


                CORRECTION REQUIRED. The previous JSON failed validation:
                {string.Join("\n", context.ValidationIssues.Select(i => $"- {i.Path ?? "$"}: {i.Message}"))}
                Return one corrected, complete JSON object only. Do not add characters that were not in the requested closed set.
                Previous response:
                {context.PreviousResponse}
                """;
        }

        var raw = await chat.CompleteAsync(
            input.SystemPrompt, user, input.Model, temperature, ct, mode).ConfigureAwait(false);
        return new ModelResponse<string>(raw, input.Model);
    }
}

internal sealed class CastJsonObjectParser : IModelResponseParser<string, Dictionary<string, object?>>
{
    public ModelParseResult<Dictionary<string, object?>> Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return ModelParseResult<Dictionary<string, object?>>.Failure(
                new ModelValidationIssue("empty_response", "The model response was empty."));
        try
        {
            return ModelParseResult<Dictionary<string, object?>>.Success(
                GrokChatClient.ParseJsonObject(ClassifierJsonParser.StripFences(response)));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return ModelParseResult<Dictionary<string, object?>>.Failure(
                new ModelValidationIssue("invalid_json", ex.Message));
        }
    }
}

internal sealed class CastExtractionValidator : IModelResultValidator<Dictionary<string, object?>>
{
    public IReadOnlyList<ModelValidationIssue> Validate(Dictionary<string, object?> result)
    {
        var issues = new List<ModelValidationIssue>();
        if (!result.TryGetValue("schema_version", out var schema) ||
            !string.Equals(schema?.ToString(), "cast_seeds.v1", StringComparison.Ordinal))
            issues.Add(new("invalid_schema", "schema_version must be cast_seeds.v1.", "$.schema_version"));

        if (!result.TryGetValue("performance_lock", out var perfLock) ||
            string.IsNullOrWhiteSpace(perfLock?.ToString()))
            issues.Add(new(
                "missing_performance_lock",
                "performance_lock is required (film-level audience/performance conventions). Empty is not allowed.",
                "$.performance_lock"));

        var seeds = FindSeeds(result);
        if (seeds is null || seeds.Count == 0)
        {
            issues.Add(new("missing_cast", "character_seed_tokens must contain at least one model-selected character.", "$.character_seed_tokens"));
            return issues;
        }

        ValidateCharacterSeeds(seeds, issues);
        ValidateLocationSeeds(result, issues);
        return issues;
    }

    private static void ValidateCharacterSeeds(
        Dictionary<string, object?> seeds,
        List<ModelValidationIssue> issues)
    {
        foreach (var (key, value) in seeds)
        {
            var path = $"$.character_seed_tokens.{key}";
            if (!key.StartsWith("Character_", StringComparison.Ordinal) || value is not Dictionary<string, object?> seed)
            {
                issues.Add(new("invalid_character", "Each cast entry must use a Character_ key and contain an object.", path));
                continue;
            }
            RequireText(seed, "canonical_given_name", path, issues);
            RequireText(seed, "description", path, issues);
            RequireText(seed, "display_name_policy", path, issues);
            RequireText(seed, "species_kind", path, issues);
            ValidateSourcePages(seed, path, issues);
        }
    }

    private static void ValidateLocationSeeds(
        Dictionary<string, object?> result,
        List<ModelValidationIssue> issues)
    {
        // Locations optional for validation fail-hard; when present, require Loc_* + description.
        if (!result.TryGetValue("location_seed_tokens", out var locObj)
            || locObj is not Dictionary<string, object?> locs
            || locs.Count == 0)
            return;

        foreach (var (key, value) in locs)
        {
            var path = $"$.location_seed_tokens.{key}";
            if (!key.StartsWith("Loc_", StringComparison.OrdinalIgnoreCase)
                || value is not Dictionary<string, object?> seed)
            {
                issues.Add(new("invalid_location", "Each location entry must use a Loc_ key and contain an object.", path));
                continue;
            }
            if (!seed.TryGetValue("description", out var desc) || string.IsNullOrWhiteSpace(desc?.ToString()))
                issues.Add(new("missing_location_field", "description is required for every location seed.", $"{path}.description"));
        }
    }

    internal static Dictionary<string, object?>? FindSeeds(Dictionary<string, object?> result)
    {
        if (result.TryGetValue("character_seed_tokens", out var direct) && direct is Dictionary<string, object?> seeds)
            return seeds;
        if (result.TryGetValue("global_production_variables", out var global) &&
            global is Dictionary<string, object?> globalObject &&
            globalObject.TryGetValue("character_seed_tokens", out var nested) &&
            nested is Dictionary<string, object?> nestedSeeds)
            return nestedSeeds;
        return null;
    }

    private static void RequireText(
        Dictionary<string, object?> seed,
        string name,
        string path,
        ICollection<ModelValidationIssue> issues)
    {
        if (!seed.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value?.ToString()))
            issues.Add(new("missing_cast_field", $"{name} is required for every character.", $"{path}.{name}"));
    }

    private static void ValidateSourcePages(
        Dictionary<string, object?> seed,
        string path,
        ICollection<ModelValidationIssue> issues)
    {
        if (!seed.TryGetValue("source_image_pages", out var value) || value is null) return;
        if (value is not List<object?> pages || pages.Any(page =>
                !int.TryParse(page?.ToString(), out var number) || number <= 0))
            issues.Add(new(
                "invalid_source_reference",
                "source_image_pages must contain only positive book page numbers.",
                $"{path}.source_image_pages"));
    }
}

internal sealed class LiteralizedCastValidator(IReadOnlySet<string> requestedKeys)
    : IModelResultValidator<Dictionary<string, object?>>
{
    public IReadOnlyList<ModelValidationIssue> Validate(Dictionary<string, object?> result)
    {
        var seeds = CastExtractionValidator.FindSeeds(result);
        if (seeds is null)
            return [new("missing_cast", "character_seed_tokens is required.", "$.character_seed_tokens")];

        var returned = seeds.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ModelValidationIssue>();
        foreach (var missing in requestedKeys.Where(k => !returned.Contains(k)))
            issues.Add(new("missing_character", "The scrub omitted a requested character.", $"$.character_seed_tokens.{missing}"));
        foreach (var added in returned.Where(k => !requestedKeys.Contains(k)))
            issues.Add(new("invented_character", "The scrub added a character outside the closed input set.", $"$.character_seed_tokens.{added}"));
        return issues;
    }
}

internal sealed class TerminalCastFallback : IDeterministicFallback<CastModelInput, Dictionary<string, object?>>
{
    public Dictionary<string, object?> Create(
        CastModelInput input,
        IReadOnlyList<ModelValidationIssue> unresolvedIssues) =>
        throw new InvalidOperationException(
            "The model did not return a valid cast after correction: " +
            string.Join(" ", unresolvedIssues.Select(i => i.Message)));
}

internal sealed class OriginalCastFallback(Dictionary<string, object?> original)
    : IDeterministicFallback<CastModelInput, Dictionary<string, object?>>
{
    public Dictionary<string, object?> Create(
        CastModelInput input,
        IReadOnlyList<ModelValidationIssue> unresolvedIssues) => original;
}
