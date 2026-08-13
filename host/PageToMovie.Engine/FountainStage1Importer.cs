using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Utils;
using PageToMovie.Fountain;

namespace PageToMovie.Engine;

/// <summary>
/// Parse Fountain into an in-memory screenplay model (beats, cast, locations)
/// used by Stage 2 and cast tooling. Does not write scenes.json — Stage 2 reads Fountain.
/// </summary>
public static class FountainStage1Importer
{
    private const string VisualEvent = "visual_event";
    private const string Establishing = "establishing";
    private const string TimeWeight = "time_weight";
    private const string Delivery = "delivery";
    private const string Ambient = "ambient";
    private const string CharactersOnScreen = "characters_on_screen";
    private const string UnspecifiedIntDay = "INT. UNSPECIFIED - DAY";
    private const string DisplayNamePolicy = "display_name_policy";
    private const string Unspecified = "Unspecified";
    private const string VisualLock = "visual_lock";

    public sealed class ImportResult
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        public string? OutPath { get; init; }
        public string? FountainSavedPath { get; init; }
        public int SceneCount { get; init; }
        public int CharacterCount { get; init; }
        public int LocationCount { get; init; }
        public string? Title { get; init; }
    }

    /// <summary>
    /// Save canonical Fountain draft only (no scenes.json). Prefer
    /// <see cref="ScreenplayService.ImportAsDraft"/> / <see cref="ScreenplayService.SignOff"/>.
    /// </summary>
    public static async Task<ImportResult> ImportToProjectAsync(
        ProjectStore projects,
        string projectId,
        string fountainText,
        string? originalFileName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fountainText))
            return new ImportResult { Ok = false, Error = "Empty Fountain text" };

        var parsed = FountainParser.Parse(fountainText);
        var doc = Stage1Normalizer.Normalize(BuildStage1(parsed));

        var projectDir = await projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var sourceDir = Path.Combine(projectDir, "source");
        Directory.CreateDirectory(sourceDir);

        var normalized = fountainText.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.EndsWith('\n')) normalized += "\n";
        var fountainPath = Path.Combine(sourceDir, ScreenplayService.CanonicalFileName);
        await File.WriteAllTextAsync(fountainPath, normalized, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(originalFileName))
        {
            var safeName = Path.GetFileName(originalFileName);
            if (!string.IsNullOrWhiteSpace(safeName) &&
                !safeName.Equals(ScreenplayService.CanonicalFileName, StringComparison.OrdinalIgnoreCase))
            {
                if (!safeName.EndsWith(".fountain", StringComparison.OrdinalIgnoreCase) &&
                    !safeName.EndsWith(".spmd", StringComparison.OrdinalIgnoreCase))
                    safeName = Path.GetFileNameWithoutExtension(safeName) + ".fountain";
                try { await File.WriteAllTextAsync(Path.Combine(sourceDir, safeName), normalized, ct).ConfigureAwait(false); } catch { /* ignore */ }
            }
        }

        projects.InvalidateSceneListCache(projectId);
        projects.TriggerAutoGitCommit(projectId, ProjectStageCommits.ScreenplayCreated);

        var gpv = doc["global_production_variables"] as Dictionary<string, object?>;
        var chars = gpv?["character_seed_tokens"] as Dictionary<string, object?>;
        var locs = gpv?["location_seed_tokens"] as Dictionary<string, object?>;
        var scenes = doc["scenes"] as List<object?>;

        return new ImportResult
        {
            Ok = true,
            OutPath = fountainPath,
            FountainSavedPath = fountainPath,
            SceneCount = scenes?.Count ?? 0,
            CharacterCount = chars?.Count ?? 0,
            LocationCount = locs?.Count ?? 0,
            Title = doc.TryGetValue(JsonKeys.MovieTitle, out var t) ? t?.ToString() : null,
        };
    }

    /// <summary>
    /// Save canonical Fountain draft only (no scenes.json). Prefer
    /// <see cref="ScreenplayService.ImportAsDraft"/> / <see cref="ScreenplayService.SignOff"/>.
    /// </summary>
    public static ImportResult ImportToProject(
        ProjectStore projects,
        string projectId,
        string fountainText,
        string? originalFileName = null)
        => ImportToProjectAsync(projects, projectId, fountainText, originalFileName).GetAwaiter().GetResult();

    /// <summary>
    /// Optional bounds (typically from <see cref="ClipDurationEstimator.ResolveBoundsForModel"/>) clamp
    /// monologue pre-splitting against the actually-selected video model's own limits instead of the
    /// global Grok-shaped defaults; omitted, behavior is unchanged.
    /// </summary>
    public static Dictionary<string, object?> BuildStage1(
        FountainParser.ParseResult parsed,
        int minSeconds = ClipDurationEstimator.MinSeconds,
        int maxSeconds = ClipDurationEstimator.MaxSeconds,
        int absMaxSeconds = ClipDurationEstimator.AbsMaxSeconds)
    {
        var title = FirstTitle(parsed, "Title") ?? FirstTitle(parsed, "title") ?? "Untitled";
        title = CleanEmphasis(title).Replace("\n", " ").Trim();
        if (title.Length == 0) title = "Untitled";

        var author = FirstTitle(parsed, "Author") ?? FirstTitle(parsed, "Authors");

        var scenes = new List<object?>();
        var charSeeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var locSeeds = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, object?>? curScene = null;
        List<object?>? beats = null;
        string? pendingChar = null;
        /// <summary>Character-cue extension from parser Meta (V.O. / O.S. / CONT'D), not performance paren.</summary>
        string? pendingMeta = null;
        string? pendingParen = null;
        /// <summary>Last filmable action visual in the open scene — reused as picture under V.O.</summary>
        string? lastPictureVisual = null;
        var actionBuf = new StringBuilder();
        var dialogueBuf = new StringBuilder();
        var beatIndex = 0;
        var sceneNum = 0;
        // Disambiguate identical content within a scene for stable ids (0-based).
        var contentOccurrence = new Dictionary<string, int>(StringComparer.Ordinal);

        string SceneKey()
        {
            if (curScene is not null && curScene.TryGetValue("setting", out var st) && st is string s && s.Length > 0)
                return s;
            if (curScene is not null && curScene.TryGetValue(JsonKeys.SceneNumber, out var sn))
                return $"scene:{sn}";
            return sceneNum > 0 ? $"scene:{sceneNum}" : "scene:0";
        }

        string NextStableBeatId(string kind, string? speaker, string? body)
        {
            var key = string.Join(
                '\u001f',
                StableBeatId.Normalize(kind),
                StableBeatId.Normalize(speaker),
                StableBeatId.Normalize(body));
            contentOccurrence.TryGetValue(key, out var n);
            contentOccurrence[key] = n + 1;
            return StableBeatId.ForContent(SceneKey(), kind, speaker, body, n);
        }

        void FlushAction()
        {
            if (actionBuf.Length == 0 || beats is null) return;
            var text = actionBuf.ToString().Trim();
            actionBuf.Clear();
            if (text.Length == 0) return;
            // Pure transitions are not filmable beats (would become empty clips)
            if (FountainParser.IsStandaloneTransitionLine(text) || IsNoopTransitionText(text))
                return;
            beatIndex++;
            var (ambient, sfx) = InferAmbientAndSfx(text);
            var isFirstInScene = beats.Count == 0;
            var actionClass = InferActionClass(text, isFirstInScene);
            lastPictureVisual = text;
            var kind = string.IsNullOrWhiteSpace(actionClass) ? "action" : actionClass;
            beats.Add(new Dictionary<string, object?>
            {
                ["beat_id"] = NextStableBeatId(kind, "", text),
                ["intent"] = Trunc(text, 120),
                [VisualEvent] = text,
                ["shot_scale_hint"] = actionClass is Establishing ? ShotScale.Wide.ToSnakeCase() : ShotScale.Medium.ToSnakeCase(),
                ["action_class"] = actionClass,
                ["continuity"] = beatIndex == 1 ? "new_setup" : "continuous_from_previous_beat",
                [TimeWeight] = Math.Clamp(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 12.0, 0.5, 3.0),
                [Delivery] = "none",
                [JsonKeys.Speaker] = "",
                [JsonKeys.Dialogue] = "",
                [Ambient] = ambient,
                ["sfx"] = sfx,
                ["audio"] = new Dictionary<string, object?>
                {
                    [Delivery] = "none",
                    [JsonKeys.Speaker] = "",
                    [JsonKeys.Dialogue] = "",
                    [Ambient] = ambient,
                    ["sfx"] = sfx,
                },
                [CharactersOnScreen] = CurrentOnScreen(curScene),
            });
        }

        void FlushDialogue()
        {
            if (pendingChar is null || dialogueBuf.Length == 0 || beats is null) return;
            var text = dialogueBuf.ToString().Trim();
            dialogueBuf.Clear();
            if (text.Length == 0)
            {
                pendingParen = null;
                pendingMeta = null;
                return;
            }

            var charKey = EnsureCharacter(charSeeds, pendingChar, pendingMeta);
            // V.O./O.S. lives in parser Meta after SplitCharacter — not in pendingChar name alone.
            var offScreen = IsOffScreenCue(pendingChar, pendingMeta);
            if (!offScreen)
                EnsureOnScreen(curScene, charKey);

            // Prose uses clean display name — never "NARRATOR (CONT'D) speaks."
            var displayName = CleanCharacterName(pendingChar);
            // On-camera: lip-sync visual. V.O.: keep picture (last action), not "X speaks."
            var visual = offScreen
                ? BuildVoiceoverVisualEvent(lastPictureVisual)
                : BuildDialogueVisualEvent(displayName, pendingParen, pendingChar);
            var delivery = offScreen ? "voiceover_internal" : "spoken_on_camera";
            // Parenthetical may carry light sfx ("whispering", rare); usually empty for dialogue
            var (_, parenSfx) = InferAmbientAndSfx(pendingParen ?? "");

            // Long monologues → multiple beats so each clip fits the video model max
            var parts = ClipDurationEstimator.SplitDialogueToFitModelMax(text, delivery, modelMaxSeconds: maxSeconds);
            if (parts.Count == 0)
                parts = new[] { text };

            // Picture cast for V.O.: who was already on screen — do not force speaker into frame
            var pictureCast = CurrentOnScreen(curScene);
            var primary = offScreen
                ? FirstCharacterKey(pictureCast)
                : charKey;

            // One content root for the full line; multi-part monologues share root via #pNofM.
            var kind = offScreen ? "voiceover" : JsonKeys.Dialogue;
            var monologueRoot = NextStableBeatId(kind, charKey, text);

            for (var p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                beatIndex++;
                var isFirst = beatIndex == 1;
                var beatId = StableBeatId.ForPart(monologueRoot, p, parts.Count);
                beats.Add(new Dictionary<string, object?>
                {
                    ["beat_id"] = beatId,
                    ["intent"] = Trunc(
                        parts.Count > 1
                            ? (offScreen
                                ? $"V.O.: {displayName} ({p + 1}/{parts.Count})"
                                : $"Dialogue: {displayName} ({p + 1}/{parts.Count})")
                            : (offScreen ? $"V.O.: {displayName}" : $"Dialogue: {displayName}"),
                        120),
                    [VisualEvent] = visual,
                    ["shot_scale_hint"] = offScreen ? ShotScale.Medium.ToSnakeCase() : "medium close",
                    ["action_class"] = offScreen ? "hold" : JsonKeys.Dialogue,
                    ["continuity"] = isFirst
                        ? "new_setup"
                        : "continuous_from_previous_beat",
                    [TimeWeight] = Math.Clamp(
                        ClipDurationEstimator.CountWords(part) / 8.0, 0.5, 4.0),
                    [Delivery] = delivery,
                    [JsonKeys.Speaker] = charKey,
                    [JsonKeys.Dialogue] = part,
                    [Ambient] = "",
                    ["sfx"] = parenSfx,
                    ["audio"] = new Dictionary<string, object?>
                    {
                        [Delivery] = delivery,
                        [JsonKeys.Speaker] = charKey,
                        [JsonKeys.Dialogue] = part,
                        [Ambient] = "",
                        ["sfx"] = parenSfx,
                    },
                    ["primary_subject"] = primary is { Length: > 0 } ? primary : null,
                    [CharactersOnScreen] = pictureCast.ToList(),
                });
            }

            pendingParen = null;
            pendingMeta = null;
        }

        Dictionary<string, object?>? CloseScene()
        {
            FlushAction();
            FlushDialogue();
            pendingChar = null;
            pendingMeta = null;
            pendingParen = null;
            lastPictureVisual = null;
            if (curScene is null || beats is null) return null;

            // Drop pure transition noise that slipped in as action
            beats.RemoveAll(b =>
                b is Dictionary<string, object?> d &&
                IsNoopBeatDict(d));

            if (beats.Count == 0)
            {
                // Real scene heading with no content yet → short establishing beat.
                // Phantom unspecified scenes that only had FADE IN are discarded.
                var setting = curScene.TryGetValue("setting", out var st) ? st?.ToString() ?? "" : "";
                if (setting.Contains("UNSPECIFIED", StringComparison.OrdinalIgnoreCase))
                {
                    curScene = null;
                    beats = null;
                    beatIndex = 0;
                    contentOccurrence.Clear();
                    return null;
                }

                beatIndex++;
                beats.Add(new Dictionary<string, object?>
                {
                    ["beat_id"] = NextStableBeatId(Establishing, "", setting),
                    ["intent"] = "Establish scene",
                    [VisualEvent] = string.IsNullOrWhiteSpace(setting) ? "Scene" : setting,
                    ["shot_scale_hint"] = ShotScale.Wide.ToSnakeCase(),
                    ["action_class"] = Establishing,
                    ["continuity"] = "new_setup",
                    [TimeWeight] = 1.0,
                    [Delivery] = "none",
                    [JsonKeys.Speaker] = "",
                    [JsonKeys.Dialogue] = "",
                    [Ambient] = "",
                    ["sfx"] = "",
                    ["audio"] = new Dictionary<string, object?>
                    {
                        [Delivery] = "none",
                        [JsonKeys.Speaker] = "",
                        [JsonKeys.Dialogue] = "",
                        [Ambient] = "",
                        ["sfx"] = "",
                    },
                    [CharactersOnScreen] = CurrentOnScreen(curScene),
                });
            }

            var dur = beats.OfType<Dictionary<string, object?>>()
                .Sum(b =>
                {
                    if (b.TryGetValue(TimeWeight, out var tw) && tw is double d) return d * 4.0;
                    return 4.0;
                });
            curScene["duration_target_seconds"] = (int)Math.Clamp(Math.Round(dur), 8, 180);
            curScene["story_beats"] = beats;
            EnrichLocationSeedFromScene(locSeeds, curScene, beats);
            curScene["summary"] = Trunc(
                string.Join(" ", beats.OfType<Dictionary<string, object?>>()
                    .Select(b => b.TryGetValue(VisualEvent, out var v) ? v?.ToString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
                280);
            var completed = curScene;
            curScene = null;
            beats = null;
            beatIndex = 0;
            contentOccurrence.Clear();
            return completed;
        }

        Dictionary<string, object?> OpenScene(string heading)
        {
            var closed = CloseScene();
            if (closed is not null)
                scenes.Add(closed);
            sceneNum++;
            var (locType, locName, setting) = ParseHeading(heading);
            var locId = EnsureLocation(locSeeds, locName, locType, setting);
            lastPictureVisual = null;
            pendingMeta = null;
            pendingParen = null;
            var storyBeats = new List<object?>();
            curScene = new Dictionary<string, object?>
            {
                [JsonKeys.SceneNumber] = sceneNum,
                ["scene_filename"] = $"sc{sceneNum:D2}_{Slug(locName)}",
                ["setting"] = setting,
                ["location_type"] = locType,
                ["location_ids"] = new List<object?> { locId },
                ["primary_location_id"] = locId,
                [CharactersOnScreen] = new List<object?>(),
                ["dramatic_function"] = "",
                ["transition_type"] = sceneNum == 1 ? "fade_in" : "cut",
                ["story_beats"] = storyBeats,
            };
            beats = storyBeats;
            return curScene;
        }

        foreach (var el in parsed.Elements)
        {
            switch (el.Type)
            {
                case FountainParser.ElementType.SceneHeading:
                    curScene = OpenScene(el.Text);
                    break;

                case FountainParser.ElementType.Action:
                case FountainParser.ElementType.Lyric:
                {
                    var actionText = CleanEmphasis(el.Text);
                    // Do not invent INT. UNSPECIFIED just for FADE IN / CUT TO before the first heading
                    if (curScene is null &&
                        (FountainParser.IsStandaloneTransitionLine(actionText) ||
                         IsNoopTransitionText(actionText)))
                        break;
                    if (curScene is null)
                        curScene = OpenScene(UnspecifiedIntDay);
                    FlushDialogue();
                    pendingChar = null;
                    if (actionBuf.Length > 0) actionBuf.Append(' ');
                    actionBuf.Append(actionText);
                    break;
                }

                case FountainParser.ElementType.Character:
                    if (curScene is null)
                        curScene = OpenScene(UnspecifiedIntDay);
                    FlushAction();
                    FlushDialogue();
                    pendingChar = el.Text.Trim();
                    // Meta holds (V.O.) / (O.S.) / CONT'D from SplitCharacter — keep separate from
                    // performance parentheticals that follow on their own line.
                    pendingMeta = string.IsNullOrWhiteSpace(el.Meta) ? null : el.Meta.Trim();
                    pendingParen = null;
                    EnsureCharacter(charSeeds, pendingChar, pendingMeta);
                    // Voice-over / O.S. speakers are not forced into the on-screen cast list
                    if (!IsOffScreenCue(pendingChar, pendingMeta))
                        EnsureOnScreen(curScene, CharacterKey(pendingChar));
                    break;

                case FountainParser.ElementType.Parenthetical:
                    // Performance direction only — must not overwrite V.O./O.S. meta on the cue
                    pendingParen = el.Text;
                    break;

                case FountainParser.ElementType.Dialogue:
                    if (curScene is null)
                        curScene = OpenScene(UnspecifiedIntDay);
                    FlushAction();
                    if (dialogueBuf.Length > 0) dialogueBuf.Append(' ');
                    dialogueBuf.Append(CleanEmphasis(el.Text));
                    break;

                case FountainParser.ElementType.Transition:
                    FlushAction();
                    FlushDialogue();
                    pendingChar = null;
                    break;

                default:
                    break;
            }
        }

        {
            var closed = CloseScene();
            if (closed is not null)
                scenes.Add(closed);
        }

        if (scenes.Count == 0)
            ImportHeadinglessFileAsSingleScene(parsed, actionBuf, OpenScene, CloseScene, scenes);

        // Ensure at least narrator if only action
        if (charSeeds.Count == 0)
        {
            charSeeds["Character_Narrator"] = new Dictionary<string, object?>
            {
                [JsonKeys.Description] = "Off-screen narrator.",
                [DisplayNamePolicy] = "never_on_screen",
                ["voice_profile"] = "Warm clear narrator.",
                ["voice_label"] = "Narrator",
            };
        }

        var totalSec = scenes.OfType<Dictionary<string, object?>>()
            .Sum(s => ToInt(s.TryGetValue("duration_target_seconds", out var d) ? d : 30));

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "stage1.v1",
            [JsonKeys.MovieTitle] = title,
            ["source_book_title"] = title,
            ["generation"] = new Dictionary<string, object?>
            {
                ["method"] = "FountainStage1Importer",
                ["format"] = "fountain",
                ["author"] = author,
                ["ts"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            },
            ["global_production_variables"] = new Dictionary<string, object?>
            {
                ["target_aspect_ratio"] = "16:9",
                ["resolution"] = "720p",
                ["frame_rate"] = 24,
                ["directorial_treatment"] = "Cinematic lighting, clear coverage, natural performances.",
                ["total_runtime_target_seconds"] = totalSec > 0 ? totalSec : 900,
                ["character_seed_tokens"] = charSeeds,
                ["location_seed_tokens"] = locSeeds,
            },
            ["scenes"] = scenes,
            ["cumulative_duration_target_seconds"] = totalSec,
        };
    }

    private static void ImportHeadinglessFileAsSingleScene(
        FountainParser.ParseResult parsed,
        StringBuilder actionBuf,
        Func<string, Dictionary<string, object?>> openScene,
        Func<Dictionary<string, object?>?> closeScene,
        ICollection<object?> scenes)
    {
        // Entire file was action without headings — one scene
        openScene(UnspecifiedIntDay);
        foreach (var el in parsed.Elements.Where(e =>
                     e.Type is FountainParser.ElementType.Action or FountainParser.ElementType.Dialogue))
        {
            if (actionBuf.Length > 0) actionBuf.Append(' ');
            actionBuf.Append(CleanEmphasis(el.Text));
        }
        var fallback = closeScene();
        if (fallback is not null)
            scenes.Add(fallback);
    }

    private static string? FirstTitle(FountainParser.ParseResult p, string key) =>
        p.TitlePage.TryGetValue(key, out var v) ? v : null;

    private static readonly Regex AmbientCueRe = new(@"\b(" +
        @"rain|raining|rainfall|drizzle|storm|thunder|wind|winds|breeze|" +
        @"hum(?:ming)?|murmur(?:ing)?|buzz(?:ing)?|drone|" +
        @"room\s+tone|ambience|ambient|" +
        @"crackling\s+fire|fire\s+crackles?|ticking\s+clock|clock\s+ticks?|" +
        @"distant\s+traffic|traffic\s+noise|waves?|ocean|surf|" +
        @"birds?(?:\s+chirp(?:ing)?)?|crickets?|cicadas?|" +
        @"crowd\s+noise|soft\s+music|underscore" +
        @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    private static readonly Regex SfxCueRe = new(@"\b(" +
        @"knock(?:s|ing)?|slam(?:s|med|ming)?|bang(?:s|ed|ing)?|crash(?:es|ed|ing)?|" +
        @"thud(?:s|ded)?|creak(?:s|ed|ing)?|click(?:s|ed|ing)?|snap(?:s|ped|ping)?|" +
        @"shatter(?:s|ed|ing)?|gunshot(?:s)?|explosion(?:s)?|blast(?:s)?|" +
        @"footsteps?|footfalls?|door\s+(?:opens?|closes?|slams?)|" +
        @"phone\s+rings?|glass\s+breaks?|splash(?:es|ed|ing)?|" +
        @"screech(?:es|ed|ing)?|roar(?:s|ed|ing)?|beep(?:s|ed|ing)?|" +
        @"alarm|siren|whistle|clap(?:s|ped|ping)?|thump(?:s|ed|ing)?" +
        @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// Split action prose into continuous <c>ambient</c> bed vs transient <c>sfx</c> hits.
    /// Deterministic keyword cues only — no free-form NLP.
    /// </summary>
    public static (string Ambient, string Sfx) InferAmbientAndSfx(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("", "");
        var ambient = new List<string>();
        var sfx = new List<string>();
        foreach (Match m in AmbientCueRe.Matches(text))
        {
            var t = m.Value.Trim().ToLowerInvariant();
            if (!ambient.Contains(t, StringComparer.OrdinalIgnoreCase))
                ambient.Add(t);
        }
        foreach (Match m in SfxCueRe.Matches(text))
        {
            var t = m.Value.Trim().ToLowerInvariant();
            if (!sfx.Contains(t, StringComparer.OrdinalIgnoreCase))
                sfx.Add(t);
        }
        return (string.Join(", ", ambient), string.Join(", ", sfx));
    }

    private static readonly Regex HeadingPrefixRe = new(// Longest-first: compound INT/EXT forms including the model-typo "EXT. AND INT."
        @"^(INT\.?\s*/\s*EXT\.?|EXT\.?\s*/\s*INT\.?|INT\s*/\s*EXT|EXT\s*/\s*INT|I\s*/\s*E|"
        + @"EXT\.?\s+AND\s+INT\.?|INT\.?\s+AND\s+EXT\.?|"
        + @"INT\.?/EXT|INT/EXT|I/E|INT\.?|EXT\.?|EST\.?)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    /// <summary>Leftover after a partial strip (e.g. "AND INT. PALACE").</summary>
    private static readonly Regex LeftoverCompoundEnvRe = new(@"^(AND\s+)?(INT\.?|EXT\.?|EST\.?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex DashSplitRe = new(@"\s+[-–]\s+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex WhitespaceCollapseRe = new(@"\s+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex VagueLocationRe = new(@"^(VARIOUS|MULTIPLE|SEVERAL|ELSEWHERE|DIFFERENT|AROUND|THROUGHOUT|"
        + @"MULTIPLE\s+LOCATIONS?|VARIOUS\s+LOCATIONS?|DIFFERENT\s+ROOMS?|"
        + @"DIFFERENT\s+PLACES?|SEVERAL\s+ROOMS?|VARIOUS\s+ROOMS?|"
        + @"AROUND\s+THE\s+HOUSE|THROUGHOUT\s+THE\s+HOUSE)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex VagueFillerWordsRe = new(@"\b(VARIOUS|MULTIPLE|SEVERAL|ELSEWHERE|DIFFERENT|LOCATIONS?|ROOMS?|PLACES?|AREAS?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex PunctuationCleanRe = new(@"[\s\-/&,]+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex TransitionEndingRe = new(@"^(FADE\s+IN|FADE\s+OUT|FADE\s+TO\s+BLACK|FADE\s+TO\s+WHITE|CUT\s+TO(\s+BLACK)?|DISSOLVE\s+TO|SMASH\s+CUT\s+TO|BLACK\s+OUT|THE\s+END)[\s\.:]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex SlugNonAlphaNumericRe = new(@"[^A-Za-z0-9]+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex CharacterParenRe = new(@"\s*\([^)]*\)\s*", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex BigActionWordsRe = new(@"\b(chase|races?|sprints?|explodes?|crashes?|fights?|attacks?|leaps?|bounds?|lunges?|slams?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex HoldWordsRe = new(@"\b(smile|smiles|smiling|nods?|turns?|looks?|gazes?|freezes?|waits?|steadies|thin smile|hands on|sits still|leans?|pauses?|watches?|listens?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ContMatchRe = new(@"\bCONT", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex ExtensionPureTokenRe = new(@"^(CONT|CONTINUED|V\.?\s*O\.?|O\.?\s*S\.?|O\.?\s*C\.?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex VoTokenRe = new(@"\bV\s*\.?\s*O\s*\.?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex OsTokenRe = new(@"\bO\s*\.?\s*S\s*\.?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex OcTokenRe = new(@"\bO\s*\.?\s*C\s*\.?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex SlugLowerRe = new(@"[^a-z0-9]+", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex AsterisksEmphasisRe = new(@"\*{1,3}([^*]+)\*{1,3}", RegexOptions.Compiled, CommonRegex.Timeout);
    private static readonly Regex UnderscoreEmphasisRe = new(@"_([^_]+)_", RegexOptions.Compiled, CommonRegex.Timeout);

    /// <summary>
    /// Parse scene heading into type + filmable location name.
    /// Strips time-of-day after the last " - " and drops vague placeholder segments
    /// (VARIOUS, MULTIPLE, …) so they never seed location_seed_tokens.
    /// Public for unit tests.
    /// </summary>
    public static (string LocType, string LocName, string Setting) ParseHeading(string heading)
    {
        heading = (heading ?? "").Trim();
        var locType = "int";
        var u = heading.ToUpperInvariant();
        // Compound env before plain EXT/INT so "EXT. AND INT. PALACE" is mixed, not ext.
        if (u.Contains("INT./EXT") || u.Contains("INT/EXT") || u.Contains("EXT./INT") || u.Contains("EXT/INT")
            || u.StartsWith("I/E")
            || CommonRegex.IsMatch(u, @"^(EXT\.?\s+AND\s+INT|INT\.?\s+AND\s+EXT)"))
            locType = "mixed";
        else if (u.StartsWith("EXT") || (u.Contains("EXT.") && !u.StartsWith("INT")))
            locType = "ext";
        else if (u.StartsWith("EST"))
            locType = "ext";

        var rest = HeadingPrefixRe.Replace(heading, "").Trim();
        // Second pass: "AND INT. PALACE" after a partial first strip (or model junk).
        rest = LeftoverCompoundEnvRe.Replace(rest, "").Trim();
        // strip time of day after last dash
        var locName = rest;
        var dash = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0) dash = rest.LastIndexOf(" – ", StringComparison.Ordinal);
        if (dash > 0)
            locName = rest[..dash].Trim();
        locName = SanitizeLocationName(locName);
        // Final safety: never keep env tokens in the place name.
        locName = LeftoverCompoundEnvRe.Replace(locName, "").Trim();
        if (string.IsNullOrWhiteSpace(locName))
            locName = Unspecified;
        return (locType, locName, heading);
    }

    /// <summary>
    /// Remove vague multi-place placeholders from a location name.
    /// e.g. "HOUSE - VARIOUS" → "HOUSE"; "MULTIPLE LOCATIONS" → "Unspecified".
    /// </summary>
    public static string SanitizeLocationName(string? locName)
    {
        locName = (locName ?? "").Trim();
        if (locName.Length == 0) return Unspecified;

        // Split compound headings: HOUSE - VARIOUS → keep solid segments only
        var parts = DashSplitRe.Split(locName)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var kept = parts.Where(p => !IsVagueLocationSegment(p)).ToList();
        if (kept.Count > 0)
            return string.Join(" - ", kept);

        // Single segment that is only vague language
        if (IsVagueLocationSegment(locName))
            return Unspecified;

        return locName;
    }

    /// <summary>True when a heading segment is a non-filmable multi-place placeholder.</summary>
    public static bool IsVagueLocationSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return true;
        var s = WhitespaceCollapseRe.Replace(segment.Trim(), " ");
        // Whole-segment placeholders
        if (VagueLocationRe.IsMatch(s))
            return true;

        // Segment reduces to empty after stripping vague filler words only
        var stripped = VagueFillerWordsRe.Replace(s, "");
        stripped = PunctuationCleanRe.Replace(stripped, "").Trim();
        return stripped.Length == 0;
    }

    /// <summary>Transition-only lines that must not become filmable beats/clips.</summary>
    private static bool IsNoopTransitionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (FountainParser.IsStandaloneTransitionLine(text)) return true;
        var t = WhitespaceCollapseRe.Replace(text.Trim(), " ");
        return TransitionEndingRe.IsMatch(t);
    }

    private static bool IsNoopBeatDict(Dictionary<string, object?> beat)
    {
        var ve = beat.TryGetValue(VisualEvent, out var v) ? v?.ToString() ?? "" : "";
        var dlg = beat.TryGetValue(JsonKeys.Dialogue, out var d) ? d?.ToString() ?? "" : "";
        if (!string.IsNullOrWhiteSpace(dlg)) return false;
        return IsNoopTransitionText(ve);
    }

    private static string EnsureLocation(
        Dictionary<string, object?> seeds,
        string locName,
        string locType,
        string setting)
    {
        var id = JsonKeys.LocationPrefix + SlugKey(locName);
        if (!seeds.ContainsKey(id))
        {
            // Place identity only — filmable set text comes from cast-extract or action enrich.
            // Do not seed description as "ext PALACE" (looked broken in the location modal).
            seeds[id] = new Dictionary<string, object?>
            {
                ["display_name"] = locName,
                [JsonKeys.Description] = "",
                [VisualLock] = "",
                ["location_type"] = locType,
                ["reference_image_placeholder"] = id.ToLowerInvariant() + "_ref.png",
            };
        }
        // setting retained only for callers that still pass it; seed stays time-agnostic
        _ = setting;
        return id;
    }

    /// <summary>
    /// Fold scene action prose into the location seed so ListLocations has usable description
    /// without a separate AI location classifier. Strips heading echoes; keeps a short set sketch.
    /// </summary>
    private static void EnrichLocationSeedFromScene(
        Dictionary<string, object?> locSeeds,
        Dictionary<string, object?> scene,
        List<object?> beats)
    {
        var locId = scene.TryGetValue("primary_location_id", out var pl) ? pl?.ToString() : null;
        if (string.IsNullOrWhiteSpace(locId) ||
            !locSeeds.TryGetValue(locId, out var raw) ||
            raw is not Dictionary<string, object?> seed)
            return;

        var display = seed.TryGetValue("display_name", out var dn) ? dn?.ToString() ?? locId : locId;
        var snippets = new List<string>();
        foreach (var b in beats.OfType<Dictionary<string, object?>>())
        {
            var dlg = b.TryGetValue(JsonKeys.Dialogue, out var d) ? d?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(dlg)) continue;
            var ve = b.TryGetValue(VisualEvent, out var v) ? v?.ToString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(ve)) continue;
            ve = CleanActionSnippetForLocation(ve, display);
            if (string.IsNullOrWhiteSpace(ve)) continue;
            if (ve.Length > 160) ve = ve[..157] + "…";
            if (!snippets.Exists(s => s.Equals(ve, StringComparison.OrdinalIgnoreCase)))
                snippets.Add(ve);
            if (snippets.Count >= 3) break;
        }
        if (snippets.Count == 0) return;

        var existing = seed.TryGetValue(JsonKeys.Description, out var ed) ? ed?.ToString()?.Trim() ?? "" : "";
        var isStub = string.IsNullOrWhiteSpace(existing)
                     || existing.Equals(display, StringComparison.OrdinalIgnoreCase)
                     || existing.Equals(locId, StringComparison.OrdinalIgnoreCase)
                     || LooksLikeHeadingEcho(existing, display);

        var sketch = isStub
            ? $"{display}. {snippets[0]}"
            : existing;
        if (!isStub)
        {
            foreach (var s in snippets.Where(s => !sketch.Contains(s, StringComparison.OrdinalIgnoreCase)))
                sketch = $"{sketch} {s}".Trim();
        }
        if (sketch.Length > 480) sketch = sketch[..477] + "…";
        seed[JsonKeys.Description] = sketch;

        var vl = seed.TryGetValue(VisualLock, out var vlo) ? vlo?.ToString()?.Trim() ?? "" : "";
        if (string.IsNullOrWhiteSpace(vl)
            || vl.Equals(display, StringComparison.OrdinalIgnoreCase)
            || vl.Equals(locId, StringComparison.OrdinalIgnoreCase)
            || LooksLikeHeadingEcho(vl, display))
        {
            seed[VisualLock] = snippets[0].Length <= 120
                ? $"{display}: {snippets[0]}"
                : snippets[0];
        }
    }

    private static bool LooksLikeHeadingEcho(string text, string display)
    {
        var t = (text ?? "").Trim();
        if (t.StartsWith("ext ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("int ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("int.", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("and int", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("and ext", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(display)
            && t.StartsWith(display, StringComparison.OrdinalIgnoreCase)
            && t.Length < display.Length + 8)
            return true;
        return false;
    }

    private static string CleanActionSnippetForLocation(string ve, string? display)
    {
        ve = (ve ?? "").Trim();
        ve = HeadingPrefixRe.Replace(ve, "").Trim();
        ve = LeftoverCompoundEnvRe.Replace(ve, "").Trim();
        if (!string.IsNullOrWhiteSpace(display)
            && ve.StartsWith(display, StringComparison.OrdinalIgnoreCase))
        {
            ve = ve[display.Length..].TrimStart(' ', '.', '-', '–', ':');
        }
        return ve.Trim();
    }

    private static string EnsureCharacter(
        Dictionary<string, object?> seeds,
        string displayName,
        string? cueMeta = null)
    {
        var key = CharacterKey(displayName);
        var off = IsOffScreenCue(displayName, cueMeta);
        if (!seeds.ContainsKey(key))
        {
            var name = CleanCharacterName(displayName);
            // Do not invent looks from Fountain. Leave description/visual_lock empty for on-screen
            // cast so Stage 2 cannot embed "as described in the screenplay" stubs into visual prompts.
            // Characters UI / cast extract / locked refs supply real identity later (gen-time CHARACTER VARIABLES).
            var seed = new Dictionary<string, object?>
            {
                [JsonKeys.Description] = off
                    ? $"{name} (voice only; not on screen)."
                    : "",
                ["canonical_given_name"] = name,
                [DisplayNamePolicy] = off ? "never_on_screen" : "ok_anytime",
                ["voice_profile"] = "Consistent character voice every scene.",
                ["voice_label"] = name.Replace(' ', '_'),
                ["reference_image_placeholder"] = ProjectStore.CharacterRefFileName(key),
            };
            if (!off)
                seed[VisualLock] = "";
            seeds[key] = seed;
        }
        else if (!off &&
                 seeds[key] is Dictionary<string, object?> existing &&
                 string.Equals(
                     CoerceSeedString(existing, DisplayNamePolicy),
                     "never_on_screen",
                     StringComparison.OrdinalIgnoreCase))
        {
            // Later on-camera appearance upgrades a V.O.-only first seed
            existing[DisplayNamePolicy] = "ok_anytime";
            if (string.IsNullOrWhiteSpace(CoerceSeedString(existing, JsonKeys.Description)) ||
                (CoerceSeedString(existing, JsonKeys.Description)?.Contains("voice only", StringComparison.OrdinalIgnoreCase) ?? false))
                existing[JsonKeys.Description] = "";
            if (!existing.ContainsKey(VisualLock))
                existing[VisualLock] = "";
        }
        return key;
    }

    private static string? CoerceSeedString(Dictionary<string, object?> seed, string key) =>
        seed.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static string CharacterKey(string name)
    {
        var core = CleanCharacterName(name);
        var slug = SlugNonAlphaNumericRe.Replace(core, "_").Trim('_');
        if (slug.Length == 0) slug = "Unknown";
        return JsonKeys.CharacterPrefix + slug;
    }

    private static string CleanCharacterName(string name)
    {
        name = CharacterParenRe.Replace(name, " ").Trim();
        name = name.TrimEnd('^').Trim();
        // Title case-ish from ALL CAPS
        if (name.Length > 0 && name.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            name = string.Join(' ', parts.Select(p =>
                p.Length <= 1 ? p.ToUpperInvariant()
                : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        }
        return name;
    }

    /// <summary>
    /// Deterministic silent-action class for duration (establishing / hold / big_action / action).
    /// Product fallback when chat classify is off or fails. First filmable silent beat in a scene
    /// is establishing; short gesture lines are holds. Prefer
    /// <see cref="SilentBeatActionClassifier"/> at shot-plan time for better labels.
    /// </summary>
    public static string InferActionClass(string actionText, bool isFirstBeatInScene)
    {
        var t = (actionText ?? "").Trim();
        if (t.Length == 0)
            return isFirstBeatInScene ? Establishing : "hold";

        var lower = t.ToLowerInvariant();
        var words = ClipDurationEstimator.CountWords(t);

        if (BigActionWordsRe.IsMatch(lower))
            return "big_action";

        if (isFirstBeatInScene)
            return Establishing;

        // Micro performance / stillness — smile, hands, look, freeze
        if (words <= 24 && HoldWordsRe.IsMatch(lower))
            return "hold";

        // Very short non-gesture lines only (avoid classifying "opens the door…" as hold)
        if (words <= 8)
            return "hold";

        return "action";
    }

    /// <summary>
    /// Filmable dialogue visual — clean display name, no fountain CONT'D / V.O. markup.
    /// </summary>
    public static string BuildDialogueVisualEvent(
        string displayName,
        string? performanceParen,
        string? rawCue = null)
    {
        var name = string.IsNullOrWhiteSpace(displayName)
            ? CleanCharacterName(rawCue ?? "Speaker")
            : displayName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Speaker";

        // Continuation cues → "continues" reads better than "speaks" after a prior beat
        var continued = rawCue is not null && ContMatchRe.IsMatch(rawCue);

        if (!string.IsNullOrWhiteSpace(performanceParen))
        {
            var p = performanceParen.Trim().Trim('(', ')').Trim();
            // Strip pure extension tokens if a performance paren was polluted with Meta
            if (p.Length > 0 &&
                !ExtensionPureTokenRe.IsMatch(p) &&
                !IsOffScreenToken(p))
                return $"{name} ({p}).";
        }

        return continued ? $"{name} continues." : $"{name} speaks.";
    }

    /// <summary>
    /// Picture under voice-over: reuse the last action visual when present; never "X speaks."
    /// Stage 2 SpeechClause adds OFF-CAMERA VOICEOVER from audio_payload.
    /// </summary>
    public static string BuildVoiceoverVisualEvent(string? lastPictureVisual)
    {
        var pic = (lastPictureVisual ?? "").Trim();
        if (pic.Length > 0)
            return pic;
        return "Scene continues under voice-over.";
    }

    /// <summary>
    /// True when the character cue (name and/or parser Meta) indicates voice-over or off-screen.
    /// FountainParser puts extensions like (V.O.) in Meta after splitting them off the name —
    /// checking the bare name alone always misses V.O.
    /// </summary>
    public static bool IsOffScreenCue(string? characterName, string? metaOrExtension = null) =>
        IsOffScreenToken(characterName) || IsOffScreenToken(metaOrExtension);

    /// <summary>True when text contains a V.O. / O.S. / O.C. extension token.</summary>
    public static bool IsOffScreenToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        // Optional dots/spaces/parens: (V.O.), V.O, VO, (O. S.), O.S., O.C.
        if (VoTokenRe.IsMatch(text))
            return true;
        if (OsTokenRe.IsMatch(text))
            return true;
        if (OcTokenRe.IsMatch(text))
            return true;
        return false;
    }

    private static string? FirstCharacterKey(List<object?> cast)
    {
        foreach (var x in cast)
        {
            var s = x?.ToString();
            if (!string.IsNullOrWhiteSpace(s) &&
                s.StartsWith(JsonKeys.CharacterPrefix, StringComparison.Ordinal))
                return s;
        }
        return null;
    }

    private static void EnsureOnScreen(Dictionary<string, object?>? scene, string charKey)
    {
        if (scene is null) return;
        if (!scene.TryGetValue(CharactersOnScreen, out var cos) || cos is not List<object?> list)
        {
            list = new List<object?>();
            scene[CharactersOnScreen] = list;
        }
        if (!list.Any(x => string.Equals(x?.ToString(), charKey, StringComparison.OrdinalIgnoreCase)))
            list.Add(charKey);
    }

    private static List<object?> CurrentOnScreen(Dictionary<string, object?>? scene)
    {
        if (scene?.TryGetValue(CharactersOnScreen, out var cos) == true && cos is List<object?> list)
            return list.ToList();
        return new List<object?>();
    }

    private static string Slug(string s) =>
        SlugLowerRe.Replace(s.ToLowerInvariant(), "_").Trim('_');

    private static string SlugKey(string s)
    {
        // Drop apostrophes before splitting so possessives ("Man's") merge into one token
        // ("Mans") instead of the trailing "s" splitting off into its own capitalized part.
        var withoutApostrophes = s.Replace("'", "").Replace("’", "");
        var parts = SlugNonAlphaNumericRe.Split(withoutApostrophes)
            .Where(p => p.Length > 0)
            .Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..].ToLowerInvariant() : ""));
        var joined = string.Join('_', parts);
        return string.IsNullOrEmpty(joined) ? Unspecified : joined;
    }

    private static string CleanEmphasis(string s)
    {
        s = AsterisksEmphasisRe.Replace(s, "$1");
        s = UnderscoreEmphasisRe.Replace(s, "$1");
        return s.Trim();
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    private static int ToInt(object? o) => o switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var i) => i,
        _ => 0,
    };
}
