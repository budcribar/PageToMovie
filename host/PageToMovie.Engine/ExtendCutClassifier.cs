using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Labels each non-first beat as hard_cut vs extend for video continuity.
/// Baseline mirrors <see cref="Stage2PlannerService"/> ForceNone rules (public helper).
/// Writes <c>cut_decision</c> = hard_cut|extend on each beat.
/// </summary>
public sealed class ExtendCutClassifier
{
    public const string PromptVersion = "v4_staging";

    /// <summary>
    /// Written onto every beat next to <c>cut_decision</c> so the shot plan records which rule
    /// decided the beat's continuation — the AI prompt version, or <see cref="HeuristicRule"/>
    /// when the classifier was off or unavailable. Stage 2 copies it onto the clip as
    /// <c>continuity_rule</c>; <see cref="ShotPlanLint"/> reads it there to spot a plan built
    /// before the staging test existed.
    /// </summary>
    public const string CutDecisionRuleKey = "cut_decision_rule";

    /// <summary>Stamp for a beat labelled by the baseline heuristic alone.</summary>
    public const string HeuristicRule = "heuristic";

    private const string HardCut = "hard_cut";
    private const string Extend = "extend";

    private readonly IChatClient _chat;
    private readonly PageToMovieOptions _opts;
    private readonly ILogger<ExtendCutClassifier> _log;

