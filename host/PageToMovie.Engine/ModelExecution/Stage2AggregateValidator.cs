using System.Text.Json;

namespace PageToMovie.Engine.ModelExecution;

public sealed record Stage2ClassifierProvenance(
    string Classifier,
    string Source,
    int? Attempts,
    int? ModelResults,
    int? FallbackResults,
    string? Model);

public sealed record Stage2AggregateManifest(
    string SchemaVersion,
    IReadOnlyList<Stage2ClassifierProvenance> Classifiers,
    IReadOnlyList<ModelOperationTrace> Operations,
    IReadOnlyList<ModelValidationIssue> ValidationIssues);

public static class Stage2AggregateValidator
{
    public const string SchemaVersion = "stage2-aggregate.v1";

    public static IReadOnlyList<ModelValidationIssue> Validate(Dictionary<string, object?> plan)
    {
        var issues = new List<ModelValidationIssue>();
        var knownCharacters = GetDict(GetDict(plan, "global_production_variables"), "character_seed_tokens").Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sceneNumbers = new HashSet<int>();
        var scenes = GetList(plan, "scenes").OfType<Dictionary<string, object?>>().ToList();
        for (var sceneIndex = 0; sceneIndex < scenes.Count; sceneIndex++)
            ValidateScene(scenes[sceneIndex], sceneIndex, knownCharacters, sceneNumbers, issues);
        return issues;
    }

