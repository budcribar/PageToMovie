using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Chat refine of ambient bed vs SFX for beats. Heuristic: <see cref="FountainStage1Importer.InferAmbientAndSfx"/>.
/// Policy: AI preferred → retry → keep heuristic. Never fall back merely because AI differs.
/// </summary>
public sealed class AmbientSfxClassifier
{
    /// <summary>Shipped prompt id (matches host/evals/classifier_benchmarks/prompts/ambient_sfx/v2_grounded).</summary>
    public const string PromptVersion = "v2_grounded";
    public const string DefaultModel = "";
    public const int DefaultBatchSize = 30;

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<AmbientSfxClassifier> _log;

    private static readonly Regex TokenSplitRegex = new(@"[,;/|]+|\s{2,}", RegexOptions.Compiled, CommonRegex.Timeout);

    public AmbientSfxClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<AmbientSfxClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled =>
        _opts.ClassifyAmbientSfxWithChat && _chat.IsConfigured;

    public async Task<AmbientSfxClassifyResult> ClassifyStage1Async(
        Dictionary<string, object?> stage1,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? overrideModel = null)
    {
        var model = ResolveClassifyModel(overrideModel);
        var temp = _opts.AmbientSfxClassifyTemperature;
        if (double.IsNaN(temp) || temp < 0) temp = 0.2;
        var maxAttempts = Math.Clamp(_opts.AmbientSfxClassifyMaxAttempts, 1, 5);
        var result = new AmbientSfxClassifyResult
        {
            PromptVersion = PromptVersion,
            Model = model,
            Temperature = temp,
            Enabled = IsEnabled,
        };

        var targets = CollectBeats(stage1);
        result.BeatCount = targets.Count;
        foreach (var t in targets)
        {
            var (a, s) = FountainStage1Importer.InferAmbientAndSfx(t.VisualEvent);
            t.HeuristicAmbient = a;
            t.HeuristicSfx = s;
            Apply(t.Beat, a, s);
        }

        if (!IsEnabled || targets.Count == 0)
        {
            result.FallbackCount = targets.Count;
            result.Note = !IsEnabled ? "disabled or chat not configured" : "no beats";
            onProgress?.Invoke($"Ambient/SFX: heuristic only ({targets.Count})");
            return result;
        }

        onProgress?.Invoke($"Classifying ambient/SFX for {targets.Count} beat(s)…");
        var byId = targets.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var labeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backoffBaseMs = Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs);

        var chunks = new List<List<Target>>();
        for (var offset = 0; offset < targets.Count; offset += DefaultBatchSize)
            chunks.Add(targets.Skip(offset).Take(DefaultBatchSize).ToList());

