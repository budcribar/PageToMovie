using System.Text.Json;
using PageToMovie.Core.Models;
using PageToMovie.Engine.Abstractions;

namespace PageToMovie.Engine;

/// <summary>
/// Resolve a user's API key from catalog model attributes (provider / key id) — never a
/// hardcoded provider name. Same binding video generation uses:
/// model → <see cref="SupportedModelCatalog.ProviderIdFor"/> → <see cref="IUserApiKeyProvider.GetKeyAsync"/>.
/// </summary>
public static class CatalogApiKey
{
    /// <summary>
    /// Catalog provider id for a video model, or a sidecar <c>source_provider</c> already stored
    /// from the catalog at generate time. Empty when neither can be bound.
    /// </summary>
    public static string ProviderIdForVideo(string? modelId, string? sourceProvider = null)
    {
        var fromModel = SupportedModelCatalog.ProviderIdFor(modelId, ModelCapability.Video);
        if (!string.IsNullOrWhiteSpace(fromModel))
            return SupportedModelCatalog.NormalizeProviderId(fromModel);
        if (!string.IsNullOrWhiteSpace(sourceProvider))
            return SupportedModelCatalog.NormalizeProviderId(sourceProvider);
        return "";
    }

    /// <summary>
    /// Video model for recovery: sidecar <c>model</c> first, else the project's selected video model.
    /// </summary>
    public static string? ResolveVideoModel(string? sidecarModel, string? projectVideoModel)
    {
        if (ProjectModelSelection.IsUsableModelId(sidecarModel)
            && SupportedModelCatalog.Find(sidecarModel, ModelCapability.Video) is { Enabled: true } fromSidecar)
            return fromSidecar.Id;
        if (ProjectModelSelection.IsUsableModelId(projectVideoModel)
            && SupportedModelCatalog.Find(projectVideoModel, ModelCapability.Video) is { Enabled: true } fromProject)
            return fromProject.Id;
        return ProjectModelSelection.IsUsableModelId(sidecarModel) ? sidecarModel!.Trim() : projectVideoModel?.Trim();
    }

    /// <summary>Read <c>pipeline_config.json</c> <c>model_name</c> from a project directory.</summary>
    public static string? TryReadProjectVideoModel(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir)) return null;
        var path = Path.Combine(projectDir, ProjectStore.PipelineConfigFileName);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var cfg = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                cfg[p.Name] = p.Value.Clone();
            return ProjectModelSelection.TryVideo(cfg);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Personal key for <paramref name="userId"/> + catalog <paramref name="providerId"/>.
    /// Never calls <c>GetKeyAsync(null, …)</c>. Empty user or provider → ambient scope only.
    /// </summary>
    public static async Task<string?> GetKeyAsync(
        IUserApiKeyProvider? keys,
        string? userId,
        string? providerId,
        CancellationToken ct = default)
    {
        var provider = SupportedModelCatalog.NormalizeProviderId(providerId);
        if (string.IsNullOrWhiteSpace(provider))
            return ApiKeyScope.Current;
        var ambient = ApiKeyScope.Get(provider);
        if (!string.IsNullOrWhiteSpace(ambient))
            return ambient;
        if (string.IsNullOrWhiteSpace(userId) || keys is null)
            return ApiKeyScope.Current;
        var fromUser = await keys.GetKeyAsync(userId, provider, ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(fromUser) ? fromUser : ApiKeyScope.Get(provider) ?? ApiKeyScope.Current;
    }

    public static IDisposable PushKey(string? providerId, string? key)
    {
        var provider = SupportedModelCatalog.NormalizeProviderId(providerId);
        if (string.IsNullOrWhiteSpace(provider))
            return ApiKeyScope.Push(key);
        return ApiKeyScope.Push(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [provider] = key,
        });
    }
}
