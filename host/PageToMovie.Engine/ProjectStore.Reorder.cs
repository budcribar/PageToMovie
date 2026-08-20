using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;

namespace PageToMovie.Engine;

/// <summary>One media file rename, project-relative with forward slashes (e.g.
/// "assets/video/scene_01_clip_02.mp4" → "…_03.mp4").</summary>
public sealed record MediaRenameEntry(string From, string To);

/// <summary>
/// What a reorder changed on disk, for callers that mirror the change elsewhere: the media
/// registry (server) and the user's local media folder (via the committed rename manifest).
/// </summary>
public sealed record ProjectReorderResult(
    IReadOnlyList<MediaRenameEntry> MediaRenames,
    IReadOnlyList<string> MediaDeletes,
    long ManifestId);

public sealed partial class ProjectStore
{
    /// <summary>
    /// Append-only rename log at the project root. Committed to the project's git repo so any
    /// clone/fork/collaborator's LOCAL media folder can replay renames it missed (the server never
    /// holds the .mp4 bytes, so it cannot rename them for the client). Entries are never rewritten.
    /// </summary>
    public const string MediaRenamesManifestFileName = "media_renames.jsonl";

    /// <summary>Temp suffix for the two-phase rename: phase 1 moves old → FINAL+marker, phase 2
    /// strips the marker. A crash between phases is healed by the sweep at the start of the next
    /// pass (stripping the marker completes the interrupted rename).</summary>
    private const string RenumberTmpSuffix = ".renumtmp";

    /// <summary>
    /// Reorder (and renumber) the clips of one scene. <paramref name="order"/> lists the CURRENT
    /// clip numbers in their new sequence; after the call the clip listed first is C01, the second
    /// C02, … (numbers become contiguous — "number = order = filename"). Renames every
    /// number-keyed artifact (sidecars/takes, client markers, history, trash, QA verifications),
    /// deletes the scene composite + its sources list (order changed ⇒ stale), appends the rename
    /// manifest entry, and commits the whole pass as one git commit.
    /// </summary>
    public ProjectReorderResult ReorderClips(string projectId, int scene, IReadOnlyList<int> order, string? author = null)
    {
        var bpPath = FindBlueprintPathSync(projectId)
                     ?? throw new InvalidOperationException("Shot plan (blueprint) not found — cannot reorder clips.");
        var (root, scenes) = ParseBlueprintScenes(bpPath);
        var clips = FindSceneClipsArray(scenes, scene)
                    ?? throw new InvalidOperationException($"Scene {scene} not found in shot plan.");

        var existing = clips.OfType<JsonObject>().Select(ClipKeying.ClipNumber).ToList();
        ValidatePermutation(order, existing, "clip");

        // old number → new number (position in the requested order, 1-based).
        var map = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++)
            if (order[i] != i + 1) map[order[i]] = i + 1;

        var alreadyOrdered = clips.OfType<JsonObject>().Select(ClipKeying.ClipNumber).SequenceEqual(order);
        if (map.Count == 0 && alreadyOrdered)
            return new ProjectReorderResult(Array.Empty<MediaRenameEntry>(), Array.Empty<string>(), 0);

        // Blueprint: put the array in the requested order and stamp the new contiguous numbers.
        var byNumber = clips.OfType<JsonObject>().ToDictionary(ClipKeying.ClipNumber);
        var reordered = order.Select(n => byNumber[n]).ToList();
        clips.Clear();
        for (var i = 0; i < reordered.Count; i++)
        {
            var node = reordered[i];
            node[JsonKeys.ClipNumber] = i + 1;
            if (node.ContainsKey("clip_index")) node["clip_index"] = i + 1;
            clips.Add(node);
        }
        File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");

        var projectDir = GetProjectDir(projectId);

        // Extend-source markers hold the PREVIOUS clip's tail — wrong once the neighbor changes.
        var deletes = new List<string>();
        foreach (var old in map.Keys)
        {
            foreach (var ext in new[] { ".json", ".mp4" })
            {
                var rel = $"{StoreLit.Assets}/{StoreLit.Video}/_extend_src_s{scene:D2}c{old:D2}{ext}";
                if (TryDeleteProjectFile(projectDir, rel)) deletes.Add(rel);
            }
        }

        var renames = RenameNumberKeyedFiles(projectDir, name => MapClipFileName(name, scene, map));
        FixRenamedJsonContents(projectDir, renames);
        deletes.AddRange(DeleteSceneCompositeArtifacts(projectDir, scene));

