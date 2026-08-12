using System.Text.Json;
using PageToMovie.Engine;
using PageToMovie.Fountain;

using PageToMovie.Core.Utils;
namespace ScreenplayBenchmark;

/// <summary>
/// Local, deterministic cross-artifact gate for the adaptation-session pilot
/// (<see cref="AdaptationSessionPilot"/>). Deliberately reuses the product's own
/// <see cref="FountainParser"/> instead of a second hand-rolled parser. Catches the exact defects a
/// prior one-shot sidecar pilot missed: silent scene-count compression, all-narration dialogue, and
/// EDL/audio records that don't reconcile with the approved Fountain.
/// </summary>
public static class AdaptationPackageValidator
{
    public sealed class ValidationReport
    {
        public string Status { get; set; } = "fail";
        public int SceneCount { get; set; }
        public int? ClipCount { get; set; }
        public List<string> Failures { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>Fast, cheap check run inline in the generate→verify→repair loop (stage 3) before any
    /// downstream stage spends money deriving EDL/cast/locations/audio from a broken screenplay.</summary>
    public static List<string> ValidateFountainOnly(string? fountainText, int sceneMin, int sceneMax)
    {
        var findings = new List<string>();
        var parsed = FountainParser.Parse(fountainText ?? "");
        var headings = parsed.Elements.Where(e => e.Type == FountainParser.ElementType.SceneHeading).ToList();

        if (headings.Count == 0)
        {
            findings.Add("No scene headings found (no INT./EXT. lines) — this is not parseable Fountain.");
            return findings; // nothing else is meaningful to check yet
        }

        // Only the under-count case is treated as a blocking finding here (this list also drives the
        // stage-3 repair loop) — a story spanning many distinct locations/eras can legitimately need
        // more scenes than the rough runtime/scene-count heuristic assumes, so an overshoot is
        // reported separately as a non-blocking warning in ValidatePackage, not fed back as a repair
        // instruction that would push the model to cut scenes that may be entirely justified.
        if (headings.Count < sceneMin)
        {
            findings.Add(
                $"Only {headings.Count} scene headings found; the target band for this runtime is " +
                $"[{sceneMin},{sceneMax}] scenes. Expand coverage of the approved beat plan — do not " +
                "compress into a short-film scene count.");
        }

        var (namedDialogueCues, totalDialogueCues) = CountDialogueCues(parsed);
        if (totalDialogueCues == 0)
        {
            findings.Add("No dialogue cues found at all — the screenplay must contain spoken dialogue, not only Action/description.");
        }
        else if (namedDialogueCues == 0)
        {
            findings.Add(
                "Every dialogue cue is NARRATOR — add direct character-to-character dialogue for scenes " +
                "where the book depicts a spoken exchange; narration/V.O. should support scenes, not replace them.");
        }

        return findings;
    }

    /// <summary>Full package validation across Fountain + EDL + cast/locations + audio plan.</summary>
    public static ValidationReport ValidatePackage(
        string? fountainText,
        string edlJson,
        string castLocationJson,
        string audioJson,
        int sceneMin,
        int sceneMax,
        string? clipPlanJson = null)
    {
        var report = new ValidationReport();
        var parsed = FountainParser.Parse(fountainText ?? "");
        var fountainHeadings = parsed.Elements
            .Where(e => e.Type == FountainParser.ElementType.SceneHeading)
            .Select(e => NormalizeHeading(e.Text))
            .ToList();
        report.SceneCount = fountainHeadings.Count;

        report.Warnings.AddRange(ValidateFountainOnly(fountainText, sceneMin, sceneMax));
        if (fountainHeadings.Count > sceneMax)
        {
            report.Warnings.Add(
                $"{fountainHeadings.Count} scene headings found, above the rough target band of " +
                $"[{sceneMin},{sceneMax}] scenes for this runtime — not necessarily wrong (a multi-location, " +
                "multi-era story can need more scenes than the heuristic assumes), but review for " +
                "over-fragmentation (one scene per paragraph rather than per location+purpose).");
        }

        List<EdlScene> edlScenes;
        try
        {
            edlScenes = ParseEdl(edlJson);
        }
        catch (Exception ex)
        {
            report.Failures.Add($"EDL JSON did not parse: {ex.Message}");
            report.Status = "fail";
            return report;
        }

        if (edlScenes.Count != fountainHeadings.Count)
        {
            report.Failures.Add(
                $"EDL has {edlScenes.Count} scene record(s) but the Fountain has {fountainHeadings.Count} " +
                "scene heading(s) — every Fountain scene must have exactly one EDL record.");
        }
        else
        {
            var mismatches = 0;
            for (var i = 0; i < edlScenes.Count; i++)
            {
                if (NormalizeHeading(edlScenes[i].Heading) != fountainHeadings[i]) mismatches++;
            }
            if (mismatches > 0)
            {
                report.Warnings.Add(
                    $"{mismatches} of {edlScenes.Count} EDL heading(s) don't textually match the Fountain " +
                    "heading at the same position — check for reordering or paraphrased headings.");
            }
        }

        (List<string> castKeys, List<string> locationKeys) = (new List<string>(), new List<string>());
        try
        {
            (castKeys, locationKeys) = ParseCastAndLocationKeys(castLocationJson);
        }
        catch (Exception ex)
        {
            report.Failures.Add($"cast/location JSON did not parse: {ex.Message}");
        }

        foreach (var scene in edlScenes)
        {
            if (!string.IsNullOrWhiteSpace(scene.LocationKey) &&
                !locationKeys.Any(k => string.Equals(k, scene.LocationKey, StringComparison.OrdinalIgnoreCase)))
            {
                report.Failures.Add($"EDL scene {scene.SceneId} references unknown location_key '{scene.LocationKey}'.");
            }
            foreach (var castRef in scene.Cast)
            {
                if (!castKeys.Any(k => k.Contains(castRef, StringComparison.OrdinalIgnoreCase) ||
                                        castRef.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    report.Warnings.Add($"EDL scene {scene.SceneId} references cast '{castRef}' not found in cast_seeds.");
                }
            }
        }

        List<string> audioSceneIds;
        try
        {
            audioSceneIds = ParseAudioSceneIds(audioJson);
        }
        catch (Exception ex)
        {
            report.Failures.Add($"Audio plan JSON did not parse: {ex.Message}");
            audioSceneIds = new List<string>();
        }

        var edlIds = edlScenes.Select(s => s.SceneId).ToList();
        var missingAudio = edlIds.Except(audioSceneIds, StringComparer.OrdinalIgnoreCase).ToList();
        var orphanAudio = audioSceneIds.Except(edlIds, StringComparer.OrdinalIgnoreCase).ToList();
        if (missingAudio.Count > 0)
            report.Failures.Add($"Audio plan is missing coverage for EDL scene(s): {string.Join(", ", missingAudio)}.");
        if (orphanAudio.Count > 0)
            report.Failures.Add($"Audio plan has record(s) for scene(s) not in the EDL: {string.Join(", ", orphanAudio)}.");

        if (!string.IsNullOrWhiteSpace(clipPlanJson))
        {
            try
            {
                var (clipSceneIds, totalClips, unresolvedDialogue, longDialogueClips) = ParseAndCheckClipPlan(clipPlanJson, edlIds, fountainText ?? "");
                report.ClipCount = totalClips;

                var missingClipScenes = edlIds.Except(clipSceneIds, StringComparer.OrdinalIgnoreCase).ToList();
                var orphanClipScenes = clipSceneIds.Except(edlIds, StringComparer.OrdinalIgnoreCase).ToList();
                if (missingClipScenes.Count > 0)
                    report.Failures.Add($"Clip plan is missing coverage for EDL scene(s): {string.Join(", ", missingClipScenes)}.");
                if (orphanClipScenes.Count > 0)
                    report.Failures.Add($"Clip plan has record(s) for scene(s) not in the EDL: {string.Join(", ", orphanClipScenes)}.");
                if (unresolvedDialogue > 0)
                    report.Warnings.Add(
                        $"{unresolvedDialogue} clip dialogue_or_vo fragment(s) don't appear verbatim in their scene's " +
                        "Fountain text — check for invented or paraphrased lines.");
                if (longDialogueClips > 0)
                    report.Warnings.Add(
                        $"{longDialogueClips} clip(s) have dialogue_or_vo over 35 words — should have been split " +
                        "across multiple clips; their estimated_duration_seconds is likely clamped, not accurate.");
            }
            catch (Exception ex)
            {
                report.Failures.Add($"Clip plan JSON did not parse: {ex.Message}");
            }
        }

        report.Status = report.Failures.Count == 0 ? "pass" : "fail";
        return report;
    }

    /// <summary>Checks clip-plan scene coverage against the EDL and, per scene, whether each clip's
    /// dialogue_or_vo fragment is actually present in that scene's Fountain text (a cheap, local
    /// substitute for a real per-clip Fountain-slice lookup — good enough to flag gross invention).</summary>
    private static (List<string> SceneIds, int TotalClips, int UnresolvedDialogueCount, int LongDialogueClips) ParseAndCheckClipPlan(
        string clipPlanJson, List<string> edlIds, string fountainText)
    {
        using var doc = JsonDocument.Parse(clipPlanJson);
        var root = doc.RootElement;
        var scenesEl = root.TryGetProperty("scenes", out var s) ? s : root;
        var sceneIds = new List<string>();
        var totalClips = 0;
        var unresolved = 0;
        var longDialogue = 0;
        var normalizedFountain = CommonRegex.Replace(fountainText, @"\s+", " ").ToLowerInvariant();
        foreach (var scene in scenesEl.EnumerateArray())
        {
            var sceneId = GetString(scene, "scene_id") ?? GetString(scene, "id") ?? "";
            if (!string.IsNullOrWhiteSpace(sceneId)) sceneIds.Add(sceneId);
            if (!scene.TryGetProperty("clips", out var clipsEl) || clipsEl.ValueKind != JsonValueKind.Array) continue;
            foreach (var clip in clipsEl.EnumerateArray())
            {
                totalClips++;
                var dialogue = GetString(clip, "dialogue_or_vo");
                if (string.IsNullOrWhiteSpace(dialogue)) continue;
                if (dialogue.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 35)
                    longDialogue++;
                var normalizedDialogue = CommonRegex.Replace(dialogue, @"\s+", " ").ToLowerInvariant().Trim();
                if (normalizedDialogue.Length > 0 && !normalizedFountain.Contains(normalizedDialogue))
                    unresolved++;
            }
        }
        return (sceneIds, totalClips, unresolved, longDialogue);
    }

    private static (int Named, int Total) CountDialogueCues(FountainParser.ParseResult parsed)
    {
        var named = 0;
        var total = 0;
        foreach (var e in parsed.Elements)
        {
            if (e.Type != FountainParser.ElementType.Character) continue;
            total++;
            if (!e.Text.Contains("NARRATOR", StringComparison.OrdinalIgnoreCase)) named++;
        }
        return (named, total);
    }

    private static string NormalizeHeading(string heading) =>
        (heading ?? "").Trim().ToUpperInvariant().Replace("  ", " ");

    private sealed record EdlScene(string SceneId, string Heading, string LocationKey, List<string> Cast);

    private static List<EdlScene> ParseEdl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var scenesEl = root.TryGetProperty("scenes", out var s) ? s : root;
        var result = new List<EdlScene>();
        foreach (var scene in scenesEl.EnumerateArray())
        {
            var sceneId = GetString(scene, "scene_id") ?? GetString(scene, "id") ?? "";
            var heading = GetString(scene, "heading") ?? "";
            var locationKey = GetString(scene, "location_key") ?? GetString(scene, "location") ?? "";
            var cast = new List<string>();
            if (scene.TryGetProperty("cast", out var castEl) && castEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in castEl.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.String) cast.Add(c.GetString() ?? "");
            }
            result.Add(new EdlScene(sceneId, heading, locationKey, cast));
        }
        return result;
    }

