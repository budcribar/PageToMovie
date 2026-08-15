using System.Text.Json;
using PageToMovie.Core.Models;

namespace PageToMovie.Engine;

public sealed partial class ProjectStore
{
    /// <summary>
    /// Read-only scan of every project's on-disk <c>pipeline_config.json</c> model slots.
    /// Does not heal or write. Uses <see cref="ListProjectsAsync"/> for the project walk.
    /// </summary>
    public async Task<IReadOnlyList<ProjectModelSelectionAuditRow>> ListProjectModelSelectionsAsync(
        CancellationToken ct = default)
    {
        var projects = await ListProjectsAsync(ct).ConfigureAwait(false);
        var rows = new List<ProjectModelSelectionAuditRow>(projects.Count);
        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await BuildModelSelectionAuditRowAsync(project, ct).ConfigureAwait(false));
        }

        return rows;
    }

    private async Task<ProjectModelSelectionAuditRow> BuildModelSelectionAuditRowAsync(
        ProjectInfo project,
        CancellationToken ct)
    {
        var slug = SlugFromProjectId(project.Id);
        try
        {
            var (cfg, _, readError) = await TryReadConfigRawAsync(project.Id, ct).ConfigureAwait(false);
            if (readError is not null)
            {
                return new ProjectModelSelectionAuditRow
                {
                    Id = project.Id,
                    Slug = slug,
                    Title = project.Title ?? project.Label,
                    Owner = project.OwnerUserId,
                    Error = readError,
                    NeedsUpdate = false,
                };
            }

            cfg ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var modelName = ResolveStored(cfg, ProjectModelSelection.VideoConfigKey, ModelCapability.Video);
            var image = ResolveStored(cfg, ProjectModelSelection.ImageConfigKey, ModelCapability.Image);
            var planning = ResolveStored(cfg, ProjectModelSelection.PlanningConfigKey, ModelCapability.Chat);
            var vision = ResolveStored(cfg, ProjectModelSelection.VisionConfigKey, ModelCapability.Vision);
            var quality = ResolveStored(cfg, ProjectModelSelection.QualityConfigKey, ModelCapability.Chat);
            var audio = ResolveStored(cfg, ProjectModelSelection.AudioConfigKey, ModelCapability.Audio);
            var voice = ResolveStored(cfg, ProjectModelSelection.VoiceConfigKey, ModelCapability.Voice);
            var videoReview = ResolveStored(cfg, ProjectCatalogModelHeal.VideoReviewModelKey, ModelCapability.Chat);
            var selections = ReadModelSelections(cfg);
            var needsUpdate = new[] { modelName, image, planning, vision, quality, audio, voice, videoReview }
                .Concat(selections?.Values ?? Enumerable.Empty<StoredModelRef>())
                .Any(r => r.NeedsUpdate);

            return new ProjectModelSelectionAuditRow
            {
                Id = project.Id,
                Slug = slug,
                Title = project.Title ?? project.Label,
                Owner = project.OwnerUserId,
                NeedsUpdate = needsUpdate,
                ModelName = modelName,
                ImageModelName = image,
                PlanningModelName = planning,
                VisionModelName = vision,
                QualityModelName = quality,
                AudioModelName = audio,
                VoiceModelName = voice,
                VideoReviewModelName = videoReview,
                QualityProvider = ReadString(cfg, ProjectCatalogModelHeal.QualityProviderKey),
                ModelSelections = selections,
            };
        }
        catch (Exception ex)
        {
            return new ProjectModelSelectionAuditRow
            {
                Id = project.Id,
                Slug = slug,
                Title = project.Title ?? project.Label,
                Owner = project.OwnerUserId,
                Error = ex.Message,
                NeedsUpdate = false,
            };
        }
    }

    /// <summary>
    /// Load <c>pipeline_config.json</c> without healing or writing. Missing file → empty dict.
    /// Unreadable file → error string.
    /// </summary>
    private async Task<(Dictionary<string, JsonElement>? Cfg, bool Missing, string? Error)> TryReadConfigRawAsync(
        string projectId,
        CancellationToken ct)
    {
        var path = ConfigPath(projectId);
        if (!File.Exists(path))
            return (new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase), true, null);

        try
        {
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
            return (dict, false, null);
        }
        catch (Exception ex)
        {
            return (null, false, ex.Message);
        }
    }

    private static string SlugFromProjectId(string id)
    {
        var trimmed = (id ?? "").Trim().Replace('\\', '/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static StoredModelRef ResolveStored(
        Dictionary<string, JsonElement> cfg,
        string key,
        ModelCapability capability)
    {
        var id = ReadString(cfg, key);
        return ResolveCatalogRef(id, capability);
    }

    private static StoredModelRef ResolveCatalogRef(string? id, ModelCapability? capability)
    {
        if (!ProjectModelSelection.IsUsableModelId(id))
            return new StoredModelRef { Id = string.IsNullOrWhiteSpace(id) ? null : id.Trim() };

        var entry = capability is { } cap
            ? SupportedModelCatalog.Find(id, cap) ?? SupportedModelCatalog.Find(id)
            : SupportedModelCatalog.Find(id);
        if (entry is null)
            return new StoredModelRef { Id = id.Trim(), InCatalog = false };

        return new StoredModelRef
        {
            Id = entry.Id,
            Enabled = entry.Enabled,
            Deprecated = entry.Deprecated,
            InCatalog = true,
        };
    }

    private static Dictionary<string, StoredModelRef>? ReadModelSelections(Dictionary<string, JsonElement> cfg)
    {
        if (!cfg.TryGetValue(ProjectCatalogModelHeal.ModelSelectionsKey, out var el)
            || el.ValueKind != JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, StoredModelRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in el.EnumerateObject())
        {
            var id = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            map[p.Name] = ResolveCatalogRef(id, CapabilityForSelectionKey(p.Name));
        }

        return map;
    }

    private static ModelCapability? CapabilityForSelectionKey(string key) =>
        key.Trim().ToLowerInvariant() switch
        {
            "video" => ModelCapability.Video,
            "image" => ModelCapability.Image,
            "chat" or "planning" => ModelCapability.Chat,
            "vision" => ModelCapability.Vision,
            "video-review" or "videoreview" or "video_review" => ModelCapability.Chat,
            "audio" or "music" => ModelCapability.Audio,
            "voice" => ModelCapability.Voice,
            _ => null,
        };

    private static string? ReadString(Dictionary<string, JsonElement> cfg, string key)
    {
        if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
