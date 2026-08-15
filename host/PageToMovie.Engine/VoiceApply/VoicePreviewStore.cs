using PageToMovie.Core.Utils;

namespace PageToMovie.Engine.VoiceApply;

/// <summary>Shared helper: write TTS preview under assets/characters/{key}/voice_preview_tts.*.</summary>
public sealed class VoicePreviewStore
{
    private readonly ProjectStore _projects;

    public VoicePreviewStore(ProjectStore projects) => _projects = projects;

    public string? GetTtsPreviewPath(string projectId, string charKey)
    {
        var dir = CharDir(projectId, charKey);
        if (!Directory.Exists(dir)) return null;
        foreach (var name in new[] { "voice_preview_tts.mp3", "voice_preview_tts.wav", "voice_preview_tts.m4a" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return Directory.EnumerateFiles(dir, "voice_preview_tts.*").FirstOrDefault();
    }

    public async Task<(string Rel, string Url)> WriteAsync(
        string projectId,
        string charKey,
        byte[] audioBytes,
        string ext,
        CancellationToken ct = default)
    {
        if (!ext.StartsWith('.')) ext = "." + ext;
        var dir = CharDir(projectId, charKey);
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "voice_preview_tts.*"))
        {
            try { File.Delete(old); } catch { /* ignore */ }
        }
        var dest = Path.Combine(dir, "voice_preview_tts" + ext);
        await File.WriteAllBytesAsync(dest, audioBytes, ct).ConfigureAwait(false);
        var projectDir = await _projects.GetProjectDirAsync(projectId, ct).ConfigureAwait(false);
        var rel = Path.GetRelativePath(projectDir, dest).Replace('\\', '/');
        var url =
            $"{ProjectIdRouting.ProjectApi(projectId)}/characters/{Uri.EscapeDataString(charKey)}/voice/tts-preview";
        return (rel, url);
    }

    public void PersistSeed(
        string projectId,
        string charKey,
        string providerId,
        string providerVoiceId,
        string voiceLabel,
        string voiceProfile)
    {
        _projects.UpdateCharacterSeedText(
            projectId,
            charKey,
            voiceProfile: voiceProfile,
            voiceLabel: voiceLabel,
            voiceProvider: providerId,
            voiceProviderVoiceId: providerVoiceId,
            voiceCloneProviderId: providerVoiceId);
    }

    private string CharDir(string projectId, string charKey) =>
        Path.Combine(_projects.GetProjectDir(projectId), "assets", "characters", Sanitize(charKey));

    internal static string Sanitize(string charKey)
    {
        var k = PageToMovie.Core.Utils.FileNameSanitizer.SanitizeFileName((charKey ?? "").Trim());
        return string.IsNullOrEmpty(k) ? "character" : k;
    }

    public static string DefaultPreviewText(string? previewText) =>
        string.IsNullOrWhiteSpace(previewText)
            ? "True! — nervous — very, very dreadfully nervous I had been and am; but why will you say that I am mad?"
            : previewText.Trim();
}
