using System.Text.RegularExpressions;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// Catalog JSON on disk is the only source of model ids and capability defaults.
/// These tests read <c>host/PageToMovie.Core/config/models_catalog.json</c> at run time —
/// they do not copy model ids into C#.
/// </summary>
[Collection("catalog-serial")]
public class CatalogJsonSourceOfTruthTests
{
    public CatalogJsonSourceOfTruthTests()
    {
        SupportedModelCatalog.ReloadCatalog();
    }

    [Fact]
    public void On_disk_catalog_file_exists_and_is_the_real_models_catalog_json()
    {
        var path = ModelsCatalogJsonFile.ResolvePath();
        Assert.True(File.Exists(path), path);
        Assert.True(
            path.Replace('\\', '/').EndsWith(ModelsCatalogJsonFile.RelativePath, StringComparison.OrdinalIgnoreCase)
            || path.Replace('\\', '/').EndsWith("PageToMovie.Core/config/models_catalog.json", StringComparison.OrdinalIgnoreCase),
            "expected the repo file host/PageToMovie.Core/config/models_catalog.json, got " + path);
        using var doc = ModelsCatalogJsonFile.Parse();
        Assert.True(ModelsCatalogJsonFile.ReadCapabilities(doc).Count > 0, "capabilities[] missing on disk");
        Assert.True(ModelsCatalogJsonFile.ReadModels(doc).Count > 0, "models[] missing on disk");
    }

    [Fact]
    public void DefaultModelIdForCapability_matches_every_JSON_defaultModelId()
    {
        using var doc = ModelsCatalogJsonFile.Parse();
        var caps = ModelsCatalogJsonFile.ReadCapabilities(doc)
            .Where(c => !string.IsNullOrWhiteSpace(c.DefaultModelId))
            .ToList();
        Assert.NotEmpty(caps);

        var mismatches = new List<string>();
        foreach (var cap in caps)
        {
            var fromApi = SupportedModelCatalog.DefaultModelIdForCapability(cap.Id);
            if (!string.Equals(fromApi, cap.DefaultModelId, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(
                    $"{cap.Id}: JSON defaultModelId={cap.DefaultModelId}, " +
                    $"DefaultModelIdForCapability={fromApi ?? "(null)"}");
            }
        }

        Assert.True(mismatches.Count == 0,
            "DefaultModelIdForCapability must return capabilities[].defaultModelId from the on-disk catalog:\n- "
            + string.Join("\n- ", mismatches));
    }

    [Fact]
    public void Chat_and_vision_defaults_match_on_disk_JSON_not_a_CSharp_literal()
    {
        using var doc = ModelsCatalogJsonFile.Parse();
        var chatDefault = ModelsCatalogJsonFile.DefaultModelIdForCapability(doc, "chat");
        var visionDefault = ModelsCatalogJsonFile.DefaultModelIdForCapability(doc, "vision");
        Assert.False(string.IsNullOrWhiteSpace(chatDefault), "capabilities[chat].defaultModelId missing on disk");
        Assert.False(string.IsNullOrWhiteSpace(visionDefault), "capabilities[vision].defaultModelId missing on disk");

        Assert.Equal(chatDefault, SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Chat));
        Assert.Equal(visionDefault, SupportedModelCatalog.DefaultModelIdForCapability(ModelCapability.Vision));
        Assert.Equal(chatDefault, SupportedModelCatalog.DefaultModelIdForCapability("chat"));
        Assert.Equal(visionDefault, SupportedModelCatalog.DefaultModelIdForCapability("vision"));
    }

