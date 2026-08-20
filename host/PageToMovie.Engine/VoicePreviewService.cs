using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Film-pipeline voice sample (not TTS): short video with VOICE LOCK + dialogue.
/// Caches MP4 under assets/characters/voice_previews/ (no native ffmpeg / no MP3 extract).
/// </summary>
public sealed class VoicePreviewService
{
    public const int PreviewDurationSeconds = 5;
    public const string PreviewResolution = "480p";

    private readonly ProjectStore _projects;
    private readonly IVideoClient _video;
    private readonly ILogger<VoicePreviewService> _log;

    public VoicePreviewService(
        ProjectStore projects,
        IVideoClient video,
        IOptions<PageToMovieOptions> opts,
        ILogger<VoicePreviewService> log)
    {
        _projects = projects;
        _video = video;
        _log = log;
    }

    public bool IsVideoConfigured => _video.IsConfigured;

    public static string BuildSampleDialogue(string? displayName)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "this character" : displayName.Trim();
        return $"Hello. My name is {name}. This is how I sound when I speak.";
    }

    public static string ComputeFingerprint(
        string charKey,
        string? voiceProfile,
        string? voiceLabel,
        string? sampleText)
    {
        var raw = string.Join('\n',
            (charKey ?? "").Trim(),
            (voiceProfile ?? "").Trim(),
            (voiceLabel ?? "").Trim(),
            (sampleText ?? "").Trim());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Fingerprint for cache status checks. When <paramref name="sampleText"/> is omitted,
    /// uses the same default sample line as <see cref="GenerateAsync"/> so Play/status match.
    /// </summary>
    public static string ComputeFingerprintForCache(
        string charKey,
        string? voiceProfile,
        string? voiceLabel,
        string? displayName,
        string? sampleText)
    {
        string sample;
        if (!string.IsNullOrWhiteSpace(sampleText))
        {
            sample = sampleText.Trim();
        }
        else
        {
            var name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : charKey.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
            sample = BuildSampleDialogue(name);
        }
        return ComputeFingerprint(charKey, voiceProfile, voiceLabel, sample);
    }

    public string GetPreviewDir(string projectId) =>
        Path.Combine(_projects.GetProjectDir(projectId), "assets", "characters", "voice_previews");

    /// <summary>Preferred sample path (short MP4; no server audio extract).</summary>
    public string GetMp4Path(string projectId, string charKey) =>
        Path.Combine(GetPreviewDir(projectId), SafeFileName(charKey) + ".mp4");

    /// <summary>Legacy MP3 path from pre-wasm era (still recognized for cache/serve).</summary>
    public string GetMp3Path(string projectId, string charKey) =>
        Path.Combine(GetPreviewDir(projectId), SafeFileName(charKey) + ".mp3");

    public string GetMetaPath(string projectId, string charKey) =>
        Path.Combine(GetPreviewDir(projectId), SafeFileName(charKey) + ".meta.json");

    /// <summary>Absolute path to cached sample (MP4 preferred, else legacy MP3), or null.</summary>
    public string? GetSampleMediaPath(string projectId, string charKey)
    {
        var mp4 = GetMp4Path(projectId, charKey);
        if (File.Exists(mp4) && new FileInfo(mp4).Length >= 512)
            return mp4;
        var mp3 = GetMp3Path(projectId, charKey);
        if (File.Exists(mp3) && new FileInfo(mp3).Length >= 64)
            return mp3;
        return null;
    }

    public VoicePreviewCacheInfo GetCacheInfo(
        string projectId,
        string charKey,
        string? voiceProfile = null,
        string? voiceLabel = null,
        string? sampleText = null,
        string? displayName = null)
    {
        var media = GetSampleMediaPath(projectId, charKey);
        var metaPath = GetMetaPath(projectId, charKey);
        var expected = ComputeFingerprintForCache(charKey, voiceProfile, voiceLabel, displayName, sampleText);
        if (media is null)
        {
            return new VoicePreviewCacheInfo
            {
                Exists = false,
                Matches = false,
                ExpectedFingerprint = expected,
            };
        }

        string? storedFp = null;
        DateTimeOffset? generatedAt = null;
        try
        {
            if (File.Exists(metaPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                storedFp = root.TryGetProperty("fingerprint", out var f) ? f.GetString() : null;
                if (root.TryGetProperty("generatedAt", out var g) &&
                    g.GetString() is { Length: > 0 } gs &&
                    DateTimeOffset.TryParse(gs, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    generatedAt = dt;
            }
        }
        catch
        {
            /* treat as stale */
        }

        return new VoicePreviewCacheInfo
        {
            Exists = true,
            Matches = !string.IsNullOrEmpty(storedFp) &&
                      string.Equals(storedFp, expected, StringComparison.OrdinalIgnoreCase),
            Fingerprint = storedFp,
            ExpectedFingerprint = expected,
            GeneratedAt = generatedAt,
            Mp3Path = media, // historical name; may be .mp4
            MediaPath = media,
            ContentType = media.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? "audio/mpeg"
                : "video/mp4",
            ByteLength = new FileInfo(media).Length,
        };
    }

    /// <summary>
    /// Generate (or reuse cache) a film-style voice sample. Returns absolute path to MP4 (or legacy MP3).
    /// </summary>
    public async Task<string> GenerateAsync(
        string projectId,
        string charKey,
        string? voiceProfile,
        string? voiceLabel,
        string? displayName,
        string? sampleText,
        bool force,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_video.IsConfigured)
            throw new InvalidOperationException("Connect service (XAI_API_KEY) for voice preview.");

        var inputs = ResolveVoiceInputs(projectId, charKey, voiceProfile, voiceLabel, displayName, sampleText);
        if (!force && TryGetCachedPreview(projectId, charKey, inputs, onProgress, out var hit))
            return hit;

        onProgress?.Invoke(2, 100, "Building film-style voice prompt…");
        var refPath = TryResolveRefPath(projectId, charKey);
        var prompt = BuildPreviewPrompt(charKey, inputs, refPath);
        return await GenerateAndCachePreviewAsync(
            projectId, charKey, inputs, prompt, refPath, force, onProgress, ct).ConfigureAwait(false);
    }

    private sealed class VoicePreviewInputs
    {
        public required string Profile { get; init; }
        public required string Label { get; init; }
        public required string Display { get; init; }
        public required string Sample { get; init; }
        public required string Fingerprint { get; init; }
        public ClipVideoPromptBuilder.CharacterProfile? Prof { get; init; }
    }

    private VoicePreviewInputs ResolveVoiceInputs(
        string projectId,
        string charKey,
        string? voiceProfile,
        string? voiceLabel,
        string? displayName,
        string? sampleText)
    {
        var profiles = _projects.LoadCharacterPromptProfiles(projectId);
        profiles.TryGetValue(charKey, out var prof);
        var profile = !string.IsNullOrWhiteSpace(voiceProfile)
            ? voiceProfile.Trim()
            : (prof?.VoiceProfile ?? "").Trim();
        var label = !string.IsNullOrWhiteSpace(voiceLabel)
            ? voiceLabel.Trim()
            : (prof?.VoiceLabel ?? "").Trim();
        var display = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : (prof?.DisplayName ?? charKey.Replace("Character_", "").Replace('_', ' '));
        var sample = !string.IsNullOrWhiteSpace(sampleText)
            ? sampleText.Trim()
            : BuildSampleDialogue(display);
        return new VoicePreviewInputs
        {
            Profile = profile,
            Label = label,
            Display = display,
            Sample = sample,
            Fingerprint = ComputeFingerprint(charKey, profile, label, sample),
            Prof = prof,
        };
    }

    private bool TryGetCachedPreview(
        string projectId,
        string charKey,
        VoicePreviewInputs inputs,
        Action<int, int, string>? onProgress,
        out string hit)
    {
        hit = "";
        var cache = GetCacheInfo(projectId, charKey, inputs.Profile, inputs.Label, inputs.Sample);
        if (cache is not { Exists: true, Matches: true, MediaPath: { Length: > 0 } path })
            return false;
        onProgress?.Invoke(100, 100, "Using cached voice sample");
        hit = path;
        return true;
    }

    private string? TryResolveRefPath(string projectId, string charKey)
    {
        try { return _projects.ResolveCharacterRefPath(projectId, charKey); }
        catch { return null; }
    }

    private static string BuildLook(ClipVideoPromptBuilder.CharacterProfile? prof)
    {
        // Cast profile fields are free-form (admin/AI-authored) — sanitize each leaf value at the
        // source; "look" itself is a structural block (nests VisualLock), so it's wrapped in
        // <Look> as-is below, not re-sanitized (see PromptTags class doc).
        if (prof is null) return "";
        var look = "";
        if (!string.IsNullOrWhiteSpace(prof.Description))
            look += PromptTags.SanitizeValue(prof.Description.Trim());
        if (!string.IsNullOrWhiteSpace(prof.VisualLock))
            look += (look.Length > 0 ? " " : "") +
                PromptTags.Wrap("VisualLock", PromptTags.SanitizeValue(prof.VisualLock.Trim()));
        return look;
    }

    private static string BuildVoiceLock(string charKey, VoicePreviewInputs inputs)
    {
        if (!string.IsNullOrWhiteSpace(inputs.Profile))
            return " " + PromptTags.Wrap("VoiceLock", $"{charKey}: {PromptTags.SanitizeValue(inputs.Profile)}");
        if (!string.IsNullOrWhiteSpace(inputs.Label))
            return " " + PromptTags.Wrap("VoiceLock", $"{charKey}: {PromptTags.SanitizeValue(inputs.Label)}");
        return " " + PromptTags.Wrap("VoiceLock", $"{charKey}: natural speaking voice for {inputs.Display}");
    }

    private static bool HasRefImage(string? refPath) =>
        !string.IsNullOrWhiteSpace(refPath) && File.Exists(refPath);

    private static string BuildPreviewPrompt(string charKey, VoicePreviewInputs inputs, string? refPath)
    {
        var look = BuildLook(inputs.Prof);
        var voiceLock = BuildVoiceLock(charKey, inputs);
        var sb = new StringBuilder();
        if (HasRefImage(refPath))
        {
            sb.AppendLine(
                $"Close-up of {inputs.Display} speaking to camera. Match appearance of reference <IMAGE_1> exactly.");
        }
        else
        {
            sb.AppendLine(
                $"Close-up of {inputs.Display}, adult person speaking directly to camera, neutral soft background, film still.");
        }

        if (look.Length > 0)
            sb.AppendLine(PromptTags.Wrap("Look", look));
        sb.AppendLine(PromptTags.Wrap("Audio",
            $"REQUIRED native Grok dialogue. {charKey} ON CAMERA lip-syncs " +
            $"exactly: \"{PromptTags.SanitizeValue(inputs.Sample)}\". Other mouths closed. Speech intelligible; never silent.{voiceLock}"));
        sb.AppendLine(
            "Single continuous take, natural performance, no music, no captions, no on-screen text.");
        return sb.ToString().Trim();
    }

    private async Task<string> GenerateAndCachePreviewAsync(
        string projectId,
        string charKey,
        VoicePreviewInputs inputs,
        string prompt,
        string? refPath,
        bool force,
        Action<int, int, string>? onProgress,
        CancellationToken ct)
    {
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        var model = ProjectModelSelection.RequireVideo(cfg, "Voice preview");
        var duration = PreviewDurationSeconds;
        var resolution = PreviewResolution;

        onProgress?.Invoke(8, 100, "Submitting short video for voice sample…");
        _log.LogInformation(
            "Voice preview submit {Char} dur={Dur}s profileLen={P} sampleLen={S} force={F}",
            charKey, duration, inputs.Profile.Length, inputs.Sample.Length, force);

        var refs = HasRefImage(refPath) ? (IReadOnlyList<string>)new[] { refPath! } : null;
        var requestId = await _video.SubmitGenerationAsync(
            prompt, duration, resolution, model, ct, referenceImagePaths: refs);

        onProgress?.Invoke(12, 100, "Generating video audio…");
        await AppendLogSafe(onProgress, 12, "request_id=" + requestId);

        var videoUrl = await _video.PollForVideoUrlAsync(requestId, msg => ReportPollProgress(onProgress, msg), ct);
        return await DownloadPreviewAsync(projectId, charKey, inputs, videoUrl, duration, model, onProgress, ct)
            .ConfigureAwait(false);
    }

    private static void ReportPollProgress(Action<int, int, string>? onProgress, string msg)
    {
        // "status=pending (42%)" → map into 12–85
        var pct = TryParseGrokProgress(msg);
        var mapped = pct is >= 0 and <= 100
            ? 12 + (int)Math.Round(pct.Value * 0.73)
            : 40;
        onProgress?.Invoke(Math.Clamp(mapped, 12, 85), 100, msg);
    }

    private async Task<string> DownloadPreviewAsync(
        string projectId,
        string charKey,
        VoicePreviewInputs inputs,
        string videoUrl,
        int duration,
        string model,
        Action<int, int, string>? onProgress,
        CancellationToken ct)
    {
        onProgress?.Invoke(88, 100, "Downloading sample…");
        Directory.CreateDirectory(GetPreviewDir(projectId));
        var mp4Path = GetMp4Path(projectId, charKey);
        var metaPath = GetMetaPath(projectId, charKey);

        await _video.DownloadToFileAsync(videoUrl, mp4Path, model, ct);
        if (!File.Exists(mp4Path) || new FileInfo(mp4Path).Length < 512)
            throw new InvalidOperationException("Voice sample download produced empty file.");

        TryDeleteLegacyMp3(projectId, charKey);
        await WritePreviewMetaAsync(metaPath, charKey, inputs, duration, ct).ConfigureAwait(false);
        onProgress?.Invoke(100, 100, "Voice sample ready");
        return mp4Path;
    }

    private void TryDeleteLegacyMp3(string projectId, string charKey)
    {
        // Drop legacy MP3 if regenerating so cache resolution prefers the new MP4
        try
        {
            var legacyMp3 = GetMp3Path(projectId, charKey);
            if (File.Exists(legacyMp3))
                File.Delete(legacyMp3);
        }
        catch { /* best effort */ }
    }

    private static Task WritePreviewMetaAsync(
        string metaPath, string charKey, VoicePreviewInputs inputs, int duration, CancellationToken ct)
    {
        var meta = new Dictionary<string, object?>
        {
            ["fingerprint"] = inputs.Fingerprint,
            ["charKey"] = charKey,
            ["displayName"] = inputs.Display,
            ["voiceProfile"] = inputs.Profile,
            ["voiceLabel"] = inputs.Label,
            ["sampleText"] = inputs.Sample,
            ["durationSeconds"] = duration,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["source"] = "video-gen",
            ["format"] = "mp4",
        };
        return File.WriteAllTextAsync(
            metaPath,
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    /// <summary>Test hook for progress percent parsing.</summary>
    public static int? TryParseGrokProgressForTests(string? msg) => TryParseGrokProgress(msg);

    /// <summary>Test hook for safe file names under voice_previews/.</summary>
    public static string SafeFileNameForTests(string charKey) => SafeFileName(charKey);

    private static int? TryParseGrokProgress(string? msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return null;
        // status=pending (42%) or (42%)
        var m = CommonRegex.Match(msg, @"\((\d+(?:\.\d+)?)\s*%\)");
        if (m.Success &&
            double.TryParse(
                m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var d))
            return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        return null;
    }

    private static Task AppendLogSafe(Action<int, int, string>? onProgress, int index, string msg)
    {
        onProgress?.Invoke(index, 100, msg);
        return Task.CompletedTask;
    }

    private static string SafeFileName(string charKey)
    {
        var s = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeFileName((charKey ?? "char").Trim());
        s = s.Replace("..", "_", StringComparison.Ordinal);
        while (s.StartsWith('.'))
            s = s.TrimStart('.');
        return string.IsNullOrWhiteSpace(s) ? "char" : s;
    }
}

public sealed class VoicePreviewCacheInfo
{
    public bool Exists { get; set; }
    public bool Matches { get; set; }
    public string? Fingerprint { get; set; }
    public string? ExpectedFingerprint { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    /// <summary>Absolute path to cached sample (MP4 or legacy MP3).</summary>
    public string? MediaPath { get; set; }
    public string? ContentType { get; set; }
    public string? Mp3Path { get; set; }
    public long ByteLength { get; set; }
}
