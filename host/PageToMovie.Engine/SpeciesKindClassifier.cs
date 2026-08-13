using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Labels cast seeds as animal | human | other. Baseline: CharacterVisualTextScrubber heuristics.
/// Writes <c>species_kind</c> on each character seed token.
/// </summary>
public sealed class SpeciesKindClassifier
{
    public const string PromptVersion = "v1";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<SpeciesKindClassifier> _log;

    public SpeciesKindClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<SpeciesKindClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifySpeciesKindWithChat && _chat.IsConfigured;

    public async Task<SimpleClassifyResult> ClassifyStage1Async(
        Dictionary<string, object?> stage1,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.SpeciesKindClassifyModel;
        var result = new SimpleClassifyResult
        {
            Name = "species_kind",
            PromptVersion = PromptVersion,
            Enabled = IsEnabled,
            Model = effectiveModel,
        };
        var seeds = GetSeeds(stage1);
        result.ItemCount = seeds.Count;
        foreach (var s in seeds)
            s.Dict["species_kind"] = BaselineKind(s.Key, s.Description, s.VisualLock);

        if (!IsEnabled || seeds.Count == 0)
        {
            result.FallbackCount = seeds.Count;
            result.Note = "heuristic only";
            onProgress?.Invoke($"Species kind: heuristic only ({seeds.Count})");
            return result;
        }

        onProgress?.Invoke($"Classifying species kind for {seeds.Count} cast…");
        var maxAttempts = Math.Clamp(_opts.SilentBeatClassifyMaxAttempts, 1, 5);
        var backoffBaseMs = Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs);
        var labeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedIds = seeds.Select(s => s.Key).ToList();
        var byKey = seeds.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        // Mutable: shrinks to only still-missing keys so each retry re-asks fewer cast members —
        // mirrors the pre-refactor hand-rolled loop exactly (single call, no chunking).
        var retry = await AiRetryPolicy.RunWithCoverageRetryAsync<string>(
            seedIds,
            callChat: async missingIds =>
            {
                var payload = missingIds.Select(k =>
                {
                    var s = byKey[k];
                    return new Dictionary<string, object?>
                    {
                        ["key"] = s.Key,
                        ["description"] = Trunc(s.Description, 70),
                        ["visual_lock"] = Trunc(s.VisualLock, 50),
                        ["heuristic"] = BaselineKind(s.Key, s.Description, s.VisualLock),
                    };
                }).ToList();
                var user = "Label each cast key animal|human|other. JSON only.\n" +
                           JsonSerializer.Serialize(new { cast = payload });
                var raw = await _chat.CompleteAsync(SystemPrompt(), user, effectiveModel, 0, ct, ChatCallModes.SpeciesKindClassify)
                    .ConfigureAwait(false);
                result.ChatCalls++;
                return raw;
            },
            parseResponse: raw =>
            {
                var parsed = ParseLabels(raw);
                return parsed;
            },
            maxAttempts,
            backoffBaseMs,
            ct,
            operationName: "stage2_species_kind",
            promptVersion: "1",
            model: effectiveModel).ConfigureAwait(false);

        if (retry.LastError is not null)
        {
            _log.LogWarning("SpeciesKind classify failed: {Error}", retry.LastError);
            result.LastError = retry.LastError;
        }
        if (retry.Result is not null)
        {
            foreach (var kv in retry.Result)
            {
                byKey[kv.Key].Dict["species_kind"] = kv.Value;
                labeled.Add(kv.Key);
            }
        }

        result.AiCount = labeled.Count;
        result.FallbackCount = seeds.Count - labeled.Count;
        result.Note = $"AI {labeled.Count}/{seeds.Count}";
        onProgress?.Invoke($"Species kind: {result.Note}");
        return result;
    }

    public static string SystemPrompt() => """
Label each cast member's body type for portrait/plate sorting (any story).
- animal: the character IS an animal body (dog, cat, talking beast, etc.)
- human: human body (including when text says match an animal's CG style)
- other: object, crowd, unclear

JSON: {"labels":[{"key":"Character_Narrator","class":"human"}]}
""";

    public static string BaselineKind(string key, string description, string visualLock)
    {
        if (CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", description, visualLock, "dog") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", description, visualLock, "cat") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", description, visualLock, "bear") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", description, visualLock, "fox") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", description, visualLock, "wolf") ||
            CharacterVisualTextScrubber.IsPrimarilyAnimalCharacter(key, "", description, visualLock, "bird"))
            return "animal";
        if (CharacterVisualTextScrubber.IsHumanAdultCharacter(key, "", description, visualLock))
            return "human";
        return "other";
    }

    public static Dictionary<string, string> ParseLabels(string raw) =>
        ClassifierLabelParser.Parse(raw, el =>
        {
            var key = el.TryGetProperty("key", out var k) ? k.GetString()
                : el.TryGetProperty("id", out var id) ? id.GetString() : null;
            var cls = el.TryGetProperty("class", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(cls)) return (key, null);
            cls = cls.Trim().ToLowerInvariant();
            if (cls is not ("animal" or "human" or "other")) return (key, null);
            return (key, cls);
        });

    private static List<SeedRow> GetSeeds(Dictionary<string, object?> stage1)
    {
        var list = new List<SeedRow>();
        var gpv = stage1.TryGetValue("global_production_variables", out var g) && g is Dictionary<string, object?> gd ? gd : null;
        var seeds = gpv is not null && gpv.TryGetValue("character_seed_tokens", out var c) && c is Dictionary<string, object?> cs ? cs : null;
        if (seeds is null) return list;
        foreach (var (key, val) in seeds)
        {
            if (val is not Dictionary<string, object?> seed) continue;
            var desc = seed.TryGetValue("description", out var d) ? d?.ToString() ?? "" : "";
            var vl = seed.TryGetValue("visual_lock", out var v) ? v?.ToString() ?? "" : "";
            list.Add(new SeedRow { Key = key, Description = desc, VisualLock = vl, Dict = seed });
        }
        return list;
    }

    // Token-accurate now (was raw character count) — see PromptTokenizer.
    private static string Trunc(string s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);

    private sealed class SeedRow
    {
        public required string Key { get; init; }
        public string Description { get; init; } = "";
        public string VisualLock { get; init; } = "";
        public required Dictionary<string, object?> Dict { get; init; }
    }
}
