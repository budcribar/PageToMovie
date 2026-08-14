using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Writes the small per-take metadata sidecar alongside a scene's background-audio segments — the
/// audio equivalent of <see cref="ClipSidecarService"/>. The segment .wav bytes themselves are never
/// stored server-side (client-storage-primary), but this metadata (model, vocal/instrumental, the
/// composed prompt/lyrics, which segment files belong to the take) is cheap enough to keep here so
/// <c>ProjectStore.GetMusicVersionsAsync</c> can list and compare past generations.
/// </summary>
public sealed class MusicSidecarService
{
    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.IndentedCaseInsensitive;
    private static readonly byte[] NewLineBytes = { (byte)'\n' };

    private readonly ILogger<MusicSidecarService> _log;

    public MusicSidecarService(ILogger<MusicSidecarService>? log = null)
    {
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicSidecarService>.Instance;
    }

    /// <summary>
    /// Write/overwrite the active sidecar for a scene's audio (<c>assets/music/scene_XX.meta.json</c>)
    /// — call once per completed generation run, after all of its segments were produced.
    /// </summary>
    public async Task WriteActiveSidecarAsync(
        string projectDir,
        int scene,
        string takeId,
        string model,
        bool isVocal,
        string prompt,
        string? lyrics,
        IReadOnlyList<string> segmentFileNames,
        CancellationToken ct = default)
    {
        var musicDir = Path.Combine(projectDir, "assets", "music");
        Directory.CreateDirectory(musicDir);
        var sidecarPath = Path.Combine(musicDir, $"scene_{scene:D2}.meta.json");
        await WriteSidecarAsync(
            sidecarPath,
            new MusicSidecarCore(projectDir, scene, takeId, model, isVocal, prompt, lyrics, segmentFileNames),
            ct).ConfigureAwait(false);
        _log.LogInformation("Written music sidecar manifest → {Path}", sidecarPath);
    }

    private sealed record MusicSidecarCore(
        string ProjectDir,
        int Scene,
        string TakeId,
        string Model,
        bool IsVocal,
        string Prompt,
        string? Lyrics,
        IReadOnlyList<string> SegmentFileNames);

    private static async Task WriteSidecarAsync(
        string sidecarPath,
        MusicSidecarCore core,
        CancellationToken ct)
    {
        var projectId = Path.GetFileName(core.ProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var sidecar = new Dictionary<string, object?>
        {
            ["schema_version"] = "music_sidecar.v1",
            ["project_id"] = projectId,
            ["scene"] = core.Scene,
            ["take_id"] = core.TakeId,
            ["model"] = core.Model ?? "",
            ["is_vocal"] = core.IsVocal,
            ["prompt"] = core.Prompt ?? "",
            ["lyrics"] = core.Lyrics,
            ["segment_file_names"] = core.SegmentFileNames,
            ["created_at_utc"] = DateTime.UtcNow.ToString("o"),
        };

        await using var stream = new FileStream(sidecarPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, sidecar, JsonOpts, ct).ConfigureAwait(false);
        await stream.WriteAsync(NewLineBytes, ct).ConfigureAwait(false);
    }
}
