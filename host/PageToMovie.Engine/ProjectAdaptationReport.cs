using System.Text.Json;
using PageToMovie.Adaptation.Contracts;

namespace PageToMovie.Engine;

/// <summary>
/// Persist Stage‑1 <see cref="AdaptationReport"/> under the project source tree.
/// Diagnostic only — not consumed by portrait/clip generation.
/// </summary>
public static class ProjectAdaptationReport
{
    public const string RelativePath = "source/adaptation_report.json";

    public static string GetPath(string projectDir) =>
        Path.Combine(projectDir, "source", "adaptation_report.json");

    public static async Task WriteAsync(string projectDir, AdaptationReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var path = GetPath(projectDir);
        if (Path.GetDirectoryName(path) is { } dir)
            Directory.CreateDirectory(dir);
        var payload = new
        {
            schema_version = "adaptation_report.v1",
            source_complete = report.SourceComplete,
            metrics = new
            {
                scenes = report.Metrics.Scenes,
                speaking_cast = report.Metrics.SpeakingCast,
                body_words = report.Metrics.BodyWords,
                est_runtime_min = report.Metrics.EstRuntimeMin,
            },
            issues = report.Issues.Select(i => new
            {
                type = i.Type,
                severity = i.Severity,
                where = i.Where,
                detail = i.Detail,
                resolution = i.Resolution,
            }),
            spec_feedback = report.SpecFeedback,
            raw_json = report.RawJson,
        };
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Indented);
        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
    }

    public static async Task<AdaptationReport?> TryReadAsync(string projectDir, CancellationToken ct = default)
    {
        var path = GetPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            // File is schema_version-wrapped but same fields as the model sidecar.
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return PageToMovie.Adaptation.Conversion.AdaptationReportParser.ParseModelJson(text);
        }
        catch
        {
            return null;
        }
    }
}
