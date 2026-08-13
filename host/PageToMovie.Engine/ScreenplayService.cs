using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Adaptation;
using PageToMovie.Fountain;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Adaptation.Conversion;
using AdaptationFountain = PageToMovie.Adaptation.Conversion.BookToFountainConverter;

namespace PageToMovie.Engine;

/// <summary>
/// Fountain draft lifecycle: load/save, create from book/import, sign-off.
/// Operator source of truth is <c>source/screenplay.fountain</c>.
/// Shot planning reads Fountain directly (in-memory beat model) — no scenes.json step.
/// Canonical file: source/screenplay.fountain (+ source/screenplay_meta.json).
/// </summary>
public static class ScreenplayService
{
    public const string CanonicalFileName = "screenplay.fountain";
    /// <summary>Immutable full-length base kept so Trim can re-derive cheaply without re-import (Track D).</summary>
    public const string MaxBaseFileName = "screenplay.max.fountain";
    public const string MetaFileName = "screenplay_meta.json";
    /// <summary>Optional cast seed cache (plates / voice edits) under source/.</summary>
    public const string CastSeedsFileName = "cast_seeds.json";
    private const string SourceDir = "source";

    public sealed class ScreenplayDoc
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string Text { get; init; } = "";
        public ScreenplayStatus Status { get; init; } = new();
    }

    public sealed class SaveResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public ScreenplayStatus Status { get; init; } = new();
        public string? Message { get; set; }
    }

    public sealed class SignOffResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? Title { get; init; }
        public int SceneCount { get; init; }
        public int CharacterCount { get; init; }
        public int LocationCount { get; init; }
        public bool HashChanged { get; init; }
        public ScreenplayStatus Status { get; init; } = new();
        public string? Message { get; init; }
    }

    private sealed class MetaDto
    {
        public string? SignedHash { get; set; }
        public string? SignedAt { get; set; }
        public string? LastSavedHash { get; set; }
        public string? LastSavedAt { get; set; }
    }

    public static string GetDraftPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), SourceDir, CanonicalFileName);

    public static string GetMetaPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), SourceDir, MetaFileName);

    public static string GetCastSeedsPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), SourceDir, CastSeedsFileName);

    /// <summary>
    /// Parse Fountain into the in-memory screenplay model used by Stage 2 / cast tooling
    /// (same shape as the old stage1.v1 dict, never written to disk for planning). Optional bounds
    /// (typically from <see cref="ClipDurationEstimator.ResolveBoundsForModel"/>) clamp monologue
    /// pre-splitting against the actually-selected video model's limits; omitted, unchanged behavior.
    /// </summary>
    public static Dictionary<string, object?> BuildModelFromFountainText(
        string fountainText,
        int minSeconds = ClipDurationEstimator.MinSeconds,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int absMaxSeconds = ClipDurationEstimator.AbsMaxSeconds)
    {
        fountainText ??= "";
        var parsed = FountainParser.Parse(fountainText);
        var doc = FountainStage1Importer.BuildStage1(parsed, minSeconds, maxSeconds, absMaxSeconds);
        return Stage1Normalizer.Normalize(doc);
    }

    /// <summary>
    /// Load project Fountain and build the in-memory screenplay model.
    /// Returns null if there is no draft.
    /// </summary>
    public static Dictionary<string, object?>? TryBuildModelFromProject(ProjectStore store, string projectId)
    {
        EnsureCanonicalDraft(store, projectId);
        var path = GetDraftPath(store, projectId);
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return BuildModelFromFountainText(text);
    }

    /// <summary>Summarise Fountain into Stage1Status (UI / readiness). No scenes.json.</summary>
    public static Stage1Status StatusFromFountainModel(
        Dictionary<string, object?>? model,
        string? fountainPath = null)
    {
        var status = new Stage1Status
        {
            ScenesFile = fountainPath is null ? CanonicalFileName : Path.GetFileName(fountainPath),
        };
        if (model is null)
            return status;

        status.Present = true;
        status.MovieTitle = TryGetModelString(model, "movie_title");
        status.SourceBookTitle = TryGetModelString(model, "source_book_title");
        status.RuntimeSeconds = TryGetRuntimeSeconds(model);
        TryApplyFountainMtime(status, fountainPath);
        TryApplyProductionVariables(status, model);
        TryApplyScenes(status, model);
        return status;
    }

    private static string? TryGetModelString(Dictionary<string, object?> model, string key) =>
        model.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static double? TryGetRuntimeSeconds(Dictionary<string, object?> model)
    {
        if (!model.TryGetValue("cumulative_duration_target_seconds", out var rt) || rt is null)
            return null;
        return rt switch
        {
            int i => i,
            long l => l,
            double d => d,
            _ => double.TryParse(rt.ToString(), out var x) ? x : null,
        };
    }

    private static void TryApplyFountainMtime(Stage1Status status, string? fountainPath)
    {
        if (fountainPath is null || !File.Exists(fountainPath))
            return;
        try { status.Mtime = File.GetLastWriteTime(fountainPath).ToString("yyyy-MM-dd HH:mm:ss"); }
        catch { /* ignore */ }
    }

    private static void TryApplyProductionVariables(Stage1Status status, Dictionary<string, object?> model)
    {
        if (!model.TryGetValue("global_production_variables", out var gpvObj) ||
            gpvObj is not Dictionary<string, object?> gpv)
            return;
        ApplyCharacterSeeds(status, gpv);
        ApplyLocationSeeds(status, gpv);
    }

    private static void ApplyCharacterSeeds(Stage1Status status, Dictionary<string, object?> gpv)
    {
        if (!gpv.TryGetValue("character_seed_tokens", out var chars) ||
            chars is not Dictionary<string, object?> charDict)
            return;
        status.CharacterCount = charDict.Count;
        foreach (var (key, val) in charDict)
            status.CastNames.Add(ResolveCastDisplayName(key, val));
    }

    private static string ResolveCastDisplayName(string key, object? val)
    {
        var display = key.Replace("Character_", "").Replace("_", " ");
        if (val is Dictionary<string, object?> seed &&
            seed.TryGetValue("canonical_given_name", out var cn) &&
            cn is string cname && cname.Length > 0)
            return cname;
        if (val is Dictionary<string, object?> seed2 &&
            seed2.TryGetValue("voice_label", out var vl) &&
            vl is string lab && lab.Length > 0)
            return lab;
        return display;
    }

    private static void ApplyLocationSeeds(Stage1Status status, Dictionary<string, object?> gpv)
    {
        if (gpv.TryGetValue("location_seed_tokens", out var locs) &&
            locs is Dictionary<string, object?> locDict)
            status.LocationCount = locDict.Count;
    }

    private static void TryApplyScenes(Stage1Status status, Dictionary<string, object?> model)
    {
        if (!model.TryGetValue("scenes", out var scenesObj) || scenesObj is not List<object?> scenes)
            return;
        foreach (var s in scenes.OfType<Dictionary<string, object?>>())
        {
            var row = BuildSceneRow(s);
            status.BeatCount += row.BeatCount;
            status.Scenes.Add(row);
        }
        status.SceneCount = status.Scenes.Count;
        status.Scenes = status.Scenes.OrderBy(x => x.SceneNumber).ToList();
    }

    private static Stage1SceneRow BuildSceneRow(Dictionary<string, object?> s)
    {
        var beats = 0;
        if (s.TryGetValue("story_beats", out var sb) && sb is List<object?> beatList)
            beats = beatList.Count;
        return new Stage1SceneRow
        {
            SceneNumber = s.TryGetValue("scene_number", out var sne) ? ToInt(sne) : 0,
            Setting = s.TryGetValue("setting", out var set) ? set?.ToString() ?? "" : "",
            BeatCount = beats,
            DurationSeconds = TryGetSceneDuration(s),
        };
    }

    private static double? TryGetSceneDuration(Dictionary<string, object?> s)
    {
        if (!s.TryGetValue("duration_target_seconds", out var d) &&
            !s.TryGetValue("estimated_duration_seconds", out d))
            return null;
        if (d is double dd) return dd;
        if (d is int di) return di;
        if (double.TryParse(d?.ToString(), out var dx)) return dx;
        return null;
    }

    private static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var n) => n,
        _ => 0,
    };

    public static string ComputeHash(string text)
    {
        // Approval / dirty hash must ignore pipeline-only stamps (Draft date on every SaveDraft)
        // and match SaveDraft transforms (scene heading unify).
        var normalized = NormalizeForApprovalHash(text);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Pre-2026 hash: NormalizeText only (kept so old SignedHash still validates).</summary>
    public static string ComputeHashLegacy(string text)
    {
        var normalized = NormalizeText(text);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Stable body for sign-off and dirty detection. Drops title-page fields the app rewrites
    /// without user edits (Draft date / Date / Last saved) and unifies scene heading wording.
    /// </summary>
        public static string NormalizeForApprovalHash(string text)
    {
        text = NormalizeText(text);
        text = AdaptationFountain.NormalizeSceneHeadingWording(text);
        var lines = text.Split('\n');
        var kept = new System.Collections.Generic.List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trim = line.TrimStart();
            if (trim.StartsWith("Draft date:", StringComparison.OrdinalIgnoreCase)
                || trim.StartsWith("Date:", StringComparison.OrdinalIgnoreCase)
                || trim.StartsWith("Last saved:", StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(line);
        }
        var joined = string.Join("\n", kept);
        if (joined.Length > 0 && !joined.EndsWith('\n'))
            joined += "\n";
        return joined;
    }

public static string NormalizeText(string text)
    {
        text ??= "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // Drop book page tags — fidelity uses UI match, not script annotations
        text = AdaptationFountain.StripBookPageTags(text);
        if (text.Length > 0 && !text.EndsWith('\n'))
            text += "\n";
        return text;
    }

    /// <summary>Read Fountain draft + sign-off status. Pass Stage1 from GetAdaptationStatus to avoid re-reading.</summary>
    public static ScreenplayStatus ReadStatus(ProjectStore store, string projectId, Stage1Status stage1)
    {
        // Surface imported .fountain files that never got the canonical name
        try { EnsureCanonicalDraft(store, projectId); } catch { /* status still useful */ }

        var draftPath = GetDraftPath(store, projectId);
        var meta = ReadMeta(store, projectId);
        var status = new ScreenplayStatus();

        if (File.Exists(draftPath))
        {
            var text = File.ReadAllText(draftPath);
            var hash = ComputeHash(text);
            var fi = new FileInfo(draftPath);
            status.DraftExists = true;
            status.DraftBytes = fi.Length;
            status.DraftHash = hash;
            status.DraftMtime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            var parsed = FountainParser.Parse(text);
            status.SceneHeadingCount = parsed.Elements.Count(e => e.Type == FountainParser.ElementType.SceneHeading);
            if (parsed.TitlePage.TryGetValue("Title", out var t) && !string.IsNullOrWhiteSpace(t))
                status.Title = t.Replace("\n", " ").Trim();
            else if (parsed.TitlePage.TryGetValue("title", out t) && !string.IsNullOrWhiteSpace(t))
                status.Title = t.Replace("\n", " ").Trim();
        }

        status.SignedHash = meta.SignedHash;
        status.SignedAt = meta.SignedAt;

        if (status.DraftExists)
        {
            // DraftHash uses v2 (approval-stable). Also accept legacy v1 hashes so existing
            // projects do not all flip to "Edited since approval" after the hash change.
            var matchesV2 = !string.IsNullOrEmpty(meta.SignedHash) &&
                            string.Equals(meta.SignedHash, status.DraftHash, StringComparison.OrdinalIgnoreCase);
            var matchesLegacy = !string.IsNullOrEmpty(meta.SignedHash) &&
                                string.Equals(meta.SignedHash, ComputeHashLegacy(File.ReadAllText(draftPath)), StringComparison.OrdinalIgnoreCase);
            status.Signed = matchesV2 || matchesLegacy;
            // Dirty only after a real prior approval that no longer matches the draft.
            // Never-approved drafts are not "edited since approval".
            status.Dirty = !string.IsNullOrEmpty(meta.SignedHash) && !status.Signed;
        }
        else
        {
            status.Signed = false;
            status.Dirty = false;
        }

        // Ready when approved Fountain has scenes (Stage 2 reads Fountain only).
        status.ReadyForShots =
            status.DraftExists && status.Signed && status.SceneHeadingCount > 0;

        return status;
    }

    public static ScreenplayDoc Get(ProjectStore store, string projectId)
    {
        // Prefer canonical draft; if missing, adopt any source/*.fountain from import
        EnsureCanonicalDraft(store, projectId);
        var draftPath = GetDraftPath(store, projectId);
        var stage1 = ReadStage1Lite(store, projectId);
        var status = ReadStatus(store, projectId, stage1);
        var text = File.Exists(draftPath) ? File.ReadAllText(draftPath) : "";
        return new ScreenplayDoc
        {
            Ok = true,
            Text = text,
            Status = status,
        };
    }

    /// <summary>
    /// If screenplay.fountain is missing, copy the newest source/*.fountain (or project root *.fountain)
    /// into the canonical path so the editor has something to load after import.
    /// </summary>
    public static bool EnsureCanonicalDraft(ProjectStore store, string projectId)
    {
        var draftPath = GetDraftPath(store, projectId);
        if (File.Exists(draftPath) && new FileInfo(draftPath).Length > 0)
            return false;

        var projectDir = store.GetProjectDir(projectId);
        var sourceDir = Path.Combine(projectDir, SourceDir);
        Directory.CreateDirectory(sourceDir);

        var recoveredPath = FindNewestRecoverableScreenplay(projectDir, sourceDir);
        if (recoveredPath is null)
            return false;

        var text = NormalizeText(File.ReadAllText(recoveredPath));
        File.WriteAllText(draftPath, text);
        var meta = ReadMeta(store, projectId);
        // Hash the bytes we actually wrote (normalized), not the pre-normalize source.
        meta.LastSavedHash = ComputeHash(text);
        meta.LastSavedAt = DateTime.UtcNow.ToString("o");
        // Do not auto-approve here — Stage 1 can exist from book import before the user
        // has reviewed Fountain. Sign-off is an explicit user action.
        WriteMeta(store, projectId, meta);
        return true;
    }

    /// <summary>
    /// Newest non-canonical fountain/spmd under source/ or the project root, or null when none exist.
    /// </summary>
    private static string? FindNewestRecoverableScreenplay(string projectDir, string sourceDir)
    {
        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var path in EnumerateRecoverableScreenplayFiles(projectDir, sourceDir))
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length == 0) continue;
                if (fi.LastWriteTimeUtc >= bestTime)
                {
                    bestTime = fi.LastWriteTimeUtc;
                    best = path;
                }
            }
            catch (Exception)
            {
                // skip unreadable files
            }
        }
        return best;
    }

    private static IEnumerable<string> EnumerateRecoverableScreenplayFiles(string projectDir, string sourceDir)
    {
        if (Directory.Exists(sourceDir))
        {
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*.fountain").Where(IsRecoverableScreenplayName))
                yield return f;
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*.spmd").Where(IsRecoverableScreenplayName))
                yield return f;
        }
        foreach (var f in Directory.EnumerateFiles(projectDir, "*.fountain").Where(IsRecoverableScreenplayName))
            yield return f;
    }

    private static bool IsRecoverableScreenplayName(string path)
    {
        var name = Path.GetFileName(path);
        return !name.Equals(CanonicalFileName, StringComparison.OrdinalIgnoreCase) &&
               !name.Equals(MaxBaseFileName, StringComparison.OrdinalIgnoreCase);
    }

    public static SaveResult SaveDraft(ProjectStore store, string projectId, string text)
    {
        text = NormalizeText(text ?? "");
        // Unify drifted same-place headings before they seed location_seed_tokens
        text = AdaptationFountain.NormalizeSceneHeadingWording(text);
        // Do NOT FixDraftDate on every save — stamping "today" changed the file after
        // approval and falsely set Dirty / "Edited since approval". Date is set at
        // draft creation / import only (CreateDraftFromBookAsync, ImportAsDraft).
        var sourceDir = Path.Combine(store.GetProjectDir(projectId), SourceDir);
        Directory.CreateDirectory(sourceDir);
        var draftPath = GetDraftPath(store, projectId);
        File.WriteAllText(draftPath, text);

        var hash = ComputeHash(text);
        var meta = ReadMeta(store, projectId);
        meta.LastSavedHash = hash;
        meta.LastSavedAt = DateTime.UtcNow.ToString("o");
        WriteMeta(store, projectId, meta);
        store.TriggerAutoGitCommit(projectId, "Save screenplay draft");

        var stage1 = ReadStage1Lite(store, projectId);
        var status = ReadStatus(store, projectId, stage1);
        return new SaveResult
        {
            Ok = true,
            Status = status,
            Message = status.Dirty
                ? "Draft saved — approve when ready"
                : "Draft saved",
        };
    }

    /// <summary>Outcome of a Fountain → Fountain descriptive edit of the current draft.</summary>
    public sealed class DraftEditResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        /// <summary>True when the edit was applied and saved (false = kept original).</summary>
        public bool Applied { get; init; }
        public int SceneCountBefore { get; init; }
        public int SceneCountAfter { get; init; }
        public string? Message { get; init; }
        public ScreenplayStatus? Status { get; init; }
    }

    /// <summary>
    /// Re-skin the current editable draft to a visual medium (descriptive layer only) and save it
    /// as the new draft when the scene structure is preserved. Non-destructive to story/dialogue —
    /// see <see cref="AdaptationService.ReskinAsync"/>. Requires an approved/imported draft to exist.
    /// </summary>
    public static Task<DraftEditResult> ReskinDraftAsync(
        ProjectStore store, string projectId, string? visualMedium,
        PageToMovie.Core.Abstractions.IChatClient chat, string model = "",
        Action<string>? onProgress = null, CancellationToken ct = default,
        XaiResponsesClient? responses = null, BookTextRegistryService? bookRegistry = null,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions = null,
        bool useFakes = false) =>
        RunReskinDraft(new DescriptiveDraftEditArgs(store, projectId, visualMedium, chat, model, onProgress, ct, responses, bookRegistry, bookFileSessions, useFakes));

    /// <summary>
    /// Enrich the current editable draft's descriptive layer for the stored medium, grounding in the
    /// prepared book text when present, and save it when the scene structure is preserved. Story and
    /// dialogue are untouched — see <see cref="AdaptationService.EmbellishAsync"/>.
    /// </summary>
    public static Task<DraftEditResult> EmbellishDraftAsync(
        ProjectStore store, string projectId, string? visualMedium,
        PageToMovie.Core.Abstractions.IChatClient chat, string model = "",
        Action<string>? onProgress = null, CancellationToken ct = default,
        XaiResponsesClient? responses = null, BookTextRegistryService? bookRegistry = null,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions = null,
        bool useFakes = false) =>
        RunEmbellishDraft(new DescriptiveDraftEditArgs(store, projectId, visualMedium, chat, model, onProgress, ct, responses, bookRegistry, bookFileSessions, useFakes));

    /// <summary>Path to the immutable full-length base (may not exist until the first trim / a max generation).</summary>
    public static string GetMaxBasePath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), SourceDir, MaxBaseFileName);

    /// <summary>
    /// True when a full-length base exists for this project (Track D0/D6). A fork inherits this file with
    /// the copied project, so a forker can re-fit to their own length/budget without paying to regenerate.
    /// </summary>
    public static bool HasMaxBase(ProjectStore store, string projectId)
    {
        try
        {
            var p = GetMaxBasePath(store, projectId);
            return File.Exists(p) && new FileInfo(p).Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Persist the full-length base that Trim derives from (Track D0). Written when a screenplay is
    /// generated at natural/max length; overwrites any prior base so a fresh generation resets it.
    /// </summary>
    public static void WriteMaxBase(ProjectStore store, string projectId, string fountain)
    {
        if (string.IsNullOrWhiteSpace(fountain)) return;
        try
        {
            var path = GetMaxBasePath(store, projectId);
            if (Path.GetDirectoryName(path) is { } dir)
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, NormalizeText(fountain));
        }
        catch { /* base is an optimization; trim can re-seed from the draft if missing */ }
    }

    /// <summary>
    /// Trim the screenplay toward the project's current target runtime and save the result as the working
    /// draft. Trims from the immutable full-length base (<see cref="MaxBaseFileName"/>), seeding that base
    /// from the current draft on first use, so re-trimming to a different target never compounds. See
    /// <see cref="AdaptationService.TrimAsync"/>.
    /// </summary>
    public static async Task<DraftEditResult> TrimDraftAsync(
        ProjectStore store,
        string projectId,
        PageToMovie.Core.Abstractions.IChatClient chat,
        string model = "",
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        XaiResponsesClient? responses = null,
        BookTextRegistryService? bookRegistry = null,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions = null,
        bool useFakes = false)
    {
        // Establish / read the full-length base to trim from (never the already-trimmed working draft).
        var basePath = GetMaxBasePath(store, projectId);
        string baseFountain;
        if (File.Exists(basePath))
        {
            baseFountain = await File.ReadAllTextAsync(basePath, ct).ConfigureAwait(false);
        }
        else
        {
            baseFountain = Get(store, projectId).Text;
            if (string.IsNullOrWhiteSpace(baseFountain))
                return new DraftEditResult { Ok = false, Error = "No screenplay draft to trim yet." };
            if (Path.GetDirectoryName(basePath) is { } dir)
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(basePath, baseFountain, ct).ConfigureAwait(false);
        }

        var runtime = await FilmRuntime.ResolveAsync(store, projectId, ct: ct).ConfigureAwait(false);
        var target = runtime.TargetMinutes > 0 ? runtime.TargetMinutes : runtime.NaturalMinutes;
        var natural = runtime.NaturalMinutes > 0 ? runtime.NaturalMinutes : target;

        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        model = await ResolvePlanningModelAsync(store, projectId, model, "Fit screenplay to length", ct).ConfigureAwait(false);
        var progress = onProgress is null ? null : new Progress<string>(onProgress);
        var viaFiles = FilesCompleter(
            responses, bookRegistry, bookFileSessions, useFakes,
            projectId, projectDir, baseFountain, bookText: null, model, onProgress,
            ScreenplayEnrichFiles.TrimInstruction(target, natural), attachBook: false, label: "Fit length");
        var result = await AdaptationService.TrimAsync(baseFountain, target, natural, chat, model, progress, ct, viaFiles)
            .ConfigureAwait(false);

        return ApplyDraftEdit(store, projectId, result,
            appliedMessage: $"Trimmed the screenplay toward ~{target} min ({result.SceneCountAfter} scenes).",
            substep: ProjectStore.BookSubstepKeys.FitLength, substepTargetMinutes: target);
    }

    private enum DescriptiveEditKind { Look, Enrich }

    private readonly record struct DescriptiveDraftEditArgs(
        ProjectStore Store,
        string ProjectId,
        string? VisualMedium,
        PageToMovie.Core.Abstractions.IChatClient Chat,
        string Model,
        Action<string>? OnProgress,
        CancellationToken Ct,
        XaiResponsesClient? Responses,
        BookTextRegistryService? BookRegistry,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? BookFileSessions,
        bool UseFakes);

    private static Task<DraftEditResult> RunReskinDraft(DescriptiveDraftEditArgs a) =>
        RunFullLengthDescriptiveEditAsync(a, DescriptiveEditKind.Look);

    private static Task<DraftEditResult> RunEmbellishDraft(DescriptiveDraftEditArgs a) =>
        RunFullLengthDescriptiveEditAsync(a, DescriptiveEditKind.Enrich);

    private readonly record struct DescriptiveEditSpec(
        string EmptyError,
        string PlanningOp,
        string Instruction,
        string Label,
        string Substep,
        bool LoadBook);

    private static DescriptiveEditSpec SpecFor(DescriptiveEditKind kind) =>
        kind == DescriptiveEditKind.Look
            ? new(
                "No screenplay draft to re-skin yet.",
                "Re-skin screenplay",
                ScreenplayEnrichFiles.ReskinInstruction,
                "Look",
                ProjectStore.BookSubstepKeys.Look,
                LoadBook: false)
            : new(
                "No screenplay draft to enrich yet.",
                "Enrich screenplay",
                ScreenplayEnrichFiles.EnrichInstruction,
                "Enrich",
                ProjectStore.BookSubstepKeys.Enrich,
                LoadBook: true);

    /// <summary>
    /// Shared setup for full-length descriptive edits (re-skin / enrich): read the max base,
    /// optionally ground in book text, resolve the planning model, then run and save.
    /// </summary>
    private static async Task<DraftEditResult> RunFullLengthDescriptiveEditAsync(
        DescriptiveDraftEditArgs a,
        DescriptiveEditKind kind)
    {
        var spec = SpecFor(kind);
        var current = ReadFullLengthSource(a.Store, a.ProjectId);
        if (string.IsNullOrWhiteSpace(current))
            return new DraftEditResult { Ok = false, Error = spec.EmptyError };

        var projectDir = await a.Store.GetProjectDirAsync(a.ProjectId, a.Ct).ConfigureAwait(false);
        string? bookText = null;
        if (spec.LoadBook)
        {
            var bookPath = Path.Combine(projectDir, SourceDir, "book_full.txt");
            if (File.Exists(bookPath))
            {
                try { bookText = await File.ReadAllTextAsync(bookPath, a.Ct).ConfigureAwait(false); }
                catch (IOException) { bookText = null; }
            }
        }

        var model = await ResolvePlanningModelAsync(a.Store, a.ProjectId, a.Model, spec.PlanningOp, a.Ct)
            .ConfigureAwait(false);
        var progress = a.OnProgress is null ? null : new Progress<string>(a.OnProgress);
        var viaFiles = FilesCompleter(
            a.Responses, a.BookRegistry, a.BookFileSessions, a.UseFakes,
            a.ProjectId, projectDir, current, bookText, model, a.OnProgress,
            spec.Instruction, attachBook: spec.LoadBook, spec.Label);
        var result = await RunDescriptiveEditAsync(
                kind, current, a.VisualMedium, a.Chat, bookText, model, progress, viaFiles, a.Ct)
            .ConfigureAwait(false);

        var applied = kind == DescriptiveEditKind.Look
            ? $"Re-applied the look to the screenplay ({result.SceneCountAfter} scenes)."
            : $"Enriched the screenplay ({result.SceneCountAfter} scenes).";
        return ApplyDraftEdit(a.Store, a.ProjectId, result,
            appliedMessage: applied, updateBase: true, substep: spec.Substep);
    }

    private static Task<AdaptationService.FountainEditResult> RunDescriptiveEditAsync(
        DescriptiveEditKind kind,
        string current,
        string? visualMedium,
        PageToMovie.Core.Abstractions.IChatClient chat,
        string? bookText,
        string model,
        IProgress<string>? progress,
        Func<string, string, CancellationToken, Task<string?>>? viaFiles,
        CancellationToken ct) =>
        kind == DescriptiveEditKind.Look
            ? AdaptationService.ReskinAsync(current, visualMedium, chat, model, progress, ct, viaFiles)
            : AdaptationService.EmbellishAsync(current, visualMedium, chat, bookText, model, progress, ct, viaFiles);

    static Func<string, string, CancellationToken, Task<string?>>? FilesCompleter(
        XaiResponsesClient? responses,
        BookTextRegistryService? bookRegistry,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions,
        bool useFakes,
        string projectId,
        string projectDir,
        string screenplay,
        string? bookText,
        string model,
        Action<string>? onProgress,
        string instruction,
        bool attachBook,
        string label,
        string screenplayKind = ProjectXaiArtifactFiles.KindScreenplayMax,
        string screenplayFilename = "screenplay.max.fountain")
    {
        if (responses is null) return null;
        var deps = new ScreenplayEnrichFiles.Deps(responses, bookRegistry, bookFileSessions, useFakes);
        return async (system, chunk, token) =>
        {
            try
            {
                var oneScene = !string.IsNullOrWhiteSpace(chunk)
                    && !string.Equals(chunk, screenplay, StringComparison.Ordinal);
                var resolvedInstruction = oneScene
                    ? "Book file attached (if any). Enrich THIS ONE scene only. Keep the exact heading. " +
                      "Dialogue unchanged. Return only this scene — no other INT./EXT. headings.\n\n" + chunk
                    : instruction;
                return await ScreenplayEnrichFiles.TryCompleteAsync(
                    deps, projectId, projectDir,
                    oneScene ? null : screenplay,
                    bookText, system, resolvedInstruction, model,
                    onProgress, token,
                    attachBook: attachBook,
                    requireScreenplay: !oneScene,
                    screenplayKind, screenplayFilename, label)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"{label} via files failed, falling back to inlined chat: " + ex.Message);
                return null;
            }
        };
    }

    /// <summary>
    /// Resolve the project's Script &amp; planning (chat) model for a fountain edit. Uses the caller's
    /// explicit id when given, else the project's configured planning model — never an empty id, which the
    /// chat client rejects with "model is required".
    /// </summary>
    private static async Task<string> ResolvePlanningModelAsync(
        ProjectStore store, string projectId, string model, string op, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return model;
        var cfg = await store.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        return ProjectModelSelection.RequirePlanning(cfg, op);
    }

    /// <summary>
    /// The full-length source to run a full-length edit (enrich / re-skin) on: the immutable base if it
    /// exists, else the current working draft (which then establishes the base). Never the trimmed draft.
    /// </summary>
    private static string ReadFullLengthSource(ProjectStore store, string projectId)
    {
        var basePath = GetMaxBasePath(store, projectId);
        if (File.Exists(basePath))
        {
            try
            {
                var text = File.ReadAllText(basePath);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch { /* fall back to the working draft */ }
        }
        return Get(store, projectId).Text;
    }

    /// <param name="updateBase">
    /// True for full-length edits (enrich / re-skin): also overwrite the full-length base so Fit length
    /// derives from the edited version. False for Trim, which shortens and must not touch the base.
    /// </param>
    /// <param name="substep">
    /// Book sub-step key (<see cref="ProjectStore.BookSubstepKeys"/>) to mark "done" once the pass is
    /// actually applied and saved, so the Book sub-strip can show a completion check. Null = don't mark.
    /// Only recorded on a real apply — a pass that keeps the original (structure not preserved) is not marked.
    /// </param>
    private static DraftEditResult ApplyDraftEdit(
        ProjectStore store, string projectId, AdaptationService.FountainEditResult result, string appliedMessage,
        bool updateBase = false, string? substep = null, double? substepTargetMinutes = null)
    {
        if (!result.Ok)
            return new DraftEditResult
            {
                Ok = true,
                Applied = false,
                SceneCountBefore = result.SceneCountBefore,
                SceneCountAfter = result.SceneCountAfter,
                Message = result.Warning ?? "Kept the original screenplay.",
            };

        var save = SaveDraft(store, projectId, result.Fountain);
        if (!save.Ok)
            return new DraftEditResult { Ok = false, Error = save.Error };

        if (updateBase)
            WriteMaxBase(store, projectId, result.Fountain); // full-length base = enriched / re-skinned

        if (substep is { Length: > 0 })
        {
            try { store.MarkBookSubstepDone(projectId, substep, substepTargetMinutes); }
            catch { /* completion marker is best-effort — never fail the edit over it */ }
        }

        return new DraftEditResult
        {
            Ok = true,
            Applied = true,
            SceneCountBefore = result.SceneCountBefore,
            SceneCountAfter = result.SceneCountAfter,
            Message = appliedMessage,
            Status = save.Status,
        };
    }

    /// <summary>Import Fountain text as the editable draft (does not materialise Stage 1).</summary>
    public static SaveResult ImportAsDraft(
        ProjectStore store,
        string projectId,
        string text,
        string? originalFileName = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SaveResult { Ok = false, Error = "Empty screenplay text" };

        text = AdaptationFountain.FixDraftDate(NormalizeText(text));
        var result = SaveDraft(store, projectId, text);

        // Keep a copy under the original name for reference when different
        if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            var safe = Path.GetFileName(originalFileName);
            if (!string.IsNullOrWhiteSpace(safe) &&
                !safe.Equals(CanonicalFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (!safe.EndsWith(".fountain", StringComparison.OrdinalIgnoreCase) &&
                    !safe.EndsWith(".spmd", StringComparison.OrdinalIgnoreCase))
                    safe = Path.GetFileNameWithoutExtension(safe) + ".fountain";
                var copyPath = Path.Combine(store.GetProjectDir(projectId), SourceDir, safe);
                try { File.WriteAllText(copyPath, NormalizeText(text)); } catch { /* ignore */ }
            }
        }

        result.Message = "Screenplay draft ready — review and approve on Screenplay";
        return result;
    }



    /// <summary>
    /// Per-project override if set, else the admin-global default, else null (hardcoded default
    /// on <see cref="AdaptationPromptTokens"/> applies downstream).
    /// </summary>
    private static int? ResolveSharedAdaptationInt(
        Dictionary<string, JsonElement>? cfg, string projectKey, int? adminValue) =>
        cfg is not null && cfg.TryGetValue(projectKey, out var el) &&
        el.ValueKind != JsonValueKind.Null && el.TryGetInt32(out var v)
            ? v
            : adminValue;

    /// <summary>
    /// Build screenplay draft from book_full.txt via chat (locations, dialogue, page tags).
    /// Requires a configured chat client.
    /// </summary>
    public static async Task<SaveResult> CreateDraftFromBookAsync(
        ProjectStore store,
        string projectId,
        PageToMovie.Core.Abstractions.IChatClient? chat = null,
        string model = "",
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        GenerationErrorLogger? errorLogger = null,
        string? jobId = null,
        BookTextRegistryService? bookRegistry = null,
        string? cacheUserId = null,
        int? totalRuntimeMinutes = null,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessionFactory = null,
        AdaptationDefaultsOptions? adaptationDefaults = null,
        XaiResponsesClient? responses = null,
        bool useFakes = false)
    {
        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var bookPath = Path.Combine(projectDir, SourceDir, "book_full.txt");
        if (!File.Exists(bookPath))
            return new SaveResult { Ok = false, Error = "No prepared book text yet" };

        var book = await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(book))
            return new SaveResult { Ok = false, Error = "Book text is empty" };

        book = await NormalizeBookForAdaptationAsync(book, bookPath, onProgress, ct).ConfigureAwait(false);

        Dictionary<string, JsonElement>? cfg = null;
        if (chat is not null)
            (cfg, model) = await ResolveDraftChatModelAsync(store, projectId, model, ct).ConfigureAwait(false);

        var (title, author) = ReadProjectTitleAuthor(projectDir, projectId);
        // Resolve + persist the target (Trim/Fit-length reads it) but do NOT constrain generation with it.
        var runtime = await FilmRuntime.ResolveAsync(store, projectId, book, overrideTargetMinutes: totalRuntimeMinutes, ct)
            .ConfigureAwait(false);
        // Track D0: always generate at natural/max length (null → unlimited directive). The user's target
        // only drives the Trim stage, which derives the working draft from the full-length base written below.
        int? minutes = null;
        onProgress?.Invoke(
            $"Writing the full-length screenplay (natural ~{runtime.NaturalMinutes} min); fit to your target on the next step.");
        const double generationTemperature = 0.2;

        var cache = await TryLoadAdaptationCacheAsync(
            store, projectId, projectDir, book, title, author, minutes, generationTemperature,
            model, bookRegistry, cacheUserId, onProgress, ct).ConfigureAwait(false);
        if (cache.ReusedSave is not null)
            return cache.ReusedSave;

        try
        {
            return await ConvertAndSaveDraftFromBookAsync(
                store, projectId, projectDir, book, title, author, minutes, generationTemperature,
                chat, model, cfg, cache, onProgress, ct, errorLogger, jobId,
                bookRegistry, cacheUserId, bookFileSessionFactory, adaptationDefaults,
                responses, useFakes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new SaveResult { Ok = false, Error = ex.Message };
        }
    }

    private static async Task<string> NormalizeBookForAdaptationAsync(
        string book, string bookPath, Action<string>? onProgress, CancellationToken ct)
    {
        // Persist-clean Gutenberg if still on disk (older txt imports); adapt + xAI file_id use cleaned text.
        var hadGutenberg = GutenbergCleaner.HasGutenbergHeader(book);
        if (hadGutenberg)
            book = GutenbergCleaner.StripHeaderAndFooter(book);
        book = book.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";
        if (hadGutenberg)
        {
            await File.WriteAllTextAsync(bookPath, book, ct).ConfigureAwait(false);
            onProgress?.Invoke("Stripped Project Gutenberg preamble from book text.");
        }
        return book;
    }

    private static async Task<(Dictionary<string, JsonElement>? Cfg, string Model)> ResolveDraftChatModelAsync(
        ProjectStore store, string projectId, string model, CancellationToken ct)
    {
        var cfg = await store.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        model = string.IsNullOrWhiteSpace(model)
            ? ProjectModelSelection.RequirePlanning(cfg, "Screenplay draft from book")
            : ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Screenplay draft from book");
        return (cfg, model);
    }

    private sealed class AdaptationCacheState
    {
        public BookTextIdentity? BookIdentity { get; init; }
        public string? PromptHash { get; init; }
        public string? PromptVersion { get; init; }
        public string? BehaviorVersions { get; init; }
        public SaveResult? ReusedSave { get; init; }
    }

    private static async Task<AdaptationCacheState> TryLoadAdaptationCacheAsync(
        ProjectStore store,
        string projectId,
        string projectDir,
        string book,
        string title,
        string? author,
        int? minutes,
        double generationTemperature,
        string model,
        BookTextRegistryService? bookRegistry,
        string? cacheUserId,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (bookRegistry is null || string.IsNullOrWhiteSpace(cacheUserId))
            return new AdaptationCacheState();

        var project = await store.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        var bookIdentity = await bookRegistry.RegisterAsync(
            book, cacheUserId, projectId, (project?.VisibilityMode ?? ProjectVisibility.Private).ToString(), ct).ConfigureAwait(false);
        var prompt = await AdaptationService.BuildSystemPromptAsync(minutes, ct).ConfigureAwait(false);
        var promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        var promptVersion = "book-to-fountain-" + promptHash[..12];
        var behaviorVersions = JsonSerializer.Serialize(new
        {
            title,
            author,
            totalRuntimeMinutes = minutes,
            visionMetaSchema = ProjectVisionMeta.CurrentSchemaVersion,
            cachePackageSchema = "adaptation-conversion.v1",
        });

        var state = new AdaptationCacheState
        {
            BookIdentity = bookIdentity,
            PromptHash = promptHash,
            PromptVersion = promptVersion,
            BehaviorVersions = behaviorVersions,
        };

        var cached = await bookRegistry.FindArtifactAsync(
            bookIdentity.BookId, cacheUserId, "adaptation_conversion", model,
            promptVersion, promptHash, generationTemperature, behaviorVersions, ct)
            .ConfigureAwait(false);
        if (cached is null)
            return state;

        var cachedConversion = JsonSerializer.Deserialize<ProjectAdaptationConversionResult>(cached.Content);
        if (cachedConversion is not { Fountain.Length: > 0, VisionMeta: not null })
            return state;

        onProgress?.Invoke($"Reused shared adaptation cache {cached.ArtifactId}.");
        var cachedFountain = AdaptationService.FixDraftDate(cachedConversion.Fountain);
        var cachedSave = SaveDraft(store, projectId, cachedFountain);
        if (!cachedSave.Ok)
            return new AdaptationCacheState
            {
                BookIdentity = bookIdentity,
                PromptHash = promptHash,
                PromptVersion = promptVersion,
                BehaviorVersions = behaviorVersions,
                ReusedSave = cachedSave,
            };
        WriteMaxBase(store, projectId, cachedFountain); // D0: full-length base for Trim
        store.ClearBookSubsteps(projectId); // fresh screenplay → prior Look/Enrich/Fit length no longer apply
        ProjectVisionMeta.Write(projectDir, cachedConversion.VisionMeta);
        cachedSave.Message = "Screenplay draft ready — reused shared book adaptation";
        return new AdaptationCacheState
        {
            BookIdentity = bookIdentity,
            PromptHash = promptHash,
            PromptVersion = promptVersion,
            BehaviorVersions = behaviorVersions,
            ReusedSave = cachedSave,
        };
    }

    private static async Task<SaveResult> ConvertAndSaveDraftFromBookAsync(
        ProjectStore store,
        string projectId,
        string projectDir,
        string book,
        string title,
        string? author,
        int? minutes,
        double generationTemperature,
        PageToMovie.Core.Abstractions.IChatClient? chat,
        string model,
        Dictionary<string, JsonElement>? cfg,
        AdaptationCacheState cache,
        Action<string>? onProgress,
        CancellationToken ct,
        GenerationErrorLogger? errorLogger,
        string? jobId,
        BookTextRegistryService? bookRegistry,
        string? cacheUserId,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessionFactory,
        AdaptationDefaultsOptions? adaptationDefaults,
        XaiResponsesClient? responses,
        bool useFakes)
    {
        // Stage‑1 generation goes through Adaptation façade (not a reimplementation).
        var onGate = BindStructuralGateLogger(errorLogger, projectId, jobId);
        IProgress<string>? progressAdapter = onProgress is null
            ? null
            : new Progress<string>(onProgress);

        if (chat is null || !chat.IsConfigured)
            return SaveHeuristicDraft(store, projectId, title, book, author);

        var bookSession = await TryCreateBookFileSessionAsync(
            bookFileSessionFactory, cache.BookIdentity, book, model, onProgress, ct)
            .ConfigureAwait(false);

        var result = await AdaptationService.ConvertAsync(
            new AdaptationRequest
            {
                BookText = book,
                Title = title,
                Author = author,
                TargetRuntimeMinutes = minutes,
                ModelId = model,
                Temperature = generationTemperature,
                VisualMedium = TryReadAdaptationMediumPreference(projectDir),
                MaxSpeakingCast = ResolveSharedAdaptationInt(cfg, "adaptation_max_speaking_cast", adaptationDefaults?.MaxSpeakingCast),
                MaxDialogueWords = ResolveSharedAdaptationInt(cfg, "adaptation_max_dialogue_words", adaptationDefaults?.MaxDialogueWords),
                VoMaxSentences = ResolveSharedAdaptationInt(cfg, "adaptation_vo_max_sentences", adaptationDefaults?.VoMaxSentences),
                SceneCountMin = ResolveSharedAdaptationInt(cfg, "adaptation_scene_count_min", adaptationDefaults?.SceneCountMin),
                SceneCountMax = ResolveSharedAdaptationInt(cfg, "adaptation_scene_count_max", adaptationDefaults?.SceneCountMax),
                MinAudioCuesPerScene = adaptationDefaults?.MinAudioCuesPerScene,
                MinAudioCuesAtPeak = adaptationDefaults?.MinAudioCuesAtPeak,
                BodyWordsPerMinute = adaptationDefaults?.BodyWordsPerMinute,
            },
            chat,
            progressAdapter,
            ct,
            onStructuralGateFailure: onGate,
            bookSession: bookSession).ConfigureAwait(false);

        var fountain = result.Fountain;
        var visionFromScript = BookToFountainConverter.MapVision(result.VisionMeta);
        // Cache package shape remains Engine ProjectAdaptationConversionResult for registry compatibility.
        var conversion = new ProjectAdaptationConversionResult
        {
            Fountain = fountain,
            VisionMeta = visionFromScript,
            VisionMetaStatus = BookToFountainConverter.MapStatus(result.VisionMetaStatus),
            VisionMetaError = result.VisionMetaError,
        };

        await TryRegisterConversionCacheAsync(
            result, conversion, visionFromScript, cache, bookRegistry, cacheUserId,
            model, generationTemperature, onProgress, ct).ConfigureAwait(false);

        fountain = AdaptationService.FixDraftDate(fountain);
        var save = SaveDraft(store, projectId, fountain);
        if (!save.Ok) return save;
        WriteMaxBase(store, projectId, fountain); // D0: full-length base for Trim (Fit length)
        store.ClearBookSubsteps(projectId); // fresh screenplay → prior Look/Enrich/Fit length no longer apply

        await TryWriteVisionSidecarAsync(
            projectDir, title, book, fountain, visionFromScript, chat, model, onProgress, ct)
            .ConfigureAwait(false);
        await TryWriteAdaptationReportAsync(projectDir, result, onProgress, ct).ConfigureAwait(false);
        await TryWriteConvertManifestAsync(projectDir, result, cache.BookIdentity, onProgress, ct)
            .ConfigureAwait(false);
        TryStageScreenplayCommit(store, projectId, onProgress);

        await TryAutoEnrichAfterDraftAsync(
            store, projectId, chat, model, onProgress, ct,
            responses, bookRegistry, bookFileSessionFactory, useFakes).ConfigureAwait(false);

        save.Message = "Screenplay draft ready — review and approve";
        return save;
    }

    private static Func<StructuralGateFailure, CancellationToken, Task>? BindStructuralGateLogger(
        GenerationErrorLogger? errorLogger, string projectId, string? jobId)
    {
        if (errorLogger is null)
            return null;
        return async (fail, token) =>
        {
            await errorLogger.LogAsync(new GenerationErrorRecord
            {
                ProjectId = projectId,
                JobId = jobId,
                Stage = fail.Stage,
                Model = fail.Model,
                ErrorType = fail.ErrorType,
                ErrorMessage = fail.ErrorMessage,
                Resolved = false,
                ResponseSummary = fail.ResponseSummary,
            }, token).ConfigureAwait(false);
        };
    }

    private static SaveResult SaveHeuristicDraft(
        ProjectStore store, string projectId, string title, string book, string? author)
    {
        var hFountain = AdaptationService.FixDraftDate(AdaptationService.ConvertHeuristic(title, book, author));
        var hSave = SaveDraft(store, projectId, hFountain);
        if (!hSave.Ok) return hSave;
        hSave.Message = "Screenplay draft ready — review and approve";
        return hSave;
    }

    private static async Task<PageToMovie.Core.Abstractions.IBookFileSession?> TryCreateBookFileSessionAsync(
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessionFactory,
        BookTextIdentity? bookIdentity,
        string book,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (bookFileSessionFactory is null || bookIdentity is null)
            return null;
        try
        {
            var bookSession = await bookFileSessionFactory.TryCreateAsync(
                bookIdentity.BookId, book, model, ct).ConfigureAwait(false);
            if (bookSession is { IsAvailable: true })
                onProgress?.Invoke(
                    $"Book file session ready for {bookIdentity.BookId} (xAI file_id reuse / Responses multi-turn).");
            return bookSession;
        }
        catch (Exception ex)
        {
            onProgress?.Invoke("Book file session unavailable — falling back to chat/completions: " + ex.Message);
            return null;
        }
    }

    private static string? TryReadAdaptationMediumPreference(string projectDir)
    {
        try
        {
            return ProjectVisionMeta.GetAdaptationMediumPreference(projectDir);
        }
        catch { return null; }
    }

    private static async Task TryRegisterConversionCacheAsync(
        AdaptationResult result,
        ProjectAdaptationConversionResult conversion,
        ProjectVisionMeta.Document? visionFromScript,
        AdaptationCacheState cache,
        BookTextRegistryService? bookRegistry,
        string? cacheUserId,
        string model,
        double generationTemperature,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        if (result.UsedHeuristicFallback ||
            visionFromScript is null ||
            bookRegistry is null ||
            cache.BookIdentity is null ||
            string.IsNullOrWhiteSpace(cacheUserId) ||
            cache.PromptHash is null ||
            cache.PromptVersion is null ||
            cache.BehaviorVersions is null)
            return;

        var cached = await bookRegistry.RegisterArtifactAsync(
            cache.BookIdentity.BookId, cacheUserId, "adaptation_conversion",
            JsonSerializer.Serialize(conversion), model, cache.PromptVersion, cache.PromptHash,
            generationTemperature, cache.BehaviorVersions, ct).ConfigureAwait(false);
        onProgress?.Invoke($"Saved shared adaptation cache {cached.ArtifactId}.");
    }

    private static async Task TryWriteVisionSidecarAsync(
        string projectDir,
        string title,
        string book,
        string fountain,
        ProjectVisionMeta.Document? visionFromScript,
        PageToMovie.Core.Abstractions.IChatClient chat,
        string model,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        // Medium sidecar from the same adaptation response (preferred).
        // Fallback: one structured LLM call if the model omitted the trailer.
        try
        {
            if (visionFromScript is not null)
            {
                ProjectVisionMeta.Write(projectDir, visionFromScript);
                onProgress?.Invoke($"Saved visual medium ({visionFromScript.VisualMedium}) to extract_meta");
            }
            else if (chat.IsConfigured)
            {
                onProgress?.Invoke("No VISION_META trailer in screenplay response — asking model for medium…");
                await ProjectVisionMeta.DecideAtAdaptationAsync(
                    projectDir,
                    title,
                    book,
                    fountain,
                    chat,
                    model,
                    onProgress,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception metaEx)
        {
            onProgress?.Invoke("Vision medium metadata skipped: " + metaEx.Message);
        }
    }

    private static async Task TryWriteAdaptationReportAsync(
        string projectDir,
        AdaptationResult result,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        // Optional ADAPTATION_REPORT diagnostic (v4+ prompts). Missing is normal on older prompts.
        try
        {
            if (result.AdaptationReport is not null)
            {
                await ProjectAdaptationReport.WriteAsync(projectDir, result.AdaptationReport, ct).ConfigureAwait(false);
                onProgress?.Invoke(
                    $"Saved adaptation report (source_complete={result.AdaptationReport.SourceComplete}, " +
                    $"issues={result.AdaptationReport.Issues.Count})");
            }
            else if (result.AdaptationReportStatus == AdaptationReportStatus.Malformed)
            {
                onProgress?.Invoke(
                    "Adaptation report trailer present but invalid: " +
                    (result.AdaptationReportError ?? "malformed JSON"));
            }
        }
        catch (Exception reportEx)
        {
            onProgress?.Invoke("Adaptation report skipped: " + reportEx.Message);
        }
    }

    private static async Task TryWriteConvertManifestAsync(
        string projectDir,
        AdaptationResult result,
        BookTextIdentity? bookIdentity,
        Action<string>? onProgress,
        CancellationToken ct)
    {
        // Stage‑1 convert attribution (prompt / adaptation / runtime / model).
        try
        {
            if (result.ConvertManifest is null)
                return;
            await ProjectStage1ConvertManifest.WriteAsync(
                projectDir,
                result.ConvertManifest,
                bookId: bookIdentity?.BookId,
                ct: ct).ConfigureAwait(false);
            onProgress?.Invoke(
                $"Saved Stage‑1 convert manifest (adaptation={result.ConvertManifest.AdaptationVersion}, " +
                $"mode={result.ConvertManifest.RuntimeMode}, model={result.ConvertManifest.ModelId})");
        }
        catch (Exception manifestEx)
        {
            onProgress?.Invoke("Stage‑1 convert manifest skipped: " + manifestEx.Message);
        }
    }

    private static void TryStageScreenplayCommit(
        ProjectStore store, string projectId, Action<string>? onProgress)
    {
        // Trajectory: screenplay + optional report/manifest on project git (text only).
        try
        {
            store.TriggerAutoGitCommit(projectId, ProjectStageCommits.ScreenplayCreated);
        }
        catch (Exception gitEx)
        {
            onProgress?.Invoke("Stage commit skipped: " + gitEx.Message);
        }
    }

    /// <summary>
    /// North Star: enrich once right after draft-from-book so cast extract + Stage 2 see richer
    /// action lines. Best-effort — never fails draft creation if enrich errors.
    /// </summary>
    private static async Task TryAutoEnrichAfterDraftAsync(
        ProjectStore store,
        string projectId,
        PageToMovie.Core.Abstractions.IChatClient? chat,
        string model,
        Action<string>? onProgress,
        CancellationToken ct,
        XaiResponsesClient? responses = null,
        BookTextRegistryService? bookRegistry = null,
        PageToMovie.Core.Abstractions.IBookFileSessionFactory? bookFileSessions = null,
        bool useFakes = false)
    {
        if (chat is null || !chat.IsConfigured) return;
        try
        {
            onProgress?.Invoke("Enriching screenplay with visual detail from the book…");
            string? medium = null;
            try
            {
                var dir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
                medium = ProjectVisionMeta.TryRead(dir)?.VisualMedium;
            }
            catch { /* enrich without medium */ }

            var enrich = await EmbellishDraftAsync(
                store, projectId, medium, chat, model, onProgress, ct,
                responses, bookRegistry, bookFileSessions, useFakes).ConfigureAwait(false);
            if (enrich.Ok)
                onProgress?.Invoke(enrich.Message ?? "Screenplay enriched.");
            else if (!string.IsNullOrWhiteSpace(enrich.Error))
                onProgress?.Invoke("Auto-enrich skipped: " + enrich.Error);
        }
        catch (Exception ex)
        {
            onProgress?.Invoke("Auto-enrich skipped: " + ex.Message);
        }
    }

    private static (string Title, string? Author) ReadProjectTitleAuthor(string projectDir, string projectId)
    {
        var title = projectId;
        string? author = null;
        try
        {
            var pj = Path.Combine(projectDir, "project.json");
            if (File.Exists(pj))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                    title = t.GetString() ?? title;
                else if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    title = n.GetString() ?? title;
                if (doc.RootElement.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String)
                    author = a.GetString();
            }
        }
        catch { /* ignore */ }
        return (title, author);
    }

    public static SignOffResult SignOff(ProjectStore store, string projectId, string? text = null)
    {
        // Optional body text: save first
        if (text is not null)
        {
            var save = SaveDraft(store, projectId, text);
            if (!save.Ok)
                return new SignOffResult { Ok = false, Error = save.Error };
        }

        // When no body was sent, still run SaveDraft so heading unify matches later saves.
        if (text is null)
        {
            var draftPath0 = GetDraftPath(store, projectId);
            if (!File.Exists(draftPath0))
                return new SignOffResult { Ok = false, Error = "No screenplay draft to approve" };
            var existing = File.ReadAllText(draftPath0);
            if (string.IsNullOrWhiteSpace(existing))
                return new SignOffResult { Ok = false, Error = "Screenplay draft is empty" };
            var pre = SaveDraft(store, projectId, existing);
            if (!pre.Ok)
                return new SignOffResult { Ok = false, Error = pre.Error };
        }

        var draftPath = GetDraftPath(store, projectId);
        if (!File.Exists(draftPath))
            return new SignOffResult { Ok = false, Error = "No screenplay draft to approve" };

        var draftText = File.ReadAllText(draftPath);
        if (string.IsNullOrWhiteSpace(draftText))
            return new SignOffResult { Ok = false, Error = "Screenplay draft is empty" };

        var hash = ComputeHash(draftText);
        var metaBefore = ReadMeta(store, projectId);
        var hashChanged = string.IsNullOrEmpty(metaBefore.SignedHash) ||
                          !string.Equals(metaBefore.SignedHash, hash, StringComparison.OrdinalIgnoreCase);

        // Validate Fountain has scenes (shot plan reads Fountain — no scenes.json write).
        Dictionary<string, object?> model;
        try
        {
            model = BuildModelFromFountainText(draftText);
        }
        catch (Exception ex)
        {
            return new SignOffResult { Ok = false, Error = $"Could not parse screenplay: {ex.Message}" };
        }

        var summary = StatusFromFountainModel(model, draftPath);
        if (summary.SceneCount <= 0)
            return new SignOffResult { Ok = false, Error = "Screenplay has no scenes (need INT./EXT. headings)." };

        var meta = ReadMeta(store, projectId);
        meta.SignedHash = hash;
        meta.SignedAt = DateTime.UtcNow.ToString("o");
        meta.LastSavedHash = hash;
        meta.LastSavedAt = meta.SignedAt;
        WriteMeta(store, projectId, meta);
        store.TriggerAutoGitCommit(projectId, "Approve screenplay");

        // Keep location_seed_tokens on cast_seeds in sync so GET /locations works without Stage 2.
        try { store.MergeLocationSeedsIntoCastFile(projectId); }
        catch { /* optional */ }

        var stage1 = ReadStage1Lite(store, projectId);
        var status = ReadStatus(store, projectId, stage1);

        return new SignOffResult
        {
            Ok = true,
            Title = summary.MovieTitle,
            SceneCount = summary.SceneCount,
            CharacterCount = summary.CharacterCount,
            LocationCount = summary.LocationCount,
            HashChanged = hashChanged,
            Status = status,
            Message =
                $"Screenplay approved · {summary.SceneCount} scenes · {summary.CharacterCount} cast" +
                (hashChanged ? " · update shot plan if you already built one" : ""),
        };
    }

    /// <summary>Heuristic book text → Fountain draft (offline stub path).</summary>
    public static string BookTextToFountainDraft(string title, string bookText) =>
        AdaptationService.ConvertHeuristic(title, bookText);

    private static MetaDto ReadMeta(ProjectStore store, string projectId)
    {
        var path = GetMetaPath(store, projectId);
        if (!File.Exists(path)) return new MetaDto();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MetaDto>(json, JsonDefaults.CaseInsensitive) ?? new MetaDto();
        }
        catch
        {
            return new MetaDto();
        }
    }

    private static void WriteMeta(ProjectStore store, string projectId, MetaDto meta)
    {
        var path = GetMetaPath(store, projectId);
        if (Path.GetDirectoryName(path) is { } dir)
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(meta, JsonDefaults.Indented);
        File.WriteAllText(path, json + "\n");
    }

    /// <summary>Lightweight status from Fountain draft.</summary>
    public static Stage1Status ReadStage1Lite(ProjectStore store, string projectId)
    {
        try
        {
            EnsureCanonicalDraft(store, projectId);
            var draftPath = GetDraftPath(store, projectId);
            if (File.Exists(draftPath))
            {
                var model = TryBuildModelFromProject(store, projectId);
                return StatusFromFountainModel(model, draftPath);
            }
        }
        catch { /* ignore */ }

        return new Stage1Status { Present = false };
    }
}
