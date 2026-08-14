using System.Text.Json;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Shared JSON parse for Stage-2 beat classifiers that return a named array of per-beat objects
/// (<c>directives</c>, <c>dof</c>, <c>sound_design</c>, …). Preserves each classifier's original
/// swallow-on-fault behavior: malformed JSON logs a warning and yields null.
/// </summary>
internal static class ClassifierDirectiveJson
{
    /// <summary>
    /// Strips fences, reads <paramref name="arrayProperty"/> as a JSON array, and folds each element
    /// through <paramref name="mapItem"/>. A null map result skips the element; a blank id is ignored.
    /// Exceptions from parse or mapping abort the whole call (matching the original per-classifier
    /// try/catch) and return null.
    /// </summary>
    public static Dictionary<string, T>? ParseKeyedArray<T>(
        string rawJson,
        string arrayProperty,
        Func<JsonElement, (string? Id, T Value)?> mapItem,
        ILogger log,
        string classifierNoun)
        where T : notnull
    {
        try
        {
            var cleaned = ClassifierJsonParser.StripFences(rawJson);
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty(arrayProperty, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr.EnumerateArray())
            {
                var mapped = mapItem(item);
                if (mapped is null) continue;
                var (id, value) = mapped.Value;
                if (!string.IsNullOrWhiteSpace(id))
                    result[id] = value;
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to parse AI {Classifier} response JSON: {RawJson}", classifierNoun, rawJson);
            return null;
        }
    }

    /// <summary>
    /// DoF/sound-style string read: missing property → empty; present non-string → <see cref="JsonElement.GetString"/> throws
    /// (caught by <see cref="ParseKeyedArray{T}"/>, same as the original helpers).
    /// </summary>
    public static string ReadLooseString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";

    /// <summary>
    /// Shared beat_id + three string fields mapper used by depth-of-field and sound-design.
    /// Missing <c>beat_id</c> skips the element; a non-string <c>beat_id</c> throws (whole parse nulls).
    /// </summary>
    public static (string? Id, T Value)? MapThreeStringFields<T>(
        JsonElement item,
        string field1,
        string field2,
        string field3,
        Func<string, string, string, T> create)
        where T : notnull
    {
        if (!item.TryGetProperty("beat_id", out var bid))
            return null;
        var id = bid.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return (id, create(
            ReadLooseString(item, field1),
            ReadLooseString(item, field2),
            ReadLooseString(item, field3)));
    }
}

/// <summary>
/// Shared ValidatedModelOperation pipeline for the scene-level text-token classifiers
/// (cinematic lighting, negative prompt).
/// </summary>
internal sealed record ClassifierDirectiveSpec(
    string SystemPrompt,
    Func<string> BuildUserPrompt,
    string? Model,
    string DefaultModel,
    string OperationName,
    string PromptVersion,
    string JsonProperty,
    string ChatMode,
    string ClassifierNoun);

internal sealed record ClassifierDirectiveRun(
    IChatClient Chat,
    ILogger Log,
    Dictionary<string, object?> Scene,
    ClassifierDirectiveSpec Spec,
    CancellationToken Ct);

internal static class ClassifierTextDirectiveRunner
{
    public static async Task<string?> ExecuteAsync(ClassifierDirectiveRun run)
    {
        var spec = run.Spec;
        try
        {
            var userPrompt = spec.BuildUserPrompt();
            var effectiveModel = !string.IsNullOrWhiteSpace(spec.Model) ? spec.Model : spec.DefaultModel;
            var pipeline = new ValidatedModelOperation<Stage2DirectiveInput, TextDirective>(
                new Stage2DirectiveOperation(run.Chat, spec.OperationName, spec.PromptVersion),
                new JsonTextDirectiveParser(spec.JsonProperty),
                new TextDirectiveValidator(spec.JsonProperty),
                new DirectiveTerminalFallback<Stage2DirectiveInput, TextDirective>(),
                new ModelOperationOptions { CorrectiveMaxAttempts = 1 });
            var result = await pipeline.ExecuteAsync(
                new(spec.SystemPrompt, userPrompt, effectiveModel, spec.ChatMode), run.Ct).ConfigureAwait(false);
            return result.Value?.Value;
        }
        catch (Exception ex)
        {
            run.Log.LogWarning(ex, "Failed to run AI {Classifier} classification for scene {Scene}", spec.ClassifierNoun, run.Scene.GetValueOrDefault("scene_number"));
            return null;
        }
    }

    /// <summary>IsEnabled gate + progress + <see cref="ExecuteAsync"/> in one hop so call sites stay unique.</summary>
    public static Task<string?> ClassifyAsync(
        bool isEnabled,
        Action<string>? onProgress,
        string progressMessage,
        ClassifierDirectiveRun run)
    {
        if (!isEnabled) return Task.FromResult<string?>(null);
        onProgress?.Invoke(progressMessage);
        return ExecuteAsync(run);
    }
}
