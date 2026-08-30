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
    public const string PromptVersion = "v2_camera_ssot";

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
        You are the only writer of the clip <Camera> tag. Action is bodies and blocking, not where the camera sits.

        Your task: Given a list of scene beats, assign cinematic camera directives per beat ID based on film grammar and narrative tension.

        DIRECTIVES TO ASSIGN PER BEAT:
        1. shot_scale: "wide", "medium", "close_up", or "extreme_close_up".
        2. lens_spec: Choice of lens (e.g. "24mm wide anamorphic lens", "35mm prime lens", "85mm portrait lens", "100mm macro lens"). Do NOT include an f-stop or aperture.
        3. camera_movement: Specific cinematic movement (e.g. "slow 10% dolly push-in", "locked tripod hold", "low-angle slow tracking shot", "steady handheld tilt").
        4. framing_prompt: A 10–25 word description of composition, lens, and move only
           (e.g. "Low-angle medium shot, 35mm lens, camera slowly pushes in as character speaks").
           Do NOT name an f-stop, aperture, depth of field, or bokeh — Optics owns aperture.

        HONOR ACTION / BLOCKING CAMERA LANGUAGE:
        If the beat's action or blocking already names the camera (OTS, back to camera, camera behind,
        wide, MCU, a lens, push-in, establishing, etc.), matching that is mandatory. Do not stack a
        medium push-in on a beat that is already a wide or a back-to-camera hold.

        SAME-SPEAKER RUNS:
        Consecutive beats that share a speaker are one talking-head run. The user prompt marks those
        beats with the previous beat id and any previous shot_scale / lens / Camera hint.
        Vary that previous framing rather than repeating the same medium: Medium → closer → hands
        (or another motivated size), unless this beat's action already names the camera.
        Never invent an over-the-shoulder / OTS when On-screen bodies is fewer than 2.

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
        AppendCanvasAndFormat(sb, scene);
        sb.AppendLine();
        sb.AppendLine(BeatsHeading);
        AppendDirectedBeats(sb, scene, beats);
        return sb.ToString();
    }

    private static void AppendCanvasAndFormat(System.Text.StringBuilder sb, Dictionary<string, object?> scene)
    {
        var targetAspectRatio = scene.GetValueOrDefault("target_aspect_ratio")?.ToString();
        var visualMedium = scene.GetValueOrDefault("visual_medium")?.ToString();
        if (string.IsNullOrWhiteSpace(targetAspectRatio) && string.IsNullOrWhiteSpace(visualMedium))
            return;

        sb.AppendLine("CANVAS & FORMAT:");
        if (!string.IsNullOrWhiteSpace(targetAspectRatio))
            sb.AppendLine($"  Target Aspect Ratio: {targetAspectRatio} ({AspectRatioGuideline(targetAspectRatio)})");
        if (!string.IsNullOrWhiteSpace(visualMedium))
            sb.AppendLine($"  Visual Medium: {visualMedium}");
    }

    private static string AspectRatioGuideline(string targetAspectRatio) => targetAspectRatio switch
    {
        "4:3" => "Classic 4:3 storybook frame: maintain generous vertical headroom above characters' heads and full grounding for children/animals.",
        "1:1" => "Square 1:1 frame: balanced vertical/horizontal centering with ample top headroom.",
        "9:16" => "Vertical 9:16 frame: vertical breathing room with ample horizontal shoulder margins.",
        _ => "Widescreen 16:9 frame: wide horizontal staging with natural vertical headroom."
    };

    private static void AppendDirectedBeats(
        System.Text.StringBuilder sb,
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats)
    {
        var prev = default(PreviousCameraHint);
        foreach (var b in beats)
            prev = AppendDirectedBeatAndAdvance(sb, scene, b, prev);
    }

    private static PreviousCameraHint AppendDirectedBeatAndAdvance(
        System.Text.StringBuilder sb,
        Dictionary<string, object?> scene,
        Dictionary<string, object?> b,
        PreviousCameraHint prev)
    {
        var (idObj, _, spkObj, dlgObj) = ReadBeatCore(b);
        var id = idObj?.ToString() ?? "";
        var spk = spkObj?.ToString() ?? "";
        var dlg = dlgObj?.ToString() ?? "";
        if (prev.IsSameSpeakerRun(spk, dlg))
            AppendDirectedBeat(sb, scene, b, prev.Id, prev.Scale, prev.Lens, prev.Camera);
        else
            AppendDirectedBeat(sb, scene, b, null, null, null, null);
        return PreviousCameraHint.FromBeat(b, id, spk, dlg);
    }

    protected override void AppendBeat(System.Text.StringBuilder sb, Dictionary<string, object?> b) =>
        AppendDirectedBeat(sb, scene: null, b, null, null, null, null);

    private static void AppendDirectedBeat(
        System.Text.StringBuilder sb,
        Dictionary<string, object?>? scene,
        Dictionary<string, object?> b,
        string? previousBeatId,
        string? previousScale,
        string? previousLens,
        string? previousCamera)
    {
        var (id, action, spk, dlg) = ReadBeatCore(b);
        var ac = b.GetValueOrDefault("action_class") ?? "";
        var spk2 = b.GetValueOrDefault("secondary_speaker") ?? "";
        var dlg2 = b.GetValueOrDefault("secondary_dialogue") ?? "";
        sb.AppendLine($"Beat '{id}' (class: {ac}):");
        if (!string.IsNullOrWhiteSpace(previousBeatId))
        {
            sb.AppendLine(
                $"  Same-speaker run after beat '{previousBeatId}'. Vary shot_scale / lens / framing_prompt from that previous assignment (Medium → closer → hands) unless this beat's action already names the camera.");
            if (!string.IsNullOrWhiteSpace(previousScale))
                sb.AppendLine($"  Previous shot_scale: {previousScale}");
            if (!string.IsNullOrWhiteSpace(previousLens))
                sb.AppendLine($"  Previous lens: {previousLens}");
            if (!string.IsNullOrWhiteSpace(previousCamera))
                sb.AppendLine($"  Previous Camera: {previousCamera}");
        }
        var bodies = CountOnScreenBodies(scene, b);
        sb.AppendLine(bodies < 2
            ? $"  On-screen bodies: {bodies} (do not invent OTS — no listener)"
            : $"  On-screen bodies: {bodies}");
        AppendSpoken(sb, spk, dlg);
        if (!string.IsNullOrWhiteSpace(spk2.ToString()) || !string.IsNullOrWhiteSpace(dlg2.ToString()))
            sb.AppendLine($"  Then spoken ({spk2}): \"{dlg2}\"");
        AppendActionProse(sb, action);
        var blocking = b.GetValueOrDefault("blocking_notes");
        if (!string.IsNullOrWhiteSpace(blocking?.ToString()))
            sb.AppendLine($"  Blocking: {blocking}");
    }

    internal static int CountOnScreenBodies(Dictionary<string, object?>? scene, Dictionary<string, object?> beat)
    {
        if (TryCountKeys(beat.GetValueOrDefault("characters_on_screen"), out var n) && n > 0)
            return n;
        if (scene is not null && TryCountKeys(scene.GetValueOrDefault("characters_on_screen"), out n))
            return n;
        return 1;
    }

    private static bool TryCountKeys(object? raw, out int count)
    {
        count = 0;
        if (raw is System.Collections.IEnumerable seq and not string)
        {
            foreach (var item in seq)
            {
                if (!string.IsNullOrWhiteSpace(item?.ToString()))
                    count++;
            }
            return true;
        }
        return false;
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
            CameraTagWriter.SanitizeCameraProse(lensStr),
            moveStr,
            CameraTagWriter.SanitizeCameraProse(item.GetStringProp("framing_prompt")),
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

    private readonly record struct PreviousCameraHint(
        string? Id,
        string? Speaker,
        bool HadSpeech,
        string? Scale,
        string? Lens,
        string? Camera)
    {
        public static PreviousCameraHint FromBeat(Dictionary<string, object?> b, string id, string spk, string dlg)
        {
            var hadSpeech = !string.IsNullOrWhiteSpace(dlg);
            return new(
                id,
                hadSpeech ? spk : null,
                hadSpeech,
                b.GetValueOrDefault("shot_scale_hint")?.ToString(),
                b.GetValueOrDefault("lens_spec")?.ToString(),
                b.GetValueOrDefault("framing_prompt")?.ToString()
                    ?? b.GetValueOrDefault("camera")?.ToString());
        }

        public bool IsSameSpeakerRun(string spk, string dlg) =>
            HadSpeech
            && !string.IsNullOrWhiteSpace(spk)
            && !string.IsNullOrWhiteSpace(dlg)
            && string.Equals(spk, Speaker, StringComparison.OrdinalIgnoreCase);
    }
}
