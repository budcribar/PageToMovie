using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

/// <summary>
/// Enablement state of a model in the SupportedModelCatalog.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelEnablementState
{
    Disabled = 0,
    Enabled = 1,
    Deprecated = 2,
    Experimental = 3,
    Preview = 4
}

/// <summary>
/// Primary states/phases of a project lifecycle.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectStateName
{
    Draft = 0,
    Active = 1,
    Archived = 2,
    Completed = 3,
    Deleted = 4,
    InProduction = 5,
    SetupRequired = 6,
    ImportRequired = 7,
    TextExtractionPending = 8,
    ScreenplayDraft = 9,
    ScreenplayApproved = 10,
    ShotPlanReady = 11,
    ReviewReady = 12
}

/// <summary>
/// Operational status of a user account.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserAccountStatus
{
    Active = 0,
    PendingConfirmation = 1,
    Unconfirmed = 2,
    Disabled = 3,
    Suspended = 4,
    Locked = 5
}

/// <summary>
/// Extension methods for Core layer enums.
/// </summary>
public static class CoreLayerEnumExtensions
{
    public static string ToApiString(this ModelEnablementState state) => state switch
    {
        ModelEnablementState.Enabled => "enabled",
        ModelEnablementState.Disabled => "disabled",
        ModelEnablementState.Deprecated => "deprecated",
        ModelEnablementState.Experimental => "experimental",
        ModelEnablementState.Preview => "preview",
        _ => "disabled"
    };

    public static ModelEnablementState ParseModelEnablementState(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "enabled" or "active" => ModelEnablementState.Enabled,
            "deprecated" or "archived" => ModelEnablementState.Deprecated,
            "experimental" or "beta" => ModelEnablementState.Experimental,
            "preview" => ModelEnablementState.Preview,
            _ => ModelEnablementState.Disabled
        };

    public static string ToApiString(this ProjectStateName state) => state switch
    {
        ProjectStateName.Draft => "draft",
        ProjectStateName.Active => "active",
        ProjectStateName.Archived => "archived",
        ProjectStateName.Completed => "completed",
        ProjectStateName.Deleted => "deleted",
        ProjectStateName.InProduction => "in_production",
        ProjectStateName.SetupRequired => "setup_required",
        ProjectStateName.ImportRequired => "import_required",
        ProjectStateName.TextExtractionPending => "text_extraction_pending",
        ProjectStateName.ScreenplayDraft => "screenplay_draft",
        ProjectStateName.ScreenplayApproved => "screenplay_approved",
        ProjectStateName.ShotPlanReady => "shot_plan_ready",
        ProjectStateName.ReviewReady => "review_ready",
        _ => "draft"
    };

    public static ProjectStateName ParseProjectStateName(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "active" => ProjectStateName.Active,
            "archived" => ProjectStateName.Archived,
            "completed" => ProjectStateName.Completed,
            "deleted" => ProjectStateName.Deleted,
            "in_production" or "inproduction" => ProjectStateName.InProduction,
            "setup_required" => ProjectStateName.SetupRequired,
            "import_required" => ProjectStateName.ImportRequired,
            "text_extraction_pending" => ProjectStateName.TextExtractionPending,
            "screenplay_draft" => ProjectStateName.ScreenplayDraft,
            "screenplay_approved" => ProjectStateName.ScreenplayApproved,
            "shot_plan_ready" => ProjectStateName.ShotPlanReady,
            "review_ready" => ProjectStateName.ReviewReady,
            _ => ProjectStateName.Draft
        };

    public static string ToApiString(this UserAccountStatus status) => status switch
    {
        UserAccountStatus.Active => "active",
        UserAccountStatus.PendingConfirmation => "pending_confirmation",
        UserAccountStatus.Unconfirmed => "unconfirmed",
        UserAccountStatus.Disabled => "disabled",
        UserAccountStatus.Suspended => "suspended",
        UserAccountStatus.Locked => "locked",
        _ => "active"
    };

    public static UserAccountStatus ParseUserAccountStatus(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "active" => UserAccountStatus.Active,
            "pending_confirmation" or "pendingconfirmation" or "pending" => UserAccountStatus.PendingConfirmation,
            "unconfirmed" => UserAccountStatus.Unconfirmed,
            "disabled" => UserAccountStatus.Disabled,
            "suspended" => UserAccountStatus.Suspended,
            "locked" => UserAccountStatus.Locked,
            _ => UserAccountStatus.Active
        };
}
