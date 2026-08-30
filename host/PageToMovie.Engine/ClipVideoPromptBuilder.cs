using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Adaptation.Contracts;
using PageToMovie.Engine.Deterministic.Pronunciation;
using PageToMovie.Core.Models;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Builds Grok video prompts (character variables + visual + audio) and resolves
/// character ref image paths for reference-to-video / image-to-video.
/// Owns CAST COUNT; strips Stage2-embedded count from action prose.
/// </summary>
public static class ClipVideoPromptBuilder
{
    public const string ModeVideoExtend = "video-extend";
    private const string ModeContinue = "continue";
    private const string CharactersOnScreenKey = "characters_on_screen";
    private const string PrimarySubjectKey = "primary_subject";
    private const string AudioTag = "Audio";
    private const string StyleLockPrefix = "STYLE LOCK: ";
    /// <summary>Provider default negatives (not stored per-clip in Stage 2 blueprint).</summary>
    public static string GlobalNegativePrompt { get; set; } = Stage2PlannerService.GlobalNegativeDefault;

    /// <summary>
    /// xAI Grok video API hard limit on the <c>prompt</c> string (~4096 chars).
    /// Build and pre-budget to this; retry shorten is a safety net only.
    /// </summary>
    public const int VideoPromptHardCapChars = 4000;

    /// <summary>
    /// Soft ceiling for internal assembly before addenda (same as video hard cap).
    /// Prefer fitting under <see cref="VideoPromptHardCapChars"/> at build time.
    /// </summary>
    public const int MaxPromptChars = VideoPromptHardCapChars;

    private static readonly string[] PromptTooLongPhrases =
    {
        "prompt too long",
        "prompt length exceeds",
        "exceeds the maximum allowed length",
        "context length",
        "maximum context",
        "max context",
        "token limit",
        "too many tokens",
        "context_length_exceeded",
        "maximum allowed length",
        "payload too large",
        "request entity too large",
    };