    public ExtendCutClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<ExtendCutClassifier> log)
    {
        _chat = chat;
        _opts = opts.Value;
        _log = log;
    }

    public bool IsEnabled => _opts.ClassifyExtendCutWithChat && _chat.IsConfigured;

    public async Task<SimpleClassifyResult> ClassifyStage1Async(
        Dictionary<string, object?> stage1,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(model) ? model : _opts.ExtendCutClassifyModel;
        var result = new SimpleClassifyResult
        {
            Name = "extend_hardcut",
            PromptVersion = PromptVersion,
            Enabled = IsEnabled,
            Model = effectiveModel,
        };
        var pairs = CollectPairs(stage1);
        result.ItemCount = pairs.Count;
        ApplyHeuristicCutDecisions(pairs);

        if (!IsEnabled || pairs.Count == 0)
        {
            result.FallbackCount = pairs.Count;
            result.Note = "heuristic only";
            onProgress?.Invoke($"Extend/cut: heuristic only ({pairs.Count})");
            return result;
        }

        onProgress?.Invoke($"Classifying extend vs hard-cut for {pairs.Count} beat(s)…");
        var labeled = await ClassifyPairsWithChatAsync(pairs, result, effectiveModel, ct).ConfigureAwait(false);

        result.AiCount = labeled.Count;
        result.FallbackCount = pairs.Count - labeled.Count;
        result.Note = $"AI {labeled.Count}/{pairs.Count}";
        onProgress?.Invoke($"Extend/cut: {result.Note}");
        return result;
    }

    private static void ApplyHeuristicCutDecisions(List<Pair> pairs)
    {
        foreach (var p in pairs)
        {
            var hard = BaselineHardCut(p);
            p.Beat["cut_decision"] = hard ? HardCut : Extend;
            p.Beat[CutDecisionRuleKey] = HeuristicRule;
            if (hard)
                p.Beat["continuity"] = "new_setup";
        }
    }

    private async Task<HashSet<string>> ClassifyPairsWithChatAsync(
        List<Pair> pairs, SimpleClassifyResult result, string effectiveModel, CancellationToken ct)
    {
        var maxAttempts = Math.Clamp(_opts.SilentBeatClassifyMaxAttempts, 1, 5);
        var backoffBaseMs = Math.Max(0, _opts.SilentBeatClassifyBackoffBaseMs);
        var labeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 25;
        var chunks = new List<List<Pair>>();
        for (var offset = 0; offset < pairs.Count; offset += batchSize)
            chunks.Add(pairs.Skip(offset).Take(batchSize).ToList());

        using var sem = new SemaphoreSlim(4);
        var tasks = chunks.Select(chunk => ClassifyChunkAsync(
            chunk, result, labeled, effectiveModel, maxAttempts, backoffBaseMs, sem, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return labeled;
    }

    private async Task ClassifyChunkAsync(
        List<Pair> chunk,
        SimpleClassifyResult result,
        HashSet<string> labeled,
        string effectiveModel,
        int maxAttempts,
        int backoffBaseMs,
        SemaphoreSlim sem,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var chunkIds = chunk.Select(p => p.Id).ToList();
            var byId = chunk.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            // Mutable: shrinks to only still-missing ids so each retry re-asks fewer beats —
            // mirrors the pre-refactor hand-rolled loop exactly.
            var retry = await AiRetryPolicy.RunWithCoverageRetryAsync<string>(
                chunkIds,
                callChat: missingIds => CompleteChunkLabelsAsync(missingIds, byId, result, labeled, effectiveModel, ct),
                parseResponse: ParseLabels,
                maxAttempts,
                backoffBaseMs,
                ct,
                operationName: "stage2_extend_cut",
                promptVersion: "1",
                model: effectiveModel).ConfigureAwait(false);

            if (retry.LastError is not null)
            {
                _log.LogWarning("ExtendCut chunk failed: {Error}", retry.LastError);
                lock (labeled) { result.LastError = retry.LastError; }
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

    private async Task<string> CompleteChunkLabelsAsync(
        IReadOnlyList<string> missingIds,
        Dictionary<string, Pair> byId,
        SimpleClassifyResult result,
        HashSet<string> labeled,
        string effectiveModel,
        CancellationToken ct)
    {
        var payload = missingIds.Select(id => BuildChunkPayload(byId[id])).ToList();
        var user = "Label hard_cut vs extend for video continuity. JSON only.\n" +
                   JsonSerializer.Serialize(new { beats = payload });
        var raw = await _chat.CompleteAsync(SystemPrompt(), user, effectiveModel, 0, ct, ChatCallModes.ExtendCutClassify)
            .ConfigureAwait(false);
        lock (labeled) { result.ChatCalls++; }
        return raw;
    }

    /// <summary>
    /// Budgets match the benchmark payload (160/200 tokens), not the 40/50 this shipped with.
    /// Deciding whether a beat opens where the previous one ended is a question about the END of
    /// the previous action, and a 40-token head truncation is where that ends up getting cut off.
    /// <c>same_location</c> is sent because the prompt reasons about it.
    /// </summary>
    private static Dictionary<string, object?> BuildChunkPayload(Pair p) => new()
    {
        ["id"] = p.Id,
        ["scene"] = p.Scene,
        ["setting"] = p.Setting,
        ["prev_visual"] = Trunc(p.PrevVisual, 160),
        ["prev_speaker"] = p.PrevSpeaker,
        ["visual_event"] = Trunc(p.VisualEvent, 200),
        ["speaker"] = p.Speaker,
        ["action_class"] = p.ActionClass,
        ["same_location"] = p.SameLocation,
        ["heuristic"] = BaselineHardCut(p) ? HardCut : Extend,
    };

    private static void ApplyChunkLabels(
        Dictionary<string, string> labels, Dictionary<string, Pair> byId, HashSet<string> labeled)
    {
        foreach (var kv in labels)
        {
            if (!byId.TryGetValue(kv.Key, out var p)) continue;
            p.Beat["cut_decision"] = kv.Value;
            p.Beat[CutDecisionRuleKey] = PromptVersion;
            p.Beat["continuity"] = kv.Value == HardCut ? "new_setup" : "continuous_from_previous_beat";
            labeled.Add(kv.Key);
        }
    }

    /// <summary>
    /// Kept byte-identical to <c>host/evals/classifier_benchmarks/prompts/extend_cut/v4_staging.txt</c>
    /// so the benchmark measures what actually ships. Change both together, and bump
    /// <see cref="PromptVersion"/> with the new prompt id.
    /// </summary>
    public static string SystemPrompt() => """
You decide video continuity for an automated film pipeline (any story).
Each beat becomes one video clip. An "extend" clip is generated by continuing the PREVIOUS
clip's last frame, so nobody can be moved before this beat starts: wherever the previous beat
left the subjects is where this beat has to open.

Classes:
- extend: this beat opens exactly where the previous beat ended. Same place, same arrangement of
  who and what is where, continuous business — a gesture, a reaction, a line answering a line, a
  move already under way that keeps going.
- hard_cut: everything else. New setup, location change, big energy/action, flashback, VO after
  on-camera speech, clear scene break — and also any beat whose staging is not where the previous
  beat left it: a subject is somewhere the previous beat never showed it reach, or the frame has
  to be rearranged before this action can happen. An extend cannot teleport anyone or skip a
  journey. If the previous beat's last frame would have to change before this action starts, it
  is a hard_cut.

Check where EVERY subject the beat names is, not only the one doing something. A beat's verbs
often belong to one subject while another is merely described — standing, sitting, waiting,
lying somewhere. That description is a staging claim too, and it is the easy one to miss: the
acting subject can be perfectly continuous while a described subject has silently changed
position. Compare each named subject's place against where the previous beat left it, and treat
a described subject in a new place exactly as you would treat one that walked there.

You may correct the heuristic field.

JSON: {"labels":[{"id":"s1_b3","class":"extend"}]}
""";

    public static Dictionary<string, string> ParseLabels(string raw) =>
        ClassifierLabelParser.Parse(raw, TryParseLabelElement);

    private static (string? id, string? cls) TryParseLabelElement(JsonElement el)
    {
        var id = el.GetProperty("id").GetString();
        string? cls;
        if (el.TryGetProperty("class", out var c))
            cls = c.GetString();
        else if (el.TryGetProperty("decision", out var d))
            cls = d.GetString();
        else
            cls = null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(cls)) return (id, null);
        cls = cls.Trim().ToLowerInvariant().Replace(' ', '_');
        if (cls is HardCut or "hardcut" or "cut" or "none") cls = HardCut;
        if (cls is Extend or "continue" or "continuous") cls = Extend;
        if (cls is not (HardCut or Extend)) return (id, null);
        return (id, cls);
    }

    /// <summary>Public baseline used by eval (mirrors Stage2 ForceNone intent for same-location pairs).</summary>
    public static bool BaselineHardCut(string visual, string actionClass, bool sameLocation, bool isFirst)
    {
        if (isFirst) return true;
        if (!sameLocation) return true;
        var ac = (actionClass ?? "").ToLowerInvariant();
        if (ac is "big_action" or "establishing") return true;
        var ve = (visual ?? "").ToLowerInvariant();
        if (CommonRegex.IsMatch(ve,
                @"\b(kick|smash|punch|sprint|crash|explod|slam|throw|rocket|wide shot|establishing|flashback|back to present|cut to)\b"))
            return true;
        return false;
    }

    private static bool BaselineHardCut(Pair p) =>
        BaselineHardCut(p.VisualEvent, p.ActionClass, p.SameLocation, p.IsFirst);

    private static List<Pair> CollectPairs(Dictionary<string, object?> stage1)
    {
        var list = new List<Pair>();
        var scenes = stage1.TryGetValue("scenes", out var sObj) && sObj is List<object?> sl ? sl : new();
        var si = 0;
        foreach (var sItem in scenes)
            CollectScenePairs(sItem, list, ref si);
        return list;
    }

    private static void CollectScenePairs(object? sItem, List<Pair> list, ref int si)
    {
        if (sItem is not Dictionary<string, object?> scene) return;
        si++;
        var setting = ReadDictString(scene, "setting");
        var primary = ReadDictString(scene, "primary_location_id");
        var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl ? bl : new();
        string? prevVe = null;
        string? prevLid = null;
        string? prevSpeaker = null;
        var bi = 0;
        var first = true;
        foreach (var bItem in beats)
            TryAddBeatPair(bItem, list, si, setting, primary, ref bi, ref first, ref prevVe, ref prevLid, ref prevSpeaker);
    }

    private static void TryAddBeatPair(
        object? bItem,
        List<Pair> list,
        int si,
        string setting,
        string primary,
        ref int bi,
        ref bool first,
        ref string? prevVe,
        ref string? prevLid,
        ref string? prevSpeaker)
    {
        if (bItem is not Dictionary<string, object?> beat) return;
        bi++;
        var ve = ReadDictString(beat, "visual_event");
        var dlg = ReadDictString(beat, "dialogue");
        if (string.IsNullOrWhiteSpace(ve) && string.IsNullOrWhiteSpace(dlg)) return;
        var lid = ReadDictString(beat, "location_id", primary);
        var ac = ReadDictString(beat, "action_class");
        var speaker = ReadDictString(beat, "speaker");
        list.Add(new Pair
        {
            Id = $"s{si}_b{bi}",
            Scene = si,
            Setting = setting,
            VisualEvent = ve,
            PrevVisual = prevVe ?? "",
            Speaker = speaker,
            PrevSpeaker = prevSpeaker ?? "",
            ActionClass = ac,
            SameLocation = prevLid is null || string.Equals(prevLid, lid, StringComparison.OrdinalIgnoreCase),
            IsFirst = first,
            Beat = beat,
        });
        first = false;
        prevVe = ve;
        prevLid = lid;
        prevSpeaker = speaker;
    }

    private static string ReadDictString(Dictionary<string, object?> d, string key, string fallback = "")
    {
        if (!d.TryGetValue(key, out var v)) return fallback;
        return v?.ToString() ?? fallback;
    }

    // Token-accurate now (was raw character count) — see PromptTokenizer.
    private static string Trunc(string s, int maxTokens) => PromptTokenizer.TruncateToTokens(s, maxTokens);

    private sealed class Pair
    {
        public required string Id { get; init; }
        public int Scene { get; init; }
        public string Setting { get; init; } = "";
        public string VisualEvent { get; init; } = "";
        public string PrevVisual { get; init; } = "";
        public string Speaker { get; init; } = "";
        public string PrevSpeaker { get; init; } = "";
        public string ActionClass { get; init; } = "";
        public bool SameLocation { get; init; }
        public bool IsFirst { get; init; }
        public required Dictionary<string, object?> Beat { get; init; }
    }
}