    [Fact]
    public void Find_and_ForCapability_resolve_every_enabled_model_id_from_JSON()
    {
        using var doc = ModelsCatalogJsonFile.Parse();
        var enabled = ModelsCatalogJsonFile.ReadModels(doc)
            .Where(m => m.Enabled)
            .ToList();
        Assert.NotEmpty(enabled);

        var errors = new List<string>();
        foreach (var model in enabled)
        {
            var found = SupportedModelCatalog.Find(model.Id);
            if (found is null)
            {
                errors.Add($"Find({model.Id}) returned null");
                continue;
            }

            if (!string.Equals(found.Id, model.Id, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Find({model.Id}).Id={found.Id}");

            if (!Enum.TryParse<ModelCapability>(model.Capability, ignoreCase: true, out var cap))
            {
                errors.Add($"{model.Id}: JSON capability '{model.Capability}' is not a ModelCapability");
                continue;
            }

            var foundCap = SupportedModelCatalog.Find(model.Id, cap);
            if (foundCap is null)
                errors.Add($"Find({model.Id}, {cap}) returned null");
            else if (!string.Equals(foundCap.Id, model.Id, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Find({model.Id}, {cap}).Id={foundCap.Id}");

            if (model.Deprecated || model.LabMode)
                continue;

            var listed = SupportedModelCatalog.ForCapability(cap);
            if (!listed.Any(e => e.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"ForCapability({cap}) does not include enabled id {model.Id}");
        }

        Assert.True(errors.Count == 0,
            "Catalog API must resolve every enabled model id from the on-disk JSON:\n- "
            + string.Join("\n- ", errors));
    }

    [Fact]
    public void DefaultModelIdForCapability_never_returns_missing_or_disabled_id()
    {
        using var doc = ModelsCatalogJsonFile.Parse();
        var models = ModelsCatalogJsonFile.ReadModels(doc);
        var byId = models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var capabilityIds = ModelsCatalogJsonFile.ReadCapabilities(doc)
            .Select(c => c.Id)
            .Concat(Enum.GetNames<ModelCapability>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = new List<string>();
        foreach (var capId in capabilityIds)
        {
            var returned = SupportedModelCatalog.DefaultModelIdForCapability(capId);
            if (string.IsNullOrWhiteSpace(returned))
                continue;
            if (!byId.TryGetValue(returned, out var rows))
            {
                errors.Add($"{capId}: returned '{returned}' which is not in the on-disk models[]");
                continue;
            }

            if (!rows.Any(r => r.Enabled))
                errors.Add($"{capId}: returned disabled id '{returned}'");
        }

        Assert.True(errors.Count == 0,
            "DefaultModelIdForCapability must not invent or return a disabled catalog id:\n- "
            + string.Join("\n- ", errors));
    }

    [Fact]
    public void Product_and_tool_CSharp_does_not_hardcode_catalog_model_id_defaults()
    {
        using var doc = ModelsCatalogJsonFile.Parse();
        var modelIds = ModelsCatalogJsonFile.ReadModelIds(doc);
        Assert.True(modelIds.Count > 0, "on-disk catalog has no model ids");

        var hostRoot = FindHostRoot();
        var pattern = BuildDefaultAssignmentPattern(modelIds);
        var offenders = new List<string>();

        foreach (var file in EnumerateProductAndToolCSharp(hostRoot))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith('*'))
                    continue;
                if (!pattern.IsMatch(line))
                    continue;
                offenders.Add($"{ToRepoRelative(hostRoot, file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Hardcoded catalog model-id default(s) found. Read the id from models_catalog.json " +
            "via SupportedModelCatalog.DefaultModelIdForCapability / Find — do not assign a literal " +
            "model id in product or tool code. Bumping the JSON default must not leave a stale C# default.\n"
            + string.Join("\n", offenders));
    }

    private static Regex BuildDefaultAssignmentPattern(IReadOnlySet<string> modelIds)
    {
        var alternation = string.Join("|",
            modelIds
                .OrderByDescending(id => id.Length)
                .Select(Regex.Escape));
        // Default assignment / coalesce / collection initializer — not comments, not telemetry of a
        // runtime-selected id, not cost labels that print a variable.
        var source =
            @"(?:" +
            @"(?:=|\?\?)\s*""" + $"({alternation})" + @"""" +
            @"|" +
            @"new(?:\(\)|\s+[\w.<>]+)\s*\{\s*""" + $"({alternation})" + @"""" +
            @")";
        return new Regex(source, RegexOptions.Compiled, CommonRegex.Timeout);
    }

    private static IEnumerable<string> EnumerateProductAndToolCSharp(string hostRoot)
    {
        var roots = new[]
        {
            Path.Combine(hostRoot, "PageToMovie.Engine"),
            Path.Combine(hostRoot, "PageToMovie.Api"),
            Path.Combine(hostRoot, "PageToMovie.Web"),
            Path.Combine(hostRoot, "PageToMovie.Adaptation"),
            Path.Combine(hostRoot, "PageToMovie.Core"),
            Path.Combine(hostRoot, "tools"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (IsExcludedSource(file))
                    continue;
                yield return file;
            }
        }
    }

    private static bool IsExcludedSource(string file)
    {
        var n = file.Replace('\\', '/');
        if (n.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.EndsWith("/SupportedModelCatalog.cs", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.Contains("/PageToMovie.Tests/", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("/PageToMovie.UiTests/", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("/evals/", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string FindHostRoot()
    {
        var catalog = ModelsCatalogJsonFile.ResolvePath();
        var coreConfig = Directory.GetParent(catalog)?.FullName
            ?? throw new DirectoryNotFoundException(catalog);
        var core = Directory.GetParent(coreConfig)?.FullName
            ?? throw new DirectoryNotFoundException(coreConfig);
        var host = Directory.GetParent(core)?.FullName
            ?? throw new DirectoryNotFoundException(core);
        return host;
    }

    private static string ToRepoRelative(string hostRoot, string file)
    {
        var hostParent = Directory.GetParent(hostRoot)?.FullName ?? hostRoot;
        return Path.GetRelativePath(hostParent, file).Replace('\\', '/');
    }
}
