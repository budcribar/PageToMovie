using PageToMovie.Engine.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace PageToMovie.Engine;

/// <summary>
/// Composes background-music prompts for scenes (preplanned blueprint prompt, or an AI-composed
/// fallback). Generation and mixing happen elsewhere: FilmJobService's music job calls
/// IAudioClient directly (mirroring IVideoClient) and hands the client a proxy URL to save
/// locally; ffmpeg mixing (concat segments, duck under dialogue, fade out) runs client-side via
/// pagetomovie-ffmpeg.js — the API host never spawns native ffmpeg.
/// </summary>
public sealed class SceneMusicScoringService
{
    private readonly IChatClient _chat;
    private readonly ILogger<SceneMusicScoringService> _log;

    public SceneMusicScoringService(
        IChatClient chat,
        ILogger<SceneMusicScoringService> log)
    {
        _chat = chat;
        _log = log;
    }

    /// <summary>Preplanned blueprint prompt if present, else an AI-composed fallback.</summary>
    public async Task<string> GetOrComposeMusicPromptAsync(
        string projectDir,
        int sceneNumber,
        string screenplayText,
        int durationSeconds,
        string scoringModel,
        CancellationToken ct = default)
    {
        var preplanned = GetPreplannedMusicPrompt(projectDir, sceneNumber);
        if (!string.IsNullOrWhiteSpace(preplanned)) return preplanned;
        return await ComposeSceneMusicPromptAsync(screenplayText, durationSeconds, scoringModel, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// AI-composed singable lyrics for a scene, for the vocal/singing generation path — a sibling to
    /// <see cref="GetOrComposeMusicPromptAsync"/> rather than a flag on it, since the instrumental
    /// prompt's system prompt explicitly forbids vocals (see <see cref="ComposeSceneMusicPromptAsync"/>)
    /// and needs a genuinely different instruction, not a toggle. The scene's existing music
    /// prompt/style (from <see cref="GetOrComposeMusicPromptAsync"/>) is still used for genre/mood —
    /// this only supplies the words to sing.
    /// </summary>
    public async Task<string> ComposeSceneLyricsAsync(
        string screenplayText,
        int durationSeconds,
        string model,
        CancellationToken ct = default)
    {
        var sysPrompt = "You are a Hollywood Film Score Composer writing SUNG LYRICS for a scene's " +
            "background song. Write concise, singable lyrics (verse/chorus structure is fine for " +
            "longer scenes, a single short verse for brief ones) that capture the scene's mood and " +
            "story beat — do not describe the music or instrumentation, and do not include stage " +
            "directions. Output ONLY the lyrics text.";
        var userPrompt = $"Scene Duration: {durationSeconds} seconds.\n\nScreenplay Content:\n{screenplayText}";

        try
        {
            var res = await _chat.CompleteAsync(sysPrompt, userPrompt, model, temperature: 0.5, ct: ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(res))
                return res.Trim().Trim('"');
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to generate AI scene lyrics; using fallback lyrics.");
        }

        return "Somewhere between the shadows and the light, we find our way tonight.";
    }

    public static string? GetPreplannedMusicPrompt(string projectDir, int sceneNumber)
    {
        var blueprintPath = Path.Combine(projectDir, "blueprint.clips.grok.json");
        if (!File.Exists(blueprintPath)) return null;

        try
        {
            var json = File.ReadAllText(blueprintPath);
            var doc = JsonNode.Parse(json);
            if (doc?["scenes"]?.AsArray() is { } scenes)
            {
                foreach (var sc in scenes)
                {
                    if (sc?["scene_number"]?.GetValue<int>() == sceneNumber)
                    {
                        var prompt = sc["music_prompt"]?.GetValue<string>()
                            ?? sc["music_score"]?["prompt"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(prompt))
                            return prompt.Trim();
                    }
                }
            }
        }
        catch
        {
            /* fallback to null */
        }
        return null;
    }

    private async Task<string> ComposeSceneMusicPromptAsync(
        string screenplayText,
        int durationSeconds,
        string model,
        CancellationToken ct)
    {
        var sysPrompt = "You are a Hollywood Film Score Composer. Create a short, highly descriptive music prompt (15-25 words) for an AI music generator based on a film scene. Specify genre, instruments, tempo (BPM), and mood. Do NOT include speech or voice descriptions. Output ONLY the music prompt text.";
        var userPrompt = $"Scene Duration: {durationSeconds} seconds.\n\nScreenplay Content:\n{screenplayText}";

        try
        {
            var res = await _chat.CompleteAsync(sysPrompt, userPrompt, model, temperature: 0.3, ct: ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(res))
                return res.Trim().Trim('"');
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to generate AI scene music prompt; using cinematic fallback prompt.");
        }

        return "Cinematic dark orchestral background score with low cellos, subtle brass swells, tense rhythmic pulse at 90 BPM.";
    }

    public static string GetConfigStr(Dictionary<string, JsonElement>? cfg, string key, string fallback)
    {
        if (cfg is not null && cfg.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var val = el.GetString();
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }
        return fallback;
    }

    public static bool GetConfigBool(Dictionary<string, JsonElement>? cfg, string key, bool fallback)
    {
        if (cfg is not null && cfg.TryGetValue(key, out var el) &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return el.GetBoolean();
        }
        return fallback;
    }

    public static int GetConfigInt(Dictionary<string, JsonElement>? cfg, string key, int fallback)
    {
        if (cfg is not null && cfg.TryGetValue(key, out var el) && el.TryGetInt32(out var v))
        {
            return v;
        }
        return fallback;
    }
}