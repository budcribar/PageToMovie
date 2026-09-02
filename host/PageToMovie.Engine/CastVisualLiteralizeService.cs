using PageToMovie.Core.Models;
using System.Text.Json;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelBacked;
using PageToMovie.Engine.ModelExecution;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// AI pass: rewrite figurative / idiomatic visual prose into literal filmable descriptions.
/// Avoids never-ending regex nickname lists — the model judges phrase risk.
/// Prompt: <c>prompts/cast_visual_literalize.txt</c>.
/// </summary>
public sealed class CastVisualLiteralizeService
{
    public const string PromptRelativePath = "prompts/cast_visual_literalize.txt";

    private const string VisualLockKey = "visual_lock";
    private const string WardrobeAlwaysKey = "wardrobe_always";
    private const string DescriptionKey = "description";

    private readonly ProjectStore _projects;
    private readonly IChatClient _chat;
    private readonly ILogger<CastVisualLiteralizeService> _log;

    public CastVisualLiteralizeService(
        ProjectStore projects,
        IChatClient chat,
        ILogger<CastVisualLiteralizeService> log)
    {
        _projects = projects;
        _chat = chat;
        _log = log;
    }

    /// <summary>
    /// Literalize description / visual_lock / wardrobe_always on each seed in-place (dict).
    /// Non-fatal: returns input seeds if chat fails.
    /// AI prompt handles figurative language + base-look vs later wardrobe — no special-case lists.
    /// </summary>
    public async Task<Dictionary<string, object?>> LiteralizeSeedsAsync(
        Dictionary<string, object?> seeds,
        string model = "",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (seeds.Count == 0 || !_chat.IsConfigured)
            return seeds;
        if (string.IsNullOrWhiteSpace(model))
        {
            onProgress?.Invoke("AI visual scrub skipped (no Script & planning model in Settings).");
            return seeds;
        }
        model = ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Cast visual scrub");

        onProgress?.Invoke("Scrubbing visual descriptions (AI prompt)…");
        try
        {
            var system = await LoadSystemPromptAsync(_projects.WorkspaceRoot, ct).ConfigureAwait(false);
            var payload = new Dictionary<string, object?>
            {
                ["character_seed_tokens"] = BuildVisualPayload(seeds),
            };
            var user =
                "Scrub these character seeds for generative image models:\n" +
                "1) figurative/idiomatic → literal filmable\n" +
                "2) base portrait look only — strip later-story wardrobe/plot from description & visual_lock\n" +
                "Return JSON only with character_seed_tokens.\n\n" +
                JsonSerializer.Serialize(payload, JsonDefaults.Indented);

            var requestedKeys = seeds.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pipeline = new ValidatedModelOperation<CastModelInput, Dictionary<string, object?>>(
                new CastChatOperation(_chat, "cast_visual_literalize", "1", ChatCallModes.CastVisualLiteralize, 0.15),
                new CastJsonObjectParser(),
                new LiteralizedCastValidator(requestedKeys),
                new OriginalCastFallback(seeds),
                new ModelOperationOptions { CorrectiveMaxAttempts = 1 });
            var result = await pipeline.ExecuteAsync(new CastModelInput(system, user, model), ct).ConfigureAwait(false);
            if (result.Source == ModelResultSource.DeterministicFallback)
                onProgress?.Invoke("AI visual scrub kept the original cast after validation failed.");
            return result.Value is null ? seeds : MergeLiteralized(seeds, result.Value);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cast visual literalize failed — keeping pre-literalize seeds");
            onProgress?.Invoke("AI visual scrub skipped (non-fatal).");
            return seeds;
        }
    }