    public sealed class CharacterProfile
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Description { get; init; } = "";
        public string VisualLock { get; init; } = "";
        /// <summary>Sticky identity garments from the seed. Clothes belong here, not in VisualLock.</summary>
        public IReadOnlyList<string> WardrobeAlways { get; init; } = Array.Empty<string>();
        public string VoiceProfile { get; init; } = "";
        public string VoiceLabel { get; init; } = "";
        /// <summary>Saved Imagine / catalog preset voice id (same cast_seeds store).</summary>
        public string ImagineVoiceId { get; init; } = "";
        public string Gender { get; init; } = "";
        public string AgeBand { get; init; } = "";
        public bool VoiceOnly { get; init; }
        /// <summary>Explicit cast_kind from the cast seed (group/chorus/ensemble/individual/…), if any.</summary>
        public string CastKind { get; init; } = "";
    }

    public sealed class PromptBuildResult
    {
        /// <summary>Full flat prompt sent to the video API (may include project house-rule addenda).</summary>
        public string Prompt { get; init; } = "";
        /// <summary>Ordered character ref images for reference_images / &lt;IMAGE_n&gt; tags.</summary>
        public IReadOnlyList<string> ReferenceImagePaths { get; init; } = Array.Empty<string>();
        /// <summary>When set, image-to-video start frame (e.g. last frame of previous clip).</summary>
        public string? StartFrameImagePath { get; init; }
        public string Mode { get; init; } = "fresh";
        /// <summary>All character keys referenced (includes voice-only speakers).</summary>
        public IReadOnlyList<string> CharacterKeys { get; init; } = Array.Empty<string>();
        /// <summary>On-screen only keys used for CAST COUNT and ref attachment.</summary>
        public IReadOnlyList<string> OnScreenKeys { get; init; } = Array.Empty<string>();
        public int CastCount { get; init; }
        public string StyleHead { get; init; } = "";
        public string CharacterVariables { get; init; } = "";
        public string AudioBlock { get; init; } = "";
        public string ContinuityBlock { get; init; } = "";
        public string ActionText { get; init; } = "";
        public string CastCountLine { get; init; } = "";
        /// <summary>Whether locked refs were attached to the API payload for this build.</summary>
        public bool RefsAttachedToApi { get; init; }
        /// <summary>Log-summary tail: everything after the leading <c>mode=</c> token.</summary>
        public string PromptLogDetails { get; init; } = "";
        /// <summary>
        /// One-line log summary — the generation <see cref="Mode"/> plus
        /// <see cref="PromptLogDetails"/>. Callers that already log the mode themselves use
        /// <see cref="PromptLogDetails"/> instead, so the token is never printed twice.
        /// </summary>
        public string PromptLogSummary =>
            PromptLogDetails.Length == 0 ? "" : $"mode={Mode} {PromptLogDetails}";
        /// <summary>Scene/clip location key when a set plate was considered.</summary>
        public string? LocationKey { get; init; }
        /// <summary>True when a locked location plate was attached as a reference image.</summary>
        public bool LocationRefAttached { get; init; }
        /// <summary><IMAGE_n> tag for the location plate when attached.</summary>
        public string? LocationImageTag { get; init; }
        /// <summary>Preset <c>voice_id</c> values for <c>reference_audios</c>, in <c>AUDIO_n</c> order.</summary>
        public IReadOnlyList<string> ReferenceAudioVoiceIds { get; init; } = Array.Empty<string>();

        public PromptBuildResult WithPrompt(string prompt, string? summarySuffix = null) => new()
        {
            Prompt = prompt,
            ReferenceImagePaths = ReferenceImagePaths,
            StartFrameImagePath = StartFrameImagePath,
            Mode = Mode,
            CharacterKeys = CharacterKeys,
            OnScreenKeys = OnScreenKeys,
            CastCount = CastCount,
            StyleHead = StyleHead,
            CharacterVariables = CharacterVariables,
            AudioBlock = AudioBlock,
            ContinuityBlock = ContinuityBlock,
            ActionText = ActionText,
            CastCountLine = CastCountLine,
            RefsAttachedToApi = RefsAttachedToApi,
            LocationKey = LocationKey,
            LocationRefAttached = LocationRefAttached,
            LocationImageTag = LocationImageTag,
            ReferenceAudioVoiceIds = ReferenceAudioVoiceIds,
            PromptLogDetails = string.IsNullOrWhiteSpace(summarySuffix)
                ? PromptLogDetails
                : PromptLogDetails + summarySuffix,
        };
    }

    public static PromptBuildResult Build(
        JsonElement clipEl,
        string projectDir,
        IReadOnlyDictionary<string, CharacterProfile>? characters = null,
        string? previousClipVisualPrompt = null,
        string? previousClipVideoPath = null,
        string? startFrameImagePath = null,
        int maxRefs = 5,
        string? styleHead = null,
        string? videoModel = null,
        string? fallbackLocationKey = null,
        string? previousClipExtendFileId = null,
        ClipCorrection? correction = null,
        string? visualMedium = null)
    {
        characters ??= new Dictionary<string, CharacterProfile>(StringComparer.OrdinalIgnoreCase);
        var promptMaxLen = ResolvePromptMaxLen(videoModel);

        // Mode follows actual media inputs, not blueprint cont alone.
        // Cast-change reseed (PR2) clears previousClipVideoPath while blueprint may still say
        // extend_previous — that must be fresh+refs, not continue-without-frame.
        var hasPrevVideo = HasExistingMedia(previousClipVideoPath) || !string.IsNullOrWhiteSpace(previousClipExtendFileId);
        var hasStartFrame = HasExistingMedia(startFrameImagePath);
        var mode = ResolveGenerationMode(hasPrevVideo, hasStartFrame);

        // On-screen cast = plan only (never free-text names from dialogue prose)
        var onScreenKeys = ResolveOnScreenCharacterKeys(clipEl)
            .Where(k => !IsVoiceOnlyKey(k, characters))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Variables may include voice-only speaker + primary subject without putting them on camera
        var allKeys = ResolveClipCharacterKeys(clipEl, characters)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rawVisual = ReadVisualPrompt(clipEl);
        var actionText = DropWhatTheSourceVideoShows(SanitizeActionText(rawVisual, onScreenKeys), mode);

        // Clip location_id, else scene primary_location_id from caller (many clips omit location_id).
        var locationKeyResolved = ResolveClipLocationKey(clipEl) ?? NormalizeLocationKey(fallbackLocationKey);
        var hasLocationPlate = HasLockedLocationPlate(projectDir, locationKeyResolved);
        // Reserve one IMAGE slot for a locked set plate so multi-cast scenes still keep place identity.
        var charRefBudget = ResolveCharRefBudget(hasLocationPlate, maxRefs);

        // QA correction: when the wrong character spoke last time, the locked speaker's portrait goes
        // first in the reference set (IMAGE_1) so the model binds the mouth to the right face.
        if (correction?.SpeakerLockKey is { Length: > 0 } lockKey && onScreenKeys.Any(k => string.Equals(k, lockKey, StringComparison.OrdinalIgnoreCase)))
            onScreenKeys = onScreenKeys.OrderBy(k => string.Equals(k, lockKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        var refPaths = FindCharacterRefPathsForKeys(onScreenKeys, projectDir, charRefBudget);

        // Fresh gen attaches locked plates when available. Location-only establishing shots
        // (no on-screen cast) still get a set plate if locked — don't require character refs first.
        var useReferenceImages = ShouldAttachReferenceImages(
            startFrameImagePath, hasPrevVideo, refPaths.Count, hasLocationPlate);

        var imageTagByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? locationKey = locationKeyResolved;
        string? locationImageTag = null;
        var locationRefAttached = false;
        if (useReferenceImages)
        {
            refPaths = AttachReferenceImages(
                onScreenKeys, projectDir, charRefBudget, maxRefs, locationKey,
                imageTagByKey, out locationImageTag, out locationRefAttached);
        }

        var resolvedMedium = ResolveVisualMedium(projectDir, visualMedium);
        var style = ResolveStyleLockForGeneration(projectDir, styleHead, rawVisual, resolvedMedium);
        // One StyleLock per prompt, from project render_style_lock / StyleLockFor.
        // Mary19 shipped house-rule watercolor at the head AND "STYLE LOCK: stylized 3D…"
        // still sitting in the action (prose, not a tag). Strip every lock from the action.
        actionText = StripStyleLocksFromAction(actionText);
        var activeKeys = ResolveFocusKeysForClip(onScreenKeys, clipEl);
        var wardrobeByKey = CollectGenerateWardrobe(rawVisual, allKeys, characters);
        var varBlock = BuildCharacterVariablesBlock(
            allKeys, characters, imageTagByKey, useReferenceImages, activeKeys, wardrobeByKey);
        var (audioTags, referenceAudioVoiceIds) = ResolveReferenceAudioTags(
            videoModel, clipEl, characters, hasPrevVideo);
        var audioBlock = BuildAudioBlock(clipEl, characters, correction, audioTags, mode);
        var continuityBlock = BuildContinuityBlock(
            mode, onScreenKeys, useReferenceImages, previousClipVisualPrompt, resolvedMedium, rawVisual);
        var castCountLine = FormatCastCountLine(onScreenKeys);
        var actionTagged = TagActionWithImageRefs(actionText, imageTagByKey);

        var assembled = EnsureSingleStyleLock(
            AppendPromptSections(
                style, varBlock, locationRefAttached, locationImageTag, locationKey,
                castCountLine, audioBlock, continuityBlock, clipEl, actionTagged, resolvedMedium),
            style);
        var prompt = FitPromptToVideoBudget(assembled, promptMaxLen);
        IReadOnlyList<string> attached = useReferenceImages ? refPaths : Array.Empty<string>();

        return new PromptBuildResult
        {
            Prompt = prompt,
            ReferenceImagePaths = attached,
            StartFrameImagePath = startFrameImagePath,
            Mode = mode,
            CharacterKeys = allKeys,
            OnScreenKeys = onScreenKeys,
            CastCount = onScreenKeys.Count,
            StyleHead = style,
            CharacterVariables = varBlock,
            AudioBlock = audioBlock,
            ContinuityBlock = continuityBlock,
            ActionText = actionTagged,
            CastCountLine = castCountLine,
            RefsAttachedToApi = useReferenceImages && attached.Count > 0,
            LocationKey = locationKey,
            LocationRefAttached = locationRefAttached,
            LocationImageTag = locationImageTag,
            ReferenceAudioVoiceIds = referenceAudioVoiceIds,
            PromptLogDetails = FormatPromptLogDetails(
                allKeys.Count, onScreenKeys.Count, attached.Count,
                FormatLocationLogLabel(locationRefAttached, locationKey),
                startFrameImagePath, prompt.Length, previousClipVideoPath),
        };
    }

    private static int ResolvePromptMaxLen(string? videoModel)
    {
        // Product callers pass the project video id. An unknown id fails; an omitted id
        // uses the hard cap so prompt-composition tests need no catalog default.
        // Never substitute capabilities[].defaultModelId.
        if (string.IsNullOrWhiteSpace(videoModel))
            return VideoPromptHardCapChars;

        var entry = SupportedModelCatalog.Find(videoModel.Trim(), ModelCapability.Video);
        if (entry is null || !entry.Enabled)
            throw new InvalidOperationException(
                ProjectModelSelection.FormatUnknownModel("video", videoModel));

        return entry.MaxPromptLength
            ?? throw new InvalidOperationException(
                $"video: model '{entry.Id}' has no maxPromptLength in models_catalog.json.");
    }

    private static bool HasExistingMedia(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static string ResolveGenerationMode(bool hasPrevVideo, bool hasStartFrame)
    {
        if (hasPrevVideo)
            return ModeVideoExtend;
        return hasStartFrame ? ModeContinue : "fresh";
    }

    private static string ReadVisualPrompt(JsonElement clipEl) =>
        clipEl.TryGetProperty("visual_prompt", out var vp)
            ? (vp.GetString() ?? "").Trim()
            : "";

    private static bool HasLockedLocationPlate(string projectDir, string? locationKey) =>
        !string.IsNullOrWhiteSpace(locationKey) &&
        ResolveLocationRefPath(projectDir, locationKey) is not null;

    private static int ResolveCharRefBudget(bool hasLocationPlate, int maxRefs) =>
        hasLocationPlate && maxRefs > 1 ? maxRefs - 1 : maxRefs;

    private static bool ShouldAttachReferenceImages(
        string? startFrameImagePath,
        bool hasPrevVideo,
        int refPathCount,
        bool hasLocationPlate) =>
        string.IsNullOrWhiteSpace(startFrameImagePath) &&
        !hasPrevVideo &&
        (refPathCount > 0 || hasLocationPlate);

    private static List<string> AttachReferenceImages(
        IReadOnlyList<string> onScreenKeys,
        string projectDir,
        int charRefBudget,
        int maxRefs,
        string? locationKey,
        Dictionary<string, string> imageTagByKey,
        out string? locationImageTag,
        out bool locationRefAttached)
    {
        var orderedPaths = new List<string>();
        var n = 0;
        foreach (var key in onScreenKeys.OrderBy(CharacterRefPriority)
                     .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (orderedPaths.Count >= charRefBudget) break;
            var path = ResolveCharacterRefPath(projectDir, key);
            if (path is null) continue;
            n++;
            orderedPaths.Add(path);
            imageTagByKey[key] = $"<IMAGE_{n}>";
        }

        // Soft: one location set plate after faces (reserved slot when plate exists).
        locationImageTag = null;
        locationRefAttached = false;
        if (!string.IsNullOrWhiteSpace(locationKey) && orderedPaths.Count < maxRefs)
        {
            var locPath = ResolveLocationRefPath(projectDir, locationKey);
            if (locPath is not null)
            {
                n++;
                orderedPaths.Add(locPath);
                locationImageTag = $"<IMAGE_{n}>";
                locationRefAttached = true;
                imageTagByKey[locationKey] = locationImageTag;
                if (locationKey.StartsWith("Loc_", StringComparison.OrdinalIgnoreCase))
                {
                    var bare = locationKey["Loc_".Length..];
                    imageTagByKey[bare] = locationImageTag;
                }
            }
        }

        return orderedPaths;
    }

    private static string BuildContinuityBlock(
        string mode,
        IReadOnlyList<string> onScreenKeys,
        bool useReferenceImages,
        string? previousClipVisualPrompt,
        string? visualMedium = null,
        string? currentClipVisualPrompt = null)
    {
        // "Positions come from that frame" is the tie-breaker, and it is here because the two tags
        // can disagree: the plan writes <Action> from a story beat, so a continuation clip whose
        // beat restages the room hands the model an Action that contradicts this block. Mary19
        // S02C02 was told to pick up from a last frame with the animal at the door and to film it
        // among the desks at the other end of the room; it followed the Action and teleported.
        // Stage 2 is where that is prevented (the extend/cut staging test, and ShotPlanLint's
        // continuation_unchecked). This only decides which way the model leans when a plan built
        // before that test still contradicts itself — holding position beats teleporting.
        const string sameMedium =
            "Same art medium and renderer as the source clip — do not switch illustration, watercolor, CG, or live-action. ";
        var continuityBlock = mode switch
        {
            ModeVideoExtend => PromptTags.Wrap("Continuity",
                "This is a seamless EXTENSION of the provided previous video. " +
                "Pick up from its last frame. Same character identity, wardrobe, lighting, and location. " +
                sameMedium +
                "Positions come from that frame: everyone and everything starts exactly where the previous " +
                "clip left them, and the action below is what happens next from there — not a new arrangement. " +
                "Natural progressive motion only — do not invent a new establishing shot or redesign faces/outfits."),
            ModeContinue => PromptTags.Wrap("Continuity",
                "Continue seamlessly from the provided starting frame (end of previous clip). " +
                "Same character identity, wardrobe, lighting, and location. " +
                sameMedium +
                "Positions come from that frame: everyone and everything starts exactly where the previous " +
                "clip left them, and the action below is what happens next from there — not a new arrangement. " +
                "Natural progressive motion only — " +
                "do not invent a new establishing shot or redesign faces/outfits."),
            _ =>
                "Follow the camera framing and location in this prompt exactly. " +
                "Prioritize the PRIMARY subject and ONE clear action with visible motion; " +
                "background characters may stay mostly still.",
        };

        // video-extend cannot attach locked plates (API continues from previous video only).
        // Reinforce identity from CHARACTER VARIABLES text so faces/wardrobe do not drift.
        if (mode is ModeVideoExtend or ModeContinue)
            continuityBlock += IdentityReinforceBlock(onScreenKeys, useReferenceImages, visualMedium);

        // Video-extend and continue both hand the model the previous clip as VISUAL input (whole
        // video, or its last frame). Re-describing that clip in prose adds nothing it cannot
        // already see — and it actively restages: Mary19 S02C02 replayed S01's "Mary comes through
        // the door, the lamb follows at her heel" because that sentence rode in here as
        // instruction text. It was worse than a wash. The block ran ~37% of the 4000-char budget,
        // its own "already provided as video input" note is the first thing CompressPromptText
        // drops, and HeadCap then truncated the clip's OWN camera direction off the end. The
        // <Continuity> block above already carries the only part that mattered: same identity,
        // wardrobe, lighting and location, pick up from the last frame.
        if (mode is ModeContinue or ModeVideoExtend)
            return continuityBlock;

        if (!string.IsNullOrWhiteSpace(previousClipVisualPrompt) && mode == "fresh")
        {
            // Cast-change reseed: no video input at all, so the prior clip's LOOK is the only way to
            // hold location/lighting across the cut. Its ACTION is not — re-describing what already
            // happened is what makes the model replay it (Mary19 S03C02 fresh take re-spoke C01's
            // verse from this block). Keep the look, drop the action.
            var look = PreviousClipLookOnly(previousClipVisualPrompt, onScreenKeys, currentClipVisualPrompt);
            if (string.IsNullOrWhiteSpace(look))
                return continuityBlock;
            return PromptTags.WrapWithNote("Context",
                "prior clip's look only — match location/lighting if still valid; " +
                "identity from Characters + locked plates only; its action already happened",
                "\n" + look + "\n") + "\n\n" + continuityBlock;
        }

        return continuityBlock;
    }

    /// <summary>
    /// Location/lighting/grade from a previous clip's prompt, with the action prose, camera and
    /// performance direction, and dialogue dropped. Used only where there is no visual input to
    /// carry the look — anything with the previous video (or its last frame) attached needs none
    /// of this. Empty when the prior prompt has no look to salvage.
    /// When this clip already has a Lighting or Grade tag, those are not re-embedded:
    /// <see cref="DropRepeatedDirectives"/> only drops byte-identical copies, so two different
    /// lightings would both ship.
    /// </summary>
    internal static string PreviousClipLookOnly(
        string? previousClipVisualPrompt,
        IReadOnlyList<string> onScreenKeys,
        string? currentClipVisualPrompt = null)
    {
        if (string.IsNullOrWhiteSpace(previousClipVisualPrompt))
            return "";
        var clean = SanitizeActionText(previousClipVisualPrompt, onScreenKeys);
        var parts = new List<string>();

        // Tags only — Stage 2 emits all three. No prose fallback: pattern-matching the old
        // flattened form would be guesswork that quietly matches nothing on a current plan.
        foreach (var tag in new[] { PromptFieldTags.Setting, PromptFieldTags.Lighting, PromptFieldTags.Grade })
        {
            if (PromptTags.Has(currentClipVisualPrompt, tag))
                continue;
            var m = CommonRegex.Match(clean, $@"<{tag}>.*?</{tag}>", RegexOptions.Singleline);
            if (m.Success)
                parts.Add(m.Value.Trim());
        }
        return string.Join(" ", parts);
    }

    private static string TagActionWithImageRefs(
        string actionText,
        IReadOnlyDictionary<string, string> imageTagByKey)
    {
        var actionTagged = actionText;
        foreach (var (key, tag) in imageTagByKey)
        {
            if (!string.IsNullOrWhiteSpace(key))
                actionTagged = actionTagged.Replace(key, $"{key} {tag}", StringComparison.OrdinalIgnoreCase);
        }
        return actionTagged;
    }

    private static string FormatCastCountLine(IReadOnlyList<string> onScreenKeys) =>
        onScreenKeys.Count > 0
            ? PromptTags.Wrap("CastCount",
                $"exactly {onScreenKeys.Count} distinct on-screen character identity(ies) only — " +
                string.Join(", ", onScreenKeys) +
                ". Do not invent extra people, duplicate faces, or crowd extras not listed.")
            : "";

    private static string FormatLocationLogLabel(bool locationRefAttached, string? locationKey)
    {
        if (locationRefAttached)
            return locationKey ?? "";
        return locationKey is null ? "none" : "unlocked";
    }

    private static string FormatPromptLogDetails(
        int allKeysCount,
        int onScreenCount,
        int attachedCount,
        string locLabel,
        string? startFrameImagePath,
        int promptLength,
        string? previousClipVideoPath)
    {
        return $"chars={allKeysCount} onScreen={onScreenCount} " +
        $"refs={attachedCount} loc={locLabel} " +
        $"startFrame={(startFrameImagePath is null ? "no" : "yes")} " +
        $"promptLen={promptLength}" +
        (previousClipVideoPath is { Length: > 0 }
            ? $" prevVideo={Path.GetFileName(previousClipVideoPath)}"
            : "");
    }

    private static string AppendPromptSections(
        string style,
        string varBlock,
        bool locationRefAttached,
        string? locationImageTag,
        string? locationKey,
        string castCountLine,
        string audioBlock,
        string continuityBlock,
        JsonElement clipEl,
        string actionTagged,
        string? visualMedium = null)
    {
        var sb = new StringBuilder();
        AppendStyleHead(sb, style);
        AppendOptionalParagraph(sb, varBlock);
        AppendSetReference(sb, locationRefAttached, locationImageTag, locationKey);
        AppendOptionalParagraph(sb, castCountLine);
        AppendOptionalParagraph(sb, audioBlock);
        sb.AppendLine(continuityBlock);
        sb.AppendLine();
        sb.AppendLine(PromptTags.Open("Clip"));
        AppendClipPacingAndClose(sb, clipEl);
        sb.Append(actionTagged);
        // Resolution/frame rate are NOT re-echoed as prompt text here — they're already real,
        // separate fields in the video API's request payload (GrokVideoClient.SubmitFreshOnceAsync:
        // "resolution", "duration"), so appending "/ 480p, 24fps" as prose was pure duplication
        // with no effect on what the API actually renders at.
        AppendTrailingBlock(sb, BuildNegativeBlock(clipEl, visualMedium));
        // clip_gen_rules.txt is retired: every remaining house bullet restated a dedicated
        // writer or named example media that fought STYLE LOCK. Do not append it.
        AppendHouseRules(sb);
        return sb.ToString().Trim();
    }

    private static void AppendStyleHead(StringBuilder sb, string style)
    {
        if (string.IsNullOrWhiteSpace(style)) return;
        sb.AppendLine(style.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
            ? style
            : StyleLockPrefix + style);
        sb.AppendLine();
    }

    private static void AppendOptionalParagraph(StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.AppendLine(text);
        sb.AppendLine();
    }

    private static void AppendSetReference(
        StringBuilder sb,
        bool locationRefAttached,
        string? locationImageTag,
        string? locationKey)
    {
        if (!locationRefAttached ||
            string.IsNullOrWhiteSpace(locationImageTag) ||
            string.IsNullOrWhiteSpace(locationKey))
            return;
        sb.AppendLine(PromptTags.Wrap("SetReference",
            $"{locationImageTag} is the locked LOCATION / SET plate for {locationKey}. " +
            "Match architecture, materials, props, and depth of that plate. " +
            "Do not invent a different building or landscape. Characters from CHARACTER VARIABLES " +
            "perform in this set — faces come from character plates, place from this set plate."));
        sb.AppendLine();
    }

    private static void AppendClipPacingAndClose(StringBuilder sb, JsonElement clipEl)
    {
        // Unlike resolution/fps (pure technical spec, no effect on content — see below), duration
        // genuinely changes how the described action should be PACED: the same camera move/action
        // described for a 12s shot needs to unfold much more gradually than for a 3s one. The API's
        // "duration" field controls actual render length; this line just tells the model how to
        // pace what it's rendering within that length.
        if (TryGetClipDurationSeconds(clipEl, out var clipDurSec))
        {
            sb.AppendLine(
                $"This is a {clipDurSec}-second shot — pace the described camera movement and action " +
                $"to unfold naturally across the full {clipDurSec} seconds; do not rush, compress, or " +
                "pad with a static hold.");
        }
        // This line used to be unconditional — telling the model to "end when the spoken line
        // finishes" even on silent beats with empty audio_payload.dialogue. With no line ever
        // specified, and CHARACTER VARIABLES listing every on-screen character's Voice profile
        // right above it, that primed the model to invent speech/mouth movement on someone.
        // Branch it so silent beats get an explicit "no dialogue, keep mouths neutral" cue instead.
        sb.AppendLine(ClipHasSpokenDialogue(clipEl)
            ? "End cleanly when the spoken line and primary action finish — " +
              "do not hold a frozen pose or empty silence after dialogue."
            : "Silent beat — no dialogue in this clip. Do not show any on-screen character " +
              "speaking or mouthing words; keep mouths closed/neutral. " +
              "End cleanly when the primary physical action finishes.");
    }

    private static bool ClipHasSpokenDialogue(JsonElement clipEl) =>
        clipEl.TryGetProperty(JsonKeys.AudioPayload, out var apForClose) &&
        apForClose.TryGetProperty("dialogue", out var dlgForClose) &&
        !string.IsNullOrWhiteSpace(dlgForClose.GetString());

    private static void AppendTrailingBlock(StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(text);
    }

    private static void AppendHouseRules(StringBuilder sb)
    {
        // Only a HOUSE RULES: body with real bullets is prompt text. Comment-only /
        // retired files (current clip_gen_rules.txt) must not ride along as addenda.
        var body = PromptBodyFromClipGenRules(TryLoadClipGenRules());
        if (string.IsNullOrWhiteSpace(body)) return;
        sb.AppendLine();
        sb.AppendLine();
        sb.Append(body);
    }

    /// <summary>
    /// Prompt-ready house-rule body, or empty when the file is comments / blank.
    /// </summary>
    internal static string PromptBodyFromClipGenRules(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var t = line.TrimEnd();
            if (t.TrimStart().StartsWith('#')) continue;
            sb.AppendLine(t);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Reads a clip's planned duration_seconds (Stage2-assigned) for the pacing line in
    /// <see cref="Build"/> — accepts number or numeric-string encodings defensively, same tolerance
    /// as <see cref="ClipDurationEstimator.EstimateForClip"/>.</summary>
    private static bool TryGetClipDurationSeconds(JsonElement clipEl, out int seconds)
    {
        if (ClipDuration.TryReadNumericSeconds(clipEl, out var d))
        {
            seconds = (int)Math.Round(d, MidpointRounding.AwayFromZero);
            return seconds > 0;
        }
        seconds = 0;
        return false;
    }

    /// <summary>
    /// Remove Stage2-embedded CAST COUNT so the builder owns a single count line, and the legacy
    /// <c>&lt;Speech&gt;</c> block so the AUDIO block owns the spoken line.
    /// Ensures each on-screen key appears at least once in action prose.
    /// </summary>
    /// <summary>
    /// On a continuation, removes the blocks describing what the source video already shows.
    /// </summary>
    /// <remarks>
    /// An extend is generated from the previous clip's video, so the setting, lighting, framing,
    /// optics and grade are all visibly present in the input. Sending them again does not confirm
    /// them — it asks the model to establish a shot, and it does, from scratch: the subject is
    /// restaged and the continuation breaks.
    ///
    /// <para>Measured against the provider's extend endpoint on Mary19 S02C02, repeated runs. With
    /// these blocks present the animal was thrown back to the front of the room; with them removed
    /// and the source video unchanged it stayed where the previous clip left it. Removing the
    /// continuity block as well made it slightly WORSE, so that one stays — it is carrying its
    /// weight, unlike these.</para>
    ///
    /// <para>What describes the NEW footage is kept: the action, the acting note, the shot length,
    /// the negatives, and the identity blocks an extend cannot supply as reference plates.</para>
    /// </remarks>
    private static string DropWhatTheSourceVideoShows(string actionText, string mode)
    {
        if (mode is not (ModeVideoExtend or ModeContinue))
            return actionText;
        var trimmed = actionText;
        foreach (var tag in SourceVideoAlreadyShows)
            trimmed = BlockRegexFor(tag).Replace(trimmed, "");
        return CollapseWhitespace(trimmed).Trim();
    }

    /// <summary>Blocks whose content is visible in the video an extend continues from.</summary>
    private static readonly string[] SourceVideoAlreadyShows =
    {
        PromptFieldTags.Setting,
        PromptFieldTags.Lighting,
        PromptFieldTags.Camera,
        PromptFieldTags.Optics,
        PromptFieldTags.Grade,
    };

    private static readonly Dictionary<string, Regex> BlockRegexCache = new(StringComparer.Ordinal);

    private static Regex BlockRegexFor(string tag)
    {
        lock (BlockRegexCache)
        {
            if (!BlockRegexCache.TryGetValue(tag, out var re))
            {
                re = new Regex($@"<{tag}>.*?</{tag}>\s*",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
                    CommonRegex.Timeout);
                BlockRegexCache[tag] = re;
            }
            return re;
        }
    }

    private static string CollapseWhitespace(string text) =>
        CommonRegex.Replace(text, @"[ 	]{2,}", " ");

    public static string SanitizeActionText(string visual, IReadOnlyList<string>? onScreenKeys = null)
    {
        if (string.IsNullOrWhiteSpace(visual)) return "";
        var v = visual.Trim();
        // Strip accidental res/fps suffixes from action text (builder re-appends current job res)
        v = ResFpsSuffixRegex1.Replace(v, "").Trim();
        v = ResFpsSuffixRegex2.Replace(v, "").Trim();
        v = ResFpsSuffixRegex3.Replace(v, "").Trim();
        v = CastCountRegex.Replace(v, "");
        v = NoExtraPeopleRegex.Replace(v, "");
        v = LegacySpeechBlockRegex.Replace(v, "");
        v = LegacySoundBlockRegex.Replace(v, "");
        v = StripFountainLeakage(v);
        // Blueprint may embed lip-sync / says quotes with crushed dashes — speech-safe for gen
        v = SanitizeSpokenQuotesInVisual(v);
        v = SimplifyVisual(v);
        if (onScreenKeys is { Count: > 0 })
        {
            foreach (var key in onScreenKeys.Where(k => !v.Contains(k, StringComparison.OrdinalIgnoreCase)))
                v = $"{v} {key} is on screen.".Trim();
        }
        return v.Trim();
    }

    /// <summary>
    /// Speech-safe form of a dialogue line for video/audio gen payloads.
    /// Same words; clearer pauses. Fixes fountain em-dashes and parser-crushed
    /// <c>True!-nervous-very</c> glue so models do not mumble hyphen compounds.
    /// Keeps real compounds (to-day, writing-desk, good-bye, …). Does not paraphrase.
    /// Empty/whitespace → empty string.
    /// </summary>
    public static string SanitizeSpokenDialogue(string? dialogue)
    {
        if (string.IsNullOrWhiteSpace(dialogue))
            return "";

        var t = dialogue.Trim();

        // Unicode dashes → spaced em-dash pause
        t = UnicodeDashesRegex.Replace(t, " — ");
        // ASCII double-hyphen pause
        t = DoubleHyphenRegex.Replace(t, " — ");
        // Parser glue after ! ? . ; :  e.g. True!-nervous → True! nervous
        t = PunctuationDashRegex.Replace(t, "$1 ");

        // Letter-letter ASCII hyphen may be (a) crushed em-dash pause or (b) a real compound.
        // Mask known/safe compounds, expand remaining mid-word hyphens as pauses, unmask.
        t = ExpandNonCompoundLetterHyphens(t);

        // Collapse whitespace
        t = WhitespaceSingleRegex.Replace(t, " ").Trim();
        // After .!? an em-dash pause is redundant — drop it and capitalize the next word
        // e.g. True! — nervous → True! Nervous
        t = DashCapitalizationRegex.Replace(
            t,
            m => m.Groups[1].Value + " " +
                 char.ToUpper(m.Groups[2].Value[0], CultureInfo.InvariantCulture) +
                 m.Groups[2].Value[1..]);
        // Capitalize first letter after sentence-ending punctuation (no dash case)
        t = PunctuationCapitalizationRegex.Replace(
            t,
            m => m.Groups[1].Value + " " +
                 char.ToUpper(m.Groups[2].Value[0], CultureInfo.InvariantCulture) +
                 m.Groups[2].Value[1..]);

        return t;
    }

    /// <summary>
    /// Expand letter-letter ASCII hyphens to speech pauses, except real compounds
    /// (Victorian to-day / writing-desk / good-bye, modern well-known, etc.).
    /// </summary>
    private static string ExpandNonCompoundLetterHyphens(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('-') < 0)
            return text;

        // Mask protected compounds so the generic expand cannot touch them
        var masks = new List<string>();
        var masked = ProtectedCompoundHyphen.Replace(text, m =>
        {
            var token = $"\uE000{masks.Count}\uE001";
            masks.Add(m.Value);
            return token;
        });

        // Mask short-left compounds only (to-day, age-old, mid-*). Do NOT mask short-right
        // (healthily-how is a crushed pause; good-bye is on the protected list).
        masked = CommonRegex.Replace(
            masked,
            @"\b(\p{L}{1,3})-(\p{L}+)\b",
            m =>
            {
                var token = $"\uE000{masks.Count}\uE001";
                masks.Add(m.Value);
                return token;
            });

        // Remaining letter-letter hyphens → pause (nervous-very, unhappy-to)
        masked = CommonRegex.Replace(masked, @"(?<=\p{L})-(?=\p{L})", " — ");

        // Unmask
        for (var i = 0; i < masks.Count; i++)
            masked = masked.Replace($"\uE000{i}\uE001", masks[i], StringComparison.Ordinal);

        return masked;
    }

    /// <summary>
    /// High-frequency hyphenated compounds (any book) that must stay hyphenated for speech.
    /// Not title-specific — Victorian / general English patterns from the fountain corpus.
    /// </summary>
    private static readonly Regex ProtectedCompoundHyphen = new(
        @"\b(?:" +
        // time / greeting
        @"to-(?:day|morrow|night)|good-(?:bye|night|day)|" +
        @"half-(?:past|an?|a)|half-an?-crown|" +
        // common literary / modern compounds
        @"writing-desk|well-known|well-used|age-old|door-nail|" +
        @"tea-(?:time|party|things|pot|cup)|bread-and-butter|" +
        @"look-out|sky-rocket|rose-tree|day-school|sea-shore|" +
        @"bed-curtains?|ill-(?:used|will)|even-handed|" +
        @"tight-fisted|grind-stone|self-\p{L}+|mid-\p{L}+|" +
        @"jack-in-the-box|pig-baby|and-butter|cattle-killer|" +
        // number words: eighty-seven, twenty-one
        @"(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)-\p{L}+|" +
        // modern speech compounds often kept hyphenated
        @"co-\p{L}+|re-\p{L}+|pre-\p{L}+|non-\p{L}+" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        CommonRegex.Timeout);

    // These five fire on none of the sample plans (278 clips, 4 projects) — and they stay. They
    // are GUARDS, not optimizations: if a plan ever does carry an embedded CAST COUNT or a res/fps
    // suffix, the builder appends its own and the model gets two contradictory instructions.
    // Never firing is what a working guard looks like. (Contrast the film-stock rewrite deleted
    // from CompressPromptText: that was a ~20-char compression win, so never firing made it
    // worthless. Dead optimizations are safe to delete; dead guards are not.)
    private static readonly Regex ResFpsSuffixRegex1 = new(@"\s*/\s*\d{3,4}p\s*,\s*\d{2}fps\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ResFpsSuffixRegex2 = new(@"\s*/\s*\d+p[^/]*24fps\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ResFpsSuffixRegex3 = new(@"\s*/\s*\d{3,4}p\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CastCountRegex = new(@"\bCAST COUNT:\s*exactly\s+\d+[^.]*\.\s*(?:No extra people\.\s*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex NoExtraPeopleRegex = new(@"\bNo extra people\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    /// <summary>
    /// A <c>&lt;Speech&gt;</c> block baked into an older plan's <c>visual_prompt</c>. Stage 2 no
    /// longer emits one — the spoken line rides in <c>audio_payload</c> and reaches the model
    /// through the AUDIO block alone — but plans built before that still carry it, and left in
    /// place it sends the same line to the model twice. Stripped here rather than migrating the
    /// stored plans, so no existing project has to be rewritten to stop paying for the duplicate.
    /// </summary>
    private static readonly Regex LegacySpeechBlockRegex = new(
        $@"<{PromptFieldTags.Speech}>.*?</{PromptFieldTags.Speech}>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, CommonRegex.Timeout);
    /// <summary>
    /// A <c>&lt;Sound&gt;</c> block baked into an older plan's <c>visual_prompt</c>. Stage 2 no
    /// longer emits one — the same cue already reaches the model as <c>&lt;Foley&gt;</c>/
    /// <c>&lt;Score&gt;</c> inside the AUDIO block, built from <c>audio_payload</c>. Left in place
    /// it asks for the foley twice against a single request for the narration, and the foley wins
    /// the opening moment: adding this block alone to an otherwise-working extend made the narrator
    /// drop the line's first word. Stripped here so existing plans stop losing words before they
    /// are replanned.
    /// </summary>
    private static readonly Regex LegacySoundBlockRegex = new(
        $@"<{PromptFieldTags.Sound}>.*?</{PromptFieldTags.Sound}>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex UnicodeDashesRegex = new(@"\s*[\u2012\u2013\u2014\u2015]\s*", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DoubleHyphenRegex = new(@"\s*--\s*", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex PunctuationDashRegex = new(@"([!?.;:])\s*-+\s*", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex WhitespaceSingleRegex = new(@"\s+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DashCapitalizationRegex = new(@"([.!?])\s+—\s+(\p{L})", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex PunctuationCapitalizationRegex = new(@"([.!?])\s+(\p{Ll})", RegexOptions.Compiled, CommonRegex.Timeout);
    /// <summary>
    /// First real word of a spoken line, skipping anything before it that is not a word.
    /// </summary>
    /// <remarks>
    /// Anchored at position 0 this found nothing the moment a line opened with punctuation, and the
    /// opening cue silently degraded to "start speaking immediately with the first word of the
    /// line". That matters because leading punctuation is deliberate: an ellipsis or em dash at the
    /// head of a line is how you ask for a beat of hesitation before the first word, and pairing it
    /// with a cue that names no word — or worse, names the punctuation — tells the model two
    /// different things at once.
    /// </remarks>
    private static readonly Regex FirstWordMatchRegex = new(@"[\p{L}\p{N}']+[!?]?", RegexOptions.Compiled, CommonRegex.Timeout);
    /// <summary>Leading <c>[cue]</c> / <c>(cue)</c> markers before the first spoken word.</summary>
    private static readonly Regex LeadingStageCueRegex =
        new(@"^(?:\s*(?:\[[^\]]*\]|\([^)]*\)))+\s*", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ContdRegex = new(@"\s*\(\s*CONT'?D\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ContinuedRegex = new(@"\s*\(\s*CONTINUED\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex VoRegex = new(@"\s*\(\s*V\s*\.?\s*O\s*\.?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex OsRegex = new(@"\s*\(\s*O\s*\.?\s*S\s*\.?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex OcRegex = new(@"\s*\(\s*O\s*\.?\s*C\s*\.?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex PronounGlueRegex1 = new(@"\b(Character_[A-Za-z0-9_]+)\s+(He|She|They)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex PronounGlueRegex2 = new(@"\b(Character_[A-Za-z0-9_]+)\s+(His|Her|Their)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DuplicateCharacterKeyRegex = new(@"\b(Character_[A-Za-z0-9_]+)(\s+\1)+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DoubleSpacesRegex = new(@"\s{2,}", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DotSpaceRegex = new(@"\s+\.", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DoubleDotsRegex = new(@"\.\s*\.", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DismemberingRegex = new(@"\bdismember(?:ing|ed|s)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DismembermentRegex = new(@"\bdismemberment\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex SeverLimbsRegex = new(@"\bsever(?:ing|ed|s)?\s+(?:head|arms?|legs?|limbs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CutOffLimbsRegex = new(@"\bcut\s+off\s+the\s+(?:head|arms?|legs?|limbs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CorpseRegex = new(@"\bcorpse\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex HumanRemainsRegex = new(@"\bhuman\s+remains\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DepositsRemainsRegex = new(@"\bdeposits?\s+the\s+remains\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex GhastlyGoryRegex = new(@"\bghastly\s+gory\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex BloodyRemainsRegex = new(@"\bbloody\s+remains\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// Apply <see cref="SanitizeSpokenDialogue"/> to quoted lines after lip-syncs / says / narrates
    /// in Stage2 visual prose so gen sees the same speech-safe text as the AUDIO block.
    /// </summary>
    public static string SanitizeSpokenQuotesInVisual(string? visual)
    {
        if (string.IsNullOrWhiteSpace(visual))
            return visual ?? "";

        return CommonRegex.Replace(
            visual,
            @"(?<=(?:lip-syncs|says|narrates(?:\s+exactly)?)\s+)""([^""]*)""",
            m => "\"" + SanitizeSpokenDialogue(m.Groups[1].Value) + "\"",
            RegexOptions.IgnoreCase);
    }

    // RedactSpokenQuotes lived here: it blanked the PREVIOUS clip's quoted lines so the new clip
    // could not re-speak them (Mary19 S03C02 repeated S03C01's narration). It has no callers left
    // and is deleted rather than kept as a guard, because the thing it guarded is now structurally
    // impossible: extend and continue send no previous-clip prose at all, and the fresh reseed
    // path emits only <Setting>, <Lighting> and <Grade>, so a <Speech> block cannot reach the next
    // prompt to be redacted. Unreachable code is not a guard that never fires.

    /// <summary>First word/token of a spoken line (for gen cues that protect the opening).</summary>
    /// <remarks>
    /// A leading stage cue is skipped, not returned. <c>[PAUSE] It made…</c> opens with a word the
    /// narrator must never say, and naming it in the cue would instruct the model to say it —
    /// turning a hesitation marker into spoken dialogue.
    /// </remarks>
    public static string FirstSpokenToken(string? dialogue)
    {
        if (string.IsNullOrWhiteSpace(dialogue))
            return "";
        var line = LeadingStageCueRegex.Replace(dialogue.Trim(), "");
        // Prefer word + trailing ! ? if present (True!)
        var m = FirstWordMatchRegex.Match(line);
        return m.Success ? m.Value : "";
    }

    /// <summary>
    /// Remove leftover fountain markup and awkward Character_* + pronoun glue from action prose.
    /// </summary>
    public static string StripFountainLeakage(string visual)
    {
        if (string.IsNullOrWhiteSpace(visual)) return "";
        var v = visual;

        // (CONT'D) / (CONTINUED) / (V.O.) / (O.S.) / (O.C.) — screenplay extensions
        v = ContdRegex.Replace(v, "");
        v = ContinuedRegex.Replace(v, "");
        v = VoRegex.Replace(v, "");
        v = OsRegex.Replace(v, "");
        v = OcRegex.Replace(v, "");

        // "Character_Narrator He steadies…" → "Character_Narrator steadies…"
        v = PronounGlueRegex1.Replace(v, "$1 ");
        // Possessive after a character key is dropped: "Character_X His hands" becomes "Character_X hands".
        v = PronounGlueRegex2.Replace(v, "$1 ");

        // Duplicate token: "Character_X Character_X"
        v = DuplicateCharacterKeyRegex.Replace(v, "$1");

        // "NARRATOR (CONT'D)" already stripped parens — collapse double spaces / empty " ."
        v = DoubleSpacesRegex.Replace(v, " ");
        v = DotSpaceRegex.Replace(v, ".");
        v = DoubleDotsRegex.Replace(v, ".");
        return v.Trim();
    }

    /// <summary>
    /// On-screen identities for CAST COUNT and ref plates.
    /// Prefer blueprint <c>characters_on_screen</c>; never free-text names from dialogue
    /// (e.g. "I loved the old man" must not attach Character_Old_Man).
    /// </summary>
    public static List<string> ResolveOnScreenCharacterKeys(JsonElement clipEl)
    {
        var found = new List<string>();
        if (clipEl.TryGetProperty(CharactersOnScreenKey, out var cos) &&
            cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
                AddUniqueCharacterKey(found, x.GetString());
        }

        // Authoritative plan list present (even empty) — do not re-infer from prose
        if (clipEl.TryGetProperty(CharactersOnScreenKey, out cos) &&
            cos.ValueKind == JsonValueKind.Array)
            return found;

        // Legacy clips without the field: explicit Character_* tokens only
        if (clipEl.TryGetProperty(PrimarySubjectKey, out var ps))
            AddUniqueCharacterKey(found, ps.GetString());
        foreach (var k in ClipCharacterKeys(clipEl))
            AddUniqueCharacterKey(found, k);

        return found;
    }

    /// <summary>
    /// Keys for character variable blocks: on-screen + speaker + primary_subject
    /// (voice-only speakers included). Does <b>not</b> promote free-text names from prose.
    /// </summary>
    public static List<string> ResolveClipCharacterKeys(
        JsonElement clipEl,
        IReadOnlyDictionary<string, CharacterProfile>? characters = null)
    {
        _ = characters; // reserved for future voice-only metadata filters
        var found = new List<string>();
        foreach (var k in ResolveOnScreenCharacterKeys(clipEl))
            AddUniqueCharacterKey(found, k);

        if (clipEl.TryGetProperty(PrimarySubjectKey, out var ps))
            AddUniqueCharacterKey(found, ps.GetString());

        if (clipEl.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty("speaker", out var sp))
            AddUniqueCharacterKey(found, sp.GetString());

        // Only when plan list is missing — Character_* tokens in visual (not free-text names)
        if (!(clipEl.TryGetProperty(CharactersOnScreenKey, out var cos) &&
              cos.ValueKind == JsonValueKind.Array))
        {
            foreach (var k in ClipCharacterKeys(clipEl))
                AddUniqueCharacterKey(found, k);
        }

        return found;
    }

    private static void AddUniqueCharacterKey(List<string> found, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        key = key.Trim();
        if (!key.StartsWith(JsonKeys.CharacterPrefix, StringComparison.OrdinalIgnoreCase))
            return;
        if (found.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            return;
        found.Add(key);
    }

    /// <summary>
    /// Descriptive blocks dropped whole, cheapest first, when squeezing is not enough. Ordered by
    /// what a viewer loses: lens character, then a negative list the model mostly infers, then
    /// acting notes. Framing (<c>Camera</c>), grade (film look — same class as StyleLock), and
    /// identity are never in this list.
    /// </summary>
    private static readonly string[] SacrificialTags =
    {
        PromptFieldTags.Optics,
        "Negative",
        PromptFieldTags.MustNot,
        PromptFieldTags.Sound,
        PromptFieldTags.Performance,
    };

    /// <summary>
    /// Fit a finished prompt under the video API hard cap before the first request, cheapest loss
    /// first: learning addenda, mechanical compression, section notes, then whole descriptive
    /// blocks. A raw character cut is the last resort and lands on a tag boundary.
    /// </summary>
    /// <remarks>
    /// The old ladder ended at a bare <see cref="HeadCap"/>, which cuts at a character offset — on
    /// real Mary19 traffic three of four prompts ended mid-tag, one at "&lt;Camera&gt;Medium
    /// tracking". A half-written directive is worse than a missing one: the model still tries to
    /// honour it, and what got cut was always the CURRENT clip's own framing, because that is
    /// built last.
    /// </remarks>
    public static string FitPromptToVideoBudget(
        string prompt,
        int hardCapChars = VideoPromptHardCapChars)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt ?? "";
        hardCapChars = Math.Max(256, hardCapChars);
        if (prompt.Length <= hardCapChars)
            return prompt;

        var p = StripLearningAddenda(prompt);
        if (p.Length <= hardCapChars)
            return p;

        p = CompressPromptText(p);
        if (p.Length <= hardCapChars)
            return p;

        // Section notes explain their block; losing one costs less than losing content.
        p = PromptTags.StripNotes(p);
        if (p.Length <= hardCapChars)
            return p;

        foreach (var tag in SacrificialTags)
        {
            p = PromptTags.Strip(p, tag);
            p = CommonRegex.Replace(p, @"[ \t]{2,}", " ").Trim();
            if (p.Length <= hardCapChars)
                return p;
        }

        // Style lock is last among trims — it is the one directive holding the film's look.
        // House-rule / tagged STYLE LOCK used to be shortened before Optics/Grade and was the
        // first real content lost on overflow.
        p = PromptTags.Shorten(p, PromptFieldTags.StyleLock, 160);
        if (p.Length <= hardCapChars)
            return p;

        return TagSafeHeadCap(p, hardCapChars);
    }

    /// <summary>
    /// Head-cap, then walk back off any tag left unclosed by the cut so the model never receives a
    /// dangling directive. Falls back to the plain cap when no tag boundary is available.
    /// </summary>
    internal static string TagSafeHeadCap(string prompt, int maxChars)
    {
        var capped = HeadCap(prompt, maxChars);
        var open = capped.LastIndexOf('<');
        if (open < 0)
            return capped;
        var close = capped.IndexOf('>', open);
        // An unterminated tag, or an opening tag whose matching close was cut away.
        if (close < 0)
            return capped[..open].TrimEnd();
        var name = CommonRegex.Match(capped[open..], @"^<(/?)([A-Za-z][A-Za-z0-9]*)");
        if (!name.Success || name.Groups[1].Value == "/")
            return capped;
        var tag = name.Groups[2].Value;
        return capped.Contains($"</{tag}>", StringComparison.OrdinalIgnoreCase)
            ? capped
            : capped[..open].TrimEnd();
    }

    /// <summary>
    /// Intelligently compresses prompt text without losing core visual action or character details.
    /// Maps long character keys (e.g. "Character_The_Narrator") to compact aliases ("C1", "C2"),
    /// image tags ("<IMAGE_1>") to ("I1"), simplifies verbose section headers/labels,
    /// and collapses duplicate blank lines / spaces.
    /// </summary>
    public static string CompressPromptText(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return prompt ?? "";
        var p = prompt;

        // 1. Simplify verbose section headers & repetitive directives.
        // Section "note" attributes are NOT dropped here any more. They are instructions — the
        // <Context> note is what tells the model the prior clip's look is history, not a cue — and
        // stripping them first meant the one line explaining a block died before any redundant
        // prose did. FitPromptToVideoBudget now drops them only when squeezing is not enough.
        p = p.Replace("REQUIRED native Grok dialogue.", "");
        p = p.Replace("Do not invent extra people, duplicate faces, or crowd extras not listed.", "");
        p = p.Replace("Follow the camera framing and location in this prompt exactly. Prioritize the PRIMARY subject and ONE clear action with visible motion; background characters may stay mostly still.", "");

        // 2. Map all distinct Character_* keys to compact aliases C1, C2, C3... — numbered in
        // first-appearance order (readable, matches existing behavior), but REPLACED longest-key-
        // first: a plain string Replace of a key that happens to be a prefix of another (e.g.
        // "Character_Mom" vs "Character_Mom_Assistant") would otherwise mangle the longer key's
        // occurrences into "C1_Assistant" before it ever got its own turn to alias, silently
        // corrupting that character's identity references for the rest of the prompt.
        var matches = CommonRegex.Matches(p, @"\bCharacter_([A-Za-z0-9_]+)\b");
        var distinctKeys = matches.Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var aliasByKey = distinctKeys
            .Select((key, i) => (key, alias: $"C{i + 1}"))
            .ToDictionary(x => x.key, x => x.alias, StringComparer.OrdinalIgnoreCase);

        foreach (var key in distinctKeys.OrderByDescending(k => k.Length))
            p = p.Replace(key, aliasByKey[key]);

        // 3. Compress image reference tags (<IMAGE_1> -> I1, <IMAGE_2> -> I2)
        p = CommonRegex.Replace(p, @"<IMAGE_(\d+)>", "I$1");

        // 4. Compress technical text. Every field is emitted as <Tag>...</Tag> at build time now,
        // so there are no prose labels left to rename — the "Color grading:" rename that used to
        // live here went with them, and so did a "Kodak Vision3 500T 5219 film stock" ->
        // "Kodak 500T film" rule. That one is worth remembering: every other replacement in this
        // method rewrites text THIS class built, so the literal is exact by construction, but that
        // one rewrote free prose from a classifier — a guess about what a model would write.
        // Mary19 grades to "Kodak Vision3 250D 5207 film stock" (15 clips) and "hand-tinted
        // watercolor print stock" (4), so it fired on none of them. Do not add its like back.
        p = p.Replace("ON CAMERA lip-syncs", "lip-syncs");

        // Drop the human display name from character lines — the C1/C2 alias is enough once the
        // keys are mapped. Was a bracket match anchored on "- C1 I1 [x]:", which had to run after
        // the aliasing above and, needing the trailing colon, silently skipped the VOICE ONLY line.
        p = PromptTags.Strip(p, PromptFieldTags.Name);

        // Strip resolution/fps suffix (e.g. " / 480p, 24fps" -> "") since resolution/fps is configured via API payload
        p = CommonRegex.Replace(p, @"\s*/\s*\d+p,\s*\d+fps$", "");

        // Strip voice descriptions/locks (visual video models do not use voice tuning text).
        // Delimited by explicit <Voice>/<VoiceLock> tags rather than a bare "Voice:"/"VOICE LOCK"
        // label — a plain-text label match risked eating part of a dialogue line if it ever
        // happened to contain that literal substring (e.g. spoken text like "a voice: faint and
        // pleading"); an explicit tag can't collide with prose.
        // Per-character <Voice> descriptions go; the SPEAKER's <VoiceLock> stays, shortened. The old
        // "visual video models do not use voice tuning text" held for silent-video models - Grok
        // Imagine generates the speech, and this lock is the only cross-clip voice identity. Dropping
        // it on every compressed prompt is how the Mary19 narrator was re-cast female (S02C05).
        p = PromptTags.Strip(p, "Voice");
        p = PromptTags.Shorten(p, "VoiceLock", 140);
        // Shorten, don't delete — this is the only explicit instruction to lock the focus
        // character's face to its attached reference image; dropping it entirely (as opposed to
        // just shortening the wording) left only the bare "I1" tag with no instruction attached,
        // exactly the failure mode most likely on busy multi-character prompts (the ones long
        // enough to trigger compression in the first place).
        p = CommonRegex.Replace(p, @"\s*Match appearance of reference\s+(I\d+)\s+exactly\.?", " Match $1 exactly.");
        p = p.Replace("Start speaking immediately with ", "Start speaking: ");
        p = p.Replace(" — do not skip, delay, or swallow the opening word. After the last word, hold a brief natural pause with a closed mouth (about half a second); do not freeze mid-syllable or trail into empty staring. Other mouths closed. Speech intelligible; never silent.", ".");
        p = p.Replace("End cleanly when the spoken line and primary action finish — do not hold a frozen pose or empty silence after dialogue.", "");

        // 5. Drop repeats. Saying the same thing twice costs budget and buys nothing; on a real
        // Mary19 extend prompt this was a byte-identical <Lighting> block plus the same STYLE LOCK
        // sentence three times — ~380 of 4000 chars.
        p = DropRepeatedDirectives(p);

        // 6. Collapse multiple blank lines & consecutive spaces
        p = CommonRegex.Replace(p, @"\n\s*\n+", "\n");
        p = CommonRegex.Replace(p, @"[ \t]+", " ");

        return p.Trim();
    }

    /// <summary>Tag blocks safe to de-duplicate: descriptive, and identical copies say nothing new.</summary>
    private static readonly string[] DedupableTags =
        { "Lighting", "Optics", "Negative", "CastCount", "Audio", "Score", "Foley" };

    /// <summary>
    /// Keep the first occurrence of each byte-identical STYLE LOCK sentence and descriptive tag
    /// block; drop later copies. Only exact repeats go — two different <c>&lt;Lighting&gt;</c>
    /// blocks are a real (if odd) instruction, and are left alone rather than silently merged.
    /// </summary>
    internal static string DropRepeatedDirectives(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return prompt ?? "";
        var p = prompt;

        foreach (var tag in DedupableTags)
            p = DropLaterDuplicates(p, $@"<{tag}>.*?</{tag}>", RegexOptions.Singleline);

        // Extra STYLE LOCK copies (house-rule line + stale plan prose) are stripped at build
        // time — one lock from VisualMediumStyles / project render_style_lock is the SSoT.
        p = DropLaterDuplicates(p, @"STYLE LOCK(?:\s*\(hard\))?:\s*[^\n]+", RegexOptions.IgnoreCase);
        p = DropLaterDuplicates(p, $@"<{PromptFieldTags.StyleLock}>.*?</{PromptFieldTags.StyleLock}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return p;
    }

    private static string DropLaterDuplicates(string text, string pattern, RegexOptions options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return CommonRegex.Replace(text, pattern, m =>
            seen.Add(NormalizeForDedupe(m.Value)) ? m.Value : "", options);
    }

    private static string NormalizeForDedupe(string value) =>
        CommonRegex.Replace(value.Trim(), @"\s+", " ");

    /// <summary>Clip gen house rules from <c>prompts/clip_gen_rules.txt</c> (embed or override dir).</summary>
    public static string? TryLoadClipGenRules()
    {
        try
        {
            return PromptFiles.TryRead("prompts/clip_gen_rules.txt");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Map free-form names in prose to Character_* keys using display names / key suffixes.
    /// </summary>
    public static List<string> InferKeysFromProse(
        string prose,
        IReadOnlyDictionary<string, CharacterProfile> characters)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(prose) || characters.Count == 0) return list;
        var text = prose.ToLowerInvariant();
        AddCollectivelyNamedKeysFromProse(text, characters, list);
        foreach (var (key, prof) in characters)
        {
            if (list.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            if (ProseMentionsAnyName(text, ProseNameHints(key, prof)))
                list.Add(key);
        }

        return list;
    }

    /// <summary>
    /// Action prose often refers to a group collectively — "the officers sit", "the children
    /// point" — instead of naming each member, so per-name matching finds none of them. When two
    /// or more cast keys share a role word and the prose uses its plural, put the whole group on
    /// screen.
    /// </summary>
    /// <remarks>
    /// This replaces a hardcoded "Officer" check that matched "three officers" / "the officers"
    /// literally. It worked (16 clips across the two Tell-Tale Heart plans) and would never have
    /// fired for any other book's cast — a story name in engine code, which AGENTS.md rule 1
    /// forbids. The shared-role-word rule covers the same clips without naming anyone.
    /// </remarks>
    private static void AddCollectivelyNamedKeysFromProse(
        string text,
        IReadOnlyDictionary<string, CharacterProfile> characters,
        List<string> list)
    {
        var byRoleWord = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in characters.Keys)
        {
            foreach (var word in RoleWordsInKey(key))
            {
                if (!byRoleWord.TryGetValue(word, out var group))
                    byRoleWord[word] = group = new List<string>();
                group.Add(key);
            }
        }

        foreach (var (word, group) in byRoleWord)
        {
            if (group.Count < 2)
                continue;
            if (!ProseUsesPlural(text, word))
                continue;
            foreach (var k in group.Where(k => !list.Contains(k, StringComparer.OrdinalIgnoreCase))
                         .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                list.Add(k);
        }
    }

    /// <summary>Role-ish words in a cast key: <c>Character_Officer_Two</c> → "officer".</summary>
    private static IEnumerable<string> RoleWordsInKey(string key) =>
        key.Replace(JsonKeys.CharacterPrefix, "", StringComparison.OrdinalIgnoreCase)
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim())
            .Where(w => w.Length >= 4 && !IsOrdinalWord(w));

    /// <summary>Index suffixes (One/Two/2nd…) name the member, never the role.</summary>
    private static bool IsOrdinalWord(string word) =>
        CommonRegex.IsMatch(
            word,
            @"^(one|two|three|four|five|six|seven|eight|nine|ten|1st|2nd|3rd|\d+(?:th)?)$",
            RegexOptions.IgnoreCase);

    /// <summary>"officer" → matches "officers"; also the irregular "-y"/"child" plurals.</summary>
    private static bool ProseUsesPlural(string text, string roleWord)
    {
        var w = System.Text.RegularExpressions.Regex.Escape(roleWord.ToLowerInvariant());
        var plurals = $"{w}s|{w}es";
        if (roleWord.EndsWith("y", StringComparison.OrdinalIgnoreCase) && roleWord.Length > 1)
            plurals += "|" + System.Text.RegularExpressions.Regex.Escape(roleWord[..^1].ToLowerInvariant()) + "ies";
        if (roleWord.Equals("child", StringComparison.OrdinalIgnoreCase))
            plurals += "|children";
        return CommonRegex.IsMatch(text, $@"\b(?:{plurals})\b", RegexOptions.IgnoreCase);
    }

    private static List<string> ProseNameHints(string key, CharacterProfile prof)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(prof.DisplayName))
            names.Add(prof.DisplayName.Trim());
        var suffix = key.Replace(JsonKeys.CharacterPrefix, "", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ').Trim();
        if (suffix.Length > 0) names.Add(suffix);

        // A key can carry an article the prose leaves off. A the-prefixed role such as
        // Character_The_Narrator yields "the narrator", which never matches prose that just
        // says "narrator". Dropping the article covers that case and every other book's
        // the-prefixed role, without hardcoding book-specific names.
        foreach (var withArticle in names.ToList())
        {
            var bare = StripLeadingArticle(withArticle);
            if (bare.Length >= 3 && !names.Contains(bare, StringComparer.OrdinalIgnoreCase))
                names.Add(bare);
        }
        return names;
    }

    private static string StripLeadingArticle(string name)
    {
        var m = CommonRegex.Match(name.Trim(), @"^(?:the|a|an)\s+(.+)$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : name.Trim();
    }

    private static bool ProseMentionsAnyName(string text, List<string> names) =>
        names.Any(n => n.Length >= 3 && text.Contains(n.ToLowerInvariant(), StringComparison.Ordinal));

    /// <summary>
    /// Style lock carried by the plan itself, used when the project has no live style head.
    /// Reads the <c>&lt;StyleLock&gt;</c> block Stage 2 emits — a tagged field, not prose.
    /// Loose <c>STYLE LOCK:</c> text in action is not a source: where that sentence ends is a
    /// guess, and StripStyleLocksFromAction removes it from the action anyway.
    /// </summary>
    public static string? ExtractStyleHead(string visual)
    {
        if (string.IsNullOrWhiteSpace(visual)) return null;
        var m = CommonRegex.Match(
            visual,
            $@"<{PromptFieldTags.StyleLock}>(.*?)</{PromptFieldTags.StyleLock}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? StyleLockPrefix + m.Groups[1].Value.Trim() : null;
    }
    public static List<string> FindCharacterRefPaths(
        JsonElement clipEl,
        string projectDir,
        int maxRefs = 5)
    {
        var keys = ResolveOnScreenCharacterKeys(clipEl)
            .Where(k => !IsVoiceOnlyKey(k, null))
            .ToList();
        return FindCharacterRefPathsForKeys(keys, projectDir, maxRefs);
    }

    public static List<string> FindCharacterRefPathsForKeys(
        IReadOnlyList<string> keys,
        string projectDir,
        int maxRefs = 5)
    {
        if (maxRefs <= 0 || string.IsNullOrWhiteSpace(projectDir))
            return new List<string>();
        maxRefs = Math.Min(maxRefs, 32);

        var paths = new List<string>();
        foreach (var key in keys
                     .OrderBy(CharacterRefPriority)
                     .ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (paths.Count >= maxRefs) break;
            if (IsVoiceOnlyKey(key, null)) continue;
            var full = ResolveCharacterRefPath(projectDir, key);
            if (full is not null)
                paths.Add(full);
        }
        return paths;
    }


    public static string? NormalizeLocationKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim();

    /// <summary>Clip <c>location_id</c>, else scene-level fields if present on the clip element.</summary>
    public static string? ResolveClipLocationKey(JsonElement clipEl)
    {
        foreach (var prop in new[] { "location_id", "primary_location_id", "location_key" })
        {
            if (clipEl.TryGetProperty(prop, out var el) &&
                el.ValueKind == JsonValueKind.String &&
                el.GetString() is { Length: > 0 } s)
                return s.Trim();
        }
        return null;
    }

    /// <summary>
    /// Locked set plate under <c>assets/locations/{key}_ref.png</c> (and Loc_ bare aliases).
    /// Soft — returns null when missing; never required for video gen.
    /// </summary>
    public static string? ResolveLocationRefPath(string projectDir, string locKey)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(locKey))
            return null;
        var dir = Path.Combine(projectDir, ProjectStore.LocationAssetsRelativeDir);
        if (!Directory.Exists(dir)) return null;

        foreach (var name in LocationRefFileCandidates(locKey))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full) && new FileInfo(full).Length >= 64)
                return full;
            if (File.Exists(full + ProjectStore.ClientMarkerExtension))
                return full;
        }
        return null;
    }

    public static IEnumerable<string> LocationRefFileCandidates(string locKey) =>
        ProjectStore.LocationRefFileNameCandidates(locKey);

    public static List<string> ClipCharacterKeys(JsonElement clipEl)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (clipEl.TryGetProperty("visual_prompt", out var vp))
            ScanCharacterKeys(found, vp.GetString());
        if (clipEl.TryGetProperty(PrimarySubjectKey, out var ps))
            ScanCharacterKeys(found, ps.GetString());
        if (clipEl.TryGetProperty(JsonKeys.AudioPayload, out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty("speaker", out var sp))
        {
            ScanCharacterKeys(found, sp.GetString());
        }
        if (clipEl.TryGetProperty(CharactersOnScreenKey, out var cos) && cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
                ScanCharacterKeys(found, x.GetString());
        }
        return found.ToList();
    }

    private static void ScanCharacterKeys(HashSet<string> found, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (Match m in CommonRegex.Matches(text, @"Character_[A-Za-z0-9_]+"))
            found.Add(m.Value);
    }

    private static string? ResolveCharacterRefPath(string projectDir, string key) =>
        ResolveCharacterRefPathPublic(projectDir, key);

    /// <summary>
    /// Single source of truth for resolving a character's locked <c>*_ref.png</c> (canonical +
    /// aliases, then a normalized-key fallback). <see cref="ProjectStore.ResolveCharacterRefPath"/>
    /// delegates here for its enumerate-and-match, adding only its voice-only guard — the two must
    /// agree because <c>FilmJobService.MissingOnScreenLockKeys</c> calls both.
    /// </summary>
    /// <param name="allowNormalizedFallback">
    /// When false, only exact candidate filenames match (no normalized-key scan). Cast listing uses
    /// false so Character_Narrator and Character_The_Narrator do not share one <c>*_ref.png</c>;
    /// video/gen uses true so a slightly different clip key still finds the locked portrait.
    /// </param>
    public static string? ResolveCharacterRefPathPublic(
        string projectDir, string key, bool allowNormalizedFallback = true)
    {
        var charDir = Path.Combine(projectDir, "assets", "characters");
        foreach (var name in ProjectStore.CharacterRefFileCandidates(key))
        {
            var full = Path.Combine(charDir, name);
            if (File.Exists(full) && new FileInfo(full).Length >= 64)
                return full;
            if (File.Exists(full + ProjectStore.ClientMarkerExtension))
                return full;
        }
        return allowNormalizedFallback ? ResolveCharacterRefPathByNormalizedKey(charDir, key) : null;
    }

    /// <summary>
    /// Fallback when the literal key has no matching file: e.g. Stage2 scene/clip data uses
    /// Character_The_Old_Man while cast_seeds.json (the actual locked portrait) uses
    /// Character_OldMan. Commit 150db61 fixed this same mismatch for the character
    /// description/visual-lock text and voice lock (<see cref="GetCharacterProfile"/> via
    /// <see cref="Stage2PlannerService.NormalizeCharacterKey"/>), but never reached this
    /// reference-IMAGE lookup — the actual photo pinning the character's face/eye/wardrobe
    /// across clips — so it silently sent no reference image at all for any on-screen
    /// character whose blueprint key didn't happen to collide with its cast_seeds key.
    /// Scans actual *_ref.png files on disk (not a passed-in key list) so it works from every
    /// call site, including ones with no character-profile dictionary in scope.
    /// </summary>
    private static string? ResolveCharacterRefPathByNormalizedKey(string charDir, string key)
    {
        if (!Directory.Exists(charDir)) return null;
        var targetNorm = Stage2PlannerService.NormalizeCharacterKey(key);
        if (targetNorm.Length == 0) return null;

        foreach (var file in Directory.EnumerateFiles(charDir, "*_ref.png*"))
        {
            if (TryMatchRefFileForNormalizedKey(file, targetNorm, out var clean))
                return Path.Combine(charDir, clean);
        }
        return null;
    }

    private static bool TryMatchRefFileForNormalizedKey(
        string file,
        string targetNorm,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? cleanFileName)
    {
        cleanFileName = null;
        var fileName = Path.GetFileName(file);
        var clean = fileName.EndsWith(ProjectStore.ClientMarkerExtension, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^ProjectStore.ClientMarkerExtension.Length]
            : fileName;
        var stem = Path.GetFileNameWithoutExtension(clean);
        if (stem.StartsWith("wardrobe_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (stem.EndsWith("_ref", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^"_ref".Length];

        if (Stage2PlannerService.NormalizeCharacterKey(stem) == targetNorm
            && File.Exists(file)
            && (new FileInfo(file).Length >= 64 || file.EndsWith(ProjectStore.ClientMarkerExtension, StringComparison.OrdinalIgnoreCase)))
        {
            cleanFileName = clean;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Keys that need a full identity lock in the CHARACTER VARIABLES block.
    /// Prefer Stage 2 <c>focus_keys</c>; else primary_subject ∪ speaker (all on-screen for high-motion).
    /// No verb-list parsing of action prose — metadata only (Agents.md).
    /// </summary>
    public static HashSet<string> ResolveFocusKeysForClip(
        IReadOnlyList<string> onScreenKeys,
        JsonElement clipEl)
    {
        var onScreen = onScreenKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (onScreen.Count <= 1)
            return new HashSet<string>(onScreen, StringComparer.OrdinalIgnoreCase);

        var fromPlan = TryFocusKeysFromPlan(onScreen, clipEl);
        if (fromPlan is not null)
            return fromPlan;

        return ResolveFocusKeys(
            onScreen,
            ReadClipString(clipEl, PrimarySubjectKey),
            ReadAudioPayloadString(clipEl, "speaker"),
            ReadClipString(clipEl, "action_class"),
            ReadAudioPayloadString(clipEl, "secondary_speaker"));
    }

    private static HashSet<string>? TryFocusKeysFromPlan(
        IReadOnlyList<string> onScreen,
        JsonElement clipEl)
    {
        if (!clipEl.TryGetProperty("focus_keys", out var fk) || fk.ValueKind != JsonValueKind.Array)
            return null;
        var fromPlan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in fk.EnumerateArray())
            TryAddPlannedFocusKey(fromPlan, onScreen, el);
        return fromPlan.Count > 0 ? fromPlan : null;
    }

    private static void TryAddPlannedFocusKey(
        HashSet<string> dest,
        IReadOnlyList<string> onScreen,
        JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String)
            return;
        var k = el.GetString();
        if (k is not { Length: > 0 })
            return;
        if (!onScreen.Any(o => string.Equals(o, k, StringComparison.OrdinalIgnoreCase)))
            return;
        dest.Add(k);
    }

    private static string? ReadClipString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        return el.GetString();
    }

    private static string? ReadAudioPayloadString(JsonElement clipEl, string name)
    {
        if (!clipEl.TryGetProperty(JsonKeys.AudioPayload, out var ap) || ap.ValueKind != JsonValueKind.Object)
            return null;
        return ReadClipString(ap, name);
    }

    /// <summary>
    /// Deterministic focus set from plan fields (shared by Stage 2 writer and gen-time builder).
    /// </summary>
    /// <param name="secondarySpeaker">Second speaker on a cross-speaker two-hander clip (see
    /// <see cref="Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats"/>) — locked to full
    /// identity alongside <paramref name="speaker"/> so both faces render correctly.</param>
    public static HashSet<string> ResolveFocusKeys(
        IReadOnlyList<string> onScreenKeys,
        string? primarySubject,
        string? speaker,
        string? actionClass,
        string? secondarySpeaker = null)
    {
        var onScreen = onScreenKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (onScreen.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (onScreen.Count == 1)
            return new HashSet<string>(onScreen, StringComparer.OrdinalIgnoreCase);

        var ac = (actionClass ?? "").Trim().ToLowerInvariant();
        // High-motion / ensemble: full locks for everyone visible
        if (ac is "big_action")
            return new HashSet<string>(onScreen, StringComparer.OrdinalIgnoreCase);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static void TryAdd(HashSet<string> dest, IReadOnlyList<string> keys, string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var hit = keys.FirstOrDefault(o =>
                string.Equals(o, key, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                dest.Add(hit);
        }

        TryAdd(set, onScreen, primarySubject);
        TryAdd(set, onScreen, speaker);
        TryAdd(set, onScreen, secondarySpeaker);

        if (set.Count == 0)
            set.Add(onScreen[0]);

        return set;
    }

    private static string BuildCharacterVariablesBlock(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, CharacterProfile> characters,
        IReadOnlyDictionary<string, string> imageTagByKey,
        bool useImageTags,
        HashSet<string>? activeKeys = null,
        IReadOnlyDictionary<string, List<string>>? wardrobeByKey = null)
    {
        if (keys.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(PromptTags.OpenWithNote("Characters",
            "use these identities consistently; do not redesign faces or wardrobe"));
        var any = false;
        foreach (var key in keys)
        {
            var p = GetCharacterProfile(characters, key);
            var worn = ResolveWardrobeItems(key, p, wardrobeByKey);
            if (p?.VoiceOnly == true || IsVoiceOnlyKey(key, characters))
                sb.AppendLine(FormatVoiceOnlyLine(key, p, imageTagByKey, useImageTags));
            else if (IsNonFocusPresent(activeKeys, keys.Count, key))
                sb.AppendLine(FormatCompactPresentLine(key, p, imageTagByKey, useImageTags, worn));
            else
                sb.AppendLine(FormatFocusCharacterLine(key, p, imageTagByKey, useImageTags, worn));
            any = true;
        }
        return any ? sb.ToString().TrimEnd() : "";
    }

    private static Dictionary<string, List<string>> CollectGenerateWardrobe(
        string? visualPrompt,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, CharacterProfile> characters)
    {
        var fromPrompt = WardrobeState.ParseStillWears(visualPrompt);
        foreach (var key in keys)
        {
            var p = GetCharacterProfile(characters, key);
            if (p?.WardrobeAlways is not { Count: > 0 })
                continue;
            if (!fromPrompt.TryGetValue(key, out var list))
            {
                list = new List<string>();
                fromPrompt[key] = list;
            }

            // AlreadyHas is a contains-match against the growing list — keep the
            // Where lazy so a later overlapping garment is skipped after an earlier add.
            foreach (var item in p.WardrobeAlways.Where(item => !WardrobeState.AlreadyHas(list, item)))
                list.Add(item);
        }

        return fromPrompt;
    }

    private static IReadOnlyList<string> ResolveWardrobeItems(
        string key,
        CharacterProfile? p,
        IReadOnlyDictionary<string, List<string>>? wardrobeByKey)
    {
        if (wardrobeByKey is not null &&
            wardrobeByKey.TryGetValue(key, out var fromPrompt) &&
            fromPrompt.Count > 0)
            return fromPrompt;
        return p?.WardrobeAlways ?? Array.Empty<string>();
    }

    private static bool IsNonFocusPresent(HashSet<string>? activeKeys, int keyCount, string key)
    {
        var isActive = activeKeys is null || activeKeys.Contains(key);
        return !isActive && keyCount > 1;
    }

    private static (string Display, string Tag, string Desc, string Vlock, string Voice) ResolveCharacterLineParts(
        string key,
        CharacterProfile? p,
        IReadOnlyDictionary<string, string> imageTagByKey,
        bool useImageTags,
        IReadOnlyList<string>? wardrobeItems = null)
    {
        var display = !string.IsNullOrWhiteSpace(p?.DisplayName)
            ? p.DisplayName
            : key.Replace(JsonKeys.CharacterPrefix, "").Replace('_', ' ');
        var tag = useImageTags && imageTagByKey.TryGetValue(key, out var t) ? $" {t}" : "";
        // Cast profile fields are free-form (admin/AI-authored) — sanitize once here at the
        // source rather than at each tag-wrap call site below.
        var desc = PromptTags.SanitizeValue(p?.Description?.Trim());
        var vlock = PromptTags.SanitizeValue(
            CharacterVisualTextScrubber.StripOutfitFromVisualLock(p?.VisualLock?.Trim(), wardrobeItems));
        var voice = PromptTags.SanitizeValue(p?.VoiceProfile?.Trim());
        return (display, tag, desc, vlock, voice);
    }

    private static string FormatVoiceOnlyLine(
        string key,
        CharacterProfile? p,
        IReadOnlyDictionary<string, string> imageTagByKey,
        bool useImageTags)
    {
        var (display, tag, _, _, voice) = ResolveCharacterLineParts(key, p, imageTagByKey, useImageTags);
        return
            $"- {key}{tag} {PromptTags.Wrap(PromptFieldTags.Name, PromptTags.SanitizeValue(display))} VOICE ONLY — not on screen." +
            (voice.Length > 0 ? $" {PromptTags.Wrap("Voice", voice)}" : "");
    }

    private static string FormatCompactPresentLine(
        string key,
        CharacterProfile? p,
        IReadOnlyDictionary<string, string> imageTagByKey,
        bool useImageTags,
        IReadOnlyList<string>? wardrobeItems = null)
    {
        // Multi-character compaction: non-focus on-screen cast get a short identity line.
        // Lead with visual_lock (not the general description) when present — it's the field
        // specifically curated to hold the one identity-critical, must-never-drift trait (e.g.
        // a distinguishing eye, scar, tattoo). Previously truncated to a fixed char count (first
        // 60, then 140) — confirmed as a real bug via a live render: Tell-Tale Heart's Old Man
        // visual_lock ("...must not drift to clear blue or to the Narrator's face...") was cut
        // mid-word at "must not drift to clear blu[e]" on every clip where he wasn't the shot's
        // focus, silently dropping the one instruction preventing his signature filmy eye from
        // rendering as an ordinary clear one. The real prompt-length constraint is the video
        // API's hard character cap (~4096 chars), already handled end-to-end by
        // GrokVideoClient's retry-driven ShortenPromptForRetry when the WHOLE built prompt
        // exceeds it — and that mechanism deliberately keeps the head (identity/action) intact
        // rather than blindly guillotining one character's identity clause on every appearance.
        // So: no fixed per-character cap here; let the full text through.
        var (display, tag, desc, vlock, _) = ResolveCharacterLineParts(key, p, imageTagByKey, useImageTags, wardrobeItems);
        var compactSource = vlock.Length > 0 ? vlock : desc;
        var compact =
            $"- {key}{tag} {PromptTags.Wrap(PromptFieldTags.Name, PromptTags.SanitizeValue(display))}: Also present (not shot focus); keep identity consistent: {compactSource}.";
        if (useImageTags && tag.Length > 0) compact += $" Match reference {tag.Trim()}.";
        return compact;
    }

    private static string FormatFocusCharacterLine(
        string key,
        CharacterProfile? p,
        IReadOnlyDictionary<string, string> imageTagByKey,
        bool useImageTags,
        IReadOnlyList<string>? wardrobeItems = null)
    {
        var (display, tag, desc, vlock, voice) = ResolveCharacterLineParts(key, p, imageTagByKey, useImageTags, wardrobeItems);
        var line = $"- {key}{tag} {PromptTags.Wrap(PromptFieldTags.Name, PromptTags.SanitizeValue(display))}:";
        if (desc.Length > 0) line += $" {desc}";
        if (vlock.Length > 0) line += $" {PromptTags.Wrap("VisualLock", vlock)}";
        if (voice.Length > 0) line += $" {PromptTags.Wrap("Voice", voice)}";
        if (useImageTags && tag.Length > 0)
            line += $" Match appearance of reference {tag.Trim()} exactly.";
        return line;
    }

    public static CharacterProfile? GetCharacterProfile(
        IReadOnlyDictionary<string, CharacterProfile>? characters,
        string? key)
    {
        if (characters is null || string.IsNullOrWhiteSpace(key)) return null;
        if (characters.TryGetValue(key, out var prof)) return prof;
        var norm = Stage2PlannerService.NormalizeCharacterKey(key);
        return characters.FirstOrDefault(kv => Stage2PlannerService.NormalizeCharacterKey(kv.Key) == norm).Value;
    }

    internal static (IReadOnlyDictionary<string, string> Tags, IReadOnlyList<string> VoiceIds)
        ResolveReferenceAudioTags(
            string? videoModel,
            JsonElement clipEl,
            IReadOnlyDictionary<string, CharacterProfile>? characters,
            bool isExtendHop)
    {
        if (isExtendHop)
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());
        var entry = string.IsNullOrWhiteSpace(videoModel)
            ? null
            : SupportedModelCatalog.Find(videoModel, ModelCapability.Video);
        if (entry is not { SupportsReferenceAudios: true })
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());
        var max = entry.MaxReferenceAudios is > 0 ? entry.MaxReferenceAudios.Value : 0;
        if (max <= 0)
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<string>());

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (var speaker in ClipSpokenLines.FromClipElement(clipEl).Select(line => line.Speaker))
        {
            if (ids.Count >= max || string.IsNullOrWhiteSpace(speaker))
                continue;
            if (tags.ContainsKey(speaker))
                continue;
            var prof = GetCharacterProfile(characters, speaker);
            var voiceId = ImagineVoicePicker.NormalizeVoiceId(entry.PresetVoices, prof?.ImagineVoiceId);
            if (voiceId is null)
                continue;
            tags[speaker] = $"<AUDIO_{ids.Count}>";
            ids.Add(voiceId);
        }
        return (tags, ids);
    }

    /// <param name="mode">
    /// Generation mode. A continuation gets no music/Foley layers — see <see cref="BuildAudioBed"/>.
    /// </param>
    private static string BuildAudioBlock(
        JsonElement clipEl,
        IReadOnlyDictionary<string, CharacterProfile>? characters,
        ClipCorrection? correction = null,
        IReadOnlyDictionary<string, string>? audioTags = null,
        string? mode = null)
    {
        if (!clipEl.TryGetProperty(JsonKeys.AudioPayload, out var audio) ||
            audio.ValueKind != JsonValueKind.Object)
            return "";

        var spoken = ReadSpokenAudioFields(audio);
        // Stage2/AI-classifier free text — sanitize at the source (see PromptTags class doc).
        var sfx = ReadSanitizedAudioField(audio, "sfx");
        var ambient = ReadSanitizedAudioField(audio, "ambient");
        var score = ReadScoreLayer(audio);

        if (HasNoAudioContent(spoken.Dialogue, sfx, ambient, score))
            return "";

        var voiceLock = BuildVoiceLock(characters, spoken.Speaker, audioTags);

        if (!string.IsNullOrWhiteSpace(spoken.Dialogue))
            return BuildSpokenDialogueAudio(
                audio, spoken, sfx, ambient, score,
                new SpokenAudioExtras(voiceLock, correction, audioTags, mode));

        if (HasAmbientLayers(ambient, sfx, score))
            return BuildAmbientOnlyAudio(ambient, sfx, score);

        return "";
    }

    private static (string Speaker, string Dialogue, string SecondarySpeaker, string SecondaryDialogue, string Delivery)
        ReadSpokenAudioFields(JsonElement audio)
    {
        // Every spoken line of this clip via the shared accessor so this reader can't diverge from
        // duration sizing / verification: the primary line PLUS any second speaker's line
        // (Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats two-hander — camera pans from
        // speaker to speaker mid-clip).
        var spokenLines = ClipSpokenLines.FromAudioPayload(audio);
        return (
            spokenLines.Count > 0 ? spokenLines[0].Speaker : "",
            spokenLines.Count > 0 ? spokenLines[0].Dialogue : "",
            spokenLines.Count > 1 ? spokenLines[1].Speaker : "",
            spokenLines.Count > 1 ? spokenLines[1].Dialogue : "",
            Stage2PlannerService.NormalizeDelivery(
                spokenLines.Count > 0 ? spokenLines[0].Delivery : "none"));
    }

    private static string ReadSanitizedAudioField(JsonElement audio, string name) =>
        PromptTags.SanitizeValue(audio.TryGetProperty(name, out var el) ? el.GetString() : null).Trim();

    private static string ReadScoreLayer(JsonElement audio)
    {
        if (audio.TryGetProperty("score_layer", out var sc))
            return PromptTags.SanitizeValue(sc.GetString()).Trim();
        if (audio.TryGetProperty("score", out sc))
            return PromptTags.SanitizeValue(sc.GetString()).Trim();
        if (audio.TryGetProperty("music_layer", out sc))
            return PromptTags.SanitizeValue(sc.GetString()).Trim();
        if (audio.TryGetProperty("music", out sc))
            return PromptTags.SanitizeValue(sc.GetString()).Trim();
        return PromptTags.SanitizeValue(null).Trim();
    }

    private static bool HasNoAudioContent(string dialogue, string sfx, string ambient, string score) =>
        string.IsNullOrWhiteSpace(dialogue) &&
        string.IsNullOrWhiteSpace(sfx) &&
        string.IsNullOrWhiteSpace(ambient) &&
        string.IsNullOrWhiteSpace(score);

    private static bool HasAmbientLayers(string ambient, string sfx, string score) =>
        !string.IsNullOrWhiteSpace(ambient) ||
        !string.IsNullOrWhiteSpace(sfx) ||
        !string.IsNullOrWhiteSpace(score);

    private static string BuildVoiceLock(
        IReadOnlyDictionary<string, CharacterProfile>? characters,
        string speaker,
        IReadOnlyDictionary<string, string>? audioTags = null)
    {
        if (audioTags is not null &&
            !string.IsNullOrWhiteSpace(speaker) &&
            audioTags.ContainsKey(speaker))
            return "";
        var prof = GetCharacterProfile(characters, speaker);
        if (string.IsNullOrWhiteSpace(speaker) ||
            prof is null ||
            string.IsNullOrWhiteSpace(prof.VoiceProfile))
            return "";
        // Every clip is generated independently: the profile text is the only cross-clip voice
        // identity. Say so, so the model does not re-cast the voice per clip.
        return " " + PromptTags.Wrap("VoiceLock",
            $"{speaker}: {PromptTags.SanitizeValue(prof.VoiceProfile)} — exactly this one voice (same sex, age and timbre) as in every other clip of this film.");
    }

    private static string SpeakerCue(string who, IReadOnlyDictionary<string, string>? audioTags)
    {
        if (audioTags is not null && audioTags.TryGetValue(who, out var tag) && !string.IsNullOrWhiteSpace(tag))
            return $"{who} {tag}";
        return who;
    }

    private static List<string> CollectAudioLayers(string score, string ambient, string sfx)
    {
        var layers = new List<string>();
        if (!string.IsNullOrWhiteSpace(score)) layers.Add(PromptTags.Wrap("Score", score));
        if (!string.IsNullOrWhiteSpace(ambient)) layers.Add(PromptTags.Wrap("Ambient", ambient));
        if (!string.IsNullOrWhiteSpace(sfx)) layers.Add(PromptTags.Wrap("Foley", sfx));
        return layers;
    }

    /// <summary>
    /// Music, ambience and Foley to lay under the line — omitted entirely on a continuation.
    /// </summary>
    /// <remarks>
    /// A continuation is generated from the previous clip's video, which already carries that clip's
    /// audio bed; the model continues it on its own. Asking for the bed again makes it re-generate
    /// the effects prominently, and they land on top of the line's opening word.
    ///
    /// <para>Measured against the provider's extend endpoint on Mary19 S02C02, with repeated runs
    /// rather than single trials: with the layers present the narrator dropped the line's first word
    /// in every run; with only Score and Foley removed and everything else identical, the word
    /// survived in every run.</para>
    ///
    /// <para>Same defect as the duplicated Sound block, one layer down. That removed the SECOND
    /// request for these effects; this removes the last one on a path where the video already
    /// supplies them. A fresh clip keeps its bed — it has no predecessor to inherit one from, and
    /// establishing the audio is exactly its job.</para>
    /// </remarks>
    private static string BuildAudioBed(string score, string ambient, string sfx, string? mode = null)
    {
        if (mode is ModeVideoExtend or ModeContinue)
            return "";
        var audioBedParts = CollectAudioLayers(score, ambient, sfx);
        return audioBedParts.Count > 0
            ? " " + string.Join(" ", audioBedParts)
            : " Secondary layer = soft room tone / Foley.";
    }

    private static string BuildPronunciationHintForLine(JsonElement audio, string quote)
    {
        var pronHintInPayload = PromptTags.SanitizeValue(
            audio.TryGetProperty("pronunciation_hint", out var ph) ? ph.GetString() : null);
        // Only honor a pre-baked hint when its target word is actually in this line; otherwise derive
        // hints from the dialogue itself (which is inherently limited to words that are spoken).
        if (!string.IsNullOrWhiteSpace(pronHintInPayload) &&
            PronunciationResolver.HintAppliesToDialogue(pronHintInPayload, quote))
        {
            return pronHintInPayload.StartsWith(' ')
                ? pronHintInPayload
                : $" {PromptTags.Wrap("Pronunciation", pronHintInPayload)}";
        }
        return BuildPronunciationHints(quote);
    }

    /// <param name="Mode">
    /// Generation mode — a continuation gets no music/Foley layers. See <see cref="BuildAudioBed"/>.
    /// </param>
    private readonly record struct SpokenAudioExtras(
        string VoiceLock,
        ClipCorrection? Correction,
        IReadOnlyDictionary<string, string>? AudioTags,
        string? Mode = null);

    private static string BuildSpokenDialogueAudio(
        JsonElement audio,
        (string Speaker, string Dialogue, string SecondarySpeaker, string SecondaryDialogue, string Delivery) spoken,
        string sfx,
        string ambient,
        string score,
        SpokenAudioExtras extras)
    {
        var audioTags = extras.AudioTags;
        var correction = extras.Correction;
        var voiceLock = extras.VoiceLock;
        var who = string.IsNullOrWhiteSpace(spoken.Speaker) ? "SPEAKER" : spoken.Speaker.Trim();
        var whoCue = SpeakerCue(who, audioTags);
        var isVoiceover = IsVoiceoverDelivery(spoken.Delivery, who);
        // Full line, speech-safe punctuation (em-dash normalize, !- glue) — same words. Story
        // dialogue text, sanitized like every other leaf value before it can reach a tag.
        var quote = PromptTags.SanitizeValue(SanitizeSpokenDialogue(spoken.Dialogue));
        // QA correction: a heteronym the model read with the wrong sense is respelled INSIDE the quoted
        // line ("TAIR up the planks!") — the one cue speech models reliably follow — with the hint kept
        // for the reader; the verifier still checks against the script text.
        // {speakerLock} carries every QA-retry sentence (speaker lock, whole-line emphasis, delivery
        // cue) so the three return branches below stay unchanged.
        var speakerLock = "";
        if (correction is not null)
        {
            quote = ApplyRespellings(quote, correction.Respellings);
            if (correction.SpeakerLockKey is { Length: > 0 })
                speakerLock += $" ONLY {who} speaks in this clip; every other character is silent with mouth closed and does not mouth the words.";
            if (correction.EmphasizeWholeLine)
                speakerLock += " The COMPLETE line above must be spoken aloud, every word in order, clearly audible from the first word to the last — nothing added, nothing dropped.";
            if (!string.IsNullOrWhiteSpace(correction.DeliveryCue))
                speakerLock += " " + correction.DeliveryCue.Trim();
        }
        var openCue = BuildOpenCue(quote);
        var bed = BuildAudioBed(score, ambient, sfx, extras.Mode);

        const string endPause =
            " After the last word, hold a brief natural pause with a closed mouth (about half a second); do not freeze mid-syllable or trail into empty staring.";
        var pronHint = BuildPronunciationHintForLine(audio, quote);

        if (isVoiceover)
        {
            return PromptTags.Wrap(AudioTag,
                $"REQUIRED native Grok off-camera voiceover. {whoCue} narrates " +
                $"exactly: \"{quote}\".{openCue}{endPause}{pronHint}{speakerLock} Do not lip-sync on-screen cast to this VO.{bed}{voiceLock}");
        }

        // Multi-hander: camera pans from speaker to speaker mid-clip instead of cutting. Only
        // applies to the on-camera case — voiceover has no second on-screen mouth to sync.
        var extraLines = ClipSpokenLines.FromAudioPayload(audio)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l.Dialogue))
            .ToList();
        if (extraLines.Count > 0)
        {
            var extraCue = new System.Text.StringBuilder();
            var extraHints = new System.Text.StringBuilder();
            extraHints.Append(pronHint);
            foreach (var line in extraLines)
            {
                var whoN = line.Speaker.Trim();
                var quoteN = PromptTags.SanitizeValue(SanitizeSpokenDialogue(line.Dialogue));
                extraCue.Append($" Then {SpeakerCue(whoN, audioTags)} ON CAMERA lip-syncs exactly: \"{quoteN}\".");
                extraHints.Append(BuildPronunciationHints(quoteN));
            }
            return PromptTags.Wrap(AudioTag,
                $"REQUIRED native Grok dialogue. {whoCue} ON CAMERA lip-syncs " +
                $"exactly: \"{quote}\".{openCue}{extraCue}{endPause}{extraHints}{speakerLock} Speech intelligible; never silent.{bed}{voiceLock}");
        }

        // spoken_on_camera / on_camera (normalized)
        return PromptTags.Wrap(AudioTag,
            $"REQUIRED native Grok dialogue. {whoCue} ON CAMERA lip-syncs " +
            $"exactly: \"{quote}\".{openCue}{endPause}{pronHint}{speakerLock} Other mouths closed. Speech intelligible; never silent.{bed}{voiceLock}");
    }

    /// <summary>Replace each respelled word in the line (whole word, case-insensitive) with its
    /// respelling in caps, so the speech model says the intended sense: "Tear up" → "TAIR up".</summary>
    internal static string ApplyRespellings(string quote, IReadOnlyList<Respelling> respellings)
    {
        if (respellings is null || respellings.Count == 0 || string.IsNullOrEmpty(quote)) return quote;
        var q = quote;
        foreach (var r in respellings)
        {
            if (string.IsNullOrWhiteSpace(r.Word) || string.IsNullOrWhiteSpace(r.Respell)) continue;
            q = System.Text.RegularExpressions.Regex.Replace(
                q, $@"\b{System.Text.RegularExpressions.Regex.Escape(r.Word)}\b", r.Respell.ToUpperInvariant(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        return q;
    }

    private static bool IsVoiceoverDelivery(string delivery, string who) =>

        delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought" ||
        (delivery is not "spoken_on_camera" and not "on_camera" &&
         who.Contains("narrator", StringComparison.OrdinalIgnoreCase));

    private static string BuildOpenCue(string quote)
    {
        var open = FirstSpokenToken(quote);
        return open.Length > 0
            ? $" Start speaking immediately with \"{open}\" — do not skip, delay, or swallow the opening word."
            : " Start speaking immediately with the first word of the line — do not skip the opening.";
    }

    private static string BuildAmbientOnlyAudio(string ambient, string sfx, string score)
    {
        var layers = CollectAudioLayers(score, ambient, sfx);
        return PromptTags.Wrap(AudioTag, $"music/ambient/Foley only — {string.Join("; ", layers)}. No dialogue.");
    }

    private static string BuildPronunciationHints(string text)
    {
        var hints = PronunciationResolver.RenderPromptHints(PronunciationResolver.Default.Resolve(text));
        return hints.Length == 0 ? "" : " " + PromptTags.Wrap("Pronunciation", hints);
    }

    /// <summary>
    /// Global provider negatives + story-specific <c>negative_prompt</c> from the blueprint
    /// + a medium-derived opposite (illustrated ↛ photoreal, and vice versa) unless this clip
    /// explicitly changes world/medium.
    /// </summary>
    private static string BuildNegativeBlock(JsonElement clipEl, string? visualMedium = null)
    {
        var story = clipEl.TryGetProperty("negative_prompt", out var np)
            ? PromptTags.SanitizeValue(np.GetString()).Trim()
            : "";
        var global = (GlobalNegativePrompt ?? "").Trim();
        var mediumNeg = ClipDeclaresDifferentMedium(clipEl, visualMedium)
            ? ""
            : VisualMediumStyles.NegativeFor(VisualMediumStyles.NormalizeMedium(visualMedium));
        if (global.Length == 0 && story.Length == 0 && mediumNeg.Length == 0)
            return "";

        // Dedupe tokens across global + story + medium
        var items = new List<string>();
        if (global.Length > 0)
            AddCsvNegatives(items, global);
        if (story.Length > 0)
            AddCsvNegatives(items, story);
        if (mediumNeg.Length > 0)
            AddCsvNegatives(items, mediumNeg);
        if (items.Count == 0)
            return "";
        return PromptTags.Wrap("Negative", string.Join(", ", items));
    }

    /// <summary>
    /// True when the clip's plan declares a different visual medium than the film — a beat that
    /// deliberately crosses over (dream, portal, flash to live-action). Those clips keep story
    /// negatives only, since the film's opposite-medium negatives would fight the beat.
    /// Read from the plan's <c>visual_medium</c> field, not from prose: sniffing the action for
    /// "the look changes" flagged ordinary description and silently dropped the medium lock.
    /// </summary>
    internal static bool ClipDeclaresDifferentMedium(JsonElement clipEl, string? filmMedium)
    {
        var film = VisualMediumStyles.NormalizeMedium(filmMedium);
        if (!VisualMediumStyles.IsDecidedMedium(film))
            return false;
        if (!clipEl.TryGetProperty(JsonKeys.VisualMedium, out var vm)
            || vm.ValueKind != JsonValueKind.String)
            return false;
        var clip = vm.GetString();
        if (!VisualMediumStyles.IsDecidedMedium(clip))
            return false;
        return !string.Equals(
            VisualMediumStyles.NormalizeMedium(clip), film, StringComparison.Ordinal);
    }

    /// <summary>Project visual medium: explicit arg, else <see cref="ProjectVisionMeta"/> on disk.</summary>
    internal static string ResolveVisualMedium(string? projectDir, string? visualMedium)
    {
        if (!string.IsNullOrWhiteSpace(visualMedium))
            return VisualMediumStyles.NormalizeMedium(visualMedium);
        if (!string.IsNullOrWhiteSpace(projectDir))
        {
            var vision = ProjectVisionMeta.TryGetDecided(projectDir);
            if (vision is not null)
                return VisualMediumStyles.NormalizeMedium(vision.VisualMedium);
        }
        return "";
    }

    /// <summary>
    /// One lock per prompt: project <c>render_style_lock</c>, else
    /// <see cref="VisualMediumStyles.StyleLockFor"/>. A disagreeing 3D lock in Stage-2
    /// action text is discarded — lint still reports the plan drift.
    /// </summary>
    internal static string ResolveStyleLockForGeneration(
        string? projectDir,
        string? styleHead,
        string? rawVisual,
        string resolvedMedium)
    {
        if (!string.IsNullOrWhiteSpace(projectDir))
        {
            var vision = ProjectVisionMeta.TryGetDecided(projectDir);
            if (!string.IsNullOrWhiteSpace(vision?.RenderStyleLock))
                return vision.RenderStyleLock.Trim();
        }

        var medium = VisualMediumStyles.NormalizeMedium(resolvedMedium);
        if (medium is VisualMediumStyles.MediumIllustrated
            or VisualMediumStyles.MediumPhotoreal
            or VisualMediumStyles.MediumStylized3d)
        {
            var canonical = VisualMediumStyles.StyleLockFor(medium);
            var fromHead = VisualMediumStyles.StripStyleLockLabel(styleHead);
            if (!string.IsNullOrWhiteSpace(fromHead)
                && VisualMediumStyles.StyleLocksAgree(fromHead, canonical))
                return styleHead!.Trim();
            return canonical;
        }

        var plannedHead = VisualMediumStyles.StripStyleLockLabel(styleHead);
        var extracted = VisualMediumStyles.StripStyleLockLabel(ExtractStyleHead(rawVisual ?? ""));
        if (!string.IsNullOrWhiteSpace(plannedHead) && !string.IsNullOrWhiteSpace(extracted)
            && !VisualMediumStyles.StyleLocksAgree(plannedHead, extracted))
            return styleHead!.Trim();
        if (!string.IsNullOrWhiteSpace(styleHead))
            return styleHead.Trim();
        var fromPlan = ExtractStyleHead(rawVisual ?? "");
        return string.IsNullOrWhiteSpace(fromPlan) ? "" : fromPlan.Trim();
    }

    /// <summary>
    /// Remove every StyleLock from action — tagged and prose — so the prompt
    /// head is the only lock. Must-fix: Stage-2 still injects
    /// <c>STYLE LOCK: stylized 3D animated children's picture-book CG…</c>.
    /// </summary>
    internal static string StripStyleLocksFromAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return action ?? "";
        // Tagged lock: a real field, removed as a block.
        var text = BlockRegexFor(PromptFieldTags.StyleLock).Replace(action, "");
        // Loose "STYLE LOCK:" prose: scanned out by hand rather than matched. A regex here has to
        // guess where the lock ends, and the unbounded form took the rest of the beat with it.
        return CollapseWhitespace(RemoveLooseStyleLockSentences(text)).Trim();
    }

    /// <summary>
    /// Remove each loose <c>STYLE LOCK:</c> run from <paramref name="text"/>, one at a time, each
    /// ending at the first sentence end, line break, or opening tag after it — whichever comes
    /// first. Bounded by construction: no single removal can pass the end of its own line.
    /// </summary>
    internal static string RemoveLooseStyleLockSentences(string text)
    {
        const string marker = "STYLE LOCK";
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var start = text.IndexOf(marker, i, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            var colon = ColonOnSameLine(text, start + marker.Length);
            if (colon < 0)
            {
                // "STYLE LOCK" with no colon on its line is prose about the lock, not a lock.
                sb.Append(text, i, start + marker.Length - i);
                i = start + marker.Length;
                continue;
            }
            sb.Append(text, i, start - i);
            i = LooseStyleLockEnd(text, colon + 1);
        }
        return sb.ToString();
    }

    /// <summary>Colon closing "STYLE LOCK" / "STYLE LOCK (hard)" on the same line, else -1.</summary>
    private static int ColonOnSameLine(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ':')
                return i;
            if (c is '\r' or '\n')
                return -1;
            if (!char.IsWhiteSpace(c) && c is not ('(' or ')') && !char.IsLetter(c))
                return -1;
        }
        return -1;
    }

    /// <summary>End of a loose lock: first sentence end, line break, or opening tag after it.</summary>
    private static int LooseStyleLockEnd(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
                return i;
            if (c == '<' && i + 1 < text.Length && char.IsLetter(text[i + 1]))
                return i;
            if (c is '.' or '!' or '?')
                return Math.Min(text.Length, i + 1);
        }
        return text.Length;
    }

    /// <summary>
    /// After house / project rules are appended (they often restate STYLE LOCK),
    /// keep exactly one lock — the canonical project/medium lock.
    /// </summary>
    public static string EnsureSingleStyleLock(string? prompt, string? styleLock)
    {
        var text = prompt ?? "";
        text = BlockRegexFor(PromptFieldTags.StyleLock).Replace(text, "");
        text = RemoveLooseStyleLockSentences(text);
        // House "- Style:" bullets named example media and bypassed the STYLE LOCK: strip.
        text = CommonRegex.Replace(text, @"(?im)^\s*-\s*Style:\s*.+\r?\n?", "");
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(styleLock)) return text;
        var head = styleLock.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
            ? styleLock.Trim()
            : StyleLockPrefix + styleLock.Trim();
        return head + "\n\n" + text;
    }

    private static void AddCsvNegatives(List<string> dest, string csv)
    {
        foreach (var p in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (p.Length == 0)
                continue;
            if (dest.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            dest.Add(p);
        }
    }

    private static string SimplifyVisual(string visual)
    {
        visual = StripFountainLeakage(visual);
        visual = ScrubContentSafetyTriggers(visual);
        visual = CommonRegex.Replace(visual, @"\s+", " ").Trim();
        return visual;
    }

    /// <summary>
    /// Soften trigger words in visual action prompts that cause AI video model safety/content moderation rejections,
    /// while preserving cinematic period action and story meaning across any story.
    /// Does not alter dialogue/audio payloads.
    /// </summary>
    public static string ScrubContentSafetyTriggers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var t = text;

        // Dismemberment / mutilation → cinematic period work / preparation
        t = DismemberingRegex.Replace(t, "working in methodical silence");
        t = DismembermentRegex.Replace(t, "methodical preparation");
        t = SeverLimbsRegex.Replace(t, "carefully separating parts");
        t = CutOffLimbsRegex.Replace(t, "methodically work with dark tools");

        // Explicit anatomical remains / corpse → deceased / quiet subject / hidden task
        t = CorpseRegex.Replace(t, "quiet form");
        t = HumanRemainsRegex.Replace(t, "hidden burden");
        t = DepositsRemainsRegex.Replace(t, "deposits the contents");

        // Excessive gore/blood terms in visual action (audio/dialogue untouched)
        t = GhastlyGoryRegex.Replace(t, "grim shadowy");
        t = BloodyRemainsRegex.Replace(t, "dark hidden burden");

        return t;
    }

    /// <summary>Normalize resolution labels for prompt technical suffix (API may use same string).</summary>
    public static string NormalizeResolutionLabel(string? resolution)
    {
        var r = (resolution ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(r)) return "480p";
        if (r is "480" or "480p") return "480p";
        if (r is "720" or "720p") return "720p";
        if (r is "1080" or "1080p") return "1080p";
        if (CommonRegex.IsMatch(r, @"^\d{3,4}p$")) return r;
        return r.EndsWith('p') ? r : r + "p";
    }

    private static int CharacterRefPriority(string key)
    {
        var k = key.ToLowerInvariant();
        if (CommonRegex.IsMatch(k, @"(^|_)(dog|cat|bear|fox|rabbit|bunny|mouse|bird|horse|pig|wolf|owl)(s|es)?($|_)"))
            return 0;
        if (CommonRegex.IsMatch(k, @"(mom|mum|mother|dad|daddy|father|parent|human)"))
            return 2;
        return 1;
    }


    /// <summary>
    /// When API cannot attach locked refs (video-extend), reinforce identity from CHARACTER VARIABLES text.
    /// </summary>
    private static string IdentityReinforceBlock(
        IReadOnlyList<string> onScreenKeys,
        bool refsAttached,
        string? visualMedium = null)
    {
        if (refsAttached || onScreenKeys.Count == 0) return "";
        var medium = VisualMediumStyles.NormalizeMedium(visualMedium);
        return " " + PromptTags.Wrap("Identity",
            "Match locked plate descriptions in Characters exactly — " +
            VisualMediumStyles.IdentityMediumDriftClause(medium) + ". " +
            "On-screen: " + string.Join(", ", onScreenKeys) + ".");
    }

    private static bool IsVoiceOnlyKey(string key, IReadOnlyDictionary<string, CharacterProfile>? characters)
    {
        // Prefer explicit profile / cast seed flag. Do NOT force VOICE ONLY merely because
        // the key contains "Narrator" — confessor roles are often on camera (e.g. Tell-Tale Heart).
        if (characters is not null &&
            characters.TryGetValue(key, out var p))
            return p.VoiceOnly;
        return false;
    }

    /// <summary>
    /// True when an API/error message indicates the prompt exceeded context or length limits.
    /// Used to decide shorten-and-retry (not permanent fail).
    /// </summary>
    public static bool IsPromptTooLongError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message;
        if (ContainsAnyIgnoreCase(m, PromptTooLongPhrases)) return true;
        if (ContainsBothIgnoreCase(m, "maximum length", "prompt")) return true;
        if (Contains4096LengthCap(m)) return true;
        return IsHttp413TooLarge(m);
    }

    private static bool ContainsAnyIgnoreCase(string message, string[] phrases) =>
        phrases.Any(phrase => message.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsBothIgnoreCase(string message, string a, string b) =>
        message.Contains(a, StringComparison.OrdinalIgnoreCase) &&
        message.Contains(b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains4096LengthCap(string message) =>
        message.Contains("4096", StringComparison.Ordinal) &&
        message.Contains("length", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp413TooLarge(string message) =>
        CommonRegex.IsMatch(message, @"\b413\b") &&
        (message.Contains("large", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("size", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Progressive shorten for API length retries. Prefer dropping HOUSE RULES / project
    /// addenda first, then cap total length while keeping the head (character locks + framing).
    /// <paramref name="attempt"/> is 1-based (first retry = 1).
    /// </summary>
    public static string ShortenPromptForRetry(string prompt, int attempt, int hardCapChars = VideoPromptHardCapChars)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt;
        attempt = Math.Max(1, attempt);
        // Retry always drops house-rule / project addenda first (even if under cap)
        var p = StripLearningAddenda(prompt);
        if (p.Length > hardCapChars)
            p = HeadCap(p, hardCapChars);

        if (attempt == 1)
            return p;

        // Later attempts: tighter caps (chars), keep head where identity/action live
        var cap = Math.Min(hardCapChars, attempt switch
        {
            2 => (int)(hardCapChars * 0.8),
            3 => (int)(hardCapChars * 0.6),
            4 => (int)(hardCapChars * 0.4),
            _ => (int)(hardCapChars * 0.3)
        });
        if (p.Length <= cap)
            return p;
        return HeadCap(p, cap);
    }

    /// <summary>
    /// Drop trailing house-rule / project-rule blocks so core action + locks fit the API cap.
    /// </summary>
    private static string StripLearningAddenda(string prompt)
    {
        var markers = new[]
        {
            "\nHOUSE RULES:",
            "\nPROJECT HOUSE RULES",
            // Legacy pack markers (old stored prompts / tests)
            "\n# Film Studio gen pack",
            "\n# Film Studio gen pack (active addendum)",
            "\nApply these house rules when building clip video prompts:",
        };
        var cut = -1;
        foreach (var m in markers)
        {
            var i = prompt.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (cut < 0 || i < cut))
                cut = i;
        }
        if (cut < 0) return prompt.TrimEnd();
        return prompt[..cut].TrimEnd();
    }

    private static string HeadCap(string prompt, int maxChars)
    {
        if (prompt.Length <= maxChars) return prompt;
        if (maxChars < 64) maxChars = 64;
        var head = prompt[..maxChars];
        // Prefer break at paragraph / sentence
        var nl = head.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (nl > maxChars * 2 / 3) head = head[..nl];
        else
        {
            var sp = head.LastIndexOf(' ');
            if (sp > maxChars * 2 / 3) head = head[..sp];
        }
        return head.TrimEnd();
    }
}