        using var sem = new SemaphoreSlim(4);
        var tasks = chunks.Select(chunk => ClassifyChunkAsync(
            chunk, byId, labeled, result, model, temp, maxAttempts, backoffBaseMs, sem, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        result.AiCount = labeled.Count;
        result.FallbackCount = targets.Count - labeled.Count;
        result.Note = $"AI {labeled.Count}/{targets.Count}; heuristic kept {result.FallbackCount}";
        onProgress?.Invoke($"Ambient/SFX: {result.Note}");
        return result;
    }

    private string ResolveClassifyModel(string? overrideModel)
    {
        if (!string.IsNullOrWhiteSpace(overrideModel))
            return overrideModel;
        if (string.IsNullOrWhiteSpace(_opts.AmbientSfxClassifyModel))
            throw new InvalidOperationException(
                "Ambient SFX classify: no model configured. Set the project Script & planning model in Settings, or AmbientSfxClassifyModel.");
        return _opts.AmbientSfxClassifyModel.Trim();
    }

    private async Task ClassifyChunkAsync(
        List<Target> chunk,
        Dictionary<string, Target> byId,
        HashSet<string> labeled,
        AmbientSfxClassifyResult result,
        string model,
        double temp,
        int maxAttempts,
        int backoffBaseMs,
        SemaphoreSlim sem,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var chunkIds = chunk.Select(t => t.Id).ToList();
            // Mutable: shrinks to only still-missing ids so each retry re-asks fewer beats —
            // mirrors the pre-refactor hand-rolled loop exactly.
            var retry = await AiRetryPolicy.RunWithCoverageRetryAsync<(string Ambient, string Sfx)>(
                chunkIds,
                callChat: async missingIds =>
                {
                    var batch = missingIds.Select(id => byId[id]).ToList();
                    var raw = await CallAsync(batch, model, temp, ct).ConfigureAwait(false);
                    lock (labeled) { result.ChatCalls++; }
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
                operationName: "stage2_ambient_sfx",
                promptVersion: PromptVersion,
                model: model).ConfigureAwait(false);

            lock (labeled) { result.Attempts += retry.Attempts; }
            if (retry.LastError is not null)
            {
                _log.LogWarning("AmbientSfx classify chunk failed: {Error}", retry.LastError);
                lock (labeled) { result.LastError = Trim(retry.LastError, 200); }
            }
            if (retry.Result is not null)
            {
                lock (labeled)
                    ApplyChunkLabels(retry.Result, byId, labeled);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    private static void ApplyChunkLabels(
        Dictionary<string, (string Ambient, string Sfx)> labels,
        Dictionary<string, Target> byId,
        HashSet<string> labeled)
    {
        foreach (var kv in labels)
        {
            if (!byId.TryGetValue(kv.Key, out var t)) continue;
            Apply(t.Beat, kv.Value.Ambient, kv.Value.Sfx);
            labeled.Add(kv.Key);
        }
    }

    private async Task<string> CallAsync(
        List<Target> batch, string model, double temp, CancellationToken ct)
    {
        var payload = batch.Select(b => new Dictionary<string, object?>
        {
            ["id"] = b.Id,
            ["visual_event"] = Trunc(b.VisualEvent, 80),
            ["heuristic_ambient"] = b.HeuristicAmbient,
            ["heuristic_sfx"] = b.HeuristicSfx,
        }).ToList();
        var user = "Split each beat into ambient bed vs sfx hits. JSON only.\n" +
                   JsonSerializer.Serialize(new { beats = payload });
        return await _chat.CompleteAsync(SystemPrompt(), user, model, temp, ct, ChatCallModes.AmbientSfxClassify)
            .ConfigureAwait(false);
    }

    public static string SystemPrompt() => """
You label film audio layers from silent or action visual prose (any story).

Return continuous ambient BED vs transient SFX hits as short lowercase phrases.

## Layers
- ambient: ongoing bed only — rain, wind, room/den tone, fire crackle, waves/underwater, crowd murmur, distant song/bugle practice, continuous animal pack whisper. Empty if none is clearly implied.
- sfx: short discrete hits only — door slam, footsteps, trunk slide, cubs tumble, wolf pads, temple bells, conches, howl hit, laughter, running/shouting as crowd hits. Empty if none.

## Strict empty cases (output empty ambient AND empty sfx)
- Pure dialogue cues: "X speaks.", "X speaks undertone…", line reads.
- Performance parentheticals only: (dry), (aloud), (smiling…), (purring), (shouting), (snuffling) — these are dialogue/performance, not beds or hits.
- Verbs that are only speech acts: scolds, whispers as dialogue delivery, "speaks", "says".
- Thin stage direction with no sound word and no physical motion that makes noise (e.g. two animals look at each other).

## Grounding (do not invent)
- Every token must be supported by a word or clear action in the visual. Prefer reusing story words (whisper → monkey whispers; rain softens → soft rain).
- Do NOT invent weather, doors, breeze, room tone, or crowd beds when the visual does not imply them.
- When unsure, leave the field empty. Empty is better than a guess.

## Layering tips
- Continuous / far / ongoing → ambient (distant weeping on the wind, distant bugle, soft rain, underwater).
- One-shot physical events → sfx (slide, footsteps, bangs, bells as hits, howl, laughter).
- Do not put the same event in both fields.
- Do not put character dialogue or speech manner into either field.

## Format
- Short comma-separated tokens for audio_payload.
- You may refine or correct heuristic_* when they are wrong or incomplete; ignore them when they invent.

JSON only:
{"labels":[{"id":"s1_b1","ambient":"rain, distant traffic","sfx":"door slam"}]}
""";

    public static Dictionary<string, (string Ambient, string Sfx)> ParseLabels(string raw)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        raw = StripFences(raw);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.TryGetProperty("labels", out var l))
                arr = l;
            else
                arr = default;
            if (arr.ValueKind != JsonValueKind.Array) return map;
            foreach (var el in arr.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var a = el.TryGetProperty("ambient", out var aEl) ? aEl.GetString() ?? "" : "";
                var s = el.TryGetProperty("sfx", out var sEl) ? sEl.GetString() ?? "" : "";
                map[id] = (NormalizeList(a), NormalizeList(s));
            }
        }
        catch { /* retry */ }
        return map;
    }

    /// <summary>Jaccard of comma/space tokens; empty vs empty = 1.</summary>
    public static double TokenJaccard(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 && tb.Count == 0) return 1.0;
        if (ta.Count == 0 || tb.Count == 0) return 0.0;
        var inter = ta.Intersect(tb, StringComparer.OrdinalIgnoreCase).Count();
        var union = ta.Union(tb, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 1.0 : (double)inter / union;
    }

    public static HashSet<string> Tokens(string? s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return set;
        // Lowercase before splitting: NormalizeList() below relies on this to produce a
        // deterministic tag casing regardless of how the AI model happened to capitalize its
        // response (models are inconsistent call-to-call — "Rain" vs "RAIN" vs "rain").
        var lower = s.ToLowerInvariant();
        foreach (var part in TokenSplitRegex.Split(lower))
        {
            var t = part.Trim().Trim('.', ' ');
            if (t.Length < 2) continue;
            // also split single commas already handled; keep multiword phrases as one token
            set.Add(t);
        }
        if (set.Count == 0)
        {
            foreach (var w in lower.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (w.Length >= 3) set.Add(w.Trim());
        }
        return set;
    }

    private static void Apply(Dictionary<string, object?> beat, string ambient, string sfx)
    {
        beat["ambient"] = ambient;
        beat["sfx"] = sfx;
        if (beat.TryGetValue("audio", out var a) && a is Dictionary<string, object?> audio)
        {
            audio["ambient"] = ambient;
            audio["sfx"] = sfx;
        }
    }

    private static List<Target> CollectBeats(Dictionary<string, object?> stage1)
    {
        var list = new List<Target>();
        foreach (var (si, bi, _, beat) in ClassifierBeatEnumerator.EnumerateSceneBeats(stage1))
        {
            var ve = beat.TryGetValue("visual_event", out var v) ? v?.ToString()?.Trim() ?? "" : "";
            if (ve.Length == 0) continue;
            list.Add(new Target { Id = $"s{si}_b{bi}", VisualEvent = ve, Beat = beat });
        }
        return list;
    }

    private static string NormalizeList(string s) =>
        string.Join(", ", Tokens(s).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    private static string StripFences(string raw) => ClassifierJsonParser.StripFences(raw);

    // Token-accurate now (was raw character count) — see PromptTokenizer.
    private static string Trunc(string s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);
    private static string Trim(string s, int n)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        return s.Length <= n ? s : s[..n] + "…";
    }

    private sealed class Target
    {
        public required string Id { get; init; }
        public string VisualEvent { get; init; } = "";
        public required Dictionary<string, object?> Beat { get; init; }
        public string HeuristicAmbient { get; set; } = "";
        public string HeuristicSfx { get; set; } = "";
    }
}

public sealed class AmbientSfxClassifyResult : ClassifierRunResultBase
{
    public int BeatCount { get; set; }

    public Dictionary<string, object?> ToMetaDict() => new()
    {
        ["enabled"] = Enabled,
        ["prompt_version"] = PromptVersion,
        ["model"] = Model,
        ["beats"] = BeatCount,
        ["ai_labels"] = AiCount,
        ["heuristic_fallback"] = FallbackCount,
        ["chat_calls"] = ChatCalls,
        ["note"] = Note,
    };
}
