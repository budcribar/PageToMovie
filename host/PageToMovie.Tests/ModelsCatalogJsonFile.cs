using System.Text.Json;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Reads the on-disk <c>models_catalog.json</c> (not the embedded resource, not a snapshot of ids).
/// Tests compare <see cref="PageToMovie.Core.Models.SupportedModelCatalog"/> against this file so a
/// catalog bump does not require a C# edit.
/// </summary>
internal static class ModelsCatalogJsonFile
{
    public const string RelativePath = "host/PageToMovie.Core/config/models_catalog.json";

    public sealed record CapabilityRow(string Id, string? DefaultModelId);

    public sealed record ModelRow(
        string Id,
        string Capability,
        bool Enabled,
        bool Deprecated,
        bool LabMode);

    public static string ResolvePath()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                foreach (var candidate in CatalogCandidates(dir.FullName))
                {
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }

                dir = dir.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Could not find {RelativePath} by walking up from the test base directory.");
    }

    public static JsonDocument Parse()
    {
        var path = ResolvePath();
        var json = File.ReadAllText(path);
        Assert.False(string.IsNullOrWhiteSpace(json), path + " is empty");
        return JsonDocument.Parse(json);
    }

    public static IReadOnlyList<CapabilityRow> ReadCapabilities(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("capabilities", out var caps) ||
            caps.ValueKind != JsonValueKind.Array)
            return Array.Empty<CapabilityRow>();

        var list = new List<CapabilityRow>();
        foreach (var cap in caps.EnumerateArray())
        {
            var id = cap.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            string? defaultId = null;
            if (cap.TryGetProperty("defaultModelId", out var defEl) &&
                defEl.ValueKind == JsonValueKind.String)
                defaultId = defEl.GetString();
            list.Add(new CapabilityRow(id, defaultId));
        }

        return list;
    }

    public static IReadOnlyList<ModelRow> ReadModels(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            return Array.Empty<ModelRow>();

        var list = new List<ModelRow>();
        foreach (var m in models.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var capability = m.TryGetProperty("capability", out var capEl) ? capEl.GetString() ?? "" : "";
            var enabled = !m.TryGetProperty("enabled", out var enEl) || enEl.ValueKind != JsonValueKind.False;
            var deprecated = m.TryGetProperty("deprecated", out var depEl) && depEl.ValueKind == JsonValueKind.True;
            var lab = m.TryGetProperty("labMode", out var labEl) && labEl.ValueKind == JsonValueKind.True;
            list.Add(new ModelRow(id, capability, enabled, deprecated, lab));
        }

        return list;
    }

    public static IReadOnlySet<string> ReadModelIds(JsonDocument doc)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ReadModels(doc))
            ids.Add(m.Id);
        return ids;
    }

    public static string? DefaultModelIdForCapability(JsonDocument doc, string capabilityId)
    {
        foreach (var cap in ReadCapabilities(doc))
        {
            if (string.Equals(cap.Id, capabilityId, StringComparison.OrdinalIgnoreCase))
                return cap.DefaultModelId;
        }

        return null;
    }

    private static IEnumerable<string> CatalogCandidates(string root)
    {
        yield return Path.Combine(root, "host", "PageToMovie.Core", "config", "models_catalog.json");
        yield return Path.Combine(root, "PageToMovie.Core", "config", "models_catalog.json");
    }
}
