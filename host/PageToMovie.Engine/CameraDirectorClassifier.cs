using System.Text.Json;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

public sealed record CameraDirective(
    ShotScale ShotScale,
    string LensSpec,
    string CameraMovement,
    string FramingPrompt,
    CameraLens Lens = CameraLens.Lens35mm,
    CameraMovementKind MovementKind = CameraMovementKind.TripodHold,
    CameraAngle Angle = CameraAngle.EyeLevel,
    LightingCondition Lighting = LightingCondition.Daylight)
{
    public ShotAngleType ShotAngleType => Angle switch
    {
        CameraAngle.LowAngle => ShotAngleType.LowAngle,
        CameraAngle.HighAngle => ShotAngleType.HighAngle,
        CameraAngle.BirdEye => ShotAngleType.BirdsEye,
        _ => ShotAngleType.EyeLevel
    };
    public CameraLensSpec LensSpecType => EngineLayerEnumExtensions.ParseCameraLensSpec(LensSpec);
    public CameraMovementPattern MovementPattern => EngineLayerEnumExtensions.ParseCameraMovementPattern(CameraMovement);
    public LightingStyleType LightingStyle => Lighting switch
    {
        LightingCondition.Daylight => LightingStyleType.Daylight,
        LightingCondition.GoldenHour => LightingStyleType.GoldenHour,
        LightingCondition.NeonLight => LightingStyleType.NeonCyberpunk,
        _ => LightingStyleType.NaturalIndoor
    };
}

/// <summary>
/// AI Classifier acting as a Virtuoso Film Director / Director of Photography.
/// Assigns cinematic lens choices, camera movements (push-in, tracking, dolly),
/// and shot framing per beat ID based on narrative emotion.
/// </summary>
public sealed class CameraDirectorClassifier : BeatChatClassifierBase<CameraDirective>
{
    public const string PromptVersion = "v1_product";

