using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;

using PageToMovie.Core.Utils;
namespace PageToMovie.Engine;

/// <summary>
/// Non-destructively augments an existing blueprint.clips.grok.json with AI-planned music score prompts
/// (music_score: prompt, genre, mood, tempo) based on full screenplay and scene context.
/// </summary>
public sealed class SceneMusicCompositionService
{
    private readonly IVisionClient _vision;
    private readonly ILogger<SceneMusicCompositionService> _log;

    public SceneMusicCompositionService(
        IVisionClient vision,
        ILogger<SceneMusicCompositionService> log)
    {
        _vision = vision;
        _log = log;
    }

    public async Task<bool> AugmentProjectMusicAsync(
        string projectDir,
        string? userModel = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(projectDir))
        {
            _log.LogWarning("Project directory {Dir} does not exist for music augmentation", projectDir);
            return false;
        }

        var bpPath = Path.Combine(projectDir, "blueprint.clips.grok.json");
        if (!File.Exists(bpPath))
        {
            bpPath = Path.Combine(projectDir, "assets", "blueprint.clips.grok.json");
            if (!File.Exists(bpPath))
            {
                _log.LogWarning("blueprint.clips.grok.json not found under {Dir}", projectDir);
                return false;
            }
        }

        var jsonText = await File.ReadAllTextAsync(bpPath, ct).ConfigureAwait(false);
        var rootNode = JsonNode.Parse(jsonText);
        if (rootNode is not JsonObject rootObj || !rootObj.TryGetPropertyValue("scenes", out var scenesNode) || scenesNode is not JsonArray scenesArray)
        {
            _log.LogWarning("Invalid blueprint structure in {Path} — missing scenes array", bpPath);
            return false;
        }

        // 1. Gather screenplay context
        var screenplayContext = "";
        var fountainPath = Path.Combine(projectDir, "screenplay.fountain");
        if (File.Exists(fountainPath))
        {
            screenplayContext = await File.ReadAllTextAsync(fountainPath, ct).ConfigureAwait(false);
        }
        else
        {
            var bookPath = Path.Combine(projectDir, "book_full.txt");
            if (File.Exists(bookPath))
            {
                screenplayContext = await File.ReadAllTextAsync(bookPath, ct).ConfigureAwait(false);
            }
        }

        if (screenplayContext.Length > 8000)
        {
            screenplayContext = screenplayContext[..8000] + "\n...(truncated)";
        }

        // 2. Build scene summaries for prompt
        var sceneSummaries = new List<object>();
        foreach (var sNode in scenesArray)
        {
            if (sNode is not JsonObject sObj) continue;
            var sn = sObj["scene_number"]?.GetValue<int>() ?? 0;
            var slug = sObj["slugline"]?.GetValue<string>() ?? sObj["heading"]?.GetValue<string>() ?? "";
            var text = sObj["content"]?.GetValue<string>() ?? sObj["script_text"]?.GetValue<string>() ?? "";
            sceneSummaries.Add(new { scene_number = sn, slugline = slug, summary = text });
        }

        var model = string.IsNullOrWhiteSpace(userModel)
            ? throw new InvalidOperationException(
                "Music composition: Script & planning model not set. Open Settings and choose a model.")
            : userModel.Trim();
        model = ProjectModelSelection.RequireExplicit(model, ModelCapability.Chat, "Music composition");
        var promptText =
            "You are an expert film music supervisor and AI audio score composer.\n" +
            "Analyze the screenplay context and scene list below and compose a cohesive, story-driven background music score direction for EVERY scene.\n" +
            "Requirements:\n" +
            "1. Maintain a unifying musical theme / leitmotif across the entire film that evolves with the narrative arc.\n" +
            "2. For each scene, generate:\n" +
            "   - 'prompt': A detailed, highly descriptive prompt suitable for AI music generator (Stable Audio / MusicGen), describing instrumentation, rhythm, texture, and emotional energy.\n" +
            "   - 'genre': Musical genre / style (e.g. 'Psychological Thriller Orchestral', 'Ambient Dark Drone').\n" +
            "   - 'mood': Emotional mood (e.g. 'Paranoid, Tense, Obsessive').\n" +
            "   - 'tempo': Tempo / rhythm (e.g. '90 BPM, accelerating pulse').\n" +
            "3. Output ONLY a strict JSON object mapping scene number (integer key) to its music object. Example:\n" +
            "{\n" +
            "  \"1\": { \"prompt\": \"Cinematic dark orchestral score with low cello pulse...\", \"genre\": \"Thriller\", \"mood\": \"Tense\", \"tempo\": \"80 BPM\" }\n" +
            "}\n\n" +
            "Screenplay Context:\n" + screenplayContext + "\n\n" +
            "Scenes:\n" + JsonSerializer.Serialize(sceneSummaries);

        _log.LogInformation("Requesting music score composition for {Count} scenes in {Project}", scenesArray.Count, Path.GetFileName(projectDir));

        var responseText = await _vision.CompleteWithImagesAsync(promptText, Array.Empty<string>(), model, detail: "low", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            _log.LogWarning("LLM returned empty response for music composition");
            return false;
        }

        // Clean JSON markdown codeblocks if present
        var cleanedJson = responseText.Trim();
        if (cleanedJson.StartsWith("```"))
        {
            var match = CommonRegex.Match(cleanedJson, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                cleanedJson = match.Groups[1].Value.Trim();
            }
        }

        Dictionary<int, MusicScoreInfo>? scoreMap = null;
        try
        {
            var rawMap = JsonSerializer.Deserialize<Dictionary<string, MusicScoreInfo>>(cleanedJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (rawMap != null)
            {
                scoreMap = new Dictionary<int, MusicScoreInfo>();
                foreach (var (k, v) in rawMap)
                {
                    if (int.TryParse(k, out var snKey))
                    {
                        scoreMap[snKey] = v;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse music score JSON map from LLM response");
            return false;
        }

        if (scoreMap is null || scoreMap.Count == 0)
        {
            _log.LogWarning("No scene music scores were parsed from response");
            return false;
        }

        // 3. Non-destructively augment scenesArray
        var modifiedCount = 0;
        foreach (var sNode in scenesArray)
        {
            if (sNode is not JsonObject sObj) continue;
            var sn = sObj["scene_number"]?.GetValue<int>() ?? 0;
            if (sn > 0 && scoreMap.TryGetValue(sn, out var info))
            {
                var scoreObj = new JsonObject
                {
                    ["prompt"] = info.Prompt ?? "",
                    ["genre"] = info.Genre ?? "",
                    ["mood"] = info.Mood ?? "",
                    ["tempo"] = info.Tempo ?? ""
                };

                sObj["music_score"] = scoreObj;
                sObj["music_prompt"] = info.Prompt ?? "";
                modifiedCount++;
            }
        }

        // 4. Save updated blueprint to disk
        var options = new JsonSerializerOptions { WriteIndented = true };
        var updatedJson = rootNode.ToJsonString(options);
        await File.WriteAllTextAsync(bpPath, updatedJson, ct).ConfigureAwait(false);

        _log.LogInformation("Successfully augmented {Count} scenes with music scores in {Path}", modifiedCount, bpPath);
        return true;
    }
}
