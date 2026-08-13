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
        var sample = !string.IsNullOrWhiteSpace(sampleText)
            ? sampleText.Trim()
            : BuildSampleDialogue(
                !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : charKey.Replace("Character_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' '));
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

        var fingerprint = ComputeFingerprint(charKey, profile, label, sample);
        var cache = GetCacheInfo(projectId, charKey, profile, label, sample);
        if (!force && cache is { Exists: true, Matches: true, MediaPath: { Length: > 0 } hit })
        {
            onProgress?.Invoke(100, 100, "Using cached voice sample");
            return hit;
        }

        onProgress?.Invoke(2, 100, "Building film-style voice prompt…");

        // Cast profile fields are free-form (admin/AI-authored) — sanitize each leaf value at the
        // source; "look" itself is a structural block (nests VisualLock), so it's wrapped in
        // <Look> as-is below, not re-sanitized (see PromptTags class doc).
        var look = "";
        if (prof is not null)
        {
            if (!string.IsNullOrWhiteSpace(prof.Description))
                look += PromptTags.SanitizeValue(prof.Description.Trim());
            if (!string.IsNullOrWhiteSpace(prof.VisualLock))
                look += (look.Length > 0 ? " " : "") +
                    PromptTags.Wrap("VisualLock", PromptTags.SanitizeValue(prof.VisualLock.Trim()));
        }

        var voiceLock = !string.IsNullOrWhiteSpace(profile)
            ? " " + PromptTags.Wrap("VoiceLock", $"{charKey}: {PromptTags.SanitizeValue(profile)}")
            : !string.IsNullOrWhiteSpace(label)
                ? " " + PromptTags.Wrap("VoiceLock", $"{charKey}: {PromptTags.SanitizeValue(label)}")
                : " " + PromptTags.Wrap("VoiceLock", $"{charKey}: natural speaking voice for {display}");

        // Optional locked portrait for lip-sync consistency (reference_images)
        string? refPath = null;
        try { refPath = _projects.ResolveCharacterRefPath(projectId, charKey); }
        catch { /* optional */ }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(refPath) && File.Exists(refPath))
        {
            sb.AppendLine(
                $"Close-up of {display} speaking to camera. Match appearance of reference <IMAGE_1> exactly.");
        }
        else
        {
            sb.AppendLine(
                $"Close-up of {display}, adult person speaking directly to camera, neutral soft background, film still.");
        }

        if (look.Length > 0)
            sb.AppendLine(PromptTags.Wrap("Look", look));
        sb.AppendLine(PromptTags.Wrap("Audio",
            $"REQUIRED native Grok dialogue. {charKey} ON CAMERA lip-syncs " +
            $"exactly: \"{PromptTags.SanitizeValue(sample)}\". Other mouths closed. Speech intelligible; never silent.{voiceLock}"));
        sb.AppendLine(
            "Single continuous take, natural performance, no music, no captions, no on-screen text.");

        var prompt = sb.ToString().Trim();
        var cfg = await _projects.GetConfigAsync(projectId, ct).ConfigureAwait(false);
        var model = ProjectModelSelection.RequireVideo(cfg, "Voice preview");
        var duration = PreviewDurationSeconds;
        var resolution = PreviewResolution;

        onProgress?.Invoke(8, 100, "Submitting short video for voice sample…");
        _log.LogInformation(
            "Voice preview submit {Char} dur={Dur}s profileLen={P} sampleLen={S} force={F}",
            charKey, duration, profile.Length, sample.Length, force);

        var refs = !string.IsNullOrWhiteSpace(refPath) && File.Exists(refPath)
            ? (IReadOnlyList<string>)new[] { refPath }
            : null;

        var requestId = await _video.SubmitGenerationAsync(
            prompt,
            duration,
            resolution,
            model,
            ct,
            referenceImagePaths: refs);

        onProgress?.Invoke(12, 100, "Generating video audio…");
        await AppendLogSafe(onProgress, 12, "request_id=" + requestId);

        var videoUrl = await _video.PollForVideoUrlAsync(
            requestId,
            msg =>
            {
                // "status=pending (42%)" → map into 12–85
                var pct = TryParseGrokProgress(msg);
                var mapped = pct is >= 0 and <= 100
                    ? 12 + (int)Math.Round(pct.Value * 0.73)
                    : 40;
                onProgress?.Invoke(Math.Clamp(mapped, 12, 85), 100, msg);
            },
            ct);

        onProgress?.Invoke(88, 100, "Downloading sample…");
        var dir = GetPreviewDir(projectId);
        Directory.CreateDirectory(dir);
        var mp4Path = GetMp4Path(projectId, charKey);
        var metaPath = GetMetaPath(projectId, charKey);

        await _video.DownloadToFileAsync(videoUrl, mp4Path, ct);

        if (!File.Exists(mp4Path) || new FileInfo(mp4Path).Length < 512)
            throw new InvalidOperationException("Voice sample download produced empty file.");

        // Drop legacy MP3 if regenerating so cache resolution prefers the new MP4
        try
        {
            var legacyMp3 = GetMp3Path(projectId, charKey);
            if (File.Exists(legacyMp3))
                File.Delete(legacyMp3);
        }
        catch { /* best effort */ }

        var meta = new Dictionary<string, object?>
        {
            ["fingerprint"] = fingerprint,
            ["charKey"] = charKey,
            ["displayName"] = display,
            ["voiceProfile"] = profile,
            ["voiceLabel"] = label,
            ["sampleText"] = sample,
            ["durationSeconds"] = duration,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["source"] = "video-gen",
            ["format"] = "mp4",
        };
        await File.WriteAllTextAsync(
            metaPath,
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        onProgress?.Invoke(100, 100, "Voice sample ready");
        return mp4Path;
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