    public CameraDirectorClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<CameraDirectorClassifier> log,
        GenerationErrorLogger? errorLogger = null)
        : base(chat, opts.Value, log, errorLogger)
    {
    }

    protected override bool OptionEnabled => _opts.ClassifyCameraDirectorWithChat;
    protected override string DefaultModel => _opts.CameraDirectorClassifyModel;
    protected override string? ChatMode => ChatCallModes.CameraDirectorClassify;
    protected override string OperationName => "stage2_camera_direction";
    protected override string ErrorLoggerName => "camera_director_classifier";
    protected override string LogNoun => "camera director";
    protected override string GetSystemPrompt() => SystemPromptText;
    protected override string ProgressMessage(int beatCount) =>
        $"AI Camera Director: Directing camera lenses & movement for {beatCount} beats…";

    public const string SystemPromptText = """
        You are a Virtuoso Film Director and Director of Photography (DP) directing camera composition and movement.

        Your task: Given a list of scene beats, assign cinematic camera directives per beat ID based on film grammar and narrative tension.

        DIRECTIVES TO ASSIGN PER BEAT:
        1. shot_scale: "wide", "medium", "close_up", or "extreme_close_up".
        2. lens_spec: Choice of lens (e.g. "24mm wide anamorphic lens", "35mm prime lens", "85mm f/1.4 portrait lens", "100mm macro lens").
        3. camera_movement: Specific cinematic movement (e.g. "slow 10% dolly push-in", "locked tripod hold", "low-angle slow tracking shot", "steady handheld tilt").
        4. framing_prompt: A 10–25 word description of the camera shot composition (e.g. "Low-angle medium shot, 35mm lens, camera slowly pushes in as character speaks").

        TWO-SPEAKER BEATS: some beats show a "Then spoken (...)" second line — the clip holds one
        continuous take covering both speakers, not a cut between them. For these, camera_movement
        must describe a pan/reframe move from the first speaker to the second, timed to land on the
        second speaker as they begin their line (e.g. "pan left from Character_A to Character_B,
        settling as B begins speaking"), and framing_prompt must describe a composition that reads
        naturally as it starts on speaker one and ends on speaker two.

        FRAMING & CANVAS RULES:
        - Universal Headroom: Always direct compositions with generous vertical headroom above characters' heads and hair. Never crop foreheads, scalps, or chin edges.
        - Avoid Edge-Crowding: Do not use "filling frame", "fill the frame", or edge-to-edge squeezing. Keep subjects naturally bounded within clean margins.
        - Multi-Height Grounding: When characters of different heights or ground-level animals (e.g. child and pet) appear together, use wide-medium framing with ample headroom so all subjects remain fully visible and comfortably grounded.

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "directives": [
            {
              "beat_id": "b1",
              "shot_scale": "wide",
              "lens_spec": "24mm wide anamorphic lens",
              "camera_movement": "locked tripod establishing shot",
              "framing_prompt": "Establishing wide shot, 24mm anamorphic lens, static locked camera framing subject centrally with ample headroom."
            },
            ...
          ]
        }
        """;

    public Task<Dictionary<string, CameraDirective>?> ClassifySceneCameraAsync(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats, Action<string>? onProgress = null, CancellationToken ct = default, string? model = null) => ClassifyAsync(scene, beats, onProgress, ct, model);

    protected override string BeatsHeading => "BEATS TO DIRECT:";

    protected override string BuildUserPrompt(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"SCENE {scene.GetValueOrDefault("scene_number")}: {scene.GetValueOrDefault("setting")}");

        var targetAspectRatio = scene.GetValueOrDefault("target_aspect_ratio")?.ToString();
        var visualMedium = scene.GetValueOrDefault("visual_medium")?.ToString();
        if (!string.IsNullOrWhiteSpace(targetAspectRatio) || !string.IsNullOrWhiteSpace(visualMedium))
        {
            sb.AppendLine("CANVAS & FORMAT:");
            if (!string.IsNullOrWhiteSpace(targetAspectRatio))
            {
                var guideline = targetAspectRatio switch
                {
                    "4:3" => "Classic 4:3 storybook frame: maintain generous vertical headroom above characters' heads and full grounding for children/animals.",
                    "1:1" => "Square 1:1 frame: balanced vertical/horizontal centering with ample top headroom.",
                    "9:16" => "Vertical 9:16 frame: vertical breathing room with ample horizontal shoulder margins.",
                    _ => "Widescreen 16:9 frame: wide horizontal staging with natural vertical headroom."
                };
                sb.AppendLine($"  Target Aspect Ratio: {targetAspectRatio} ({guideline})");
            }
            if (!string.IsNullOrWhiteSpace(visualMedium))
                sb.AppendLine($"  Visual Medium: {visualMedium}");
        }

        sb.AppendLine();
        sb.AppendLine(BeatsHeading);

        foreach (var b in beats)
            AppendBeat(sb, b);

        return sb.ToString();
    }

    protected override void AppendBeat(System.Text.StringBuilder sb, Dictionary<string, object?> b)
    {
        var (id, action, spk, dlg) = ReadBeatCore(b);
        var ac = b.GetValueOrDefault("action_class") ?? "";
        var spk2 = b.GetValueOrDefault("secondary_speaker") ?? "";
        var dlg2 = b.GetValueOrDefault("secondary_dialogue") ?? "";
        sb.AppendLine($"Beat '{id}' (class: {ac}):");
        AppendSpoken(sb, spk, dlg);
        if (!string.IsNullOrWhiteSpace(spk2.ToString()) || !string.IsNullOrWhiteSpace(dlg2.ToString()))
            sb.AppendLine($"  Then spoken ({spk2}): \"{dlg2}\"");
        AppendActionProse(sb, action);
    }

    protected override Dictionary<string, CameraDirective>? ParseResponse(string rawJson) =>
        ClassifierDirectiveJson.ParseKeyedArray(rawJson, "directives", MapCameraItem, _log, "camera director");

    private static (string? Id, CameraDirective Value)? MapCameraItem(JsonElement item)
    {
        var id = item.GetStringProp("beat_id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var scaleStr = item.GetStringProp("shot_scale", ShotScale.Medium.ToSnakeCase());
        var lensStr = item.GetStringProp("lens_spec", "35mm lens");
        var moveStr = item.GetStringProp("camera_movement", "locked tripod");
        return (id, new CameraDirective(
            ShotScaleExtensions.ParseShotScale(scaleStr, ShotScale.Medium),
            lensStr, moveStr, item.GetStringProp("framing_prompt"),
            MediaEngineEnumExtensions.ParseCameraLens(lensStr),
            MediaEngineEnumExtensions.ParseCameraMovementKind(moveStr),
            ParseCameraAngle(item.GetStringProp("camera_angle", "eye_level")),
            ParseLightingCondition(item.GetStringProp("lighting_condition", "daylight"))));
    }

    public static CameraAngle ParseCameraAngle(string? input) => input?.ToLowerInvariant() switch
    {
        "low" or "low_angle" or "lowangle" => CameraAngle.LowAngle,
        "high" or "high_angle" or "highangle" => CameraAngle.HighAngle,
        "bird" or "bird_eye" or "birdeye" or "birds_eye" => CameraAngle.BirdEye,
        _ => CameraAngle.EyeLevel
    };

    public static LightingCondition ParseLightingCondition(string? input) => input?.ToLowerInvariant() switch
    {
        "night" or "night_interior" or "nightinterior" => LightingCondition.NightInterior,
        "golden" or "golden_hour" or "goldenhour" => LightingCondition.GoldenHour,
        "neon" or "neon_light" or "neonlight" => LightingCondition.NeonLight,
        _ => LightingCondition.Daylight
    };
}
