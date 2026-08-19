using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.Abstractions;
using PageToMovie.Engine.Deterministic;
using PageToMovie.Engine.ModelExecution;

namespace PageToMovie.Engine;

/// <summary>
/// 100% Automated clip dialogue & speaker verification pass.
/// Uses multimodal vision (IVisionClient) + character reference plates to evaluate
/// generated video clips, transcribe spoken dialogue, and verify speaker identity.
/// Runs automatically in the background when a clip finishes generating.
/// </summary>
public sealed class ClipDialogueVerificationService
{
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;

    private readonly ProjectStore _projects;
    private readonly IVisionClient _vision;
    private readonly IGeminiVideoAnalysisClient? _gemini;
    private readonly ILogger<ClipDialogueVerificationService> _log;

    public ClipDialogueVerificationService(
        ProjectStore projects,
        IVisionClient vision,
        ProjectTelemetryService telemetry,
        IGeminiVideoAnalysisClient? gemini = null,
        ILogger<ClipDialogueVerificationService>? log = null)
    {
        _projects = projects;
        _vision = vision;
        _gemini = gemini;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ClipDialogueVerificationService>.Instance;
    }

    public bool IsConfigured => _vision.IsConfigured || (_gemini?.IsConfigured ?? false);

    public string VerificationPath(string projectId, int scene, int clip) =>
        BuildVerificationPath(_projects.GetProjectDir(projectId), scene, clip);

    /// <summary>
    /// Static so callers that already have projectDir (e.g. ProjectStore, which this service
    /// itself depends on and so can't take an instance dependency on) can build the exact same
    /// path without duplicating the naming convention — a prior duplication of this path drifted
    /// out of sync and left dialogue verification results permanently unreadable.
    /// </summary>
    public static string BuildVerificationPath(string projectDir, int scene, int clip) =>
        Path.Combine(projectDir, "assets", "qa", $"scene_{scene:D2}_clip_{clip:D2}_dialogue_verification.json");

