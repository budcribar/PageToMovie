using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly ProjectTelemetryService _telemetry;
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
        _telemetry = telemetry;
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

    public async Task SaveVerificationAsync(string projectId, ClipDialogueVerificationResult result, CancellationToken ct = default)
    {
        var path = VerificationPath(projectId, result.SceneNumber, result.ClipNumber);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        var clipPath = (!string.IsNullOrWhiteSpace(overrideVideoPath) && File.Exists(overrideVideoPath))
            ? overrideVideoPath
            : _projects.ResolveClipVideoPath(projectId, sceneNumber, clipNumber);
        var detail = await _projects.GetSceneDetailAsync(projectId, sceneNumber, ct: ct).ConfigureAwait(false);
        var clip = detail?.Clips?.FirstOrDefault(c => c.ClipNumber == clipNumber);

        // Every spoken line of this clip via the shared accessor — a two-hander's second speaker's
        // line used to be dropped here, so the teacher's answer was never verified and could not
        // feed the timing/accuracy feedback loop.
        var spokenLines = ClipSpokenLines.FromClip(clip);
        var expectedSpeaker = clip?.Speaker ?? "Unknown";
        var expectedDialogue = BuildExpectedDialogue(clip);

        // If no spoken dialogue planned for this clip, return verified no-speech status immediately
        if (string.IsNullOrWhiteSpace(expectedDialogue))
        {
            var noSpeechResult = new ClipDialogueVerificationResult
            {
                SceneNumber = sceneNumber,
                ClipNumber = clipNumber,
                ExpectedSpeaker = string.IsNullOrWhiteSpace(expectedSpeaker) || string.Equals(expectedSpeaker, "Unknown", StringComparison.OrdinalIgnoreCase) ? "None" : expectedSpeaker,
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

        // Cache Validation: If not forced and no override video path provided, return existing saved verification if clip file & dialogue haven't changed
        if (!force && string.IsNullOrWhiteSpace(overrideVideoPath))
        {
            var existing = await LoadVerificationAsync(projectId, sceneNumber, clipNumber, ct).ConfigureAwait(false);
            if (existing is not null && !string.Equals(existing.Status, "unverified", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(clipPath) && File.Exists(clipPath))
                {
                    var videoMTime = File.GetLastWriteTimeUtc(clipPath);
                    if (existing.VerifiedAt >= videoMTime &&
                        string.Equals(existing.ExpectedDialogue, expectedDialogue, StringComparison.Ordinal) &&
                        string.Equals(existing.ExpectedSpeaker, expectedSpeaker, StringComparison.OrdinalIgnoreCase))
                    {
                        _log.LogInformation("Dialogue verification for {Project} S{Scene} C{Clip} is up-to-date (cached)", projectId, sceneNumber, clipNumber);
                        return existing;
                    }
                }
            }
        }

        if (!IsConfigured)
        {
            _log.LogWarning("Vision/Gemini client not configured — skipping dialogue verification for {Project} S{Scene} C{Clip}", projectId, sceneNumber, clipNumber);
            var unverified = new ClipDialogueVerificationResult
            {
                SceneNumber = sceneNumber,
                ClipNumber = clipNumber,
                ExpectedSpeaker = expectedSpeaker,
                ExpectedDialogue = expectedDialogue,
                Status = "unverified",
                SummaryNote = "Google Gemini key (GEMINI_API_KEY) required for native MP4 video & audio dialogue verification. Please set key in Configuration.",
                VerifiedAt = DateTime.UtcNow,
            };
            await SaveVerificationAsync(projectId, unverified, ct).ConfigureAwait(false);
            return unverified;
        }

        var mediaToPass = new List<string>();

        // Attach actual MP4 clip file so Gemini can evaluate video lip sync + audio track
        if (!string.IsNullOrWhiteSpace(clipPath) && File.Exists(clipPath))
        {
            mediaToPass.Add(clipPath);
        }

        // Collect character reference portraits for characters in this scene
        var charSummaryList = _projects.ListCharacters(projectId);
        var sceneChars = clip?.CharactersOnScreen is { Count: > 0 } ? clip.CharactersOnScreen : new List<string> { expectedSpeaker };
        // Every speaking character (primary + any second speaker) must have its reference portrait
        // attached so the vision pass can match each line's face.
        foreach (var lineSpeaker in spokenLines.Select(l => l.Speaker)
                     .Append(expectedSpeaker)
                     .Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (!sceneChars.Any(c => string.Equals(c, lineSpeaker, StringComparison.OrdinalIgnoreCase)))
            {
                sceneChars = sceneChars.Concat(new[] { lineSpeaker }).ToList();
            }
        }

        var charGuides = new List<string>();
        int mediaIndex = 1; // Index 1 is the MP4 video clip

        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        foreach (var cName in sceneChars)
        {
            var charObj = charSummaryList.FirstOrDefault(c => string.Equals(c.Key, cName, StringComparison.OrdinalIgnoreCase) || string.Equals(c.DisplayName, cName, StringComparison.OrdinalIgnoreCase));
            if (charObj?.PreferredUrl is { Length: > 0 } url)
            {
                var localRef = Path.Combine(projectDir, url.TrimStart('/'));
                if (File.Exists(localRef))
                {
                    mediaToPass.Add(localRef);
                    mediaIndex++;
                    var desc = !string.IsNullOrWhiteSpace(charObj.Description) ? $" ({charObj.Description})" : "";
                    var nameLabel = charObj.DisplayName ?? charObj.Key;
                    charGuides.Add($"- Attached Image #{mediaIndex}: Character '{nameLabel}' (Key: '{charObj.Key}'){desc}");
                }
            }
        }

        // Include passed keyframe images as supplementary input
        if (keyframePaths is { Count: > 0 })
        {
            mediaToPass.AddRange(keyframePaths.Where(File.Exists));
        }

        if (mediaToPass.Count == 0)
        {
            var result = new ClipDialogueVerificationResult
            {
                SceneNumber = sceneNumber,
                ClipNumber = clipNumber,
                ExpectedSpeaker = expectedSpeaker,
                ExpectedDialogue = expectedDialogue,
                Status = "unverified",
                SummaryNote = "Clip video file (.mp4) not found on server disk. Please generate video clips for this scene first.",
                VerifiedAt = DateTime.UtcNow,
            };
            await SaveVerificationAsync(projectId, result, ct).ConfigureAwait(false);
            return result;
        }

        var guideText = charGuides.Count > 0
            ? "CHARACTER REFERENCE PORTRAITS (MATCH FACES IN VIDEO TO THESE ATTACHED IMAGES):\n" + string.Join("\n", charGuides)
            : "No character reference portraits attached.";

        var expectedCharObj = charSummaryList.FirstOrDefault(c => string.Equals(c.Key, expectedSpeaker, StringComparison.OrdinalIgnoreCase) || string.Equals(c.DisplayName, expectedSpeaker, StringComparison.OrdinalIgnoreCase));
        var expectedSpeakerDisplayName = expectedCharObj?.DisplayName ?? expectedSpeaker;

        var prompt = $@"
You are an automated film quality assurance inspector evaluating a generated movie clip.

EXPECTED SCRIPT:
- Expected Speaker: '{expectedSpeakerDisplayName}' (Character Key: '{expectedSpeaker}')
- Expected Spoken Dialogue: '{expectedDialogue}'

{guideText}

TASKS:
1. Watch the attached MP4 video clip (Attached File #1) and LISTEN carefully to the audio track / spoken dialogue.
2. Observe on-screen character faces and lip movements. Compare the face of the character who is speaking against the attached character reference portraits listed above to determine who is speaking.
3. Transcribe the EXACT spoken dialogue you hear in the video clip.
4. Compare detected speaker vs expected speaker ('{expectedSpeakerDisplayName}'), and transcribed dialogue vs expected dialogue.
   NOTE: Ignore minor US/UK spelling differences (e.g. 'neighbour' vs 'neighbor', 'colour' vs 'color'). If the spoken words match the script, score dialogue accuracy as 1.0 (100% match).

Return ONLY a JSON object:
{{
  ""detectedSpeaker"": ""Character Name or Key"",
  ""transcribedDialogue"": ""Spoken dialogue text heard in video audio track"",
  ""dialogueAccuracyScore"": 0.95,
  ""speakerMatch"": true,
  ""status"": ""verified"",
  ""summaryNote"": ""Expected: '{expectedDialogue}' | Heard: '...' (Match 95%)""
}}
Status options: 'verified' (dialogue & speaker match), 'mismatch' (dialogue incorrect), 'speaker_swap' (wrong character speaking), 'no_speech' (no spoken dialogue heard).
".Trim();

        try
        {
            var sw = Stopwatch.StartNew();
            string targetModel;
            SupportedModelEntry entry;
            try
            {
                var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
                targetModel = ProjectModelSelection.RequireVideoReview(cfg, "Dialogue verification");
                entry = SupportedModelCatalog.Find(targetModel)
                        ?? throw new InvalidOperationException(
                            $"Dialogue verification: model '{targetModel}' missing from catalog.");
            }
            catch (InvalidOperationException)
            {
                throw;
            }

            var hasVideoFile = mediaToPass.Any(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mov", StringComparison.OrdinalIgnoreCase));

            string responseJson;
            // Prefer the project-selected model. If it cannot take video and Gemini is available
            // as the selected provider's path, still require catalog SupportVideoReview on selection —
            // do not silently swap to a hard-coded Gemini id.
            if (hasVideoFile && !entry.SupportsVideoReview)
            {
                throw new InvalidOperationException(
                    "Dialogue verification: the selected Video review model does not support native video. " +
                    "Open Settings and choose a video-review-capable model (e.g. Gemini with SupportsVideoReview).");
            }
            if (hasVideoFile && entry.SupportsVideoReview && _gemini is not null && _gemini.IsConfigured
                && string.Equals(entry.ProviderId, "gemini", StringComparison.OrdinalIgnoreCase))
            {
                responseJson = await _gemini.CompleteWithImagesAsync(prompt, mediaToPass, model: targetModel, ct: ct).ConfigureAwait(false);
            }
            else
            {
                responseJson = await _vision.CompleteWithImagesAsync(prompt, mediaToPass, model: targetModel, ct: ct).ConfigureAwait(false);
            }
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

            // Normalize speaker names to prevent false speaker_swap when Key vs DisplayName differ
            static string CleanSpkName(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                var clean = s.Trim().ToLowerInvariant()
                    .Replace("character_", "")
                    .Replace("character", "")
                    .Replace("_", " ")
                    .Replace("the ", "");
                return System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
            }

            var cleanDetected = CleanSpkName(detected);
            var cleanExpectedKey = CleanSpkName(expectedSpeaker);
            var cleanExpectedDisp = CleanSpkName(expectedSpeakerDisplayName);

            if (!string.IsNullOrWhiteSpace(cleanDetected) && (cleanDetected == cleanExpectedKey || cleanDetected == cleanExpectedDisp))
            {
                speakerMatch = true;
                if (string.Equals(status, "speaker_swap", StringComparison.OrdinalIgnoreCase))
                {
                    status = accuracy >= 0.5 ? "verified" : "mismatch";
                }
            }

            // Deterministic validation: if dialogue was expected but transcribed audio is empty, enforce mismatch (0%)
            if (!string.IsNullOrWhiteSpace(expectedDialogue) && string.IsNullOrWhiteSpace(transcribed))
            {
                accuracy = 0.0;
                status = "mismatch";
                summary = $"Expected: '{expectedDialogue}' | Heard: (no audio/speech detected) (0% match)";
            }
            else if (!string.IsNullOrWhiteSpace(expectedDialogue))
            {
                var computedAcc = CalculateAccuracyScore(expectedDialogue, transcribed);
                if (computedAcc < accuracy) accuracy = computedAcc;
                if (accuracy < 0.5 && string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase))
                {
                    status = "mismatch";
                }
            }

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
                EstimatedDurationSeconds = Math.Round(estSec, 1),
                WordCount = ClipDurationEstimator.CountWords(expectedDialogue),
                SyllableCount = ClipDurationEstimator.CountSyllables(expectedDialogue),
                SpeechDurationSeconds = speechSec,
                ActionDurationSeconds = actionSec,
                ActualDurationSeconds = Math.Round(actualSec, 1),
                VerifiedAt = DateTime.UtcNow,
            };
            result.SpeechTruncated = LooksTruncated(result);

            await SaveVerificationAsync(projectId, result, ct).ConfigureAwait(false);
            _log.LogInformation("Automated dialogue verification completed for {Project} S{Scene} C{Clip}: {Status} ({Score:P0})", projectId, sceneNumber, clipNumber, status, accuracy);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dialogue verification failed for {Project} S{Scene} C{Clip}", projectId, sceneNumber, clipNumber);
            var failedResult = new ClipDialogueVerificationResult
            {
                SceneNumber = sceneNumber,
                ClipNumber = clipNumber,
                ExpectedSpeaker = expectedSpeaker,
                ExpectedDialogue = expectedDialogue,
                Status = "unverified",
                SummaryNote = $"Verification error: {ex.Message}",
                VerifiedAt = DateTime.UtcNow,
            };
            await SaveVerificationAsync(projectId, failedResult, ct).ConfigureAwait(false);
            return failedResult;
        }
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

        int matches = 0;
        foreach (var ew in expWords)
        {
            if (actWords.Any(aw => IsWordEquivalent(ew, ew, aw)))
            {
                matches++;
            }
        }

        return (double)matches / expWords.Count;
    }

    private static bool IsWordEquivalent(string rawExp, string normExp, string normAct)
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
        var match = Regex.Match(input, @"\{[\s\S]*\}");
        return match.Success ? match.Value : input;
    }

    private static string GetJsonString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name.Replace("_", ""), name.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var val = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                        }
                    }
                }
            }
        }
        return "";
    }

    private static bool GetJsonBool(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el))
            {
                if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return el.GetBoolean();
            }
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name.Replace("_", ""), name.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            return prop.Value.GetBoolean();
                    }
                }
            }
        }
        return false;
    }

    private static double? GetJsonDouble(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v))
                return v;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name.Replace("_", ""), name.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var v))
                            return v;
                    }
                }
            }
        }
        return null;
    }
}