    /// <summary>
    /// Scrub one character's look fields via the same API prompt (Save look / generate).
    /// Returns cleaned description + visual_lock; non-fatal falls back to input.
    /// </summary>
    public async Task<(string Description, string VisualLock, bool UsedAi)> ScrubLookFieldsAsync(
        string charKey,
        string? description,
        string? visualLock,
        string? wardrobeAlwaysJson = null,
        string model = "",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var descIn = description ?? "";
        var visIn = visualLock ?? "";
        if (!_chat.IsConfigured)
            return (descIn, visIn, false);
        if (string.IsNullOrWhiteSpace(model))
            return (descIn, visIn, false);
        if (string.IsNullOrWhiteSpace(descIn) && string.IsNullOrWhiteSpace(visIn))
            return (descIn, visIn, false);
        model = ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Look scrub");

        var seed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [DescriptionKey] = descIn,
            [VisualLockKey] = visIn,
        };
        if (!string.IsNullOrWhiteSpace(wardrobeAlwaysJson))
        {
            try
            {
                var wa = JsonSerializer.Deserialize<List<object?>>(wardrobeAlwaysJson);
                if (wa is not null) seed[WardrobeAlwaysKey] = wa;
            }
            catch { /* optional */ }
        }

        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [charKey] = seed,
        };
        onProgress?.Invoke("AI scrub: base look + literal filmable…");
        var cleaned = await LiteralizeSeedsAsync(bag, model, onProgress, ct).ConfigureAwait(false);
        if (cleaned.TryGetValue(charKey, out var cval) && cval is Dictionary<string, object?> cseed)
        {
            var d = cseed.TryGetValue(DescriptionKey, out var dv) ? dv?.ToString() ?? descIn : descIn;
            var v = cseed.TryGetValue(VisualLockKey, out var vv) ? vv?.ToString() ?? visIn : visIn;
            return (d.Trim(), v.Trim(), true);
        }
        return (descIn, visIn, false);
    }

    public static Task<string> LoadSystemPromptAsync(string workspaceRoot, CancellationToken ct = default) =>
        PromptFiles.ReadAsync(PromptRelativePath, workspaceRoot, ct);

    private static Dictionary<string, object?> BuildVisualPayload(Dictionary<string, object?> seeds)
    {
        var outSeeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in seeds)
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var slim = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (seed.TryGetValue(DescriptionKey, out var d)) slim[DescriptionKey] = d;
            if (seed.TryGetValue(VisualLockKey, out var v)) slim[VisualLockKey] = v;
            if (seed.TryGetValue(WardrobeAlwaysKey, out var w)) slim[WardrobeAlwaysKey] = w;
            if (seed.TryGetValue("display_name_policy", out var p)) slim["display_name_policy"] = p;
            if (seed.TryGetValue("canonical_given_name", out var n)) slim["canonical_given_name"] = n;
            outSeeds[key] = slim;
        }
        return outSeeds;
    }

    private static Dictionary<string, object?> MergeLiteralized(
        Dictionary<string, object?> original,
        Dictionary<string, object?> parsed)
    {
        Dictionary<string, object?>? cleanedSeeds = TryReadCleanedSeeds(parsed);
        if (cleanedSeeds is null || cleanedSeeds.Count == 0)
            return original;

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in original)
            result[key] = MergeOneLiteralized(key, val, cleanedSeeds);
        return result;
    }

    private static Dictionary<string, object?>? TryReadCleanedSeeds(Dictionary<string, object?> parsed)
    {
        if (parsed.TryGetValue("character_seed_tokens", out var s) && s is Dictionary<string, object?> d)
            return d;
        if (parsed.TryGetValue("global_production_variables", out var g) &&
            g is Dictionary<string, object?> gpv &&
            gpv.TryGetValue("character_seed_tokens", out var s2) &&
            s2 is Dictionary<string, object?> d2)
            return d2;
        return null;
    }

    private static object? MergeOneLiteralized(
        string key, object? val, Dictionary<string, object?> cleanedSeeds)
    {
        if (val is not Dictionary<string, object?> seed)
            return val;

        var copy = new Dictionary<string, object?>(seed, StringComparer.OrdinalIgnoreCase);
        if (cleanedSeeds.TryGetValue(key, out var cval) && cval is Dictionary<string, object?> clean)
            ApplyCleanedFields(copy, clean);
        return copy;
    }

    private static void ApplyCleanedFields(Dictionary<string, object?> copy, Dictionary<string, object?> clean)
    {
        if (clean.TryGetValue(DescriptionKey, out var desc) && desc is not null)
            copy[DescriptionKey] = desc.ToString()?.Trim();
        // Rewrite an existing lock; never author one. This pass is a scrubber - it turns
        // figurative prose literal. A seed whose look the pipeline invented deliberately carries
        // an empty visual_lock (see LookProvenance), and letting the scrub fill it would smuggle
        // that invention straight back into the film's must-never-drift contract.
        if (clean.TryGetValue(VisualLockKey, out var vl) && vl is not null
            && copy.TryGetValue(VisualLockKey, out var had)
            && !string.IsNullOrWhiteSpace(had?.ToString()))
            copy[VisualLockKey] = vl.ToString()?.Trim();
        if (clean.TryGetValue(WardrobeAlwaysKey, out var wa) && wa is List<object?> list)
            copy[WardrobeAlwaysKey] = list;
    }
}
