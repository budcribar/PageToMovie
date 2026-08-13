using System.Text.Json;
using System.Text.RegularExpressions;
using PageToMovie.Core.Options;
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
    public const string PromptVersion = "v1_product";

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
        You are an expert Focus Puller and Optical Cinematographer directing camera focus and depth of field.

        Your task: Given a list of scene beats and camera directives, assign optical focus specifications per beat ID:

        DIRECTIVES TO ASSIGN PER BEAT:
        1. aperture: f-stop spec (e.g. "f/1.4 shallow depth of field, creamy background bokeh", "f/2.8 moderate depth of field", "f/8 deep focus, sharp environment").
        2. focal_plane: Primary subject focus target (e.g. "Foreground: lantern latch", "Midground: Narrator's eyes", "Background: closed bedroom door").
        3. rack_focus: Focus transition instruction, if any (e.g. "Rack focus from foreground lantern latch at t=0s to Old Man's eyes in background at t=2s", "Static focus on narrator").

        OUTPUT FORMAT:
        Return ONLY valid JSON matching this schema:
        {
          "dof": [
            {
              "beat_id": "b1",
              "aperture": "f/1.4 shallow depth of field, creamy soft bokeh",
              "focal_plane": "Midground: Narrator's eyes",
              "rack_focus": "Static focus on narrator's eyes"
            },
            ...
          ]
        }
        """;

    public Task<Dictionary<string, DepthOfFieldDirective>?> ClassifySceneDepthOfFieldAsync(
        Dictionary<string, object?> scene,
        List<Dictionary<string, object?>> beats,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        string? model = null) => ClassifyAsync(scene, beats, onProgress, ct, model);

    protected override string BeatsHeading => "BEATS TO DIRECT OPTICALLY:";

    protected override void AppendBeat(System.Text.StringBuilder sb, Dictionary<string, object?> b)
    {
        var id = b.GetValueOrDefault("beat_id") ?? "b";
        var action = b.GetValueOrDefault("visual_event") ?? "";
        var psub = b.GetValueOrDefault("primary_subject") ?? "";
        var dlg = b.GetValueOrDefault("dialogue") ?? "";

        sb.AppendLine($"Beat '{id}' (subject: {psub}):");
        if (!string.IsNullOrWhiteSpace(dlg.ToString()))
            sb.AppendLine($"  Spoken: \"{dlg}\"");
        AppendActionProse(sb, action);
    }

    protected override Dictionary<string, DepthOfFieldDirective>? ParseResponse(string rawJson)
    {
        try
        {
            var cleaned = ClassifierJsonParser.StripFences(rawJson);
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("dof", out var dofArray) ||
                dofArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new Dictionary<string, DepthOfFieldDirective>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in dofArray.EnumerateArray())
            {
                if (item.TryGetProperty("beat_id", out var bid))
                {
                    var id = bid.GetString() ?? "";
                    var ap = item.TryGetProperty("aperture", out var a) ? a.GetString() ?? "" : "";
                    var fp = item.TryGetProperty("focal_plane", out var f) ? f.GetString() ?? "" : "";
                    var rf = item.TryGetProperty("rack_focus", out var r) ? r.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result[id] = new DepthOfFieldDirective(ap, fp, rf);
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse AI depth of field response JSON: {RawJson}", rawJson);
            return null;
        }
    }
}
