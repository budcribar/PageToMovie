using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Closed-set on-screen cast keys from beat visual (+ speaker). Baseline: name substring match.
/// Writes <c>characters_on_screen</c> on each beat when AI succeeds.
/// </summary>
public sealed class OnScreenCastClassifier
{
    /// <summary>Shipped prompt id (matches host/evals/classifier_benchmarks/prompts/onscreen_cast/v2_grounded).</summary>
    public const string PromptVersion = "v2_grounded";

    /// <summary>
    /// Matches "voiceover" / "voice-over" / "voice over" or the abbreviation "V.O." / "VO" as a
    /// whole word — NOT a bare "vo" substring, which false-positives on "voice", "avoid", "provoke", etc.
    /// </summary>
    private static readonly Regex VoiceoverPattern = new(@"\bvoice[\s-]?over\b|\bv\.?\s*o\.?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<OnScreenCastClassifier> _log;

    public OnScreenCastClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<OnScreenCastClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyOnScreenCastWithChat && _chat.IsConfigured;

    public async Task<SimpleClassifyResult> ClassifyStage1Async(
        Dictionary<string, object?> stage1,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.OnScreenCastClassifyModel;
        var result = new SimpleClassifyResult
        {
            Name = "onscreen_cast",
            PromptVersion = PromptVersion,
            Enabled = IsEnabled,
            Model = effectiveModel,
        };
        var castKeys = ExtractCastKeys(stage1);
        var targets = CollectSilentAndDialogue(stage1);
        result.ItemCount = targets.Count;
        if (castKeys.Count == 0 || targets.Count == 0)
        {
            result.Note = "no cast or beats";
            return result;
        }

        // Baseline heuristic into beat field for fallback
        var profiles = castKeys.ToDictionary(
            k => k,
            k => new ClipVideoPromptBuilder.CharacterProfile { DisplayName = k.Replace("Character_", "").Replace('_', ' ') },
            StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            var inferred = ClipVideoPromptBuilder.InferKeysFromProse(t.VisualEvent + " " + t.Dialogue, profiles);
            if (!string.IsNullOrWhiteSpace(t.SpeakerKey) &&
                !inferred.Contains(t.SpeakerKey, StringComparer.OrdinalIgnoreCase) &&
                !t.IsVoiceover)
                inferred.Add(t.SpeakerKey);
            t.HeuristicKeys = inferred;
            t.Beat["characters_on_screen"] = inferred.Cast<object?>().ToList();
        }

        if (!IsEnabled)
        {
            result.FallbackCount = targets.Count;
            result.Note = "heuristic only";
            onProgress?.Invoke($"On-screen cast: heuristic only ({targets.Count})");
            return result;
        }

        onProgress?.Invoke($"Classifying on-screen cast for {targets.Count} beat(s)…");
        var maxAttempts = Math.Clamp(_opts.SilentBeatClassifyMaxAttempts, 1, 5);
        var backoffBaseMs = Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs);
        var labeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 25;

        var chunks = new List<List<Target>>();
        for (var offset = 0; offset < targets.Count; offset += batchSize)
            chunks.Add(targets.Skip(offset).Take(batchSize).ToList());

        using var sem = new SemaphoreSlim(4);
        var tasks = chunks.Select(async chunk =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var chunkIds = chunk.Select(t => t.Id).ToList();
                var byId = chunk.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
                // Mutable: shrinks to only still-missing ids so each retry re-asks fewer beats —
                // mirrors the pre-refactor hand-rolled loop exactly.
                var retry = await AiRetryPolicy.RunWithCoverageRetryAsync<List<string>>(
                    chunkIds,
                    callChat: async missingIds =>
                    {
                        var payload = missingIds.Select(id =>
                        {
                            var t = byId[id];
                            return new Dictionary<string, object?>
                            {
                                ["id"] = t.Id,
                                ["visual_event"] = Trunc(t.VisualEvent, 60),
                                ["dialogue"] = Trunc(t.Dialogue, 30),
                                ["speaker_key"] = t.SpeakerKey,
                                ["is_voiceover"] = t.IsVoiceover,
                                ["heuristic_keys"] = t.HeuristicKeys,
                            };
                        }).ToList();
                        var user = "Pick on-screen Character_* keys from the closed cast. JSON only.\n" +
                                   JsonSerializer.Serialize(new { cast_keys = castKeys, beats = payload });
                        var raw = await _chat.CompleteAsync(SystemPrompt(), user, effectiveModel, 0, ct, ChatCallModes.OnScreenCastClassify)
                            .ConfigureAwait(false);
                        lock (labeled) { result.ChatCalls++; }
                        return raw;
                    },
                    parseResponse: raw =>
                    {
                        var parsed = ParseLabels(raw, castKeys);
                        return parsed;
                    },
                    maxAttempts,
                    backoffBaseMs,
                    ct,
                    operationName: "stage2_on_screen_cast",
                    promptVersion: "1",
                    model: effectiveModel).ConfigureAwait(false);

                if (retry.LastError is not null)
                {
                    _log.LogWarning("OnScreenCast chunk failed: {Error}", retry.LastError);
                    lock (labeled) { result.LastError = retry.LastError; }
                }
                if (retry.Result is not null)
                {
                    lock (labeled)
                    {
                        foreach (var kv in retry.Result)
                        {
                            if (!byId.TryGetValue(kv.Key, out var t)) continue;
                            t.Beat["characters_on_screen"] = kv.Value.Cast<object?>().ToList();
                            labeled.Add(kv.Key);
                        }
                    }
                }
            }
            finally
            {
                sem.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        result.AiCount = labeled.Count;
        result.FallbackCount = targets.Count - labeled.Count;
        result.Note = $"AI {labeled.Count}/{targets.Count}";
        onProgress?.Invoke($"On-screen cast: {result.Note}");
        return result;
    }

    public static string SystemPrompt() => """
You assign which locked cast members are ON CAMERA for each beat (any story).

Closed set only: return Character_* keys from cast_keys. Never invent keys outside that list.

## Include
- Everyone clearly visible or physically acting in the visual_event (named or unambiguously described).
- Non-voiceover speakers when the beat is their on-camera line (visual like "X speaks." or they appear in the action).
- Group cast keys when the visual shows that group acting (e.g. monkey troop, cub litter, seal crowd) and the key exists in cast_keys.

## Exclude
- Voiceover-only or off-screen speakers (is_voiceover: true, or spoken from off-screen / another room) UNLESS the visual explicitly shows their physical body on camera.
- Names mentioned only in dialogue or as possession of a prop ("Shere Khan's hide", "Kala Nag" in a line) when the visual does not show that character body on screen.
- Corpses, skins, hides, trophies, photos, statues — not living on-screen cast.
- Anonymous crowd/pack without a matching group cast key (do not invent individuals).

## Disambiguation
- Prefer the most specific matching key (longest / full name). Never also add a shorter key that is only a substring of another matched name (e.g. Kala Nag → Character_Kala_Nag only, not Character_Nag).
- Nicknames and hyphen variants count (Rikki / Rikki-tikki → Character_Rikki_Tikki when that key exists).
- Age-variant keys (e.g. Character_Young_Nick alongside base Character_Nick) follow the same rule: a beat showing the character's childhood/teen self picks ONLY the age-variant key, never the base adult key too — the age-variant is the more specific match for that beat.
- Pronoun-only beats: if the subject is clearly a continuing named cast member from story context in the visual prose, include that key; if truly ambiguous, empty or only keys grounded in text.

## Heuristic
- You may correct heuristic_keys when they over-include (props, substring false hits) or under-include (groups, nicknames).

Empty list is OK for pure environment or sound-only beats with no readable faces/bodies.

JSON only:
{"labels":[{"id":"s1_b1","keys":["Character_Narrator"]}]}
""";

    public static Dictionary<string, List<string>> ParseLabels(string raw, IReadOnlyList<string> castKeys)
    {
        var allowed = new HashSet<string>(castKeys, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        raw = Strip(raw);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.GetProperty("labels");
            foreach (var el in arr.EnumerateArray())
            {
                var id = el.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var keys = new List<string>();
                if (el.TryGetProperty("keys", out var kEl) && kEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in kEl.EnumerateArray())
                    {
                        var s = k.GetString();
                        if (s is null) continue;
                        var hit = allowed.FirstOrDefault(a => a.Equals(s, StringComparison.OrdinalIgnoreCase));
                        if (hit is not null && !keys.Contains(hit, StringComparer.OrdinalIgnoreCase))
                            keys.Add(hit);
                    }
                }
                map[id] = keys;
            }
        }
        catch (Exception)
        {
            // Malformed classifier JSON: return labels parsed before the fault.
            return map;
        }
        return map;
    }

