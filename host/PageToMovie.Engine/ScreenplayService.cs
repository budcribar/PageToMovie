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
        Path.Combine(store.GetProjectDir(projectId), "source", CanonicalFileName);

    public static string GetMetaPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), "source", MetaFileName);

    public static string GetCastSeedsPath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), "source", CastSeedsFileName);

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
        status.MovieTitle = model.TryGetValue("movie_title", out var mt) ? mt?.ToString() : null;
        status.SourceBookTitle = model.TryGetValue("source_book_title", out var sbt) ? sbt?.ToString() : null;
        if (model.TryGetValue("cumulative_duration_target_seconds", out var rt) && rt is not null)
        {
            status.RuntimeSeconds = rt switch
            {
                int i => i,
                long l => l,
                double d => d,
                _ => double.TryParse(rt.ToString(), out var x) ? x : null,
            };
        }

        if (fountainPath is not null && File.Exists(fountainPath))
        {
            try { status.Mtime = File.GetLastWriteTime(fountainPath).ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { /* ignore */ }
        }

        if (model.TryGetValue("global_production_variables", out var gpvObj) &&
            gpvObj is Dictionary<string, object?> gpv)
        {
            if (gpv.TryGetValue("character_seed_tokens", out var chars) &&
                chars is Dictionary<string, object?> charDict)
            {
                status.CharacterCount = charDict.Count;
                foreach (var (key, val) in charDict)
                {
                    var display = key.Replace("Character_", "").Replace("_", " ");
                    if (val is Dictionary<string, object?> seed &&
                        seed.TryGetValue("canonical_given_name", out var cn) &&
                        cn is string cname && cname.Length > 0)
                        display = cname;
                    else if (val is Dictionary<string, object?> seed2 &&
                             seed2.TryGetValue("voice_label", out var vl) &&
                             vl is string lab && lab.Length > 0)
                        display = lab;
                    status.CastNames.Add(display);
                }
            }

            if (gpv.TryGetValue("location_seed_tokens", out var locs) &&
                locs is Dictionary<string, object?> locDict)
                status.LocationCount = locDict.Count;
        }

        if (model.TryGetValue("scenes", out var scenesObj) && scenesObj is List<object?> scenes)
        {
            foreach (var s in scenes.OfType<Dictionary<string, object?>>())
            {
                var sn = s.TryGetValue("scene_number", out var sne) ? ToInt(sne) : 0;
                var beats = 0;
                if (s.TryGetValue("story_beats", out var sb) && sb is List<object?> beatList)
                    beats = beatList.Count;
                status.BeatCount += beats;
                double? dur = null;
                if (s.TryGetValue("duration_target_seconds", out var d) ||
                    s.TryGetValue("estimated_duration_seconds", out d))
                {
                    if (d is double dd) dur = dd;
                    else if (d is int di) dur = di;
                    else if (double.TryParse(d?.ToString(), out var dx)) dur = dx;
                }

                status.Scenes.Add(new Stage1SceneRow
                {
                    SceneNumber = sn,
                    Setting = s.TryGetValue("setting", out var set) ? set?.ToString() ?? "" : "",
                    BeatCount = beats,
                    DurationSeconds = dur,
                });
            }

            status.SceneCount = status.Scenes.Count;
            status.Scenes = status.Scenes.OrderBy(x => x.SceneNumber).ToList();
        }

        return status;
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
        var sourceDir = Path.Combine(projectDir, "source");
        Directory.CreateDirectory(sourceDir);

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        void Consider(string path)
        {
            if (!File.Exists(path)) return;
            var name = Path.GetFileName(path);
            if (name.Equals(CanonicalFileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(MaxBaseFileName, StringComparison.OrdinalIgnoreCase))
                return; // canonical draft / immutable full-length base are not recovery candidates
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length == 0) return;
                if (fi.LastWriteTimeUtc >= bestTime)
                {
                    bestTime = fi.LastWriteTimeUtc;
                    best = path;
                }
            }
            catch { /* ignore */ }
        }

        if (Directory.Exists(sourceDir))
        {
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*.fountain"))
                Consider(f);
            foreach (var f in Directory.EnumerateFiles(sourceDir, "*.spmd"))
                Consider(f);
        }
        foreach (var f in Directory.EnumerateFiles(projectDir, "*.fountain"))
            Consider(f);

        if (best is null)
            return false;

        var text = NormalizeText(File.ReadAllText(best));
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

    public static SaveResult SaveDraft(ProjectStore store, string projectId, string text)
    {
        text = NormalizeText(text ?? "");
        // Unify drifted same-place headings before they seed location_seed_tokens
        text = AdaptationFountain.NormalizeSceneHeadingWording(text);
        // Do NOT FixDraftDate on every save — stamping "today" changed the file after
        // approval and falsely set Dirty / "Edited since approval". Date is set at
        // draft creation / import only (CreateDraftFromBookAsync, ImportAsDraft).
        var sourceDir = Path.Combine(store.GetProjectDir(projectId), "source");
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
    public static async Task<DraftEditResult> ReskinDraftAsync(
        ProjectStore store,
        string projectId,
        string? visualMedium,
        PageToMovie.Core.Abstractions.IChatClient chat,
        string model = "",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        // Re-skin is a full-length edit — run it on the full-length base so it doesn't operate on an
        // already-trimmed draft, and update the base so Fit length trims from the re-skinned version.
        var current = ReadFullLengthSource(store, projectId);
        if (string.IsNullOrWhiteSpace(current))
            return new DraftEditResult { Ok = false, Error = "No screenplay draft to re-skin yet." };

        model = await ResolvePlanningModelAsync(store, projectId, model, "Re-skin screenplay", ct).ConfigureAwait(false);
        var progress = onProgress is null ? null : new Progress<string>(onProgress);
        var result = await new AdaptationService()
            .ReskinAsync(current, visualMedium, chat, model, progress, ct)
            .ConfigureAwait(false);

        return ApplyDraftEdit(store, projectId, result,
            appliedMessage: $"Re-applied the look to the screenplay ({result.SceneCountAfter} scenes).",
            updateBase: true, substep: ProjectStore.BookSubstepKeys.Look);
    }

    /// <summary>
    /// Enrich the current editable draft's descriptive layer for the stored medium, grounding in the
    /// prepared book text when present, and save it when the scene structure is preserved. Story and
    /// dialogue are untouched — see <see cref="AdaptationService.EmbellishAsync"/>.
    /// </summary>
    public static async Task<DraftEditResult> EmbellishDraftAsync(
        ProjectStore store,
        string projectId,
        string? visualMedium,
        PageToMovie.Core.Abstractions.IChatClient chat,
        string model = "",
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        // Enrich is a full-length edit — run it on the full-length base and update the base so Fit length
        // trims from the enriched version (otherwise trimming would discard the enrichment).
        var current = ReadFullLengthSource(store, projectId);
        if (string.IsNullOrWhiteSpace(current))
            return new DraftEditResult { Ok = false, Error = "No screenplay draft to enrich yet." };

        string? bookText = null;
        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
        if (File.Exists(bookPath))
        {
            try { bookText = await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false); }
            catch { /* enrich without grounding */ }
        }

        model = await ResolvePlanningModelAsync(store, projectId, model, "Enrich screenplay", ct).ConfigureAwait(false);
        var progress = onProgress is null ? null : new Progress<string>(onProgress);
        var result = await new AdaptationService()
            .EmbellishAsync(current, visualMedium, chat, bookText, model, progress, ct)
            .ConfigureAwait(false);

        return ApplyDraftEdit(store, projectId, result,
            appliedMessage: $"Enriched the screenplay ({result.SceneCountAfter} scenes).",
            updateBase: true, substep: ProjectStore.BookSubstepKeys.Enrich);
    }

    /// <summary>Path to the immutable full-length base (may not exist until the first trim / a max generation).</summary>
    public static string GetMaxBasePath(ProjectStore store, string projectId) =>
        Path.Combine(store.GetProjectDir(projectId), "source", MaxBaseFileName);

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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        CancellationToken ct = default)
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
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            await File.WriteAllTextAsync(basePath, baseFountain, ct).ConfigureAwait(false);
        }

        var runtime = await FilmRuntime.ResolveAsync(store, projectId, ct: ct).ConfigureAwait(false);
        var target = runtime.TargetMinutes > 0 ? runtime.TargetMinutes : runtime.NaturalMinutes;
        var natural = runtime.NaturalMinutes > 0 ? runtime.NaturalMinutes : target;

        model = await ResolvePlanningModelAsync(store, projectId, model, "Fit screenplay to length", ct).ConfigureAwait(false);
        var progress = onProgress is null ? null : new Progress<string>(onProgress);
        var result = await new AdaptationService()
            .TrimAsync(baseFountain, target, natural, chat, model, progress, ct)
            .ConfigureAwait(false);

        return ApplyDraftEdit(store, projectId, result,
            appliedMessage: $"Trimmed the screenplay toward ~{target} min ({result.SceneCountAfter} scenes).",
            substep: ProjectStore.BookSubstepKeys.FitLength, substepTargetMinutes: target);
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
                var copyPath = Path.Combine(store.GetProjectDir(projectId), "source", safe);
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
        AdaptationDefaultsOptions? adaptationDefaults = null)
    {
        var projectDir = await store.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var bookPath = Path.Combine(projectDir, "source", "book_full.txt");
        if (!File.Exists(bookPath))
            return new SaveResult { Ok = false, Error = "No prepared book text yet" };

        var book = await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(book))
            return new SaveResult { Ok = false, Error = "Book text is empty" };

        // Persist-clean Gutenberg if still on disk (older txt imports); adapt + xAI file_id use cleaned text.
        if (GutenbergCleaner.HasGutenbergHeader(book))
        {
            book = GutenbergCleaner.StripHeaderAndFooter(book);
            book = book.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";
            await File.WriteAllTextAsync(bookPath, book, ct).ConfigureAwait(false);
            onProgress?.Invoke("Stripped Project Gutenberg preamble from book text.");
        }
        else
        {
            book = book.Replace("\r\n", "\n").Replace('\r', '\n').Trim() + "\n";
        }

        Dictionary<string, JsonElement>? cfg = null;
        if (chat is not null)
        {
            cfg = await store.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            model = string.IsNullOrWhiteSpace(model)
                ? ProjectModelSelection.RequirePlanning(cfg, "Screenplay draft from book")
                : ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Screenplay draft from book");
        }

        var (title, author) = ReadProjectTitleAuthor(projectDir, projectId);
        var analysis = BookTextAnalyzer.Analyze(book);
        // Resolve + persist the target (Trim/Fit-length reads it) but do NOT constrain generation with it.
        var runtime = await FilmRuntime.ResolveAsync(store, projectId, book, overrideTargetMinutes: totalRuntimeMinutes, ct)
            .ConfigureAwait(false);
        // Track D0: always generate at natural/max length (null → unlimited directive). The user's target
        // only drives the Trim stage, which derives the working draft from the full-length base written below.
        int? minutes = null;
        onProgress?.Invoke(
            $"Writing the full-length screenplay (natural ~{runtime.NaturalMinutes} min); fit to your target on the next step.");
        const double generationTemperature = 0.2;

        BookTextIdentity? bookIdentity = null;
        string? promptHash = null;
        string? promptVersion = null;
        string? behaviorVersions = null;
        if (bookRegistry is not null && !string.IsNullOrWhiteSpace(cacheUserId))
        {
            var project = await store.GetProjectAsync(projectId, ct).ConfigureAwait(false);
            bookIdentity = await bookRegistry.RegisterAsync(
                book, cacheUserId, projectId, (project?.VisibilityMode ?? ProjectVisibility.Private).ToString(), ct).ConfigureAwait(false);
            var prompt = await new AdaptationService()
                .BuildSystemPromptAsync(minutes, ct).ConfigureAwait(false);
            promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
            promptVersion = "book-to-fountain-" + promptHash[..12];
            behaviorVersions = JsonSerializer.Serialize(new
            {
                title,
                author,
                totalRuntimeMinutes = minutes,
                visionMetaSchema = ProjectVisionMeta.SchemaVersion,
                cachePackageSchema = "adaptation-conversion.v1",
            });

            var cached = await bookRegistry.FindArtifactAsync(
                bookIdentity.BookId, cacheUserId, "adaptation_conversion", model,
                promptVersion, promptHash, generationTemperature, behaviorVersions, ct)
                .ConfigureAwait(false);
            if (cached is not null)
            {
                var cachedConversion = JsonSerializer.Deserialize<ProjectAdaptationConversionResult>(cached.Content);
                if (cachedConversion is { Fountain.Length: > 0, VisionMeta: not null })
                {
                    onProgress?.Invoke($"Reused shared adaptation cache {cached.ArtifactId}.");
                    var cachedFountain = new AdaptationService().FixDraftDate(cachedConversion.Fountain);
                    var cachedSave = SaveDraft(store, projectId, cachedFountain);
                    if (!cachedSave.Ok) return cachedSave;
                    WriteMaxBase(store, projectId, cachedFountain); // D0: full-length base for Trim
                    store.ClearBookSubsteps(projectId); // fresh screenplay → prior Look/Enrich/Fit length no longer apply
                    ProjectVisionMeta.Write(projectDir, cachedConversion.VisionMeta);
                    cachedSave.Message = "Screenplay draft ready — reused shared book adaptation";
                    return cachedSave;
                }
            }
        }

        try
        {
            // Stage‑1 generation goes through Adaptation façade (not a reimplementation).
            var adaptation = new AdaptationService();
            Func<StructuralGateFailure, CancellationToken, Task>? onGate = null;
            if (errorLogger is not null)
            {
                onGate = async (fail, token) =>
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

            IProgress<string>? progressAdapter = onProgress is null
                ? null
                : new Progress<string>(onProgress);

            if (chat is null || !chat.IsConfigured)
            {
                var hAdaptation = new AdaptationService();
                var hFountain = hAdaptation.FixDraftDate(hAdaptation.ConvertHeuristic(title, book, author));
                var hSave = SaveDraft(store, projectId, hFountain);
                if (!hSave.Ok) return hSave;
                hSave.Message = "Screenplay draft ready — review and approve";
                return hSave;
            }

            PageToMovie.Core.Abstractions.IBookFileSession? bookSession = null;
            if (bookFileSessionFactory is not null && bookIdentity is not null)
            {
                try
                {
                    bookSession = await bookFileSessionFactory.TryCreateAsync(
                        bookIdentity.BookId, book, model, ct).ConfigureAwait(false);
                    if (bookSession is { IsAvailable: true })
                        onProgress?.Invoke(
                            $"Book file session ready for {bookIdentity.BookId} (xAI file_id reuse / Responses multi-turn).");
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke("Book file session unavailable — falling back to chat/completions: " + ex.Message);
                    bookSession = null;
                }
            }

            // UI / project medium preference (auto = model infers).
            string? preferredMedium = null;
            try
            {
                preferredMedium = ProjectVisionMeta.GetAdaptationMediumPreference(projectDir);
            }
            catch { /* ignore */ }

            var result = await adaptation.ConvertAsync(
                new AdaptationRequest
                {
                    BookText = book,
                    Title = title,
                    Author = author,
                    TargetRuntimeMinutes = minutes,
                    ModelId = model,
                    Temperature = generationTemperature,
                    VisualMedium = preferredMedium,
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

            if (!result.UsedHeuristicFallback && visionFromScript is not null && bookRegistry is not null &&
                bookIdentity is not null && !string.IsNullOrWhiteSpace(cacheUserId) &&
                promptHash is not null && promptVersion is not null && behaviorVersions is not null)
            {
                var cached = await bookRegistry.RegisterArtifactAsync(
                    bookIdentity.BookId, cacheUserId, "adaptation_conversion",
                    JsonSerializer.Serialize(conversion), model, promptVersion, promptHash,
                    generationTemperature, behaviorVersions, ct).ConfigureAwait(false);
                onProgress?.Invoke($"Saved shared adaptation cache {cached.ArtifactId}.");
            }

            fountain = new AdaptationService().FixDraftDate(fountain);
            var save = SaveDraft(store, projectId, fountain);
            if (!save.Ok) return save;
            WriteMaxBase(store, projectId, fountain); // D0: full-length base for Trim (Fit length)
            store.ClearBookSubsteps(projectId); // fresh screenplay → prior Look/Enrich/Fit length no longer apply

            // Medium sidecar from the same adaptation response (preferred).
            // Fallback: one structured LLM call if the model omitted the trailer.
            try
            {
                if (visionFromScript is not null)
                {
                    ProjectVisionMeta.Write(projectDir, visionFromScript);
                    onProgress?.Invoke($"Saved visual medium ({visionFromScript.VisualMedium}) to extract_meta");
                }
                else if (chat is not null && chat.IsConfigured)
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

            // Stage‑1 convert attribution (prompt / adaptation / runtime / model).
            try
            {
                if (result.ConvertManifest is not null)
                {
                    await ProjectStage1ConvertManifest.WriteAsync(
                        projectDir,
                        result.ConvertManifest,
                        bookId: bookIdentity?.BookId,
                        ct: ct).ConfigureAwait(false);
                    onProgress?.Invoke(
                        $"Saved Stage‑1 convert manifest (adaptation={result.ConvertManifest.AdaptationVersion}, " +
                        $"mode={result.ConvertManifest.RuntimeMode}, model={result.ConvertManifest.ModelId})");
                }
            }
            catch (Exception manifestEx)
            {
                onProgress?.Invoke("Stage‑1 convert manifest skipped: " + manifestEx.Message);
            }

            // Trajectory: screenplay + optional report/manifest on project git (text only).
            try
            {
                store.TriggerAutoGitCommit(projectId, ProjectStageCommits.ScreenplayCreated);
            }
            catch (Exception gitEx)
            {
                onProgress?.Invoke("Stage commit skipped: " + gitEx.Message);
            }

            save.Message = "Screenplay draft ready — review and approve";
            return save;
        }
        catch (Exception ex)
        {
            return new SaveResult { Ok = false, Error = ex.Message };
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
        new AdaptationService().ConvertHeuristic(title, bookText);

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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
