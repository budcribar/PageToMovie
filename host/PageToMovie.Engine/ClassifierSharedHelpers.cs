using System.Text.Json;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine.ModelBacked;

/// <summary>
/// Small value conversions shared by the Stage 2 chat classifiers (both the
/// <see cref="BeatChatClassifierBase{TItem}"/>-derived ones and the standalone ones) for coverage
/// error logging.
/// </summary>
internal static class ClassifierValueHelpers
{
    /// <summary>Coerces a loosely-typed JSON/dictionary value into an int, or null when not numeric.</summary>
    public static int? ToIntOrNull(object? val) => val switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var p) => p,
        _ => null,
    };

    /// <summary>Maps a model id to its catalog provider id (null when the model is blank).</summary>
    public static string? ResolveProvider(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : SupportedModelCatalog.Find(model)?.ProviderId;
}

/// <summary>
/// Shared Stage-1 traversal for the heuristic-baseline classifiers that walk every beat of every
/// scene assigning stable <c>s{scene}_b{beat}</c> ids.
/// </summary>
internal static class ClassifierBeatEnumerator
{
    /// <summary>
    /// Yields each beat with its 1-based scene/beat indices (both incremented per surviving
    /// dictionary entry, exactly as the classifiers' original hand-rolled loops did, so
    /// <c>s{SceneIndex}_b{BeatIndex}</c> ids are unchanged). Non-dictionary scene/beat entries and
    /// missing <c>scenes</c>/<c>story_beats</c> lists are skipped without advancing the counters.
    /// </summary>
    public static IEnumerable<(int SceneIndex, int BeatIndex, Dictionary<string, object?> Scene, Dictionary<string, object?> Beat)>
        EnumerateSceneBeats(Dictionary<string, object?> stage1)
    {
        var scenes = stage1.TryGetValue("scenes", out var sObj) && sObj is List<object?> sl ? sl : new();
        var si = 0;
        foreach (var sItem in scenes)
        {
            if (sItem is not Dictionary<string, object?> scene) continue;
            si++;
            var beats = scene.TryGetValue("story_beats", out var sb) && sb is List<object?> bl ? bl : new();
            var bi = 0;
            foreach (var bItem in beats)
            {
                if (bItem is not Dictionary<string, object?> beat) continue;
                bi++;
                yield return (si, bi, scene, beat);
            }
        }
    }
}

/// <summary>
/// Shared parser for the simple <c>{"labels":[…]}</c> (or bare-array) label responses used by the
/// heuristic-baseline classifiers (extend/cut, species kind, …).
/// </summary>
internal static class ClassifierLabelParser
{
    /// <summary>
    /// Strips fences, accepts either a bare JSON array or a <c>{"labels":[…]}</c> envelope, and folds
    /// each element into a case-insensitive map using <paramref name="extract"/>, which returns the
    /// element's id/key and normalized class (return a null/blank value to skip the element). Malformed
    /// input yields whatever was accumulated before the fault — matching each classifier's original
    /// swallow-and-return behavior.
    /// </summary>
    public static Dictionary<string, string> Parse(
        string raw,
        Func<JsonElement, (string? Key, string? Value)> extract)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        raw = ClassifierJsonParser.StripFences(raw);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.GetProperty("labels");
            foreach (var el in arr.EnumerateArray())
            {
                var (key, value) = extract(el);
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    map[key!] = value!;
            }
        }
        catch (Exception)
        {
            return map;
        }
        return map;
    }
}
