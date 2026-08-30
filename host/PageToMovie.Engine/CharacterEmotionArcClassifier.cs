using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

public sealed record EmotionDirective(
    int Intensity,
    string MicroExpression,
    string ActingPrompt);

/// <summary>
/// AI Classifier acting as an Acting Coach & Performance Director.
/// Calculates emotional intensity (1–10 scale) and facial micro-expressions
/// per beat for each character on screen, driving acting performances in video generation.
/// </summary>
public sealed class CharacterEmotionArcClassifier : BeatChatClassifierBase<EmotionDirective>
{
    public const string PromptVersion = "v2_performance_lock";

    public CharacterEmotionArcClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<CharacterEmotionArcClassifier> log,
        GenerationErrorLogger? errorLogger = null)
        : base(chat, opts.Value, log, errorLogger)
    {
    }

    protected override bool OptionEnabled => _opts.ClassifyCharacterEmotionArcWithChat;
    protected override string DefaultModel => _opts.CharacterEmotionArcClassifyModel;
    protected override string? ChatMode => ChatCallModes.CharacterEmotionArcClassify;
    protected override string OperationName => "stage2_character_emotion_arc";
    protected override string ErrorLoggerName => "character_emotion_arc_classifier";
    protected override string LogNoun => "character emotion arc";
    protected override string GetSystemPrompt() => SystemPrompt;
    protected override string ProgressMessage(int beatCount) =>
        $"AI Acting Coach: Directing emotional intensity & micro-acting for {beatCount} beats…";

    public const string SystemPrompt = """
        You are an expert film Acting Coach and Performance Director directing character micro-acting.
        You are the only writer of the clip <Performance> tag. Action is bodies and blocking, not gaze.

        Your task: Given a list of scene beats, determine the emotional intensity (1 to 10 scale) and facial micro-expressions per beat ID.

        FILM PERFORMANCE LOCK:
        The user prompt includes this film's PERFORMANCE LOCK (default address: who they look at).
        Honor that default for gaze and address. Intensity and micro-expression may change with the beat.
        Do not flip who they are talking to (lens vs scene / partner) unless this beat's action or dialogue actually changes address.
        Do not write "look down the lens" / "address the viewer" when the lock is objective / observational.
        Do not write a look-away-from-camera address (face a place, not the lens) when the lock is confessional / viewer-address.

        DIRECTIVES TO ASSIGN PER BEAT:
        1. intensity: Integer scale 1 (calm/neutral) to 10 (extreme panic/rage/terror).
        2. micro_expression: Specific facial muscle movement (e.g. "feverishly intense wide-eyed stare, tight unnatural smile, jaw muscle twitch").
        3. acting_prompt: Concise 10–20 word performance instruction (e.g. "Acting intensity 8/10: Feverishly intense wide-eyed stare with tight unnatural smile"). Honor the film PERFORMANCE LOCK for gaze.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "emotions": [
            {
              "beat_id": "b1",
              "intensity": 8,
              "micro_expression": "Feverishly intense wide-eyed stare, tight unnatural smile, jaw muscle twitch",
              "acting_prompt": "Acting intensity 8/10: Feverishly intense wide-eyed stare with tight unnatural smile"
            },
            ...
          ]
        }
        """;

    public Task<Dictionary<string, EmotionDirective>?> ClassifySceneEmotionAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null) => ClassifyAsync(scene, beats, onProgress, ct, model);

    protected override string BeatsHeading => "BEATS TO DIRECT:";

    protected override string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats)
    {
        var sb = new System.Text.StringBuilder();
        AppendPerformanceLock(sb, scene);
        sb.Append(base.BuildUserPrompt(scene, beats));
        return sb.ToString();
    }

    private static void AppendPerformanceLock(System.Text.StringBuilder sb, Dictionary<string, object?> scene)
    {
        var lockText = scene.GetValueOrDefault(JsonKeys.PerformanceLock)?.ToString();
        if (string.IsNullOrWhiteSpace(lockText))
            return;
        sb.AppendLine("FILM PERFORMANCE LOCK (default address — honor unless this beat changes address):");
        sb.AppendLine($"  {lockText.Trim()}");
        sb.AppendLine();
    }

    protected override Dictionary<string, EmotionDirective>? ParseResponse(string rawJson)
    {
        try
        {
            var cleaned = ClassifierJsonParser.StripFences(rawJson);
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("emotions", out var emoArray) ||
                emoArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, EmotionDirective>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in emoArray.EnumerateArray())
            {
                var id = item.GetStringProp("beat_id");
                var intensity = Math.Clamp(item.GetIntProp("intensity", 5), 1, 10);
                var micro = item.GetStringProp("micro_expression");
                var prompt = item.GetStringProp("acting_prompt");

                if (!string.IsNullOrWhiteSpace(id))
                {
                    result[id] = new EmotionDirective(intensity, micro, prompt);
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI character emotion response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