    private static void ValidateScene(
        Dictionary<string, object?> scene,
        int sceneIndex,
        HashSet<string> knownCharacters,
        HashSet<int> sceneNumbers,
        List<ModelValidationIssue> issues)
    {
        var scenePath = $"$.scenes[{sceneIndex}]";
        var sceneNumber = ToInt(Value(scene, "scene_number"));
        if (sceneNumber <= 0 || !sceneNumbers.Add(sceneNumber))
            issues.Add(new("invalid_scene_reference", "Scene numbers must be unique positive integers.", scenePath + ".scene_number"));

        var sceneCast = Strings(GetList(scene, "characters_on_screen"));
        ValidateKnownCharacters(sceneCast, knownCharacters, scenePath + ".characters_on_screen", issues);
        var clips = GetList(scene, "veo_clips").OfType<Dictionary<string, object?>>().ToList();
        if (clips.Count == 0)
            issues.Add(new("missing_clips", "Every planned scene must contain at least one clip.", scenePath + ".veo_clips"));

        var beatMap = Strings(GetList(scene, "stage1_beat_map"));
        // Single credits predicate (scene/clip is_credits or CREDITS heading/setting) so a
        // credits card is exempt from the beat/clip 1:1 rule however it is marked.
        var credits = ProjectStore.IsCreditsScene(scene);
        if (!credits && beatMap.Count != clips.Count)
            issues.Add(new("beat_clip_mismatch", "Stage 1 beat references must map one-to-one to planned clips.", scenePath + ".stage1_beat_map"));

        var clipNumbers = new HashSet<int>();
        string? priorLocation = null;
        for (var clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            priorLocation = ValidateClip(clips[clipIndex], clipIndex, scenePath, sceneCast, knownCharacters, clipNumbers, priorLocation, issues);
    }

    private static string ValidateClip(
        Dictionary<string, object?> clip,
        int clipIndex,
        string scenePath,
        List<string> sceneCast,
        HashSet<string> knownCharacters,
        HashSet<int> clipNumbers,
        string? priorLocation,
        List<ModelValidationIssue> issues)
    {
        var clipPath = $"{scenePath}.veo_clips[{clipIndex}]";
        var clipNumber = ToInt(Value(clip, "clip_number") ?? Value(clip, "clip_index"));
        if (clipNumber <= 0 || !clipNumbers.Add(clipNumber))
            issues.Add(new("invalid_clip_reference", "Clip numbers must be unique positive integers within a scene.", clipPath + ".clip_number"));

        var clipCast = Strings(GetList(clip, "characters_on_screen"));
        ValidateKnownCharacters(clipCast, knownCharacters, clipPath + ".characters_on_screen", issues);
        ValidateClipCastMembership(clipCast, sceneCast, clipPath, issues);
        ValidateClipFocusAndPrimary(clip, clipCast, clipPath, issues);
        ValidateAudio(clip, clipCast, clipPath, issues);
        var location = Text(Value(clip, "location_id"));
        ValidateClipContinuity(clip, clipIndex, location, priorLocation, clipPath, issues);
        return location;
    }

    private static void ValidateClipCastMembership(
        List<string> clipCast, List<string> sceneCast, string clipPath, List<ModelValidationIssue> issues)
    {
        foreach (var character in clipCast.Where(character => !sceneCast.Contains(character, StringComparer.OrdinalIgnoreCase)))
            issues.Add(new("clip_cast_not_in_scene", $"Clip character '{character}' is absent from the scene cast.", clipPath + ".characters_on_screen"));
    }

    private static void ValidateClipFocusAndPrimary(
        Dictionary<string, object?> clip, List<string> clipCast, string clipPath, List<ModelValidationIssue> issues)
    {
        foreach (var focus in Strings(GetList(clip, "focus_keys")).Where(focus => !clipCast.Contains(focus, StringComparer.OrdinalIgnoreCase)))
            issues.Add(new("focus_not_on_screen", $"Focus character '{focus}' is absent from the clip cast.", clipPath + ".focus_keys"));

        var primary = Text(Value(clip, "primary_subject"));
        if (primary.StartsWith("Character_", StringComparison.Ordinal) && !clipCast.Contains(primary, StringComparer.OrdinalIgnoreCase))
            issues.Add(new("primary_subject_not_on_screen", $"Primary subject '{primary}' is absent from the clip cast.", clipPath + ".primary_subject"));
    }

    private static void ValidateClipContinuity(
        Dictionary<string, object?> clip,
        int clipIndex,
        string location,
        string? priorLocation,
        string clipPath,
        List<ModelValidationIssue> issues)
    {
        var continuation = Text(Value(clip, "veo_continuation_source"));
        if (continuation.Equals("extend_previous", StringComparison.OrdinalIgnoreCase) &&
            (clipIndex == 0 || !string.Equals(location, priorLocation, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new("invalid_continuity_reference", "extend_previous requires a preceding clip at the same location.", clipPath + ".veo_continuation_source"));
    }

    public static IReadOnlyList<Stage2ClassifierProvenance> BuildClassifierProvenance(
        Dictionary<string, object?>? enrichments)
    {
        if (enrichments is null) return [];
        return enrichments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => FromMeta(pair.Key, pair.Value as Dictionary<string, object?>))
            .ToArray();
    }

    public static async Task WriteManifestAsync(
        string projectDir,
        IReadOnlyList<Stage2ClassifierProvenance> classifiers,
        IReadOnlyList<ModelOperationTrace> operations,
        IReadOnlyList<ModelValidationIssue> issues,
        CancellationToken ct)
    {
        var dir = Path.Combine(projectDir, "artifacts", "model_operations");
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new Stage2AggregateManifest(SchemaVersion, classifiers, operations, issues),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + "\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "stage2_aggregate.lifecycle.json"), json, ct).ConfigureAwait(false);
    }

    private static Stage2ClassifierProvenance FromMeta(string name, Dictionary<string, object?>? meta)
    {
        if (meta is null) return new(name, "not_exposed", null, null, null, null);
        var ai = NullableInt(meta, "ai_labels");
        var fallback = NullableInt(meta, "heuristic_fallback");
        var enabled = Value(meta, "enabled") is true;
        string source;
        if (!enabled)
            source = "disabled";
        else if (ai > 0 && fallback > 0)
            source = "mixed";
        else if (ai > 0)
            source = "model";
        else if (fallback > 0)
            source = "deterministic_fallback";
        else
            source = "no_targets";
        return new(name, source, NullableInt(meta, "attempts"), ai, fallback, Text(Value(meta, "model")));
    }

    private static void ValidateAudio(Dictionary<string, object?> clip, IReadOnlyList<string> clipCast, string path, List<ModelValidationIssue> issues)
    {
        var audio = GetDict(clip, "audio_payload");
        var dialogue = Text(Value(audio, "dialogue"));
        var speaker = Text(Value(audio, "speaker"));
        var delivery = Text(Value(audio, "delivery"));
        if (!string.IsNullOrWhiteSpace(dialogue) && (string.IsNullOrWhiteSpace(speaker) || delivery is "" or "none"))
            issues.Add(new("incomplete_dialogue_reference", "Spoken dialogue requires both a speaker and delivery mode.", path + ".audio_payload"));
        if (!string.IsNullOrWhiteSpace(dialogue) && Stage2PlannerService.IsOnCameraDelivery(delivery) && !clipCast.Contains(speaker, StringComparer.OrdinalIgnoreCase))
            issues.Add(new("speaker_not_on_screen", $"On-camera speaker '{speaker}' is absent from the clip cast.", path + ".audio_payload.speaker"));
    }

    private static void ValidateKnownCharacters(IEnumerable<string> characters, HashSet<string> known, string path, List<ModelValidationIssue> issues)
    {
        foreach (var character in characters.Where(character => character.StartsWith("Character_", StringComparison.Ordinal) && !known.Contains(character)))
            issues.Add(new("unknown_cast_reference", $"Character '{character}' has no cast seed.", path));
    }
    private static Dictionary<string, object?> GetDict(Dictionary<string, object?> source, string key) => Value(source, key) as Dictionary<string, object?> ?? new();
    private static List<object?> GetList(Dictionary<string, object?> source, string key) => Value(source, key) as List<object?> ?? [];
    private static object? Value(Dictionary<string, object?> source, string key) => source.TryGetValue(key, out var value) ? value : null;
    private static List<string> Strings(IEnumerable<object?> values) => values.Select(Text).Where(value => value.Length > 0).ToList();
    private static string Text(object? value) => value?.ToString()?.Trim() ?? "";
    private static int ToInt(object? value) => value switch { int i => i, long l => (int)l, JsonElement e when e.TryGetInt32(out var i) => i, _ when int.TryParse(Text(value), out var i) => i, _ => 0 };
    private static int? NullableInt(Dictionary<string, object?> source, string key) => source.ContainsKey(key) ? ToInt(Value(source, key)) : null;
}