    internal static (List<string> CastKeys, List<string> LocationKeys) ParseCastAndLocationKeys(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var castKeys = new List<string>();
        // Tolerate a bare top-level array of character objects (no {"cast_seeds":{"characters":[...]}}
        // wrapper) — a real deviation seen from the model on at least one run, previously masked by
        // ExtractJson silently returning only the array's first element instead of the whole array;
        // now that the whole array reaches here intact, still recover the cast keys from it rather
        // than reporting zero cast coverage just because the wrapper object is missing.
        var chars = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("cast_seeds", out var cs) && cs.TryGetProperty("characters", out var charsProp) &&
              charsProp.ValueKind == JsonValueKind.Array
                ? charsProp
                : default;
        if (chars.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in chars.EnumerateArray())
            {
                var key = GetString(c, "key");
                var displayName = GetString(c, "display_name");
                if (!string.IsNullOrWhiteSpace(key)) castKeys.Add(key);
                if (!string.IsNullOrWhiteSpace(displayName)) castKeys.Add(displayName);
                if (c.TryGetProperty("wardrobe_variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in variants.EnumerateArray())
                    {
                        var vKey = GetString(v, "key");
                        if (!string.IsNullOrWhiteSpace(vKey)) castKeys.Add(vKey);
                    }
                }
            }
        }

        var locationKeys = new List<string>();
        // A bare top-level array (see the cast fallback above) has no location_bible at all — root
        // isn't an object, so TryGetProperty would throw rather than just miss; guard explicitly.
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("location_bible", out var lb) && lb.TryGetProperty("locations", out var locs) &&
            locs.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in locs.EnumerateArray())
            {
                var key = GetString(l, "key");
                if (!string.IsNullOrWhiteSpace(key)) locationKeys.Add(key);
            }
        }

        return (castKeys, locationKeys);
    }

    private static List<string> ParseAudioSceneIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var scenesEl = root.TryGetProperty("scenes", out var s) ? s : root;
        var ids = new List<string>();
        foreach (var scene in scenesEl.EnumerateArray())
        {
            var id = GetString(scene, "scene_id") ?? GetString(scene, "id");
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }
        return ids;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
