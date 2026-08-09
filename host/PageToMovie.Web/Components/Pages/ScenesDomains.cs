using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Typed domain modules for the Scenes page.
//
// Behavior lives in partial class files on Scenes:
//   Scenes.ListState.cs, Scenes.ClipEditor.cs, Scenes.Generation.cs,
//   Scenes.Playback.cs, Scenes.DialogueVerify.cs, Scenes.Music.cs, Scenes.History.cs
//
// Shared mutable UI state stays on Scenes (fields in Scenes.razor.cs).
// Markup children keep calling Host.Method(...). New code can use Host.List / Host.Gen / …
// for a clearer module boundary; forwarders below call the same partial methods.

/// <summary>
/// <b>ListState</b> domain for <see cref="Scenes"/>.
/// Implementation: <c>Scenes.ListState.cs</c>.
/// </summary>
/// <remarks>
/// Methods: AddSceneAsync, BackToListAsync, ClearFilters, ClearSelection, ConfirmDeleteSceneAsync, EstimateSceneCostUsd, EstimateSelectedCostUsd, LoadDetailAsync, LoadGenResolutionFromConfigAsync, OpenSceneAsync, RebuildShotPlanAsync, RefreshCastGateAsync, RefreshCostEstimateAsync, RefreshResolutionLockAsync, ReloadListAsync, RequestDeleteScene, SelectAll, SelectByCharacter, SelectByLocation, SelectMissingScenes, ToggleSelect, ToggleSelectAllShown, ToggleSort
/// </remarks>
internal sealed class ScenesListState
{
    public Scenes Page { get; }
    public ScenesListState(Scenes page) => Page = page;
}

/// <summary>
/// <b>ClipEditor</b> domain for <see cref="Scenes"/>.
/// Implementation: <c>Scenes.ClipEditor.cs</c>.
/// </summary>
/// <remarks>
/// Methods: CancelDeleteClip, ClearClipSelection, CloseClipCompare, CloseClipEditor, CloseVideoEditPrompt, ConfirmDeleteClipAsync, EmptyClipTrashAsync, EnsurePredecessorsUploadedAsync, EstimateSelectedClips, EstimateSelectedClipsCostUsd, IsClipGenBusy, OnClipEditorCastToggled, OpenAddClipDialog, OpenClipCompareAsync, OpenClipEditor, OpenInExternalEditorAsync, OpenVideoEditPrompt, PreviousClipMissing, PromoteClipVersionAsync, RegenClipAsync, RegenSelectedClipsAsync, RequestDeleteClip, ResolveActiveVideoExtendModelAsync, ResolveExpectedClipSizeAsync, RestoreClipVersionAsync, SaveClipEditorAsync, SelectClip, SelectMissingClips, SoftDeleteClipVersionAsync, SubmitVideoEditAsync, ToggleClipDurationSort, ToggleClipEditorCast, ToggleClipSelect, ToggleSelectAllClips
/// </remarks>
internal sealed class ScenesClipEditor
{
    public Scenes Page { get; }
    public ScenesClipEditor(Scenes page) => Page = page;
}

/// <summary>
/// <b>Generation</b> domain for <see cref="Scenes"/>.
/// Implementation: <c>Scenes.Generation.cs</c>.
/// </summary>
/// <remarks>
/// Methods: CancelAsync, CloseGenerateConfirm, ConfirmGenerateAsync, EnsureHubAsync, GenOneSceneAsync, GenerateCreditsEntryAsync, IsCreditsSceneNum, IsSceneGenBusy, LiveGenProgressPercent, LoadVideoModelsAsync, OnJobLog, OnJobUpdated, OpenGenerateConfirmAsync, RefreshMyJobsAsync, RenderCreditsSceneClientSideAsync, RenderOneCreditsClipAsync, ShouldRefreshSceneListWhileRunning, SoftReloadAsync, SoftReloadListLiveAsync, StartBatchAsync
/// </remarks>
internal sealed class ScenesGeneration
{
    public Scenes Page { get; }
    public ScenesGeneration(Scenes page) => Page = page;
}

/// <summary>
/// <b>Playback</b> domain for <see cref="Scenes"/>.
/// Implementation: <c>Scenes.Playback.cs</c>.
/// </summary>
/// <remarks>
/// Methods: CompareVideoUrl, HidePreviewPlayerAsync, HideScenePlayer, LoadClipVideoAndTakesCountAsync, PlaySceneCompositeAsync, PlaySelectedAsync, RefreshCompareVideoUrlsAsync, ScenePlayerSrc
/// </remarks>
internal sealed class ScenesPlayback
{
    public Scenes Page { get; }
    public ScenesPlayback(Scenes page) => Page = page;
}

/// <summary>
/// <b>DialogueVerify</b> domain for <see cref="Scenes"/>.
/// Implementation: <c>Scenes.DialogueVerify.cs</c>.
/// </summary>
/// <remarks>
/// Methods: SelectMismatchedClips, VerifyClipDialogueManualAsync, VerifySelectedScenesDialogueAsync
/// </remarks>
internal sealed class ScenesDialogueVerify
{
    public Scenes Page { get; }
    public ScenesDialogueVerify(Scenes page) => Page = page;
}

/// <summary>
/// <b>Music</b> domain for <see cref="Scenes"/>.
/// Implementation: <c>Scenes.Music.cs</c>.
/// </summary>
/// <remarks>
/// Methods: CloseMusicCompare, CloseScoreMenu, CompleteSceneMusicDownloadAsync, LoadAudioModelsAsync, OpenMusicCompareAsync, OpenScoreMenu, PromoteMusicVersionAsync, RefreshMusicCompareUrlsAsync, RestoreMusicVersionAsync, ScoreFromMenuAsync, ScoreSceneBackgroundMusicAsync, SoftDeleteMusicVersionAsync
/// </remarks>
internal sealed class ScenesMusic
{
    public Scenes Page { get; }
    public ScenesMusic(Scenes page) => Page = page;
}

/// <summary>
/// <b>History</b> domain for <see cref="Scenes"/>.
/// Implementation: <c>Scenes.History.cs</c>.
/// </summary>
/// <remarks>
/// Methods: CloseSceneHistory, HideSceneHistory, OnSceneHistoryRestored, OpenSceneHistoryAsync, RevertSceneToVersionAsync
/// </remarks>
internal sealed class ScenesHistory
{
    public Scenes Page { get; }
    public ScenesHistory(Scenes page) => Page = page;
}

