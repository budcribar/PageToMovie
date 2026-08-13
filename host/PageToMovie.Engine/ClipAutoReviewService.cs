using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.ModelExecution;
using PageToMovie.Engine.ModelBacked;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Per-clip AI review: sample previous tail + current frames, draft structured suggestions.
/// Does not apply edits or regen — user confirms via Apply → Regen.
/// </summary>
public sealed class ClipAutoReviewService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private const string VoiceProfileKey = "voice_profile";
    private const string CharacterLayer = "character";
    private const string VisualPromptKey = "visual_prompt";
    private const string CurrentClipLabel = "CURRENT_CLIP";

    private readonly ProjectStore _projects;
    private readonly IVisionClient _vision;
    private readonly EditLogService _logs;
    private readonly ProjectRulesService _projectRules;
    private readonly ReviewIndexService _reviewIndex;
    private readonly ILogger<ClipAutoReviewService> _log;

    public ClipAutoReviewService(
        ProjectStore projects,
        IVisionClient vision,
        EditLogService logs,
        ProjectRulesService projectRules,
        ReviewIndexService reviewIndex,
        ILogger<ClipAutoReviewService> log)
    {
        _projects = projects;
        _vision = vision;
        _logs = logs;
        _projectRules = projectRules;
        _reviewIndex = reviewIndex;
        _log = log;
    }

    public bool IsConfigured => _vision.IsConfigured;

    private async Task<string> GetConfigStringAsync(
        string projectId, string key, string fallback, CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        if (cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? fallback;
        return fallback;
    }

    public string DraftPath(string projectId, int scene, int clip) =>
        Path.Combine(
            _projects.GetProjectDir(projectId),
            "assets",
            "review",
            $"S{scene:D2}C{clip:D2}.auto_review.json");

    public async Task<ClipAutoReviewDraft?> LoadDraftAsync(string projectId, int scene, int clip, CancellationToken ct = default)
    {
        var path = DraftPath(projectId, scene, clip);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ClipAutoReviewDraft>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveDraftAsync(ClipAutoReviewDraft draft, CancellationToken ct = default)
    {
        var projectDir = await _projects.GetProjectDirAsync(draft.ProjectId, ct).ConfigureAwait(false);
        var path = Path.Combine(
            projectDir,
            "assets",
            "review",
            $"S{draft.Scene:D2}C{draft.Clip:D2}.auto_review.json");
        var issues = StructuredOperationArtifacts.RequireJsonProperties(
            draft, "projectId", "suggestion", "confidence");
        if (issues.Any(i => i.Severity == ModelValidationSeverity.Error))
            throw new InvalidOperationException(string.Join(" ", issues.Select(i => i.Message)));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(draft, JsonOpts) + "\n", ct).ConfigureAwait(false);
        await StructuredOperationArtifacts.WriteAsync(
            projectDir, "clip_multimodal_review", null,
            new { draft.ProjectId, draft.Scene, draft.Clip }, draft, issues, ct).ConfigureAwait(false);
    }

    public async Task<ClipAutoReviewDraft> ReviewAsync(
        string projectId,
        int scene,
        int clip,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default,
        IReadOnlyList<ClipAutoReviewClientFrame>? clientFrames = null)
    {
        if (!_vision.IsConfigured)
            throw new InvalidOperationException("Connect service (XAI_API_KEY) for clip review.");

        using var _telScope = ProjectTelemetryService.UseProject(projectId);
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);

        onProgress?.Invoke(5, 100, "Loading clip plan…");
        var plan = await LoadClipPlanAsync(projectId, scene, clip).ConfigureAwait(false);
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);

        var workDir = Path.Combine(projectDir, "assets", "review", $"_frames_S{scene:D2}C{clip:D2}");
        try
        {
            ResetWorkDir(workDir);
            return await ReviewWithClientFramesAsync(
                projectId, scene, clip, projectDir, workDir, plan, profiles, onProgress, ct, clientFrames)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static void ResetWorkDir(string workDir)
    {
        if (Directory.Exists(workDir))
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort stale frame cleanup */ }
        }
        Directory.CreateDirectory(workDir);
    }

    private static void TryDeleteDirectory(string workDir)
    {
        try
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private async Task<ClipAutoReviewDraft> ReviewWithClientFramesAsync(
        string projectId,
        int scene,
        int clip,
        string projectDir,
        string workDir,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        Action<int, int, string>? onProgress,
        CancellationToken ct,
        IReadOnlyList<ClipAutoReviewClientFrame>? clientFrames)
    {
        var (images, curFramePaths, hasPrev) = await RequireClientFramesAsync(workDir, clientFrames, onProgress, ct)
            .ConfigureAwait(false);

        var durableFrames = await TryPersistDurableFramesAsync(projectId, scene, clip, curFramePaths, ct)
            .ConfigureAwait(false);

        onProgress?.Invoke(55, 100, "AI reviewing continuity and quality…");
        var prompt = await BuildReviewPromptAsync(scene, clip, plan, profiles, images, hasPrev).ConfigureAwait(false);
        prompt = await AppendActiveProjectRulesAsync(projectId, prompt, ct).ConfigureAwait(false);
        var qualityModel = await ResolveReviewModelAsync(projectId, ct).ConfigureAwait(false);

        onProgress?.Invoke(85, 100, "Parsing suggestions…");
        var draft = await ExecuteReviewOperationAsync(
            projectId, scene, clip, projectDir, plan, profiles, hasPrev, images, prompt, qualityModel, ct)
            .ConfigureAwait(false);

        await TryLogAsync(projectId, scene, clip, draft, ct);
        await TryRecordAutoReviewAsync(projectId, scene, clip, draft, ct).ConfigureAwait(false);
        await TryUpsertReviewIndexAsync(projectId, scene, clip, durableFrames, draft, ct).ConfigureAwait(false);

        onProgress?.Invoke(100, 100, "Review draft ready");
        return draft;
    }

    private static async Task<(List<(string Path, string Label)> Images, List<string> CurrentClipPaths, bool HasPrev)>
        RequireClientFramesAsync(
            string workDir,
            IReadOnlyList<ClipAutoReviewClientFrame>? clientFrames,
            Action<int, int, string>? onProgress,
            CancellationToken ct)
    {
        if (clientFrames is { Count: > 0 })
        {
            onProgress?.Invoke(20, 100, "Receiving browser sample frames…");
            var materialized = await MaterializeClientFramesAsync(workDir, clientFrames, ct).ConfigureAwait(false);
            if (materialized.Images.Count == 0)
                throw new InvalidOperationException("No usable sample frames (upload empty or invalid).");
            return materialized;
        }

        // No native ffmpeg on server — browser must sample via ffmpeg.wasm.
        throw new InvalidOperationException(
            "Browser frame samples required for auto-review (no server ffmpeg). " +
            "Use Review → Auto-review in the app so the client can sample frames first.");
    }

    private async Task<IReadOnlyList<string>> TryPersistDurableFramesAsync(
        string projectId, int scene, int clip, List<string> curFramePaths, CancellationToken ct)
    {
        // PR3: keep 2–4 current-clip frames for humans/export (before temp workDir is deleted)
        try
        {
            return await _reviewIndex.PersistDurableFramesAsync(
                projectId, scene, clip, curFramePaths, maxFrames: 4, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Persist durable frames skipped S{Scene}C{Clip}", scene, clip);
            return Array.Empty<string>();
        }
    }

    private async Task<string> AppendActiveProjectRulesAsync(string projectId, string prompt, CancellationToken ct)
    {
        // Project-scoped rules only (checklist lives in embedded clip_auto_review.txt).
        try
        {
            var rules = await _projectRules.GetActiveRulesBlockAsync(projectId, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(rules))
                return prompt + "\n\n" + rules.Trim();
        }
        catch { /* non-fatal */ }
        return prompt;
    }

    private async Task<string> ResolveReviewModelAsync(string projectId, CancellationToken ct)
    {
        var qualityModel = await GetConfigStringAsync(projectId, "quality_model_name", "", ct);
        // legacy next line may fill vision — we re-resolve below
        if (string.IsNullOrWhiteSpace(qualityModel))
            qualityModel = await GetConfigStringAsync(projectId, "vision_model_name", "", ct);
        if (string.IsNullOrWhiteSpace(qualityModel))
        {
            var cfgMap = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
            return ProjectModelSelection.RequireVideoReview(cfgMap, "Clip auto-review");
        }

        return ProjectModelSelection.RequireExplicit(qualityModel, ModelCapability.Chat, "Clip auto-review");
    }

    private async Task<ClipAutoReviewDraft> ExecuteReviewOperationAsync(
        string projectId,
        int scene,
        int clip,
        string projectDir,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        bool hasPrev,
        List<(string Path, string Label)> images,
        string prompt,
        string qualityModel,
        CancellationToken ct)
    {
        var imagePaths = images.Select(i => i.Path).ToList();
        var operation = new MultimodalReviewOperation<ClipAutoReviewDraft>(
            _vision, imagePaths, qualityModel, "clip_multimodal_review", "clip-auto-review.v1",
            raw => ParseDraft(raw, projectId, scene, clip, plan, profiles, hasPrev),
            ValidateDraft);
        var observation = new MultimodalReviewObservation(
            $"Clip S{scene:D2}C{clip:D2}", images.Select(i => i.Label).ToArray(), prompt);
        var execution = await operation.ExecuteAsync(observation, ct).ConfigureAwait(false);
        if (!execution.Success || execution.Value is null)
            throw new InvalidOperationException(execution.Error ?? string.Join(" ", execution.ValidationIssues.Select(i => i.Message)));
        var draft = execution.Value;
        draft.GeneratedAt = DateTimeOffset.UtcNow;
        await SaveDraftAsync(draft, ct).ConfigureAwait(false);
        await SaveExecutionManifestAsync(projectDir, "clip_multimodal_review", execution, ct).ConfigureAwait(false);
        return draft;
    }

    private async Task TryRecordAutoReviewAsync(
        string projectId, int scene, int clip, ClipAutoReviewDraft draft, CancellationToken ct)
    {
        try
        {
            await _logs.RecordAutoReviewAsync(
                projectId, scene, clip,
                draft.Suggestion, draft.Category, draft.Note, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "RecordAutoReview for assembly gate skipped");
        }
    }

    private async Task TryUpsertReviewIndexAsync(
        string projectId, int scene, int clip, IReadOnlyList<string> durableFrames,
        ClipAutoReviewDraft draft, CancellationToken ct)
    {
        try
        {
            await _reviewIndex.UpsertClipAsync(projectId, scene, clip, durableFrames, draft, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Review index upsert skipped S{Scene}C{Clip}", scene, clip);
        }
    }

    /// <summary>Write accepted suggestion values into cast seeds / blueprint clip (with before/after log).</summary>
    public async Task ApplySuggestionsAsync(
        string projectId,
        int scene,
        int clip,
        IReadOnlyList<ClipAutoReviewApplyItem> items,
        CancellationToken ct = default)
    {
        if (items is null || items.Count == 0)
            throw new InvalidOperationException("No suggestions selected to apply.");

        var plan = await LoadClipPlanAsync(projectId, scene, clip).ConfigureAwait(false);
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);
        var beforeParts = new List<string>();
        var afterParts = new List<string>();

        foreach (var item in items)
            ApplySuggestionItem(projectId, scene, clip, item, plan, profiles, beforeParts, afterParts);

        var draft = await StampDraftAppliedAsync(projectId, scene, clip, ct).ConfigureAwait(false);
        await TryLogApplyAsync(projectId, scene, clip, items.Count, beforeParts, afterParts, draft, ct)
            .ConfigureAwait(false);
    }

    private void ApplySuggestionItem(
        string projectId,
        int scene,
        int clip,
        ClipAutoReviewApplyItem item,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        List<string> beforeParts,
        List<string> afterParts)
    {
        var layer = (item.Layer ?? "clip").Trim().ToLowerInvariant();
        var field = (item.Field ?? "").Trim().ToLowerInvariant();
        var value = item.Value ?? "";

        if (layer == CharacterLayer && !string.IsNullOrWhiteSpace(item.CharKey))
            ApplyCharacterSuggestion(projectId, item.CharKey, field, value, profiles, beforeParts, afterParts);
        else if (layer == "clip" && field is VisualPromptKey or "prompt")
            ApplyClipVisualSuggestion(projectId, scene, clip, value, plan, beforeParts, afterParts);
    }

    private void ApplyCharacterSuggestion(
        string projectId,
        string charKey,
        string field,
        string value,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        List<string> beforeParts,
        List<string> afterParts)
    {
        profiles.TryGetValue(charKey, out var p);
        var before = ReadCharacterField(p, field);
        WriteCharacterField(projectId, charKey, field, value);
        beforeParts.Add($"{charKey}.{field}: {Trim(before, 400)}");
        afterParts.Add($"{charKey}.{field}: {Trim(value, 400)}");
    }

    private static string ReadCharacterField(ClipVideoPromptBuilder.CharacterProfile? p, string field) =>
        field switch
        {
            "description" => p?.Description ?? "",
            "visual_lock" => p?.VisualLock ?? "",
            VoiceProfileKey => p?.VoiceProfile ?? "",
            _ => "",
        };

    private void WriteCharacterField(string projectId, string charKey, string field, string value)
    {
        switch (field)
        {
            case VoiceProfileKey:
                _projects.UpdateCharacterSeedText(projectId, charKey, voiceProfile: value);
                break;
            case "description":
                _projects.UpdateCharacterSeedText(projectId, charKey, description: value);
                break;
            case "visual_lock":
                _projects.UpdateCharacterSeedText(projectId, charKey, visualLock: value);
                break;
            default:
                _log.LogWarning("Unknown character field {Field}", field);
                break;
        }
    }

    private void ApplyClipVisualSuggestion(
        string projectId,
        int scene,
        int clip,
        string value,
        ClipPlan plan,
        List<string> beforeParts,
        List<string> afterParts)
    {
        var before = plan.VisualPrompt;
        _projects.UpdateClipVisualPrompt(projectId, scene, clip, value);
        beforeParts.Add($"clip.visual_prompt: {Trim(before, 600)}");
        afterParts.Add($"clip.visual_prompt: {Trim(value, 600)}");
        plan.VisualPrompt = value;
    }

    private async Task<ClipAutoReviewDraft?> StampDraftAppliedAsync(
        string projectId, int scene, int clip, CancellationToken ct)
    {
        var draft = await LoadDraftAsync(projectId, scene, clip, ct).ConfigureAwait(false);
        if (draft is null)
            return null;
        draft.RawSummary = (draft.RawSummary ?? "") + "\n[applied " + DateTimeOffset.UtcNow.ToString("O") + "]";
        await SaveDraftAsync(draft, ct).ConfigureAwait(false);
        return draft;
    }

    private async Task TryLogApplyAsync(
        string projectId,
        int scene,
        int clip,
        int suggestionCount,
        List<string> beforeParts,
        List<string> afterParts,
        ClipAutoReviewDraft? draft,
        CancellationToken ct)
    {
        try
        {
            await _logs.AddAsync(
                projectId,
                "auto_review_apply",
                $"Applied {suggestionCount} suggestion(s) to S{scene:D2}C{clip:D2}",
                scene: scene,
                clip: clip,
                actionTaken: "apply_suggestions",
                before: string.Join("\n---\n", beforeParts),
                after: string.Join("\n---\n", afterParts),
                category: draft?.Category,
                suggestionCount: suggestionCount,
                ct: ct).ConfigureAwait(false);
        }
        catch { /* non-fatal */ }
    }

    private async Task TryLogAsync(
        string projectId, int scene, int clip, ClipAutoReviewDraft draft, CancellationToken ct)
    {
        try
        {
            await _logs.AddAsync(
                projectId,
                "auto_review",
                draft.Note.Length > 0 ? draft.Note : draft.Suggestion,
                scene: scene,
                clip: clip,
                actionTaken: $"suggestion={draft.Suggestion};category={draft.Category};confidence={draft.Confidence}",
                category: draft.Category,
                suggestion: draft.Suggestion,
                confidence: draft.Confidence,
                continuity: draft.Continuity,
                suggestionCount: draft.Suggestions.Count,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "auto_review log skip");
        }
    }

    /// <summary>Test/helper: load planned clip fields from Stage 2 blueprint (<c>veo_clips</c>).</summary>
    public static async Task<ClipPlanSnapshot> LoadClipPlanForTestsAsync(
        ProjectStore projects, string projectId, int scene, int clip)
    {
        var plan = await LoadClipPlanCoreAsync(projects, projectId, scene, clip, log: null).ConfigureAwait(false);
        return new ClipPlanSnapshot(plan.VisualPrompt, plan.Dialogue, plan.Speaker, plan.Delivery);
    }

    public readonly record struct ClipPlanSnapshot(
        string VisualPrompt, string Dialogue, string Speaker, string Delivery);

    private Task<ClipPlan> LoadClipPlanAsync(string projectId, int scene, int clip) =>
        LoadClipPlanCoreAsync(_projects, projectId, scene, clip, _log);

    private static async Task<ClipPlan> LoadClipPlanCoreAsync(
        ProjectStore projects,
        string projectId,
        int scene,
        int clip,
        ILogger? log)
    {
        var plan = new ClipPlan();
        try
        {
            using var doc = await projects.LoadBlueprintAsync(projectId).ConfigureAwait(false);
            if (doc is null) return plan;
            var root = doc.RootElement;
            if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
                return plan;
            var clipEl = FindSceneClip(scenes, scene, clip);
            if (clipEl is not null)
                ApplyClipPlanFields(plan, clipEl.Value);
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "LoadClipPlan failed");
        }
        return plan;
    }

    private static JsonElement? FindSceneClip(JsonElement scenes, int scene, int clip)
    {
        foreach (var s in scenes.EnumerateArray())
        {
            if (!IsSceneNumber(s, scene))
                continue;
            if (!TryGetClipsArray(s, out var clips))
                break;
            var found = FindClipInArray(clips, clip);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static bool IsSceneNumber(JsonElement s, int scene)
    {
        if (!s.TryGetProperty("scene_number", out var sn) || !sn.TryGetInt32(out var n) || n != scene)
            return false;
        return true;
    }

    private static bool TryGetClipsArray(JsonElement s, out JsonElement clips)
    {
        clips = default;
        // Canonical Stage 2 key is veo_clips
        if (s.TryGetProperty("veo_clips", out clips) && clips.ValueKind == JsonValueKind.Array)
            return true;
        if (s.TryGetProperty("clips", out clips) && clips.ValueKind == JsonValueKind.Array)
            return true;
        return false;
    }

    private static JsonElement? FindClipInArray(JsonElement clips, int clip)
    {
        foreach (var c in clips.EnumerateArray())
        {
            if (!c.TryGetProperty("clip_number", out var cn) || !cn.TryGetInt32(out var cnum) || cnum != clip)
                continue;
            return c;
        }
        return null;
    }

    private static void ApplyClipPlanFields(ClipPlan plan, JsonElement c)
    {
        plan.VisualPrompt = ReadJsonStringOrEmpty(c, VisualPromptKey);
        ApplyAudioPayload(plan, c);
        ApplyCharactersPresent(plan, c);
    }

    private static string ReadJsonStringOrEmpty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";

    private static void ApplyAudioPayload(ClipPlan plan, JsonElement c)
    {
        if (!c.TryGetProperty("audio_payload", out var ap) || ap.ValueKind != JsonValueKind.Object)
            return;
        plan.Dialogue = ReadJsonStringOrEmpty(ap, "dialogue");
        plan.Speaker = ReadJsonStringOrEmpty(ap, "speaker");
        plan.Delivery = ReadJsonStringOrEmpty(ap, "delivery");
    }

    private static void ApplyCharactersPresent(ClipPlan plan, JsonElement c)
    {
        if (!c.TryGetProperty("characters_present", out var cp) || cp.ValueKind != JsonValueKind.Array)
            return;
        foreach (var x in cp.EnumerateArray())
        {
            var k = x.GetString();
            if (!string.IsNullOrWhiteSpace(k))
                plan.Characters.Add(k);
        }
    }

    private static async Task<string> BuildReviewPromptAsync(
        int scene,
        int clip,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        List<(string Path, string Label)> images,
        bool hasPrev)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a film QC assistant for a short children's/book adaptation.");
        sb.AppendLine($"Review clip S{scene:D2}C{clip:D2}.");
        sb.AppendLine();
        sb.AppendLine("Images are labeled in order:");
        for (var i = 0; i < images.Count; i++)
            sb.AppendLine($"  IMAGE_{i + 1}: {images[i].Label}");
        if (hasPrev)
            sb.AppendLine("PREVIOUS_CLIP_TAIL frames are the END of the prior clip — judge continuity into CURRENT_CLIP (especially its START).");
        else
            sb.AppendLine("No previous clip tail available.");
        sb.AppendLine();
        sb.AppendLine("Planned visual_prompt:");
        sb.AppendLine(plan.VisualPrompt.Length > 0 ? plan.VisualPrompt : "(missing)");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(plan.Dialogue))
        {
            sb.AppendLine($"Dialogue speaker={plan.Speaker} delivery={plan.Delivery}:");
            sb.AppendLine($"\"{plan.Dialogue}\"");
        }
        else
            sb.AppendLine("No dialogue on this clip.");
        sb.AppendLine();
        if (plan.Characters.Count > 0)
        {
            sb.AppendLine("Cast present:");
            foreach (var key in plan.Characters)
            {
                profiles.TryGetValue(key, out var p);
                var name = p?.DisplayName ?? key;
                var look = p?.Description ?? "";
                var vlock = p?.VisualLock ?? "";
                var voice = p?.VoiceProfile ?? "";
                // Token-accurate (was character-count Trim): this line becomes part of the
                // vision-review prompt, unlike the other Trim(...) calls in this file (those
                // trim audit-log diffs / stored response summaries, not outgoing prompt text).
                // SIGNATURE surfaces visual_lock explicitly and separately from the general
                // look — previously this line only used Description, so the reviewer never saw
                // the one field the rest of the system treats as the must-never-drift identity
                // trait (a specific eye/scar/mark), and had nothing to specifically cross-check
                // a frame against.
                var signaturePart = !string.IsNullOrWhiteSpace(vlock)
                    ? $" SIGNATURE (must match exactly): {PromptTokenizer.TruncateToTokens(vlock, 50)}"
                    : "";
                sb.AppendLine(
                    $"- {key} ({name}) look: {PromptTokenizer.TruncateToTokens(look, 50)}{signaturePart} " +
                    $"voice: {PromptTokenizer.TruncateToTokens(voice, 30)}");
            }
        }
        sb.AppendLine();
        sb.Append(await LoadAutoReviewRulesBlockAsync().ConfigureAwait(false));
        return sb.ToString();
    }

    /// <summary>Checklist + JSON schema from <c>prompts/clip_auto_review.txt</c> (embed or override).</summary>
    public static async Task<string> LoadAutoReviewRulesBlockAsync()
    {
        try
        {
            var text = await PromptFiles.ReadAsync("prompts/clip_auto_review.txt").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim() + "\n";
        }
        catch
        {
            /* fall through to built-in fallback */
        }

        // Minimal fallback if embed/override missing (tests without resources).
        return """
            CHECKLIST (fail when confidence high; put the primary issue in category):
            1) IDENTITY — faces match cast Character_*; no role swap/merge. If a character has a
               SIGNATURE trait listed above, check that exact trait against the frame specifically.
            2) PROMPT COMPLETENESS — flag stub/truncated plan text; rewrite visual_prompt if needed.
            3) SILENCE vs EXPRESSION — no mid-shout / open-mouth yell when silent or no dialogue.
            4) ADDRESS / GAZE — honor PROJECT performance rules (confessional vs observational).
            5) STYLE + WARDROBE — match project medium and cast period/wardrobe locks.
            6) FACE READABILITY — fail beauty-blank or wrong-emotion face when confidence high.
            Also: continuity from prev tail, lip/speech vs dialogue, empty/dead frames, wrong action.
            Respond with JSON ONLY (no markdown):
            {
              "suggestion": "pass"|"fail"|"unclear",
              "category": "continuity"|"wrong_look"|"wrong_style"|"wrong_voice"|"silent"|"framing"|"other",
              "confidence": "high"|"medium"|"low",
              "continuity": "ok"|"jump"|"unclear"|"n/a",
              "note": "one short human-readable review note covering the main checklist hit",
              "suggestions": []
            }
            Rules: prefer clip visual_prompt rewrites. Keep Character_* keys. Empty suggestions[] if pass.

            """;
    }

    internal static ModelParseResult<ClipAutoReviewDraft> ParseDraftForReplay(
        string raw, string projectId, int scene, int clip, bool hasPrev) =>
        ParseDraft(raw, projectId, scene, clip, new ClipPlan(),
            new Dictionary<string, ClipVideoPromptBuilder.CharacterProfile>(), hasPrev);

    private static ModelParseResult<ClipAutoReviewDraft> ParseDraft(
        string raw,
        string projectId,
        int scene,
        int clip,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles,
        bool hasPrev)
    {
        var draft = new ClipAutoReviewDraft
        {
            ProjectId = projectId,
            Scene = scene,
            Clip = clip,
            IncludedPreviousTail = hasPrev,
            RawSummary = Trim(raw, 2000),
        };

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return ModelParseResult<ClipAutoReviewDraft>.Failure(
                    new ModelValidationIssue("invalid_json", "Review response did not contain a JSON object."));
            }

            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            draft.Suggestion = GetStr(root, "suggestion", "unclear").ToLowerInvariant();
            draft.Category = GetStr(root, "category", "other").ToLowerInvariant();
            draft.Confidence = GetStr(root, "confidence", "medium").ToLowerInvariant();
            draft.Continuity = GetStr(root, "continuity", hasPrev ? "unclear" : "n/a").ToLowerInvariant();
            draft.Note = GetStr(root, "note", "");

            if (root.TryGetProperty("suggestions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var suggestion = ParseSuggestionItem(item, plan, profiles);
                    if (suggestion is not null)
                        draft.Suggestions.Add(suggestion);
                }
            }
        }
        catch (Exception ex)
        {
            return ModelParseResult<ClipAutoReviewDraft>.Failure(
                new ModelValidationIssue("invalid_json", $"Review response JSON could not be parsed: {ex.Message}"));
        }

        return ModelParseResult<ClipAutoReviewDraft>.Success(draft);
    }

    private static ClipAutoReviewSuggestion? ParseSuggestionItem(
        JsonElement item,
        ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var layer = GetStr(item, "layer", "clip").ToLowerInvariant();
        var field = ResolveSuggestionField(item, layer);
        var charKey = ResolveSuggestionCharKey(item);

        var suggested = GetStr(item, "suggested_value", "");
        if (string.IsNullOrWhiteSpace(suggested))
            suggested = GetStr(item, "suggestedValue", "");
        if (string.IsNullOrWhiteSpace(suggested)) return null;

        var current = ResolveCurrentValue(layer, field, charKey, plan, profiles);
        var include = ResolveIncludeByDefault(item);

        return new ClipAutoReviewSuggestion
        {
            Layer = layer is CharacterLayer or "scene" ? layer : "clip",
            Field = field,
            CharKey = charKey,
            Label = GetStr(item, "label", field),
            CurrentValue = current,
            SuggestedValue = suggested,
            IncludeByDefault = include,
            Rationale = GetStr(item, "rationale", ""),
        };
    }

    private static string ResolveSuggestionField(JsonElement item, string layer)
    {
        var field = GetStr(item, "field", "");
        if (string.IsNullOrWhiteSpace(field) && item.TryGetProperty("suggested_value", out _))
            field = layer == CharacterLayer ? VoiceProfileKey : VisualPromptKey;
        return field;
    }

    private static string? ResolveSuggestionCharKey(JsonElement item)
    {
        var charKey = GetStr(item, "char_key", "") is { Length: > 0 } ck ? ck : null;
        if (charKey is null && item.TryGetProperty("charKey", out var ck2))
            charKey = ck2.GetString();
        return charKey;
    }

    private static string ResolveCurrentValue(
        string layer, string field, string? charKey, ClipPlan plan,
        IReadOnlyDictionary<string, ClipVideoPromptBuilder.CharacterProfile> profiles)
    {
        if (layer == "clip" && field is VisualPromptKey or "prompt")
            return plan.VisualPrompt;
        if (layer == CharacterLayer && charKey is not null &&
            profiles.TryGetValue(charKey, out var p))
        {
            return field switch
            {
                "description" => p.Description,
                "visual_lock" => p.VisualLock,
                VoiceProfileKey => p.VoiceProfile,
                _ => "",
            };
        }
        return "";
    }

    private static bool ResolveIncludeByDefault(JsonElement item)
    {
        var include = true;
        if (item.TryGetProperty("include_by_default", out var ib) &&
            ib.ValueKind is JsonValueKind.False)
            include = false;
        if (item.TryGetProperty("includeByDefault", out var ib2) &&
            ib2.ValueKind is JsonValueKind.False)
            include = false;
        return include;
    }

    internal static IReadOnlyList<ModelValidationIssue> ValidateDraft(ClipAutoReviewDraft draft)
    {
        var issues = new List<ModelValidationIssue>();
        if (draft.Suggestion is not ("pass" or "fail" or "unclear"))
            issues.Add(new("invalid_suggestion", "suggestion must be pass, fail, or unclear.", "$.suggestion"));
        if (draft.Confidence is not ("high" or "medium" or "low"))
            issues.Add(new("invalid_confidence", "confidence must be high, medium, or low.", "$.confidence"));
        if (string.IsNullOrWhiteSpace(draft.Note))
            issues.Add(new("missing_note", "A specific review note is required.", "$.note"));
        return issues;
    }

    private static async Task SaveExecutionManifestAsync<TResult>(
        string projectDir, string operationName, ValidatedModelResult<TResult> execution, CancellationToken ct = default)
        where TResult : class
    {
        var dir = Path.Combine(projectDir, "artifacts", "model_operations");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, operationName + ".lifecycle.json"),
            ModelExecutionManifest.Serialize(execution), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write browser-uploaded base64 frames to workDir. Max 8 images, ~2.5MB each decoded.
    /// </summary>
    private static async Task<(List<(string Path, string Label)> Images, List<string> CurrentClipPaths, bool HasPrev)> MaterializeClientFramesAsync(
        string workDir,
        IReadOnlyList<ClipAutoReviewClientFrame> clientFrames,
        CancellationToken ct = default)
    {
        const int maxFrames = 8;
        const int maxBytesEach = 2_500_000;
        var currentClipPaths = new List<string>();
        var hasPrev = false;
        var images = new List<(string Path, string Label)>();
        var i = 0;
        foreach (var frame in clientFrames.Take(maxFrames))
        {
            if (!TryDecodeClientFrame(frame, maxBytesEach, out var bytes, out var label, out var ext))
                continue;

            i++;
            var path = Path.Combine(workDir, $"f{i:D2}_{label.ToLowerInvariant()}.{ext}");
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            images.Add((path, label));
            if (label == "PREVIOUS_CLIP_TAIL")
                hasPrev = true;
            else
                currentClipPaths.Add(path);
        }

        return (images, currentClipPaths, hasPrev);
    }

    private static bool TryDecodeClientFrame(
        ClipAutoReviewClientFrame? frame,
        int maxBytesEach,
        out byte[] bytes,
        out string label,
        out string ext)
    {
        bytes = Array.Empty<byte>();
        label = CurrentClipLabel;
        ext = "jpg";
        if (frame is null || string.IsNullOrWhiteSpace(frame.Base64))
            return false;

        if (!TryDecodeFrameBytes(frame.Base64, maxBytesEach, out bytes))
            return false;

        var mime = (frame.Mime ?? "image/jpeg").Trim().ToLowerInvariant();
        ext = mime.Contains("png", StringComparison.Ordinal) ? "png" : "jpg";
        label = NormalizeClientFrameLabel(frame.Label);
        return true;
    }

    private static bool TryDecodeFrameBytes(string base64, int maxBytesEach, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var b64 = StripDataUrlPrefix(base64.Trim());
        try
        {
            bytes = Convert.FromBase64String(b64);
        }
        catch
        {
            return false;
        }

        if (bytes.Length < 32 || bytes.Length > maxBytesEach)
            return false;
        return true;
    }

    private static string StripDataUrlPrefix(string b64)
    {
        // Allow accidental data-URL paste
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            return b64[(comma + 1)..];
        return b64;
    }

    private static string NormalizeClientFrameLabel(string? raw)
    {
        var label = string.IsNullOrWhiteSpace(raw)
            ? CurrentClipLabel
            : raw.Trim().ToUpperInvariant();
        if (label is not ("PREVIOUS_CLIP_TAIL" or CurrentClipLabel))
            return CurrentClipLabel;
        return label;
    }

    private static string GetStr(JsonElement el, string name, string fallback)
    {
        if (!el.TryGetProperty(name, out var p)) return fallback;
        return p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];

    private sealed class ClipPlan
    {
        public string VisualPrompt { get; set; } = "";
        public string Dialogue { get; set; } = "";
        public string Speaker { get; set; } = "";
        public string Delivery { get; set; } = "";
        public List<string> Characters { get; } = new();
    }
}
