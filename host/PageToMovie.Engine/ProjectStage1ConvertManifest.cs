using System.Text.Json;
using PageToMovie.Adaptation.Contracts;

namespace PageToMovie.Engine;

/// <summary>
/// Persist Stage‑1 convert attribution pins under the project source tree.
/// </summary>
public static class ProjectStage1ConvertManifest
{
    public const string RelativePath = "source/stage1_convert_manifest.json";

    public static string GetPath(string projectDir) =>
        Path.Combine(projectDir, "source", "stage1_convert_manifest.json");

    public static async Task WriteAsync(
        string projectDir,
        AdaptationConvertManifest manifest,
        string? bookId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var path = GetPath(projectDir);
        if (Path.GetDirectoryName(path) is { } dir)
            Directory.CreateDirectory(dir);

        var payload = new
        {
            schema_version = AdaptationConvertManifest.SchemaVersion,
            completed_utc = manifest.CompletedUtc,
            model_id = manifest.ModelId,
            temperature = manifest.Temperature,
            reasoning_effort = manifest.ReasoningEffort,
            prompt_content_sha256 = manifest.PromptContentSha256,
            adaptation_version = manifest.AdaptationVersion,
            runtime_mode = manifest.RuntimeMode,
            natural_runtime_minutes = manifest.NaturalRuntimeMinutes,
            target_runtime_minutes = manifest.TargetRuntimeMinutes,
            used_heuristic_fallback = manifest.UsedHeuristicFallback,
            vision_meta_status = manifest.VisionMetaStatus,
            adaptation_report_status = manifest.AdaptationReportStatus,
            book_file_id = manifest.BookFileSessionId,
            book_id = bookId ?? manifest.BookId,
            title = manifest.Title,
            author = manifest.Author,
            fountain_chars = manifest.FountainChars,
            scene_count_approx = manifest.SceneCountApprox,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonDefaults.Indented) + "\n", ct).ConfigureAwait(false);
    }

    public static void Write(string projectDir, AdaptationConvertManifest manifest, string? bookId = null) =>
        WriteAsync(projectDir, manifest, bookId).GetAwaiter().GetResult();

    public static async Task<AdaptationConvertManifest?> TryReadAsync(string projectDir, CancellationToken ct = default)
    {
        var path = GetPath(projectDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            var r = doc.RootElement;
            return new AdaptationConvertManifest
            {
                CompletedUtc = Str(r, "completed_utc"),
                ModelId = Str(r, "model_id"),
                Temperature = r.TryGetProperty("temperature", out var temp) && temp.TryGetDouble(out var td) ? td : 0.2,
                ReasoningEffort = StrOrNull(r, "reasoning_effort"),
                PromptContentSha256 = Str(r, "prompt_content_sha256"),
                AdaptationVersion = Str(r, "adaptation_version"),
                RuntimeMode = Str(r, "runtime_mode"),
                NaturalRuntimeMinutes = Int(r, "natural_runtime_minutes"),
                TargetRuntimeMinutes = r.TryGetProperty("target_runtime_minutes", out var tr) && tr.ValueKind == JsonValueKind.Number && tr.TryGetInt32(out var ti) ? ti : null,
                UsedHeuristicFallback = r.TryGetProperty("used_heuristic_fallback", out var uh) && uh.ValueKind == JsonValueKind.True,
                VisionMetaStatus = Str(r, "vision_meta_status"),
                AdaptationReportStatus = Str(r, "adaptation_report_status"),
                BookFileSessionId = StrOrNull(r, "book_file_id"),
                BookId = StrOrNull(r, "book_id"),
                Title = Str(r, "title"),
                Author = StrOrNull(r, "author"),
                FountainChars = Int(r, "fountain_chars"),
                SceneCountApprox = Int(r, "scene_count_approx"),
            };
        }
        catch
        {
            return null;
        }
    }

    public static AdaptationConvertManifest? TryRead(string projectDir) =>
        TryReadAsync(projectDir).GetAwaiter().GetResult();

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static string? StrOrNull(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int Int(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.TryGetInt32(out var i) ? i : 0;
}
