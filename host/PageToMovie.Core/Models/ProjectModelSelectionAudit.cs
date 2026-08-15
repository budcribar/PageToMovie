namespace PageToMovie.Core.Models;

/// <summary>
/// One on-disk stored model id plus catalog enablement. Null enablement means the id is
/// empty / a sentinel / not in the catalog — never invent a model row.
/// </summary>
public sealed class StoredModelRef
{
    public string? Id { get; init; }
    public bool? Enabled { get; init; }
    public bool? Deprecated { get; init; }
    public bool InCatalog { get; init; }

    public bool NeedsUpdate =>
        ProjectModelSelection.IsUsableModelId(Id) && (!InCatalog || Enabled != true || Deprecated == true);
}

/// <summary>Read-only audit of one project's stored Settings model slots.</summary>
public sealed class ProjectModelSelectionAuditRow
{
    public string Id { get; init; } = "";
    public string? Slug { get; init; }
    public string? Title { get; init; }
    public string? Owner { get; init; }
    public string? Error { get; init; }
    public bool NeedsUpdate { get; init; }
    public StoredModelRef? ModelName { get; init; }
    public StoredModelRef? ImageModelName { get; init; }
    public StoredModelRef? PlanningModelName { get; init; }
    public StoredModelRef? VisionModelName { get; init; }
    public StoredModelRef? QualityModelName { get; init; }
    public StoredModelRef? AudioModelName { get; init; }
    public StoredModelRef? VoiceModelName { get; init; }
    public StoredModelRef? VideoReviewModelName { get; init; }
    public string? QualityProvider { get; init; }
    public Dictionary<string, StoredModelRef>? ModelSelections { get; init; }
}