    public static double SetF1(IReadOnlyList<string> pred, IReadOnlyList<string> gold)
    {
        var p = new HashSet<string>(pred, StringComparer.OrdinalIgnoreCase);
        var g = new HashSet<string>(gold, StringComparer.OrdinalIgnoreCase);
        if (p.Count == 0 && g.Count == 0) return 1.0;
        if (p.Count == 0 || g.Count == 0) return 0.0;
        var inter = p.Intersect(g, StringComparer.OrdinalIgnoreCase).Count();
        var prec = (double)inter / p.Count;
        var rec = (double)inter / g.Count;
        return prec + rec <= 0 ? 0 : 2 * prec * rec / (prec + rec);
    }

    private static List<string> ExtractCastKeys(Dictionary<string, object?> stage1)
    {
        var gpv = stage1.TryGetValue("global_production_variables", out var g) && g is Dictionary<string, object?> gd ? gd : null;
        var seeds = gpv is not null && gpv.TryGetValue("character_seed_tokens", out var c) && c is Dictionary<string, object?> cs ? cs : null;
        if (seeds is null) return new List<string>();
        return seeds.Keys.Where(k => k.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).ToList();
    }

    private static List<Target> CollectSilentAndDialogue(Dictionary<string, object?> stage1)
    {
        var list = new List<Target>();
        foreach (var (si, bi, _, beat) in ClassifierBeatEnumerator.EnumerateSceneBeats(stage1))
        {
            var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString() ?? "" : "";
            var dlg = beat.TryGetValue("dialogue", out var d) ? d?.ToString() ?? "" : "";
            var sp = beat.TryGetValue("speaker", out var s) ? s?.ToString() ?? "" : "";
            var del = beat.TryGetValue("delivery", out var delv) ? delv?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(ve) && string.IsNullOrWhiteSpace(dlg)) continue;
            list.Add(new Target
            {
                Id = $"s{si}_b{bi}",
                VisualEvent = ve,
                Dialogue = dlg,
                SpeakerKey = sp,
                IsVoiceover = VoiceoverPattern.IsMatch(del),
                Beat = beat,
            });
        }
        return list;
    }

    private static string Strip(string raw) => ClassifierJsonParser.StripFences(raw);

    // Token-accurate now (was raw character count) — see PromptTokenizer.
    private static string Trunc(string s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);

    private sealed class Target
    {
        public required string Id { get; init; }
        public string VisualEvent { get; init; } = "";
        public string Dialogue { get; init; } = "";
        public string SpeakerKey { get; init; } = "";
        public bool IsVoiceover { get; init; }
        public required Dictionary<string, object?> Beat { get; init; }
        public List<string> HeuristicKeys { get; set; } = new();
    }
}

public sealed class SimpleClassifyResult
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string PromptVersion { get; set; } = "";
    public string Model { get; set; } = "";
    public int ItemCount { get; set; }
    public int AiCount { get; set; }
    public int FallbackCount { get; set; }
    public int ChatCalls { get; set; }
    public string Note { get; set; } = "";
    public string? LastError { get; set; }

    public Dictionary<string, object?> ToMetaDict() => new()
    {
        ["name"] = Name,
        ["enabled"] = Enabled,
        ["prompt_version"] = PromptVersion,
        ["model"] = Model,
        ["items"] = ItemCount,
        ["ai_labels"] = AiCount,
        ["heuristic_fallback"] = FallbackCount,
        ["chat_calls"] = ChatCalls,
        ["note"] = Note,
    };
}
