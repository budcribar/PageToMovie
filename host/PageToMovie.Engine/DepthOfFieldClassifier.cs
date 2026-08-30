using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
using PageToMovie.Core.Utils;
using PageToMovie.Engine.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PageToMovie.Engine.ModelBacked;

public sealed record DepthOfFieldDirective(
    string Aperture,
    string FocalPlane,
    string RackFocus);

/// <summary>
/// AI Classifier acting as a Focus Puller & Optical Cinematographer.
/// Assigns optical aperture settings (f/1.4 to f/8), primary focal planes, and dynamic
/// rack-focus transitions per shot to guide viewer attention.
/// </summary>
public sealed class DepthOfFieldClassifier : BeatChatClassifierBase<DepthOfFieldDirective>
{
    public const string PromptVersion = "v2_fstop_only";

    public DepthOfFieldClassifier(
        IChatClient chat,
        IOptions<PageToMovieOptions> opts,
        ILogger<DepthOfFieldClassifier> log,
        GenerationErrorLogger? errorLogger = null)
        : base(chat, opts.Value, log, errorLogger)
    {
    }

    protected override bool OptionEnabled => _opts.ClassifyDepthOfFieldWithChat;
    protected override string DefaultModel => _opts.DepthOfFieldClassifyModel;
    protected override string? ChatMode => ChatCallModes.DepthOfFieldClassify;
    protected override string OperationName => "stage2_depth_of_field";
    protected override string ErrorLoggerName => "depth_of_field_classifier";
    protected override string LogNoun => "depth of field";
    protected override string GetSystemPrompt() => SystemPrompt();
    protected override string ProgressMessage(int beatCount) =>
        $"AI Focus Puller: Directing optical aperture & rack focus for {beatCount} beats…";

    public static string SystemPrompt() => """
        You are an expert Focus Puller and Optical Cinematographer directing aperture.

        Your task: Given a list of scene beats, assign an f-stop per beat ID. Camera owns framing, lens, and move.
        <Optics> is the f-stop only — do not describe depth of field or bokeh.

        DIRECTIVES TO ASSIGN PER BEAT:
        1. aperture: f-stop only (e.g. "f/1.4", "f/2.8", "f/8"). No prose.
        2. focal_plane: Primary subject focus target (e.g. "Foreground: lantern latch", "Midground: Narrator's eyes", "Background: closed bedroom door").
        3. rack_focus: Focus transition instruction, if any (e.g. "Rack focus from foreground lantern latch at t=0s to Old Man's eyes in background at t=2s", "Static focus on narrator").

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "dof": [
            {
              "beat_id": "b1",
              "aperture": "f/1.4",
              "focal_plane": "Midground: Narrator's eyes",
              "rack_focus": "Static focus on narrator's eyes"
            },
            ...
          ]
        }
        """;

    public Task<Dictionary<string, DepthOfFieldDirective>?> ClassifySceneDepthOfFieldAsync(Dictionary<string, object?> scene, List<Dictionary<string, object?>> beats, Action<string>? onProgress = null, CancellationToken ct = default, string? model = null) => ClassifyAsync(scene, beats, onProgress, ct, model);

    protected override string BeatsHeading => "BEATS TO DIRECT OPTICALLY:";

    protected override void AppendBeat(System.Text.StringBuilder sb, Dictionary<string, object?> b)
    {
        var (id, action, _, dlg) = ReadBeatCore(b);
        var psub = b.GetValueOrDefault("primary_subject") ?? "";
        sb.AppendLine($"Beat '{id}' (subject: {psub}):");
        if (!string.IsNullOrWhiteSpace(dlg.ToString()))
            sb.AppendLine($"  Spoken: \"{dlg}\"");
        AppendActionProse(sb, action);
    }

    protected override Dictionary<string, DepthOfFieldDirective>? ParseResponse(string rawJson) =>
        ClassifierDirectiveJson.ParseKeyedArray(rawJson, "dof", item => ClassifierDirectiveJson.MapThreeStringFields(item, "aperture", "focal_plane", "rack_focus", static (a, fp, rf) => new DepthOfFieldDirective(SanitizeAperture(a), fp, rf)), _log, "depth of field");

    /// <summary>Keep the f-stop; drop "shallow depth of field" / bokeh so Optics does not rewrite Camera.</summary>
    public static string SanitizeAperture(string? aperture)
    {
        if (string.IsNullOrWhiteSpace(aperture))
            return "";
        var m = CommonRegex.Match(aperture, @"f\s*/\s*\d+(?:\.\d+)?", RegexOptions.IgnoreCase);
        return m.Success ? CommonRegex.Replace(m.Value, @"\s+", "") : "";
    }
}