        var manifestId = AppendRenameManifest(projectDir, new JsonObject
        {
            ["op"] = "reorder_clips",
            ["scene"] = scene,
        }, renames, deletes);

        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        TriggerAutoGitCommit(projectId,
            $"Reorder clips in scene S{scene:D2}: [{string.Join(",", order)}] → [1..{order.Count}]", author);
        return new ProjectReorderResult(renames, deletes, manifestId);
    }

    /// <summary>
    /// Reorder (and renumber) whole scenes. <paramref name="order"/> lists the CURRENT scene
    /// numbers in their new sequence. The Fountain screenplay is the scene-order authority for
    /// replans, so its scene chunks are permuted too — the reorder is refused when the screenplay
    /// and shot plan disagree on the scene count (fix/replan first; silently diverging them would
    /// hide the problem). An end-credits scene must stay last.
    /// </summary>
    public ProjectReorderResult ReorderScenes(string projectId, IReadOnlyList<int> order, string? author = null)
    {
        var bpPath = FindBlueprintPathSync(projectId)
                     ?? throw new InvalidOperationException("Shot plan (blueprint) not found — cannot reorder scenes.");
        var (root, scenes) = ParseBlueprintScenes(bpPath);

        var sceneNodes = scenes.OfType<JsonObject>().ToList();
        var existing = sceneNodes.Select(s => ReadJsonNodeInt(s[JsonKeys.SceneNumber])).ToList();
        ValidatePermutation(order, existing, "scene");

        var byNumber = sceneNodes.ToDictionary(s => ReadJsonNodeInt(s[JsonKeys.SceneNumber]));
        var isCredits = sceneNodes.ToDictionary(
            s => ReadJsonNodeInt(s[JsonKeys.SceneNumber]),
            s => IsCreditsScene(s.Deserialize<JsonElement>()));
        for (var i = 0; i < order.Count - 1; i++)
            if (isCredits[order[i]])
                throw new InvalidOperationException("The end-credits scene must stay last.");

        var map = new Dictionary<int, int>();
        for (var i = 0; i < order.Count; i++)
            if (order[i] != i + 1) map[order[i]] = i + 1;

        var alreadyOrdered = existing.SequenceEqual(order);
        if (map.Count == 0 && alreadyOrdered)
            return new ProjectReorderResult(Array.Empty<MediaRenameEntry>(), Array.Empty<string>(), 0);

        // Screenplay first: it can refuse (count mismatch) and must do so BEFORE files move.
        ReorderFountainScenes(projectId, order, isCredits);

        scenes.Clear();
        for (var i = 0; i < order.Count; i++)
        {
            var node = byNumber[order[i]];
            node[JsonKeys.SceneNumber] = i + 1;
            scenes.Add(node);
        }
        File.WriteAllText(bpPath, root.ToJsonString(JsonDefaults.Indented) + "\n");

        var projectDir = GetProjectDir(projectId);
        var renames = RenameNumberKeyedFiles(projectDir, name => MapSceneFileName(name, map));
        FixRenamedJsonContents(projectDir, renames);

        // Composite bytes are still that scene's video after a pure renumber, but the sources list
        // references the clips' OLD filenames — delete it so the composite is rebuilt-on-demand.
        var deletes = new List<string>();
        foreach (var newNumber in map.Values)
        {
            var rel = $"{StoreLit.Assets}/{StoreLit.Video}/scene_{newNumber:D2}.mp4.sources.json";
            if (TryDeleteProjectFile(projectDir, rel)) deletes.Add(rel);
        }

        var manifestId = AppendRenameManifest(projectDir, new JsonObject { ["op"] = "reorder_scenes" }, renames, deletes);

        InvalidateSceneListCache(projectId);
        InvalidateReadCaches(projectId);
        TriggerAutoGitCommit(projectId,
            $"Reorder scenes: [{string.Join(",", order)}] → [1..{order.Count}]", author);
        return new ProjectReorderResult(renames, deletes, manifestId);
    }

    private static void ValidatePermutation(IReadOnlyList<int> order, IReadOnlyList<int> existing, string what)
    {
        if (order.Count == 0)
            throw new InvalidOperationException($"No {what} order given.");
        if (order.Distinct().Count() != order.Count)
            throw new InvalidOperationException($"Duplicate {what} number in the requested order.");
        var a = order.OrderBy(n => n);
        var b = existing.OrderBy(n => n);
        if (!a.SequenceEqual(b))
            throw new InvalidOperationException(
                $"Requested {what} order [{string.Join(",", order)}] is not a permutation of the existing {what}s [{string.Join(",", existing.OrderBy(n => n))}].");
    }

    // ---- filename mapping ------------------------------------------------------------------

    private static readonly Regex SceneClipNameRegex = new(
        @"^scene_(\d{2,})_clip_(\d{2,})(?=[._])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SceneOnlyNameRegex = new(
        @"^scene_(\d{2,})(?=[._])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExtendSrcNameRegex = new(
        @"^_extend_src_s(\d{2,})c(\d{2,})(?=\.)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>New file name for a clip renumber within one scene, or null when untouched.</summary>
    internal static string? MapClipFileName(string fileName, int scene, IReadOnlyDictionary<int, int> map)
    {
        var m = SceneClipNameRegex.Match(fileName);
        if (!m.Success) return null;
        if (int.Parse(m.Groups[1].Value) != scene) return null;
        if (!map.TryGetValue(int.Parse(m.Groups[2].Value), out var newClip)) return null;
        return $"scene_{scene:D2}_clip_{newClip:D2}{fileName[m.Length..]}";
    }

    /// <summary>New file name for a scene renumber, or null when untouched.</summary>
    internal static string? MapSceneFileName(string fileName, IReadOnlyDictionary<int, int> map)
    {
        var ext = ExtendSrcNameRegex.Match(fileName);
        if (ext.Success)
        {
            return map.TryGetValue(int.Parse(ext.Groups[1].Value), out var ns)
                ? $"_extend_src_s{ns:D2}c{int.Parse(ext.Groups[2].Value):D2}{fileName[ext.Length..]}"
                : null;
        }
        var m = SceneOnlyNameRegex.Match(fileName);
        if (!m.Success) return null;
        return map.TryGetValue(int.Parse(m.Groups[1].Value), out var newScene)
            ? $"scene_{newScene:D2}{fileName[m.Length..]}"
            : null;
    }

    // ---- the rename pass -------------------------------------------------------------------

    /// <summary>Every directory that can hold scene/clip-number-keyed files.</summary>
    private static IEnumerable<string> NumberKeyedDirs(string projectDir)
    {
        var video = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Video);
        var music = Path.Combine(projectDir, StoreLit.Assets, StoreLit.Music);
        yield return video;
        yield return Path.Combine(video, StoreLit.History);
        yield return Path.Combine(video, StoreLit.TrashDir);
        yield return Path.Combine(projectDir, StoreLit.Assets, "qa");
        yield return music;
        yield return Path.Combine(music, StoreLit.History);
        yield return Path.Combine(music, StoreLit.TrashDir);
        yield return Path.Combine(projectDir, StoreLit.Assets, "audio", "revoice");
        yield return Path.Combine(projectDir, StoreLit.Assets, StoreLit.Scenes);
    }

    /// <summary>
    /// Rename every number-keyed file per <paramref name="mapName"/> (null = leave alone), two-phase
    /// so swap cycles (C02↔C03) never collide: old → final+tmp-marker, then strip the marker. Starts
    /// by sweeping any marker left by an interrupted earlier pass. Returns the project-relative
    /// renames (forward slashes), old path → new path.
    /// </summary>
    private static List<MediaRenameEntry> RenameNumberKeyedFiles(string projectDir, Func<string, string?> mapName)
    {
        var renames = new List<MediaRenameEntry>();
        foreach (var dir in NumberKeyedDirs(projectDir))
        {
            if (!Directory.Exists(dir)) continue;

            // Heal an interrupted pass: the tmp name IS the final name + marker.
            foreach (var tmp in Directory.EnumerateFiles(dir, "*" + RenumberTmpSuffix).ToList())
            {
                var final = tmp[..^RenumberTmpSuffix.Length];
                if (!File.Exists(final)) File.Move(tmp, final);
            }

            var phase2 = new List<(string TmpPath, string FinalPath, string FromName, string ToName)>();
            foreach (var path in Directory.EnumerateFiles(dir).ToList())
            {
                var name = Path.GetFileName(path);
                var newName = mapName(name);
                if (newName is null || string.Equals(newName, name, StringComparison.Ordinal)) continue;
                var finalPath = Path.Combine(dir, newName);
                var tmpPath = finalPath + RenumberTmpSuffix;
                File.Move(path, tmpPath);
                phase2.Add((tmpPath, finalPath, name, newName));
            }
            foreach (var (tmpPath, finalPath, fromName, toName) in phase2)
            {
                File.Move(tmpPath, finalPath);
                var relDir = Path.GetRelativePath(projectDir, dir).Replace('\\', '/');
                renames.Add(new MediaRenameEntry($"{relDir}/{fromName}", $"{relDir}/{toName}"));
            }
        }
        return renames;
    }

    /// <summary>
    /// Renamed sidecars and QA verifications carry their scene/clip numbers INSIDE the JSON too —
    /// rewrite those fields to match the new file name so content and name never disagree.
    /// </summary>
    private static void FixRenamedJsonContents(string projectDir, IReadOnlyList<MediaRenameEntry> renames)
    {
        foreach (var r in renames)
        {
            var name = Path.GetFileName(r.To);
            var m = SceneClipNameRegex.Match(name);
            var sOnly = m.Success ? null : SceneOnlyNameRegex.Match(name);
            int? scene = m.Success ? int.Parse(m.Groups[1].Value)
                : sOnly is { Success: true } ? int.Parse(sOnly.Groups[1].Value) : null;
            int? clip = m.Success ? int.Parse(m.Groups[2].Value) : null;
            if (scene is null) continue;

            var path = Path.Combine(projectDir, r.To.Replace('/', Path.DirectorySeparatorChar));
            if (name.EndsWith(StoreLit.ClipJsonSuffix, StringComparison.OrdinalIgnoreCase))
                RewriteJsonNumberFields(path, ("scene", scene.Value), ("clip", clip ?? 0));
            else if (name.EndsWith("_dialogue_verification.json", StringComparison.OrdinalIgnoreCase))
                RewriteJsonNumberFields(path, ("SceneNumber", scene.Value), ("ClipNumber", clip ?? 0));
        }
    }

    private static void RewriteJsonNumberFields(string path, params (string Key, int Value)[] fields)
    {
        if (!File.Exists(path)) return;
        JsonObject? obj;
        try { obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return; } // unreadable sidecar: leave as-is, the name is authoritative
        if (obj is null) return;
        foreach (var (key, value) in fields)
        {
            // Preserve whatever casing the file already uses; reads are case-insensitive.
            var actual = obj.Select(kv => kv.Key)
                .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            obj[actual] = value;
        }
        File.WriteAllText(path, obj.ToJsonString(JsonDefaults.Indented) + "\n");
    }

    /// <summary>Clip order changed ⇒ the stitched scene video and its sources list are wrong.</summary>
    private static List<string> DeleteSceneCompositeArtifacts(string projectDir, int scene)
    {
        var deletes = new List<string>();
        foreach (var rel in new[]
        {
            $"{StoreLit.Assets}/{StoreLit.Video}/scene_{scene:D2}.mp4",
            $"{StoreLit.Assets}/{StoreLit.Video}/scene_{scene:D2}_complete.mp4",
            $"{StoreLit.Assets}/{StoreLit.Video}/scene_{scene:D2}.mp4.sources.json",
            $"{StoreLit.Assets}/{StoreLit.Scenes}/scene_{scene:D2}.mp4",
            $"{StoreLit.Assets}/{StoreLit.Scenes}/scene_{scene:D2}_complete.mp4",
        })
        {
            if (TryDeleteProjectFile(projectDir, rel)) deletes.Add(rel);
        }
        return deletes;
    }

    private static bool TryDeleteProjectFile(string projectDir, string relPath)
    {
        var path = Path.Combine(projectDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ---- rename manifest ---------------------------------------------------------------------

    /// <summary>Append one manifest entry; returns its id (1 + the last entry's id).</summary>
    private static long AppendRenameManifest(
        string projectDir, JsonObject header, IReadOnlyList<MediaRenameEntry> renames, IReadOnlyList<string> deletes)
    {
        var path = Path.Combine(projectDir, MediaRenamesManifestFileName);
        var id = LastManifestId(path) + 1;
        header["id"] = id;
        header["at_utc"] = DateTime.UtcNow.ToString("o");
        header["renames"] = new JsonArray(renames
            .Select(r => (JsonNode)new JsonObject { ["from"] = r.From, ["to"] = r.To }).ToArray());
        header["deletes"] = new JsonArray(deletes.Select(d => (JsonNode)JsonValue.Create(d)).ToArray());
        File.AppendAllText(path, header.ToJsonString() + "\n"); // one compact JSON line per entry
        return id;
    }

    private static long LastManifestId(string path)
    {
        if (!File.Exists(path)) return 0;
        long last = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject o && o["id"] is { } idNode)
                    last = Math.Max(last, idNode.GetValue<long>());
            }
            catch (JsonException) { /* skip corrupt line */ }
        }
        return last;
    }

    /// <summary>Manifest entries with id &gt; <paramref name="afterId"/> (raw JSON lines, oldest first).</summary>
    public IReadOnlyList<JsonObject> ReadRenameManifest(string projectId, long afterId = 0)
    {
        var path = Path.Combine(GetProjectDir(projectId), MediaRenamesManifestFileName);
        var entries = new List<JsonObject>();
        if (!File.Exists(path)) return entries;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject o
                    && o["id"] is { } idNode && idNode.GetValue<long>() > afterId)
                    entries.Add(o);
            }
            catch (JsonException) { /* skip corrupt line */ }
        }
        return entries;
    }

    /// <summary>
    /// Fountain scene chunks permuted to the requested order. Refuses (throws) when the screenplay's
    /// heading count disagrees with the shot plan's scene count — reordering only one of them would
    /// silently diverge plan and script. A blueprint-only trailing credits scene (no Fountain heading)
    /// is tolerated: it has no chunk to move and stays last by validation.
    /// </summary>
    private void ReorderFountainScenes(string projectId, IReadOnlyList<int> order, IReadOnlyDictionary<int, bool> isCredits)
    {
        var draftPath = GetScreenplayPath(projectId);
        if (!File.Exists(draftPath))
            throw new InvalidOperationException("Screenplay draft not found — cannot reorder scenes.");
        var text = File.ReadAllText(draftPath);
        var chunks = SplitFountainSceneChunks(text, out var prefix);

        var nonCreditsOrder = order.Where(n => !isCredits[n]).ToList();
        if (chunks.Count != nonCreditsOrder.Count && chunks.Count != order.Count)
            throw new InvalidOperationException(
                $"Screenplay has {chunks.Count} scenes but the shot plan has {order.Count} — regenerate the shot plan (or fix the screenplay) before reordering scenes.");

        var effective = chunks.Count == order.Count ? order : nonCreditsOrder;
        var newText = prefix + string.Concat(effective.Select(n => chunks[n - 1]));
        File.WriteAllText(draftPath, newText);
    }

    /// <summary>
    /// Split the Fountain draft into per-scene chunks at scene-heading lines (auto INT/EXT headings
    /// after a blank line, and forced ".HEADING" lines), mirroring FountainParser's numbering:
    /// scene N = the Nth heading. Text before the first heading (title page) is the prefix.
    /// </summary>
    internal static List<string> SplitFountainSceneChunks(string text, out string prefix)
    {
        var lines = text.Split('\n');
        var starts = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r').Trim();
            if (trimmed.Length == 0) continue;
            var prevBlank = i == 0 || string.IsNullOrWhiteSpace(lines[i - 1].TrimEnd('\r'));
            if (!prevBlank) continue;
            var forced = trimmed.Length > 1 && trimmed[0] == '.' && char.IsLetterOrDigit(trimmed[1]);
            var next = i + 1 < lines.Length ? lines[i + 1].TrimEnd('\r') : "";
            var nextOk = string.IsNullOrWhiteSpace(next) || next.TrimStart().StartsWith('='); // blank / page-tag / synopsis
            if (forced || (PageToMovie.Fountain.FountainLexer.IsSceneHeadingStart(trimmed) && nextOk))
                starts.Add(i);
        }

        // Guard against drift from the real parser: the counts must agree or we refuse to split.
        var parsedCount = PageToMovie.Fountain.FountainParser.Parse(text).Elements
            .Count(e => e.Type == PageToMovie.Fountain.FountainParser.ElementType.SceneHeading);
        if (starts.Count != parsedCount)
            throw new InvalidOperationException(
                $"Screenplay scene detection disagrees with the parser ({starts.Count} vs {parsedCount}) — cannot safely reorder scenes in the screenplay text.");

        prefix = starts.Count == 0 ? text : string.Join('\n', lines[..starts[0]]) + (starts[0] > 0 ? "\n" : "");
        var chunks = new List<string>();
        for (var c = 0; c < starts.Count; c++)
        {
            var end = c + 1 < starts.Count ? starts[c + 1] : lines.Length;
            var chunk = string.Join('\n', lines[starts[c]..end]);
            if (end < lines.Length) chunk += "\n";
            chunks.Add(chunk);
        }
        return chunks;
    }
}