    public ClipDialogueVerificationResult? LoadVerification(string projectId, int scene, int clip)
    {
        var path = VerificationPath(projectId, scene, clip);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClipDialogueVerificationResult>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ClipDialogueVerificationResult?> LoadVerificationAsync(string projectId, int scene, int clip, CancellationToken ct = default)
    {
        var path = VerificationPath(projectId, scene, clip);
        return await StreamJsonStore.LoadAsync<ClipDialogueVerificationResult>(path, JsonOpts, ct).ConfigureAwait(false);
    }

    private static readonly byte[] NewLineBytes = new byte[] { (byte)'\n' };
    private const string StatusUnverified = "unverified";

    public async Task SaveVerificationAsync(string projectId, ClipDialogueVerificationResult result, CancellationToken ct = default)
    {
        var path = VerificationPath(projectId, result.SceneNumber, result.ClipNumber);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // Write to a temp file then atomically rename — a crash/cancellation mid-write must never
        // leave the verification file truncated. Readers (ProjectStore's mtime-validated cache)
        // rely on this atomicity to treat "file exists" as "file is a complete, valid write."
        var tmp = path + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, result, JsonOpts, ct).ConfigureAwait(false);
            await stream.WriteAsync(NewLineBytes, ct).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Runs automated dialogue & speaker verification for a completed clip.
    /// </summary>
    public async Task<ClipDialogueVerificationResult> VerifyClipDialogueAsync(
        string projectId,
        int sceneNumber,
        int clipNumber,
        IReadOnlyList<string>? keyframePaths = null,
        string? overrideVideoPath = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var clipPath = ResolveVerificationClipPath(projectId, sceneNumber, clipNumber, overrideVideoPath);
        var detail = await _projects.GetSceneDetailAsync(projectId, sceneNumber, ct: ct).ConfigureAwait(false);
        var clip = detail?.Clips?.FirstOrDefault(c => c.ClipNumber == clipNumber);

        var spokenLines = ClipSpokenLines.FromClip(clip);
        var expectedSpeaker = clip?.Speaker ?? "Unknown";
        var expectedDialogue = BuildExpectedDialogue(clip);

        if (string.IsNullOrWhiteSpace(expectedDialogue))
            return await BuildNoSpeechResultAsync(projectId, sceneNumber, clipNumber, expectedSpeaker, ct).ConfigureAwait(false);

        var cached = await TryLoadCachedVerificationAsync(
            projectId, sceneNumber, clipNumber, clipPath, expectedDialogue, expectedSpeaker,
            force, overrideVideoPath, ct).ConfigureAwait(false);
        if (cached is not null)
            return cached;

        if (!IsConfigured)
        {
            _log.LogWarning("Vision/Gemini client not configured — skipping dialogue verification for {Project} S{Scene} C{Clip}", projectId, sceneNumber, clipNumber);
            return await SaveUnverifiedAsync(projectId, sceneNumber, clipNumber, expectedSpeaker, expectedDialogue,
                "Google Gemini key (GEMINI_API_KEY) required for native MP4 video & audio dialogue verification. Please set key in Configuration.",
                ct).ConfigureAwait(false);
        }

        // The server keeps no clip bytes: when the clip is not on disk (saved to the user's folder, or
        // still only at the provider) bring a temp copy from the provider URL — lead-in trimmed for a
        // video-extend clip, otherwise the previous clip's line would be "heard" in this one.
        string? materialized = null;
        var leadInOffsetSec = 0.0;
        if (string.IsNullOrWhiteSpace(clipPath) || !File.Exists(clipPath))
        {
            var videoDir = Path.Combine(_projects.GetProjectDir(projectId), "assets", "video");
            var mat = await ClipProviderSource.TryMaterializeAsync(
                ClipProviderSource.ReadForClip(videoDir, sceneNumber, clipNumber), ct).ConfigureAwait(false);
            if (mat is not null)
            {
                materialized = mat.Path;
                clipPath = mat.Path;
                // No native ffmpeg here (production): the copy is still the combined extend video —
                // tell the verifier where this clip starts instead of trimming.
                leadInOffsetSec = mat.LeadInSecondsRemaining;
            }
        }
        try
        {
        var media = await CollectMediaAndCharGuidesAsync(
            projectId, clipPath, clip, spokenLines, expectedSpeaker, keyframePaths, ct).ConfigureAwait(false);
        if (media.MediaToPass.Count == 0)
            return await SaveUnverifiedAsync(projectId, sceneNumber, clipNumber, expectedSpeaker, expectedDialogue,
                "Clip video is neither on the server nor reachable at the provider (no source_url in the sidecar). Generate or re-import the clip first.",
                ct).ConfigureAwait(false);

        var prompt = BuildVerificationPrompt(expectedSpeaker, media.ExpectedSpeakerDisplayName, expectedDialogue, media.CharGuides, leadInOffsetSec);

        try
        {
            var responseJson = await RunDialogueModelCallAsync(projectId, prompt, media.MediaToPass, ct).ConfigureAwait(false);
            var result = await ParseAndNormalizeResultAsync(
                responseJson, sceneNumber, clipNumber, expectedSpeaker, media.ExpectedSpeakerDisplayName,
                expectedDialogue, clip, clipPath, ct).ConfigureAwait(false);
            await SaveVerificationAsync(projectId, result, ct).ConfigureAwait(false);
            _log.LogInformation("Automated dialogue verification completed for {Project} S{Scene} C{Clip}: {Status} ({Score:P0})", projectId, sceneNumber, clipNumber, result.Status, result.DialogueAccuracyScore);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dialogue verification failed for {Project} S{Scene} C{Clip}", projectId, sceneNumber, clipNumber);
            return await SaveUnverifiedAsync(projectId, sceneNumber, clipNumber, expectedSpeaker, expectedDialogue,
                $"Verification error: {ex.Message}", ct).ConfigureAwait(false);
        }
        }
        finally
        {
            ClipProviderSource.TryDelete(materialized);
        }
    }

    private string? ResolveVerificationClipPath(
        string projectId, int sceneNumber, int clipNumber, string? overrideVideoPath)
    {
        if (!string.IsNullOrWhiteSpace(overrideVideoPath) && File.Exists(overrideVideoPath))
            return overrideVideoPath;
        return _projects.ResolveClipVideoPath(projectId, sceneNumber, clipNumber);
    }

    private async Task<ClipDialogueVerificationResult> BuildNoSpeechResultAsync(
        string projectId, int sceneNumber, int clipNumber, string expectedSpeaker, CancellationToken ct)
    {
        var noSpeechResult = new ClipDialogueVerificationResult
        {
            SceneNumber = sceneNumber,
            ClipNumber = clipNumber,
            ExpectedSpeaker = NoSpeechExpectedSpeaker(expectedSpeaker),
            ExpectedDialogue = "",
            DetectedSpeaker = "None",
            TranscribedDialogue = "",
            DialogueAccuracyScore = 1.0,
            SpeakerMatch = true,
            Status = "no_speech",
            SummaryNote = "No spoken dialogue planned for this clip.",
            VerifiedAt = DateTime.UtcNow,
        };
        await SaveVerificationAsync(projectId, noSpeechResult, ct).ConfigureAwait(false);
        return noSpeechResult;
    }

    private static string NoSpeechExpectedSpeaker(string expectedSpeaker)
    {
        if (string.IsNullOrWhiteSpace(expectedSpeaker) ||
            string.Equals(expectedSpeaker, "Unknown", StringComparison.OrdinalIgnoreCase))
            return "None";
        return expectedSpeaker;
    }

    private async Task<ClipDialogueVerificationResult?> TryLoadCachedVerificationAsync(
        string projectId, int sceneNumber, int clipNumber, string? clipPath,
        string expectedDialogue, string expectedSpeaker,
        bool force, string? overrideVideoPath, CancellationToken ct)
    {
        if (force || !string.IsNullOrWhiteSpace(overrideVideoPath))
            return null;

        var existing = await LoadVerificationAsync(projectId, sceneNumber, clipNumber, ct).ConfigureAwait(false);
        if (!IsUsableCachedVerification(existing, clipPath))
            return null;

        var videoMTime = File.GetLastWriteTimeUtc(clipPath!);
        if (existing!.VerifiedAt < videoMTime ||
            !string.Equals(existing.ExpectedDialogue, expectedDialogue, StringComparison.Ordinal) ||
            !string.Equals(existing.ExpectedSpeaker, expectedSpeaker, StringComparison.OrdinalIgnoreCase))
            return null;

        _log.LogInformation("Dialogue verification for {Project} S{Scene} C{Clip} is up-to-date (cached)", projectId, sceneNumber, clipNumber);
        return existing;
    }

    private static bool IsUsableCachedVerification(ClipDialogueVerificationResult? existing, string? clipPath) =>
        existing is not null &&
        !string.Equals(existing.Status, StatusUnverified, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(clipPath) &&
        File.Exists(clipPath);

    private async Task<ClipDialogueVerificationResult> SaveUnverifiedAsync(
        string projectId, int sceneNumber, int clipNumber,
        string expectedSpeaker, string expectedDialogue, string summaryNote, CancellationToken ct)
    {
        var result = new ClipDialogueVerificationResult
        {
            SceneNumber = sceneNumber,
            ClipNumber = clipNumber,
            ExpectedSpeaker = expectedSpeaker,
            ExpectedDialogue = expectedDialogue,
            Status = StatusUnverified,
            SummaryNote = summaryNote,
            VerifiedAt = DateTime.UtcNow,
        };
        await SaveVerificationAsync(projectId, result, ct).ConfigureAwait(false);
        return result;
    }

    private sealed class DialogueMediaContext
    {
        public required List<string> MediaToPass { get; init; }
        public required List<string> CharGuides { get; init; }
        public required string ExpectedSpeakerDisplayName { get; init; }
    }

    private async Task<DialogueMediaContext> CollectMediaAndCharGuidesAsync(
        string projectId,
        string? clipPath,
        ClipSummary? clip,
        IReadOnlyList<ClipSpokenLines.SpokenLine> spokenLines,
        string expectedSpeaker,
        IReadOnlyList<string>? keyframePaths,
        CancellationToken ct)
    {
        var mediaToPass = new List<string>();
        if (!string.IsNullOrWhiteSpace(clipPath) && File.Exists(clipPath))
            mediaToPass.Add(clipPath);

        var charSummaryList = _projects.ListCharacters(projectId);
        var sceneChars = clip?.CharactersOnScreen is { Count: > 0 }
            ? clip.CharactersOnScreen
            : new List<string> { expectedSpeaker };
        var extraSpeakers = CollectExtraSpeakers(spokenLines, expectedSpeaker, sceneChars);
        if (extraSpeakers.Count > 0)
            sceneChars = sceneChars.Concat(extraSpeakers).ToList();

        var charGuides = new List<string>();
        var mediaIndex = 1; // Index 1 is the MP4 video clip

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        foreach (var cName in sceneChars)
            TryAddCharacterGuide(FindCharacter(charSummaryList, cName), projectDir, mediaToPass, charGuides, ref mediaIndex);

        if (keyframePaths is { Count: > 0 })
            mediaToPass.AddRange(keyframePaths.Where(File.Exists));

        var expectedCharObj = FindCharacter(charSummaryList, expectedSpeaker);
        return new DialogueMediaContext
        {
            MediaToPass = mediaToPass,
            CharGuides = charGuides,
            ExpectedSpeakerDisplayName = expectedCharObj?.DisplayName ?? expectedSpeaker,
        };
    }

    private static List<string> CollectExtraSpeakers(
        IReadOnlyList<ClipSpokenLines.SpokenLine> spokenLines,
        string expectedSpeaker,
        IReadOnlyList<string> sceneChars)
    {
        var extras = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in spokenLines)
            ConsiderExtraSpeaker(line.Speaker, sceneChars, extras, seen);
        ConsiderExtraSpeaker(expectedSpeaker, sceneChars, extras, seen);
        return extras;
    }

    private static void ConsiderExtraSpeaker(
        string s, IReadOnlyList<string> sceneChars, List<string> extras, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        if (ContainsIgnoreCase(sceneChars, s)) return;
        if (!seen.Add(s)) return;
        extras.Add(s);
    }

    private static bool ContainsIgnoreCase(IEnumerable<string> items, string value) =>
        items.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));

    private static CharacterSummary? FindCharacter(IReadOnlyList<CharacterSummary> list, string name) =>
        list.FirstOrDefault(c =>
            string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase));

    private static void TryAddCharacterGuide(
        CharacterSummary? charObj, string projectDir,
        List<string> mediaToPass, List<string> charGuides, ref int mediaIndex)
    {
        if (charObj?.PreferredUrl is not { Length: > 0 } url)
            return;
        var localRef = Path.Combine(projectDir, url.TrimStart('/'));
        if (!File.Exists(localRef))
            return;
        mediaToPass.Add(localRef);
        mediaIndex++;
        var desc = !string.IsNullOrWhiteSpace(charObj.Description) ? $" ({charObj.Description})" : "";
        var nameLabel = charObj.DisplayName ?? charObj.Key;
        charGuides.Add($"- Attached Image #{mediaIndex}: Character '{nameLabel}' (Key: '{charObj.Key}'){desc}");
    }

    /// <summary>
    /// Heteronyms in the expected line, resolved deterministically, become explicit checks for the
    /// verifier: "tear" said as /tɪər/ still transcribes as "tear", so only a listener that knows the
    /// intended sense can catch it — this is the wrong_sense kind.
    /// </summary>
    internal static string BuildSenseChecks(string expectedDialogue)
    {
        var res = Deterministic.Pronunciation.PronunciationResolver.Default.Resolve(expectedDialogue);
        if (res.Annotations.Count == 0) return "";
        var lines = res.Annotations.Select(a =>
            $"   - '{a.Token}' must be pronounced /{a.Ipa}/" +
            (string.IsNullOrWhiteSpace(a.Respell) ? "" : $" (\"{a.Respell}\"" + (string.IsNullOrWhiteSpace(a.Rhymes) ? "" : $", rhymes with '{a.Rhymes}'") + ")") +
            $" — meaning: {a.Meaning}. If it was said with the other meaning, report kind \"wrong_sense\" with word '{a.Token}'.");
        return "   PRONUNCIATION CHECKS (words with two pronunciations — listen for the intended one):\n" + string.Join("\n", lines) + "\n";
    }

    private static string BuildVerificationPrompt(

        string expectedSpeaker, string expectedSpeakerDisplayName, string expectedDialogue, List<string> charGuides,
        double leadInOffsetSec = 0)
    {
        // Combined extend video that could not be trimmed: the head belongs to the PREVIOUS clip.
        var leadInNote = leadInOffsetSec > 0.1
            ? $"IMPORTANT: the first {leadInOffsetSec:F1} seconds of the attached video are the PREVIOUS clip (a continuation input) — ignore everything before {leadInOffsetSec:F1}s. Only speech AFTER {leadInOffsetSec:F1}s belongs to this clip; a line heard only before that point counts as NOT spoken here.\n"
            : "";
        var guideText = charGuides.Count > 0
            ? "CHARACTER REFERENCE PORTRAITS (MATCH FACES IN VIDEO TO THESE ATTACHED IMAGES):\n" + string.Join("\n", charGuides)
            : "No character reference portraits attached.";
        var senseChecks = BuildSenseChecks(expectedDialogue);

        return $@"
You are an automated film quality assurance inspector evaluating a generated movie clip.

EXPECTED SCRIPT:
- Expected Speaker: '{expectedSpeakerDisplayName}' (Character Key: '{expectedSpeaker}')
- Expected Spoken Dialogue: '{expectedDialogue}'

{guideText}

TASKS:
{leadInNote}1. Watch the attached MP4 video clip (Attached File #1) and LISTEN carefully to the audio track / spoken dialogue.
2. Observe on-screen character faces and lip movements. Compare the face of the character who is speaking against the attached character reference portraits listed above to determine who is speaking.
3. Transcribe the EXACT spoken dialogue you hear in the video clip.
4. Compare detected speaker vs expected speaker ('{expectedSpeakerDisplayName}'), and transcribed dialogue vs expected dialogue.
   NOTE: Ignore minor US/UK spelling differences (e.g. 'neighbour' vs 'neighbor', 'colour' vs 'color'). If the spoken words match the script and you found no issues, score dialogue accuracy as 1.0 (100% match).
5. Explain every deduction. Any score below 1.0 must be accounted for by at least one entry in ""issues"". Use ONLY these kinds:
   wrong_speaker (another character's voice/mouth delivers the line), wrong_words (different words / different meaning), wrong_sense (a word with two pronunciations was said with the wrong meaning — see the checks below), cut_off (line truncated before its last word), missing_line (line not spoken at all),
   unclear_audio, robotic_delivery, timing (line lands off the shot / after the mouth stops),
   mispronounced (awkward but unambiguous), extra_word, missing_word (filler/article), accent.
6. WATCH the picture too. Report kind ""visual_defect"" (severity major) for anything a viewer would see as broken: anatomy errors (a head, limb or body part detaching, extra or missing limbs, melting or morphing faces), a character turning into someone else mid-clip, a prop or animal changing species/shape, or a sudden style break (photoreal ↔ cartoon). A clip with a visual_defect is NOT acceptable even when every word is right — the dialogue score stays, the issue fails the clip.
{senseChecks}
Return ONLY a JSON object:
{{
  ""detectedSpeaker"": ""Character Name or Key"",
  ""transcribedDialogue"": ""Spoken dialogue text heard in video audio track"",
  ""dialogueAccuracyScore"": 0.95,
  ""speakerMatch"": true,
  ""status"": ""verified"",
  ""issues"": [ {{ ""kind"": ""mispronounced"", ""word"": ""Officer"", ""detail"": ""said off-ee-sir"", ""severity"": ""minor"" }} ],
  ""summaryNote"": ""Expected: '{expectedDialogue}' | Heard: '...' (Match 95%)""
}}
Status options: 'verified' (dialogue & speaker match, picture intact), 'mismatch' (dialogue incorrect), 'speaker_swap' (wrong character speaking), 'no_speech' (no spoken dialogue heard), 'visual_defect' (picture broken — see 6).
".Trim();
    }

    private async Task<string> RunDialogueModelCallAsync(
        string projectId, string prompt, List<string> mediaToPass, CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        var targetModel = ProjectModelSelection.RequireVideoReview(cfg, "Dialogue verification");
        var entry = SupportedModelCatalog.Find(targetModel)
                ?? throw new InvalidOperationException(
                    $"Dialogue verification: model '{targetModel}' missing from catalog.");

        var hasVideoFile = mediaToPass.Any(IsVideoMediaPath);

        if (hasVideoFile && !entry.SupportsVideoReview)
        {
            throw new InvalidOperationException(
                "Dialogue verification: the selected Video review model does not support native video. " +
                "Open Settings and choose a video-review-capable model (e.g. Gemini with SupportsVideoReview).");
        }
        if (ShouldUseGeminiVideo(hasVideoFile, entry))
            return await _gemini!.CompleteWithImagesAsync(prompt, mediaToPass, model: targetModel, ct: ct).ConfigureAwait(false);
        return await _vision.CompleteWithImagesAsync(prompt, mediaToPass, model: targetModel, ct: ct).ConfigureAwait(false);
    }

    private static bool IsVideoMediaPath(string p) =>
        p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

    private bool ShouldUseGeminiVideo(bool hasVideoFile, SupportedModelEntry entry) =>
        hasVideoFile && entry.SupportsVideoReview && _gemini is not null && _gemini.IsConfigured
        && string.Equals(entry.ProviderId, "gemini", StringComparison.OrdinalIgnoreCase);

    private async Task<ClipDialogueVerificationResult> ParseAndNormalizeResultAsync(
        string responseJson,
        int sceneNumber,
        int clipNumber,
        string expectedSpeaker,
        string expectedSpeakerDisplayName,
        string expectedDialogue,
        ClipSummary? clip,
        string? clipPath,
        CancellationToken ct)
    {
        var cleanJson = ExtractJson(responseJson);
        using var doc = JsonDocument.Parse(cleanJson);
        var root = doc.RootElement;

        var detected = GetJsonString(root, "detectedSpeaker", "detected_speaker", "speaker");
        var transcribed = GetJsonString(root, "transcribedDialogue", "transcribed_dialogue", "dialogue", "transcript", "spoken_dialogue");
        var accuracyOpt = GetJsonDouble(root, "dialogueAccuracyScore", "dialogue_accuracy_score", "accuracy_score", "accuracy");
        var accuracy = accuracyOpt ?? CalculateAccuracyScore(expectedDialogue, transcribed);
        var speakerMatch = GetJsonBool(root, "speakerMatch", "speaker_match");
        var status = GetJsonString(root, "status");
        if (string.IsNullOrWhiteSpace(status)) status = "verified";
        var summary = GetJsonString(root, "summaryNote", "summary_note", "summary", "notes");

        var issues = ParseIssues(root);
        (speakerMatch, status) = NormalizeSpeakerMatch(
            detected, expectedSpeaker, expectedSpeakerDisplayName, speakerMatch, status, accuracy);
        (accuracy, status, summary) = ApplyAccuracyGuards(expectedDialogue, transcribed, accuracy, status, summary, issues);

        var estSec = clip?.DurationSeconds > 0 ? (double)clip.DurationSeconds : ClipDurationEstimator.Estimate(expectedDialogue, "", "dialogue", "none");
        var (speechSec, actionSec) = ClipDurationEstimator.EstimateBreakdown(expectedDialogue, clip?.VisualPrompt ?? "", "", clip?.Delivery ?? "none");
        var durationProbe = new MediaDurationProbe(Microsoft.Extensions.Options.Options.Create(new PageToMovie.Core.Options.PageToMovieOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaDurationProbe>.Instance);
        var actualSec = await durationProbe.TryProbeSecondsAsync(clipPath, ct).ConfigureAwait(false) ?? 0.0;

        var result = new ClipDialogueVerificationResult
        {
            SceneNumber = sceneNumber,
            ClipNumber = clipNumber,
            ExpectedSpeaker = expectedSpeaker,
            ExpectedDialogue = expectedDialogue,
            DetectedSpeaker = detected,
            TranscribedDialogue = transcribed,
            DialogueAccuracyScore = Math.Round(accuracy, 2),
            SpeakerMatch = speakerMatch,
            Status = status,
            SummaryNote = summary,
            Issues = issues,
            EstimatedDurationSeconds = Math.Round(estSec, 1),
            WordCount = ClipDurationEstimator.CountWords(expectedDialogue),
            SyllableCount = ClipDurationEstimator.CountSyllables(expectedDialogue),
            SpeechDurationSeconds = speechSec,
            ActionDurationSeconds = actionSec,
            ActualDurationSeconds = Math.Round(actualSec, 1),
            VerifiedAt = DateTime.UtcNow,
        };
        result.SpeechTruncated = LooksTruncated(result);
        return result;
    }

    private static string CleanSpkName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var clean = s.Trim().ToLowerInvariant()
            .Replace("character_", "")
            .Replace("character", "")
            .Replace("_", " ")
            .Replace("the ", "");
        return CommonRegex.Replace(clean, @"\s+", " ").Trim();
    }

    private static (bool SpeakerMatch, string Status) NormalizeSpeakerMatch(
        string detected, string expectedSpeaker, string expectedSpeakerDisplayName,
        bool speakerMatch, string status, double accuracy)
    {
        var cleanDetected = CleanSpkName(detected);
        var cleanExpectedKey = CleanSpkName(expectedSpeaker);
        var cleanExpectedDisp = CleanSpkName(expectedSpeakerDisplayName);

        if (string.IsNullOrWhiteSpace(cleanDetected) ||
            (cleanDetected != cleanExpectedKey && cleanDetected != cleanExpectedDisp))
            return (speakerMatch, status);

        speakerMatch = true;
        if (string.Equals(status, "speaker_swap", StringComparison.OrdinalIgnoreCase))
            status = accuracy >= 0.5 ? "verified" : "mismatch";
        return (speakerMatch, status);
    }

    /// <summary>
    /// Score/status from two signals — the model's verdict and our own text similarity — plus the
    /// model's itemized issues. Rules: a blocking issue (wrong speaker / words / sense, cut off,
    /// missing line) fails the clip regardless of how well the transcript matches (a heteronym read
    /// with the wrong sense transcribes perfectly); cosmetic-only issues are a 100% line (rounding
    /// "off-ee-sir" up is right); otherwise the lower of the two signals stands — text similarity
    /// never overrides a lower model verdict, because that hid exactly the wrong-sense case.
    /// </summary>
    internal static (double Accuracy, string Status, string Summary) ApplyAccuracyGuards(
        string expectedDialogue, string transcribed, double accuracy, string status, string summary,
        IReadOnlyList<DialogueVerificationIssue>? issues = null)
    {
        issues ??= Array.Empty<DialogueVerificationIssue>();
        if (!string.IsNullOrWhiteSpace(expectedDialogue) && string.IsNullOrWhiteSpace(transcribed))
        {
            return (0.0, "mismatch", $"Expected: '{expectedDialogue}' | Heard: (no audio/speech detected) (0% match)");
        }
        if (string.IsNullOrWhiteSpace(expectedDialogue))
            return (accuracy, status, summary);

        var blocking = issues.Where(i => DialogueIssueKinds.IsBlocking(i.Kind)).ToList();
        if (blocking.Count > 0)
        {
            var kinds = string.Join(", ", blocking.Select(i => i.Kind + (string.IsNullOrWhiteSpace(i.Word) ? "" : $" '{i.Word}'")).Distinct());
            var newStatus = blocking.Any(i => string.Equals(i.Kind, "wrong_speaker", StringComparison.OrdinalIgnoreCase)) ? "speaker_swap"
                : blocking.Any(i => string.Equals(i.Kind, "visual_defect", StringComparison.OrdinalIgnoreCase)) ? "visual_defect"
                : "mismatch";

            return (Math.Min(accuracy, 0.49), newStatus, $"{summary} | Blocking: {kinds}".Trim(' ', '|'));
        }

        var computedAcc = CalculateAccuracyScore(expectedDialogue, transcribed);
        var onlyCosmetic = issues.Count > 0 && issues.All(i => DialogueIssueKinds.IsCosmetic(i.Kind));
        if (computedAcc >= 0.99 && (issues.Count == 0 || onlyCosmetic))
            accuracy = 1.0;
        else if (computedAcc < accuracy)
            accuracy = computedAcc;

        if (accuracy < 0.5 && string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase))
            status = "mismatch";
        return (accuracy, status, summary);
    }

    internal static List<DialogueVerificationIssue> ParseIssues(JsonElement root)
    {
        var list = new List<DialogueVerificationIssue>();
        if (!root.TryGetProperty("issues", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var kind = (el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null)?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(kind)) continue;
            if (!DialogueIssueKinds.IsBlocking(kind) && !DialogueIssueKinds.IsDegraded(kind) && !DialogueIssueKinds.IsCosmetic(kind))
                kind = "other";
            list.Add(new DialogueVerificationIssue
            {
                Kind = kind,
                Word = el.TryGetProperty("word", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()?.Trim() : null,
                Detail = el.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "",
                Severity = el.TryGetProperty("severity", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() ?? "minor" : "minor",
            });
        }
        return list;
    }

    /// <summary>
    /// Expected spoken content for a clip — every line via <see cref="ClipSpokenLines"/>, joined in
    /// speaking order. For a single-speaker clip this is just its line; for a cross-speaker
    /// two-hander it covers BOTH speakers, so the second speaker's line is actually verified (and a
    /// missing/cut secondary line is caught and can feed the existing feedback path).
    /// </summary>
    public static string BuildExpectedDialogue(ClipSummary? clip) =>
        string.Join(" ", ClipSpokenLines.FromClip(clip).Select(l => l.Dialogue));

    /// <summary>
    /// Best-effort signal that a clip's dialogue was likely cut off because the clip ran out of time
    /// before the line finished, rather than misheard/substituted content or a speaker-identity
    /// mismatch. Feeds <c>ClipTimingTelemetryRepository</c>'s <c>DialogueTruncated</c> column so the
    /// timing ledger learns which categories/word-budgets actually cause truncation in practice,
    /// instead of that column staying permanently false.
    /// </summary>
    public static bool LooksTruncated(ClipDialogueVerificationResult result)
    {
        if (string.Equals(result.Status, "speaker_swap", StringComparison.OrdinalIgnoreCase))
            return false; // wrong speaker entirely — an identity problem, not a timing one

        var expectedWords = ClipDurationEstimator.CountWords(result.ExpectedDialogue);
        if (expectedWords == 0)
            return false; // nothing was supposed to be said

        var transcribedWords = ClipDurationEstimator.CountWords(result.TranscribedDialogue ?? "");
        // Meaningfully fewer words heard than expected suggests the line was cut off mid-delivery
        // rather than fully spoken with a few words misheard.
        return transcribedWords < expectedWords * 0.7;
    }

    public static double CalculateAccuracyScore(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) && string.IsNullOrWhiteSpace(actual)) return 1.0;
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return 0.0;

        var expWords = DialogueComparisonNormalizer.Normalize(expected).Tokens;
        var actWords = DialogueComparisonNormalizer.Normalize(actual).Tokens;

        if (expWords.Count == 0) return 1.0;

        int matches = expWords.Count(ew => actWords.Any(aw => IsWordEquivalent(ew, aw)));

        return (double)matches / expWords.Count;
    }

    private static bool IsWordEquivalent(string normExp, string normAct)
    {
        if (string.Equals(normExp, normAct, StringComparison.OrdinalIgnoreCase)) return true;

        // Levenshtein edit distance fallback for minor single-character typos (e.g. 1 char diff in 5+ char word)
        if (normExp.Length >= 4 && Math.Abs(normExp.Length - normAct.Length) <= 1)
        {
            var dist = LevenshteinDistance(normExp, normAct);
            if (dist <= 1) return true;
        }
        return false;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        var d = new int[s.Length + 1, t.Length + 1];
        for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= t.Length; j++) d[0, j] = j;

        for (int i = 1; i <= s.Length; i++)
        {
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = (s[i - 1] == t[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return d[s.Length, t.Length];
    }

    private static string ExtractJson(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "{}";
        var match = CommonRegex.Match(input, @"\{[\s\S]*\}");
        return match.Success ? match.Value : input;
    }

    private static string GetJsonString(JsonElement root, params string[] names)
    {
        if (TryGetNamedString(root, names, out var exact))
            return exact;
        return TryGetFuzzyString(root, names) ?? "";
    }

    private static bool TryGetNamedString(JsonElement root, string[] names, out string value)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
                continue;
            var val = el.GetString();
            if (string.IsNullOrWhiteSpace(val))
                continue;
            value = val.Trim();
            return true;
        }
        value = "";
        return false;
    }

    private static string? TryGetFuzzyString(JsonElement root, string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var val in root.EnumerateObject()
                     .Where(p => JsonPropertyNameMatches(p.Name, names))
                     .Select(p => p.Value))
        {
            if (val.ValueKind != JsonValueKind.String)
                continue;
            var s = val.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }
        return null;
    }

    private static bool GetJsonBool(JsonElement root, params string[] names)
    {
        if (TryGetNamedBool(root, names, out var exact))
            return exact;
        return TryGetFuzzyBool(root, names);
    }

    private static bool TryGetNamedBool(JsonElement root, string[] names, out bool value)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var el) ||
                el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                continue;
            value = el.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryGetFuzzyBool(JsonElement root, string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        return root.EnumerateObject()
            .Where(p => JsonPropertyNameMatches(p.Name, names) &&
                        p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            .Select(p => p.Value.GetBoolean())
            .FirstOrDefault();
    }

    private static double? GetJsonDouble(JsonElement root, params string[] names)
    {
        if (TryGetNamedDouble(root, names, out var exact))
            return exact;
        return TryGetFuzzyDouble(root, names);
    }

    private static bool TryGetNamedDouble(JsonElement root, string[] names, out double value)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) &&
                el.ValueKind == JsonValueKind.Number &&
                el.TryGetDouble(out var v))
            {
                value = v;
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static double? TryGetFuzzyDouble(JsonElement root, string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var val in root.EnumerateObject()
                     .Where(p => JsonPropertyNameMatches(p.Name, names))
                     .Select(p => p.Value))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out var v))
                return v;
        }
        return null;
    }

    private static bool JsonPropertyNameMatches(string propName, string[] names)
    {
        var compact = propName.Replace("_", "");
        return names.Any(name =>
            string.Equals(compact, name.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
    }
}
