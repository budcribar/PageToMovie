using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PageToMovie.Engine.ModelExecution;

/// <summary>
/// Common validation and provenance sink for large structured adaptation operations.
/// It never supplies missing model data: errors remain errors, and the catalog-selected
/// model id is recorded exactly as used.
/// </summary>
public static class StructuredOperationArtifacts
{
    public const string SchemaVersion = "structured-operation.v1";

    public static IReadOnlyList<ModelValidationIssue> RequireJsonProperties(
        object value,
        params string[] propertyNames)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return [new("invalid_root", "The structured result must be a JSON object.", "$")];

        var issues = new List<ModelValidationIssue>();
        foreach (var name in propertyNames)
        {
            if (!doc.RootElement.TryGetProperty(name, out var item) || IsEmpty(item))
                issues.Add(new("missing_required_data", $"Required model data '{name}' is missing.", $"$.{name}"));
        }
        return issues;
    }

    public static async Task<string> WriteAsync(
        string projectDir,
        string operationName,
        string? model,
        object inputIdentity,
        object result,
        IReadOnlyList<ModelValidationIssue> issues,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(projectDir, "artifacts", "model_operations");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Sanitize(operationName) + ".json");
        var envelope = new
        {
            schemaVersion = SchemaVersion,
            operationName,
            model,
            inputHash = Hash(JsonSerializer.Serialize(inputIdentity)),
            resultHash = Hash(JsonSerializer.Serialize(result)),
            valid = !issues.Any(i => i.Severity == ModelValidationSeverity.Error),
            validationIssues = issues,
        };
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }) + "\n";
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        return path;
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(),
        _ => false,
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
