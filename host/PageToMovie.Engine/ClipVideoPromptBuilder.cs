using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Engine.Deterministic.Pronunciation;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

/// <summary>
/// Builds Grok video prompts (character variables + visual + audio) and resolves
/// character ref image paths for reference-to-video / image-to-video.
/// Owns CAST COUNT; strips Stage2-embedded count from action prose.
/// </summary>
public static class ClipVideoPromptBuilder
{
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

    public sealed class CharacterProfile
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Description { get; init; } = "";
        public string VisualLock { get; init; } = "";
        public string VoiceProfile { get; init; } = "";
        public string VoiceLabel { get; init; } = "";
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
        public string PromptLogSummary { get; init; } = "";
        /// <summary>Scene/clip location key when a set plate was considered.</summary>
        public string? LocationKey { get; init; }
        /// <summary>True when a locked location plate was attached as a reference image.</summary>
        public bool LocationRefAttached { get; init; }
        /// <summary><IMAGE_n> tag for the location plate when attached.</summary>
        public string? LocationImageTag { get; init; }

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
            PromptLogSummary = string.IsNullOrWhiteSpace(summarySuffix)
                ? PromptLogSummary
                : PromptLogSummary + summarySuffix,
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
        string? fallbackLocationKey = null)
    {
        characters ??= new Dictionary<string, CharacterProfile>(StringComparer.OrdinalIgnoreCase);
        // Model-aware, not a hardcoded constant: a future model with a larger (or smaller) prompt
        // budget only needs its models_catalog.json MaxPromptLength updated, never a code change.
        // Same resolution pattern already proven in FalVideoClient/FilmJobService; VideoPromptHardCapChars
        // is only the fallback for models with no catalog-specific value set.
        var selectedVideoModel = string.IsNullOrWhiteSpace(videoModel)
            ? SupportedModelCatalog.DefaultModelIdForCapability("video")
            : videoModel;
        var promptMaxLen = SupportedModelCatalog.ResolveOrDefault(selectedVideoModel, ModelCapability.Video)
            .MaxPromptLength
            ?? throw new InvalidOperationException(
                $"Model has no maxPromptLength in models_catalog.json for video prompt budget.");

        // Mode follows actual media inputs, not blueprint cont alone.
        // Cast-change reseed (PR2) clears previousClipVideoPath while blueprint may still say
        // extend_previous — that must be fresh+refs, not continue-without-frame.
        var hasPrevVideo = !string.IsNullOrWhiteSpace(previousClipVideoPath) &&
                           File.Exists(previousClipVideoPath);
        var hasStartFrame = !string.IsNullOrWhiteSpace(startFrameImagePath) &&
                            File.Exists(startFrameImagePath!);

        var mode = hasPrevVideo ? "video-extend"
            : hasStartFrame ? "continue"
            : "fresh";

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

        var rawVisual = clipEl.TryGetProperty("visual_prompt", out var vp)
            ? (vp.GetString() ?? "").Trim()
            : "";
        var actionText = SanitizeActionText(rawVisual, onScreenKeys);

        // Clip location_id, else scene primary_location_id from caller (many clips omit location_id).
        var locationKeyResolved = ResolveClipLocationKey(clipEl) ?? NormalizeLocationKey(fallbackLocationKey);
        var hasLocationPlate = !string.IsNullOrWhiteSpace(locationKeyResolved) &&
                               ResolveLocationRefPath(projectDir, locationKeyResolved) is not null;
        // Reserve one IMAGE slot for a locked set plate so multi-cast scenes still keep place identity.
        var charRefBudget = hasLocationPlate && maxRefs > 1 ? maxRefs - 1 : maxRefs;

        var refPaths = FindCharacterRefPathsForKeys(onScreenKeys, projectDir, charRefBudget);
        // Fresh gen attaches locked plates when available. Location-only establishing shots
        // (no on-screen cast) still get a set plate if locked — don't require character refs first.
        var useReferenceImages =
            string.IsNullOrWhiteSpace(startFrameImagePath) &&
            !hasPrevVideo &&
            (refPaths.Count > 0 || hasLocationPlate);

        var imageTagByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? locationKey = locationKeyResolved;
        string? locationImageTag = null;
        var locationRefAttached = false;
        if (useReferenceImages)
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
            if (!string.IsNullOrWhiteSpace(locationKey) && orderedPaths.Count < maxRefs)
            {
                var locPath = ResolveLocationRefPath(projectDir, locationKey);
                if (locPath is not null)
                {
                    n++;
                    orderedPaths.Add(locPath);
                    locationImageTag = $"<IMAGE_{n}>";
                    locationRefAttached = true;
                }
            }
            refPaths = orderedPaths;
        }

        var style = (styleHead ?? ExtractStyleHead(rawVisual) ?? "").Trim();
        var activeKeys = ResolveFocusKeysForClip(onScreenKeys, clipEl);
        var varBlock = BuildCharacterVariablesBlock(allKeys, characters, imageTagByKey, useReferenceImages, activeKeys);
        var audioBlock = BuildAudioBlock(clipEl, characters);

        var continuityBlock = mode switch
        {
            "video-extend" => PromptTags.Wrap("Continuity",
                "This is a seamless EXTENSION of the provided previous video. " +
                "Pick up from its last frame. Same character identity, wardrobe, lighting, and location. " +
                "Natural progressive motion only — do not invent a new establishing shot or redesign faces/outfits."),
            "continue" => PromptTags.Wrap("Continuity",
                "Continue seamlessly from the provided starting frame (end of previous clip). " +
                "Same character identity, wardrobe, lighting, and location. Natural progressive motion only — " +
                "do not invent a new establishing shot or redesign faces/outfits."),
            _ =>
                "Follow the camera framing and location in this prompt exactly. " +
                "Prioritize the PRIMARY subject and ONE clear action with visible motion; " +
                "background characters may stay mostly still.",
        };

        // video-extend cannot attach locked plates (API continues from previous video only).
        // Reinforce identity from CHARACTER VARIABLES text so faces/wardrobe do not drift.
        if (mode is "video-extend" or "continue")
            continuityBlock += IdentityReinforceBlock(onScreenKeys, useReferenceImages);

        if (!string.IsNullOrWhiteSpace(previousClipVisualPrompt) &&
            mode is "continue" or "video-extend")
        {
            var prevClean = SanitizeActionText(previousClipVisualPrompt!, onScreenKeys);
            var note = mode == "video-extend"
                ? "already provided as video input — continue from its last frame"
                : "context — match look and continue motion from its end";
            // prevClean is a re-embedded previous clip's own action text — it may itself already
            // contain Camera/Performance/Optics tags from that clip's construction, so this is a
            // structural wrap (no additional SanitizeValue here; see PromptTags' class doc).
            continuityBlock =
                PromptTags.WrapWithNote("PreviousClip", note, "\n" + prevClean + "\n") + "\n\n" + continuityBlock;
        }
        else if (!string.IsNullOrWhiteSpace(previousClipVisualPrompt) && mode == "fresh")
        {
            // Cast-change reseed: no video input, but keep prior clip prose for location/lighting only.
            var prevClean = SanitizeActionText(previousClipVisualPrompt!, onScreenKeys);
            continuityBlock = PromptTags.WrapWithNote("Context",
                "prior clip in scene — new cast plate refs attached; match location/lighting if still " +
                "valid; identity from Characters + locked plates only",
                "\n" + prevClean + "\n") + "\n\n" + continuityBlock;
        }

        var castCountLine = onScreenKeys.Count > 0
            ? PromptTags.Wrap("CastCount",
                $"exactly {onScreenKeys.Count} distinct on-screen character identity(ies) only — " +
                string.Join(", ", onScreenKeys) +
                ". Do not invent extra people, duplicate faces, or crowd extras not listed.")
            : "";

        var actionTagged = actionText;
        foreach (var (key, tag) in imageTagByKey)
        {
            if (!string.IsNullOrWhiteSpace(key))
                actionTagged = actionTagged.Replace(key, $"{key} {tag}", StringComparison.OrdinalIgnoreCase);
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(style))
        {
            sb.AppendLine(style.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
                ? style
                : "STYLE LOCK: " + style);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(varBlock))
        {
            sb.AppendLine(varBlock);
            sb.AppendLine();
        }

        if (locationRefAttached && !string.IsNullOrWhiteSpace(locationImageTag) &&
            !string.IsNullOrWhiteSpace(locationKey))
        {
            sb.AppendLine(PromptTags.Wrap("SetReference",
                $"{locationImageTag} is the locked LOCATION / SET plate for {locationKey}. " +
                "Match architecture, materials, props, depth, and lighting of that plate. " +
                "Do not invent a different building or landscape. Characters from CHARACTER VARIABLES " +
                "perform in this set — faces come from character plates, place from this set plate."));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(castCountLine))
        {
            sb.AppendLine(castCountLine);
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(audioBlock))
        {
            sb.AppendLine(audioBlock);
            sb.AppendLine();
        }

        sb.AppendLine(continuityBlock);
        sb.AppendLine();
        sb.AppendLine(PromptTags.Open("Clip"));
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
        var hasDialogue =
            clipEl.TryGetProperty("audio_payload", out var apForClose) &&
            apForClose.TryGetProperty("dialogue", out var dlgForClose) &&
            !string.IsNullOrWhiteSpace(dlgForClose.GetString());
        sb.AppendLine(hasDialogue
            ? "End cleanly when the spoken line and primary action finish — " +
              "do not hold a frozen pose or empty silence after dialogue."
            : "Silent beat — no dialogue in this clip. Do not show any on-screen character " +
              "speaking or mouthing words; keep mouths closed/neutral. " +
              "End cleanly when the primary physical action finishes.");
        sb.Append(actionTagged);
        // Resolution/frame rate are NOT re-echoed as prompt text here — they're already real,
        // separate fields in the video API's request payload (GrokVideoClient.SubmitFreshOnceAsync:
        // "resolution", "duration"), so appending "/ 480p, 24fps" as prose was pure duplication
        // with no effect on what the API actually renders at.

        var negBlock = BuildNegativeBlock(clipEl);
        if (!string.IsNullOrWhiteSpace(negBlock))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(negBlock);
        }

        // Embedded house rules (git-owned). Placed after core action so budget strip can drop
        // them first without cutting CHARACTER VARIABLES / THIS CLIP. Marker: HOUSE RULES:
        var houseRules = TryLoadClipGenRules();
        if (!string.IsNullOrWhiteSpace(houseRules))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(houseRules.Trim());
        }

        var prompt = FitPromptToVideoBudget(sb.ToString().Trim(), promptMaxLen);
        IReadOnlyList<string> attached = useReferenceImages ? refPaths : Array.Empty<string>();

        var summary =
            $"mode={mode} chars={allKeys.Count} onScreen={onScreenKeys.Count} " +
            $"refs={attached.Count} loc={(locationRefAttached ? locationKey : locationKey is null ? "none" : "unlocked")} " +
            $"startFrame={(startFrameImagePath is null ? "no" : "yes")} " +
            $"promptLen={prompt.Length}" +
            (previousClipVideoPath is { Length: > 0 }
                ? $" prevVideo={Path.GetFileName(previousClipVideoPath)}"
                : "");

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
            PromptLogSummary = summary,
        };
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
    /// Remove Stage2-embedded CAST COUNT so the builder owns a single count line.
    /// Ensures each on-screen key appears at least once in action prose.
    /// </summary>
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
        v = StripFountainLeakage(v);
        // Blueprint may embed lip-sync / says quotes with crushed dashes — speech-safe for gen
        v = SanitizeSpokenQuotesInVisual(v);
        v = SimplifyVisual(v);
        if (onScreenKeys is { Count: > 0 })
        {
            foreach (var key in onScreenKeys)
            {
                if (!v.Contains(key, StringComparison.OrdinalIgnoreCase))
                    v = $"{v} {key} is on screen.".Trim();
            }
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
        masked = Regex.Replace(
            masked,
            @"\b(\p{L}{1,3})-(\p{L}+)\b",
            m =>
            {
                var token = $"\uE000{masks.Count}\uE001";
                masks.Add(m.Value);
                return token;
            });

        // Remaining letter-letter hyphens → pause (nervous-very, unhappy-to)
        masked = Regex.Replace(masked, @"(?<=\p{L})-(?=\p{L})", " — ");

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
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ResFpsSuffixRegex1 = new(@"\s*/\s*\d{3,4}p\s*,\s*\d{2}fps\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResFpsSuffixRegex2 = new(@"\s*/\s*\d+p[^/]*24fps\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResFpsSuffixRegex3 = new(@"\s*/\s*\d{3,4}p\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CastCountRegex = new(@"\bCAST COUNT:\s*exactly\s+\d+[^.]*\.\s*(?:No extra people\.\s*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoExtraPeopleRegex = new(@"\bNo extra people\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnicodeDashesRegex = new(@"\s*[\u2012\u2013\u2014\u2015]\s*", RegexOptions.Compiled);
    private static readonly Regex DoubleHyphenRegex = new(@"\s*--\s*", RegexOptions.Compiled);
    private static readonly Regex PunctuationDashRegex = new(@"([!?.;:])\s*-+\s*", RegexOptions.Compiled);
    private static readonly Regex WhitespaceSingleRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DashCapitalizationRegex = new(@"([.!?])\s+—\s+(\p{L})", RegexOptions.Compiled);
    private static readonly Regex PunctuationCapitalizationRegex = new(@"([.!?])\s+(\p{Ll})", RegexOptions.Compiled);
    private static readonly Regex FirstWordMatchRegex = new(@"^[\p{L}\p{N}']+[!?]?", RegexOptions.Compiled);
    private static readonly Regex ContdRegex = new(@"\s*\(\s*CONT'?D\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ContinuedRegex = new(@"\s*\(\s*CONTINUED\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VoRegex = new(@"\s*\(\s*V\s*\.?\s*O\s*\.?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OsRegex = new(@"\s*\(\s*O\s*\.?\s*S\s*\.?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OcRegex = new(@"\s*\(\s*O\s*\.?\s*C\s*\.?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PronounGlueRegex1 = new(@"\b(Character_[A-Za-z0-9_]+)\s+(He|She|They)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PronounGlueRegex2 = new(@"\b(Character_[A-Za-z0-9_]+)\s+(His|Her|Their)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DuplicateCharacterKeyRegex = new(@"\b(Character_[A-Za-z0-9_]+)(\s+\1)+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DoubleSpacesRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex DotSpaceRegex = new(@"\s+\.", RegexOptions.Compiled);
    private static readonly Regex DoubleDotsRegex = new(@"\.\s*\.", RegexOptions.Compiled);
    private static readonly Regex DismemberingRegex = new(@"\bdismember(?:ing|ed|s)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DismembermentRegex = new(@"\bdismemberment\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeverLimbsRegex = new(@"\bsever(?:ing|ed|s)?\s+(?:head|arms?|legs?|limbs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CutOffLimbsRegex = new(@"\bcut\s+off\s+the\s+(?:head|arms?|legs?|limbs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CorpseRegex = new(@"\bcorpse\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HumanRemainsRegex = new(@"\bhuman\s+remains\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DepositsRemainsRegex = new(@"\bdeposits?\s+the\s+remains\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GhastlyGoryRegex = new(@"\bghastly\s+gory\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BloodyRemainsRegex = new(@"\bbloody\s+remains\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Apply <see cref="SanitizeSpokenDialogue"/> to quoted lines after lip-syncs / says / narrates
    /// in Stage2 visual prose so gen sees the same speech-safe text as the AUDIO block.
    /// </summary>
    public static string SanitizeSpokenQuotesInVisual(string? visual)
    {
        if (string.IsNullOrWhiteSpace(visual))
            return visual ?? "";

        return Regex.Replace(
            visual,
            @"(?<=(?:lip-syncs|says|narrates(?:\s+exactly)?)\s+)""([^""]*)""",
            m => "\"" + SanitizeSpokenDialogue(m.Groups[1].Value) + "\"",
            RegexOptions.IgnoreCase);
    }

    /// <summary>First word/token of a spoken line (for gen cues that protect the opening).</summary>
    public static string FirstSpokenToken(string? dialogue)
    {
        if (string.IsNullOrWhiteSpace(dialogue))
            return "";
        // Prefer word + trailing ! ? if present (True!)
        var m = FirstWordMatchRegex.Match(dialogue.Trim());
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
        // "Character_Narrator His hands…" → "Character_Narrator hands…" is wrong;
        // drop possessive pronoun after key: "Character_X His " → "Character_X "
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
        void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            key = key.Trim();
            if (!key.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)) return;
            if (found.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(key);
        }

        if (clipEl.TryGetProperty("characters_on_screen", out var cos) &&
            cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
                Add(x.GetString());
        }

        // Authoritative plan list present (even empty) — do not re-infer from prose
        if (clipEl.TryGetProperty("characters_on_screen", out cos) &&
            cos.ValueKind == JsonValueKind.Array)
            return found;

        // Legacy clips without the field: explicit Character_* tokens only
        if (clipEl.TryGetProperty("primary_subject", out var ps))
            Add(ps.GetString());
        foreach (var k in ClipCharacterKeys(clipEl))
            Add(k);

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
        void Add(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            key = key.Trim();
            if (!key.StartsWith("Character_", StringComparison.OrdinalIgnoreCase)) return;
            if (found.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(key);
        }

        foreach (var k in ResolveOnScreenCharacterKeys(clipEl))
            Add(k);

        if (clipEl.TryGetProperty("primary_subject", out var ps))
            Add(ps.GetString());

        if (clipEl.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object &&
            ap.TryGetProperty("speaker", out var sp))
            Add(sp.GetString());

        // Only when plan list is missing — Character_* tokens in visual (not free-text names)
        if (!(clipEl.TryGetProperty("characters_on_screen", out var cos) &&
              cos.ValueKind == JsonValueKind.Array))
        {
            foreach (var k in ClipCharacterKeys(clipEl))
                Add(k);
        }

        return found;
    }

    /// <summary>
    /// Fit a finished prompt under the video API hard cap before the first request.
    /// Drops HOUSE RULES / project-rule addenda first, then head-caps if still over.
    /// </summary>
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

        return HeadCap(p, hardCapChars);
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

        // 1. Simplify verbose section headers & repetitive directives. Section tags
        // (<Characters>, <Context>, <PreviousClip>) carry a "note" attribute with the full
        // instructional wording for the uncompressed prompt — drop it here, the bare tag name is
        // enough once the prompt is already tight on budget.
        p = PromptTags.StripNotes(p);
        p = p.Replace("REQUIRED native Grok dialogue.", "");
        p = p.Replace("Do not invent extra people, duplicate faces, or crowd extras not listed.", "");
        p = p.Replace("Follow the camera framing and location in this prompt exactly. Prioritize the PRIMARY subject and ONE clear action with visible motion; background characters may stay mostly still.", "");

        // 2. Map all distinct Character_* keys to compact aliases C1, C2, C3... — numbered in
        // first-appearance order (readable, matches existing behavior), but REPLACED longest-key-
        // first: a plain string Replace of a key that happens to be a prefix of another (e.g.
        // "Character_Mom" vs "Character_Mom_Assistant") would otherwise mangle the longer key's
        // occurrences into "C1_Assistant" before it ever got its own turn to alias, silently
        // corrupting that character's identity references for the rest of the prompt.
        var matches = Regex.Matches(p, @"\bCharacter_([A-Za-z0-9_]+)\b");
        var distinctKeys = matches.Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var aliasByKey = distinctKeys
            .Select((key, i) => (key, alias: $"C{i + 1}"))
            .ToDictionary(x => x.key, x => x.alias, StringComparer.OrdinalIgnoreCase);

        foreach (var key in distinctKeys.OrderByDescending(k => k.Length))
            p = p.Replace(key, aliasByKey[key]);

        // 3. Compress image reference tags (<IMAGE_1> -> I1, <IMAGE_2> -> I2)
        p = Regex.Replace(p, @"<IMAGE_(\d+)>", "I$1");

        // 4. Compress technical camera/stock text. Camera/Performance/Optics/VisualLock/Audio/
        // Score/Ambient/Foley/Pronunciation/Negative/CastCount are already emitted as
        // <Tag>...</Tag> at build time (see the "note" attribute strip above and the Voice/
        // VoiceLock strip below) — no label rename needed for those anymore. "Color grading:" is
        // the one holdout: it's partly embedded in ColorPaletteGradingClassifier's own AI prompt
        // template (an example output format shown to the model), not purely deterministic C#
        // string building like the others — converting it means editing what the model is shown,
        // a different/riskier kind of change than a label rename, so it's left as plain text and
        // still needs the rename here.
        p = p.Replace("Kodak Vision3 500T 5219 film stock", "Kodak 500T film");
        p = p.Replace("Color grading:", "Grade:");
        p = p.Replace("ON CAMERA lip-syncs", "lip-syncs");

        // Strip [Display Name] bracketed titles in character lines (C1/C2 alias is sufficient)
        p = Regex.Replace(p, @"(-\s*C\d+(?:\s+I\d+)?)\s*\[[^\]]+\]:", "$1:");

        // Strip resolution/fps suffix (e.g. " / 480p, 24fps" -> "") since resolution/fps is configured via API payload
        p = Regex.Replace(p, @"\s*/\s*\d+p,\s*\d+fps$", "");

        // Strip voice descriptions/locks (visual video models do not use voice tuning text).
        // Delimited by explicit <Voice>/<VoiceLock> tags rather than a bare "Voice:"/"VOICE LOCK"
        // label — a plain-text label match risked eating part of a dialogue line if it ever
        // happened to contain that literal substring (e.g. spoken text like "a voice: faint and
        // pleading"); an explicit tag can't collide with prose.
        p = PromptTags.Strip(p, "Voice");
        p = PromptTags.Strip(p, "VoiceLock");
        // Shorten, don't delete — this is the only explicit instruction to lock the focus
        // character's face to its attached reference image; dropping it entirely (as opposed to
        // just shortening the wording) left only the bare "I1" tag with no instruction attached,
        // exactly the failure mode most likely on busy multi-character prompts (the ones long
        // enough to trigger compression in the first place).
        p = Regex.Replace(p, @"\s*Match appearance of reference\s+(I\d+)\s+exactly\.?", " Match $1 exactly.");
        p = p.Replace("Start speaking immediately with ", "Start speaking: ");
        p = p.Replace(" — do not skip, delay, or swallow the opening word. After the last word, hold a brief natural pause with a closed mouth (about half a second); do not freeze mid-syllable or trail into empty staring. Other mouths closed. Speech intelligible; never silent.", ".");
        p = p.Replace("End cleanly when the spoken line and primary action finish — do not hold a frozen pose or empty silence after dialogue.", "");

        // 5. Collapse multiple blank lines & consecutive spaces
        p = Regex.Replace(p, @"\n\s*\n+", "\n");
        p = Regex.Replace(p, @"[ \t]+", " ");

        return p.Trim();
    }

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

        var officerKeys = characters.Keys
            .Where(k => k.Contains("Officer", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (officerKeys.Count > 0 &&
            Regex.IsMatch(text, @"\b(three|3)\s+officers?\b|\bofficers?\s+sit\b|\bthe officers\b"))
        {
            foreach (var k in officerKeys)
            {
                if (!list.Contains(k, StringComparer.OrdinalIgnoreCase))
                    list.Add(k);
            }
        }

        foreach (var (key, prof) in characters)
        {
            if (list.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(prof.DisplayName))
                names.Add(prof.DisplayName.Trim());
            var suffix = key.Replace("Character_", "", StringComparison.OrdinalIgnoreCase)
                .Replace('_', ' ').Trim();
            if (suffix.Length > 0) names.Add(suffix);
            if (key.Contains("Old_Man", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("OldMan", StringComparison.OrdinalIgnoreCase))
                names.Add("old man");
            if (key.Contains("Narrator", StringComparison.OrdinalIgnoreCase))
                names.Add("narrator");

            foreach (var n in names)
            {
                if (n.Length < 3) continue;
                if (text.Contains(n.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    list.Add(key);
                    break;
                }
            }
        }

        return list;
    }

    /// <summary>Pull leading STYLE LOCK sentence from plan visual if present.</summary>
    public static string? ExtractStyleHead(string visual)
    {
        if (string.IsNullOrWhiteSpace(visual)) return null;
        var m = Regex.Match(
            visual,
            @"STYLE LOCK:\s*([^.]+\.)",
            RegexOptions.IgnoreCase);
        return m.Success ? ("STYLE LOCK: " + m.Groups[1].Value.Trim()) : null;
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
        }
        return null;
    }

    public static IEnumerable<string> LocationRefFileCandidates(string locKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = Path.GetFileName(name.Trim().Replace(' ', '_')).ToLowerInvariant();
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = name.EndsWith("_ref", StringComparison.OrdinalIgnoreCase) ? name + ".png" : name + "_ref.png";
            if (seen.Add(name))
                list.Add(name);
        }

        Add(ProjectStore.LocationRefFileName(locKey));
        var raw = (locKey ?? "").Trim();
        if (raw.StartsWith("Loc_", StringComparison.OrdinalIgnoreCase))
        {
            var bare = raw["Loc_".Length..];
            Add(ProjectStore.LocationRefFileName(bare));
            Add(bare + "_ref.png");
        }
        else
        {
            Add(ProjectStore.LocationRefFileName("Loc_" + raw));
        }
        return list;
    }

    public static List<string> ClipCharacterKeys(JsonElement clipEl)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in Regex.Matches(text, @"Character_[A-Za-z0-9_]+"))
                found.Add(m.Value);
        }
        if (clipEl.TryGetProperty("visual_prompt", out var vp))
            Scan(vp.GetString());
        if (clipEl.TryGetProperty("primary_subject", out var ps))
            Scan(ps.GetString());
        if (clipEl.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object)
        {
            if (ap.TryGetProperty("speaker", out var sp))
                Scan(sp.GetString());
        }
        if (clipEl.TryGetProperty("characters_on_screen", out var cos) && cos.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in cos.EnumerateArray())
                Scan(x.GetString());
        }
        return found.ToList();
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

        foreach (var file in Directory.EnumerateFiles(charDir, "*_ref.png"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.StartsWith("wardrobe_", StringComparison.OrdinalIgnoreCase))
                continue; // shared costume plates, not a character's own portrait
            if (stem.EndsWith("_ref", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^"_ref".Length];
            if (Stage2PlannerService.NormalizeCharacterKey(stem) == targetNorm &&
                new FileInfo(file).Length >= 64)
                return file;
        }
        return null;
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

        // Prefer explicit Stage 2 list when present
        if (clipEl.TryGetProperty("focus_keys", out var fk) && fk.ValueKind == JsonValueKind.Array)
        {
            var fromPlan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in fk.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String &&
                    el.GetString() is { Length: > 0 } k &&
                    onScreen.Any(o => string.Equals(o, k, StringComparison.OrdinalIgnoreCase)))
                    fromPlan.Add(k);
            }
            if (fromPlan.Count > 0)
                return fromPlan;
        }

        string? primary = null;
        if (clipEl.TryGetProperty("primary_subject", out var psEl) && psEl.ValueKind == JsonValueKind.String)
            primary = psEl.GetString();

        string? speaker = null;
        string? secondarySpeaker = null;
        if (clipEl.TryGetProperty("audio_payload", out var ap) && ap.ValueKind == JsonValueKind.Object)
        {
            if (ap.TryGetProperty("speaker", out var spEl) && spEl.ValueKind == JsonValueKind.String)
                speaker = spEl.GetString();
            if (ap.TryGetProperty("secondary_speaker", out var ssEl) && ssEl.ValueKind == JsonValueKind.String)
                secondarySpeaker = ssEl.GetString();
        }

        string? actionClass = null;
        if (clipEl.TryGetProperty("action_class", out var acEl) && acEl.ValueKind == JsonValueKind.String)
            actionClass = acEl.GetString();

        return ResolveFocusKeys(onScreen, primary, speaker, actionClass, secondarySpeaker);
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
        void TryAdd(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var hit = onScreen.FirstOrDefault(o =>
                string.Equals(o, key, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                set.Add(hit);
        }

        TryAdd(primarySubject);
        TryAdd(speaker);
        TryAdd(secondarySpeaker);

        if (set.Count == 0)
            set.Add(onScreen[0]);

        return set;
    }

    private static string BuildCharacterVariablesBlock(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, CharacterProfile> characters,
        IReadOnlyDictionary<string, string> imageTagByKey,
        bool useImageTags,
        HashSet<string>? activeKeys = null)
    {
        if (keys.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(PromptTags.OpenWithNote("Characters",
            "use these identities consistently; do not redesign faces or wardrobe"));
        var any = false;
        foreach (var key in keys)
        {
            var p = GetCharacterProfile(characters, key);
            var display = !string.IsNullOrWhiteSpace(p?.DisplayName)
                ? p!.DisplayName
                : key.Replace("Character_", "").Replace('_', ' ');
            var tag = useImageTags && imageTagByKey.TryGetValue(key, out var t) ? $" {t}" : "";
            // Cast profile fields are free-form (admin/AI-authored) — sanitize once here at the
            // source rather than at each tag-wrap call site below.
            var desc = PromptTags.SanitizeValue(p?.Description?.Trim());
            var vlock = PromptTags.SanitizeValue(p?.VisualLock?.Trim());
            var voice = PromptTags.SanitizeValue(p?.VoiceProfile?.Trim());
            if (p?.VoiceOnly == true || IsVoiceOnlyKey(key, characters))
            {
                sb.AppendLine(
                    $"- {key}{tag} [{display}] VOICE ONLY — not on screen." +
                    (voice.Length > 0 ? $" {PromptTags.Wrap("Voice", voice)}" : ""));
                any = true;
                continue;
            }

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
            var isActive = activeKeys is null || activeKeys.Contains(key);
            if (!isActive && keys.Count > 1)
            {
                var compactSource = vlock.Length > 0 ? vlock : desc;
                var compact =
                    $"- {key}{tag} [{display}]: Also present (not shot focus); keep identity consistent: {compactSource}.";
                if (useImageTags && tag.Length > 0) compact += $" Match reference {tag.Trim()}.";
                sb.AppendLine(compact);
                any = true;
                continue;
            }

            var line = $"- {key}{tag} [{display}]:";
            if (desc.Length > 0) line += $" {desc}";
            if (vlock.Length > 0) line += $" {PromptTags.Wrap("VisualLock", vlock)}";
            if (voice.Length > 0) line += $" {PromptTags.Wrap("Voice", voice)}";
            if (useImageTags && tag.Length > 0)
                line += $" Match appearance of reference {tag.Trim()} exactly.";
            sb.AppendLine(line);
            any = true;
        }
        return any ? sb.ToString().TrimEnd() : "";
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

    private static string BuildAudioBlock(
        JsonElement clipEl,
        IReadOnlyDictionary<string, CharacterProfile>? characters)
    {
        if (!clipEl.TryGetProperty("audio_payload", out var audio) ||
            audio.ValueKind != JsonValueKind.Object)
            return "";

        // Every spoken line of this clip via the shared accessor so this reader can't diverge from
        // duration sizing / verification: the primary line PLUS any second speaker's line
        // (Stage2PlannerService.CoalesceCrossSpeakerDialogueBeats two-hander — camera pans from
        // speaker to speaker mid-clip).
        var spokenLines = ClipSpokenLines.FromAudioPayload(audio);
        var speaker = spokenLines.Count > 0 ? spokenLines[0].Speaker : "";
        var dialogue = spokenLines.Count > 0 ? spokenLines[0].Dialogue : "";
        var secondarySpeaker = spokenLines.Count > 1 ? spokenLines[1].Speaker : "";
        var secondaryDialogue = spokenLines.Count > 1 ? spokenLines[1].Dialogue : "";
        var delivery = Stage2PlannerService.NormalizeDelivery(
            spokenLines.Count > 0 ? spokenLines[0].Delivery : "none");
        // Stage2/AI-classifier free text — sanitize at the source (see PromptTags class doc).
        var sfx = PromptTags.SanitizeValue(audio.TryGetProperty("sfx", out var sx) ? sx.GetString() : null).Trim();
        var ambient = PromptTags.SanitizeValue(audio.TryGetProperty("ambient", out var am) ? am.GetString() : null).Trim();
        var score = PromptTags.SanitizeValue(
            audio.TryGetProperty("score_layer", out var sc) ? sc.GetString() :
            audio.TryGetProperty("score", out sc) ? sc.GetString() :
            audio.TryGetProperty("music_layer", out sc) ? sc.GetString() :
            audio.TryGetProperty("music", out sc) ? sc.GetString() : null).Trim();

        if (string.IsNullOrWhiteSpace(dialogue) &&
            string.IsNullOrWhiteSpace(sfx) &&
            string.IsNullOrWhiteSpace(ambient) &&
            string.IsNullOrWhiteSpace(score))
            return "";

        var voiceLock = "";
        var prof = GetCharacterProfile(characters, speaker);
        if (!string.IsNullOrWhiteSpace(speaker) &&
            prof is not null &&
            !string.IsNullOrWhiteSpace(prof.VoiceProfile))
        {
            voiceLock = " " + PromptTags.Wrap("VoiceLock",
                $"{speaker}: {PromptTags.SanitizeValue(prof.VoiceProfile)}");
        }

        if (!string.IsNullOrWhiteSpace(dialogue))
        {
            var who = string.IsNullOrWhiteSpace(speaker) ? "SPEAKER" : speaker.Trim();
            var isVoiceover = delivery is "voiceover_internal" or "internal" or "narration" or "vo" or "thought" ||
                              (delivery is not "spoken_on_camera" and not "on_camera" && who.Contains("narrator", StringComparison.OrdinalIgnoreCase));
            // Full line, speech-safe punctuation (em-dash normalize, !- glue) — same words. Story
            // dialogue text, sanitized like every other leaf value before it can reach a tag.
            var quote = PromptTags.SanitizeValue(SanitizeSpokenDialogue(dialogue));
            var open = FirstSpokenToken(quote);
            var openCue = open.Length > 0
                ? $" Start speaking immediately with \"{open}\" — do not skip, delay, or swallow the opening word."
                : " Start speaking immediately with the first word of the line — do not skip the opening.";

            var audioBedParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(score)) audioBedParts.Add(PromptTags.Wrap("Score", score));
            if (!string.IsNullOrWhiteSpace(ambient)) audioBedParts.Add(PromptTags.Wrap("Ambient", ambient));
            if (!string.IsNullOrWhiteSpace(sfx)) audioBedParts.Add(PromptTags.Wrap("Foley", sfx));

            var bed = audioBedParts.Count > 0
                ? " " + string.Join(" ", audioBedParts)
                : " Secondary layer = soft room tone / Foley.";

            const string endPause =
                " After the last word, hold a brief natural pause with a closed mouth (about half a second); do not freeze mid-syllable or trail into empty staring.";
            var pronHintInPayload = PromptTags.SanitizeValue(
                audio.TryGetProperty("pronunciation_hint", out var ph) ? ph.GetString() : null);
            // Only honor a pre-baked hint when its target word is actually in this line; otherwise derive
            // hints from the dialogue itself (which is inherently limited to words that are spoken).
            var pronHint = !string.IsNullOrWhiteSpace(pronHintInPayload)
                           && PronunciationResolver.HintAppliesToDialogue(pronHintInPayload, quote)
                ? (pronHintInPayload.StartsWith(" ") ? pronHintInPayload : $" {PromptTags.Wrap("Pronunciation", pronHintInPayload)}")
                : BuildPronunciationHints(quote);

            if (isVoiceover)
            {
                return PromptTags.Wrap("Audio",
                    $"REQUIRED native Grok off-camera voiceover. {who} narrates " +
                    $"exactly: \"{quote}\".{openCue}{endPause}{pronHint} Do not lip-sync on-screen cast to this VO.{bed}{voiceLock}");
            }

            // Two-hander: camera pans from {who} to {who2} mid-clip instead of cutting. Only
            // applies to the on-camera case — voiceover has no second on-screen mouth to sync.
            if (!string.IsNullOrWhiteSpace(secondarySpeaker) && !string.IsNullOrWhiteSpace(secondaryDialogue))
            {
                var who2 = secondarySpeaker.Trim();
                var quote2 = PromptTags.SanitizeValue(SanitizeSpokenDialogue(secondaryDialogue));
                var pronHint2 = BuildPronunciationHints(quote2);
                return PromptTags.Wrap("Audio",
                    $"REQUIRED native Grok dialogue. {who} ON CAMERA lip-syncs " +
                    $"exactly: \"{quote}\".{openCue} Then {who2} ON CAMERA lip-syncs " +
                    $"exactly: \"{quote2}\".{endPause}{pronHint}{pronHint2} Speech intelligible; never silent.{bed}{voiceLock}");
            }

            // spoken_on_camera / on_camera (normalized)
            return PromptTags.Wrap("Audio",
                $"REQUIRED native Grok dialogue. {who} ON CAMERA lip-syncs " +
                $"exactly: \"{quote}\".{openCue}{endPause}{pronHint} Other mouths closed. Speech intelligible; never silent.{bed}{voiceLock}");
        }

        if (!string.IsNullOrWhiteSpace(ambient) || !string.IsNullOrWhiteSpace(sfx) || !string.IsNullOrWhiteSpace(score))
        {
            var layers = new List<string>();
            if (!string.IsNullOrWhiteSpace(score)) layers.Add(PromptTags.Wrap("Score", score));
            if (!string.IsNullOrWhiteSpace(ambient)) layers.Add(PromptTags.Wrap("Ambient", ambient));
            if (!string.IsNullOrWhiteSpace(sfx)) layers.Add(PromptTags.Wrap("Foley", sfx));
            return PromptTags.Wrap("Audio", $"music/ambient/Foley only — {string.Join("; ", layers)}. No dialogue.");
        }
        return "";
    }

    private static string BuildPronunciationHints(string text)
    {
        var hints = PronunciationResolver.RenderPromptHints(PronunciationResolver.Default.Resolve(text));
        return hints.Length == 0 ? "" : " " + PromptTags.Wrap("Pronunciation", hints);
    }

    /// <summary>
    /// Global provider negatives + story-specific <c>negative_prompt</c> from the blueprint.
    /// </summary>
    private static string BuildNegativeBlock(JsonElement clipEl)
    {
        var story = clipEl.TryGetProperty("negative_prompt", out var np)
            ? PromptTags.SanitizeValue(np.GetString()).Trim()
            : "";
        var global = (GlobalNegativePrompt ?? "").Trim();
        if (global.Length == 0 && story.Length == 0)
            return "";

        // Dedupe tokens across global + story
        var items = new List<string>();
        void AddCsv(string csv)
        {
            foreach (var p in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (p.Length == 0) continue;
                if (items.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
                items.Add(p);
            }
        }
        if (global.Length > 0) AddCsv(global);
        if (story.Length > 0) AddCsv(story);
        if (items.Count == 0) return "";
        return PromptTags.Wrap("Negative", string.Join(", ", items));
    }

    private static string SimplifyVisual(string visual)
    {
        visual = StripFountainLeakage(visual);
        visual = ScrubContentSafetyTriggers(visual);
        visual = Regex.Replace(visual, @"\s+", " ").Trim();
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
        if (Regex.IsMatch(r, @"^\d{3,4}p$")) return r;
        return r.EndsWith('p') ? r : r + "p";
    }

    private static int CharacterRefPriority(string key)
    {
        var k = key.ToLowerInvariant();
        if (Regex.IsMatch(k, @"(^|_)(dog|cat|bear|fox|rabbit|bunny|mouse|bird|horse|pig|wolf|owl)(s|es)?($|_)"))
            return 0;
        if (Regex.IsMatch(k, @"(mom|mum|mother|dad|daddy|father|parent|human)"))
            return 2;
        return 1;
    }


    /// <summary>
    /// When API cannot attach locked refs (video-extend), reinforce identity from CHARACTER VARIABLES text.
    /// </summary>
    private static string IdentityReinforceBlock(IReadOnlyList<string> onScreenKeys, bool refsAttached)
    {
        if (refsAttached || onScreenKeys.Count == 0) return "";
        return " " + PromptTags.Wrap("Identity",
            "Match locked plate descriptions in Characters exactly — " +
            "do not drift to illustration, anime, cartoon, or a different face/wardrobe. " +
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
        // Common xAI / OpenAI-style and HTTP body phrases
        if (m.Contains("prompt too long", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("prompt length exceeds", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("exceeds the maximum allowed length", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("context length", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("maximum context", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("max context", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("token limit", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("too many tokens", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("maximum length", StringComparison.OrdinalIgnoreCase) &&
            m.Contains("prompt", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("maximum allowed length", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("payload too large", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Contains("request entity too large", StringComparison.OrdinalIgnoreCase)) return true;
        // xAI video often returns 4096 char hard cap in the error body
        if (m.Contains("4096", StringComparison.Ordinal) &&
            m.Contains("length", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(m, @"\b413\b") &&
            (m.Contains("large", StringComparison.OrdinalIgnoreCase) ||
             m.Contains("size", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

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
