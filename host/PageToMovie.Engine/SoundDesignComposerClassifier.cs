using PageToMovie.Core.Options;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

public sealed record SoundDesignDirective(
    string AmbientLayer,
    string FoleyLayer,
    string ScoreLayer);

/// <summary>
/// AI Classifier acting as a Film Sound Designer & Audio Director.
/// Composes 3-track cinematic audio blueprints (ambient background, physical foley, score mood)
/// per beat for audio synthesis (client stitch/export; no server remux).
/// </summary>
public sealed class SoundDesignComposerClassifier : BeatChatClassifierBase<SoundDesignDirective>
{
    public const string PromptVersion = "v1_product";

    public SoundDesignComposerClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<SoundDesignComposerClassifier> log,
        GenerationErrorLogger? errorLogger = null)
        : base(chat, opts.Value, log, errorLogger)
    {
    }

    protected override bool OptionEnabled => _opts.ClassifySoundDesignComposerWithChat;
    protected override string DefaultModel => _opts.SoundDesignComposerClassifyModel;
    protected override string? ChatMode => ChatCallModes.SoundDesignComposerClassify;
    protected override string OperationName => "stage2_sound_design";
    protected override string ErrorLoggerName => "sound_design_composer_classifier";
    protected override string LogNoun => "sound design";
    protected override string GetSystemPrompt() => SystemPrompt();
    protected override string ProgressMessage(int beatCount) =>
        $"AI Sound Director: Composing 3-layer sound design for {beatCount} beats…";

    public static string SystemPrompt() => """
        You are an expert film Sound Designer and Audio Supervisor creating multi-track cinematic sound designs.

        Your task: Given a list of scene beats, compose 3 distinct audio layers per beat ID:

        LAYERS TO BUILD PER BEAT:
        1. ambient_layer: Environmental acoustics & background ambience (e.g. "Heavy wind howling outside with room reverb 0.4").
        2. foley_layer: Specific physical contact and movement sound effects (e.g. "Creaking wooden floorboards under deliberate slow footsteps").
        3. score_layer: Musical mood, tonal texture, or rhythmic pulse (e.g. "Low sub-bass cello drone rising to an 80 BPM heartbeat pulse").

        RULES:
        - Keep each layer description concise (5–15 words).
        - Ensure layers reflect the scene's emotional tension and setting.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "sound_design": [
            {
              "beat_id": "b1",
              "ambient_layer": "Quiet night room room-tone with distant howling wind",
              "foley_layer": "Subtle rustle of clothes, quiet rhythmic breathing",
              "score_layer": "Tense low-frequency dark ambient drone"
            },
            ...
          ]
        }
        """;

    public Task<Dictionary<string, SoundDesignDirective>?> ClassifySceneSoundDesignAsync(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats, Action<string>? onProgress = null, CancellationToken ct = default, string? model = null) => ClassifyAsync(scene, beats, onProgress, ct, model);

    protected override string BeatsHeading => "BEATS TO COMPOSE SOUND FOR:";

    protected override void AppendBeat(System.Text.StringBuilder sb, Dictionary<string, object?> b)
    {
        var (id, action, spk, dlg) = ReadBeatCore(b);
        var amb = b.GetValueOrDefault("ambient") ?? "";
        var sfx = b.GetValueOrDefault("sfx") ?? "";
        sb.AppendLine($"Beat '{id}':");
        AppendSpoken(sb, spk, dlg);
        AppendActionProse(sb, action);
        if (!string.IsNullOrWhiteSpace(amb.ToString()))
            sb.AppendLine($"  Base ambient: {amb}");
        if (!string.IsNullOrWhiteSpace(sfx.ToString()))
            sb.AppendLine($"  Base SFX: {sfx}");
    }

    protected override Dictionary<string, SoundDesignDirective>? ParseResponse(string rawJson) =>
        ClassifierDirectiveJson.ParseKeyedArray(rawJson, "sound_design", item => ClassifierDirectiveJson.MapThreeStringFields(item, "ambient_layer", "foley_layer", "score_layer", static (a, f, s) => new SoundDesignDirective(a, f, s)), _log, "sound design");
}
