using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class Scenes
{
    /// <summary>Clip version compare / history domain for the Scenes page.</summary>
    public sealed class ScenesClipVersions
    {
        private readonly Scenes S;
        public ScenesClipVersions(Scenes host) => S = host;

        internal bool _showClipCompare;

        internal int _compareSceneNumber;

        internal int _compareClipNumber;

        internal bool _loadingClipVersions;

        internal bool _promotingVersion;

        internal string? _clipCompareMessage;

        internal List<ClipVersionItem>? _clipVersions;

        internal List<ClipVersionItem>? _trashVersions;

        internal string? _selectedCompareVersionId;

        internal async Task OpenClipCompareAsync(int sceneNumber, int clipNumber)
        {
            _compareSceneNumber = sceneNumber;
            _compareClipNumber = clipNumber;
            _showClipCompare = true;
            _loadingClipVersions = true;
            _clipCompareMessage = null;
            _selectedCompareVersionId = null;
            S.StateHasChanged();

            try
            {
                var res = await S.Engine.GetClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                _clipVersions = res?.Versions?.ToList();

                if (_clipVersions is null || _clipVersions.Count == 0)
                {
                    var clip = S.List._detail?.Clips?.FirstOrDefault(c => c.ClipNumber == clipNumber);
                    if (clip is not null && clip.OnDisk)
                    {
                        _clipVersions = new List<ClipVersionItem>
                        {
                            new ClipVersionItem
                            {
                                VersionId = $"scene_{sceneNumber:D2}_clip_{clipNumber:D2}.mp4",
                                Scene = sceneNumber,
                                Clip = clipNumber,
                                Take = 1,
                                IsCurrent = true,
                                CreatedAtUtc = DateTime.UtcNow,
                                Mp4FileName = $"scene_{sceneNumber:D2}_clip_{clipNumber:D2}.mp4",
                                RelativePath = $"assets/video/scene_{sceneNumber:D2}_clip_{clipNumber:D2}.mp4",
                                DurationSeconds = clip.ActualDurationSeconds ?? clip.DurationSeconds,
                                VisualPrompt = clip.VisualPrompt
                            }
                        };
                    }
                }

                _selectedCompareVersionId = _clipVersions?.FirstOrDefault(v => !v.IsCurrent)?.VersionId ?? _clipVersions?.FirstOrDefault()?.VersionId;
                await S.Playback.RefreshCompareVideoUrlsAsync();

                var trashRes = await S.Engine.GetTrashClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                _trashVersions = trashRes?.Versions;
            }
            catch (Exception ex)
            {
                S._error = $"Failed to load clip versions: {ex.Message}";
            }
            finally
            {
                _loadingClipVersions = false;
                S.StateHasChanged();
            }
        }

        internal void CloseClipCompare()
        {
            _showClipCompare = false;
            _clipVersions = null;
            _trashVersions = null;
            _clipCompareMessage = null;
        }

        internal async Task PromoteClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
        {
            _promotingVersion = true;
            _clipCompareMessage = null;
            S.StateHasChanged();

            try
            {
                var res = await S.Engine.PromoteClipVersionAsync(S._projectId, sceneNumber, clipNumber, versionId);
                if (res.Ok)
                {
                    _clipCompareMessage = $"Successfully promoted version {versionId} to active clip.";
                    var resV = await S.Engine.GetClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _clipVersions = resV?.Versions;
                    _selectedCompareVersionId = _clipVersions?.FirstOrDefault(v => !v.IsCurrent)?.VersionId ?? _clipVersions?.FirstOrDefault()?.VersionId;
                    await S.Playback.RefreshCompareVideoUrlsAsync();
                    if (S.List._detail is not null && S.List._detail.SceneNumber == sceneNumber)
                    {
                        S.List._detail = (await S.Engine.GetSceneDetailAsync(S._projectId, sceneNumber))?.Scene;
                    }
                    await S.RefreshUncommittedStatusAsync();
                }
                else
                {
                    _clipCompareMessage = res.Error ?? "Failed to promote clip version.";
                }
            }
            catch (Exception ex)
            {
                _clipCompareMessage = $"Promote failed: {ex.Message}";
            }
            finally
            {
                _promotingVersion = false;
                S.StateHasChanged();
            }
        }

        internal Task SoftDeleteClipVersionAsync(int sceneNumber, int clipNumber, string versionId) =>
            MutateAndRefreshTakesAsync(
                sceneNumber, clipNumber, versionId,
                (pid, sn, cn, vid) => S.Engine.SoftDeleteClipVersionAsync(pid, sn, cn, vid),
                "Take deleted. You can restore it from the Trash Bin below.",
                "Failed to delete take.",
                "Delete failed");

        internal Task RestoreClipVersionAsync(int sceneNumber, int clipNumber, string versionId) =>
            MutateAndRefreshTakesAsync(
                sceneNumber, clipNumber, versionId,
                (pid, sn, cn, vid) => S.Engine.RestoreClipVersionAsync(pid, sn, cn, vid),
                "Take restored from Trash Bin.",
                "Failed to restore take.",
                "Restore failed");

        private async Task MutateAndRefreshTakesAsync(
            int sceneNumber,
            int clipNumber,
            string versionId,
            Func<string, int, int, string, Task<EngineApiClient.SceneRevertEnvelope>> mutate,
            string okMessage,
            string failFallback,
            string catchPrefix)
        {
            _promotingVersion = true;
            _clipCompareMessage = null;
            S.StateHasChanged();

            try
            {
                var res = await mutate(S._projectId, sceneNumber, clipNumber, versionId);
                if (res.Ok)
                {
                    _clipCompareMessage = okMessage;
                    var resV = await S.Engine.GetClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _clipVersions = resV?.Versions;
                    var resT = await S.Engine.GetTrashClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _trashVersions = resT?.Versions;
                }
                else
                {
                    _clipCompareMessage = res.Error ?? failFallback;
                }
            }
            catch (Exception ex)
            {
                _clipCompareMessage = $"{catchPrefix}: {ex.Message}";
            }
            finally
            {
                _promotingVersion = false;
                S.StateHasChanged();
            }
        }

        internal async Task EmptyClipTrashAsync(int sceneNumber, int clipNumber)
        {
            _promotingVersion = true;
            _clipCompareMessage = null;
            S.StateHasChanged();

            try
            {
                var res = await S.Engine.EmptyClipTrashAsync(S._projectId, sceneNumber, clipNumber);
                if (res.Ok)
                {
                    _clipCompareMessage = "Purged deleted take(s).";
                    var resT = await S.Engine.GetTrashClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _trashVersions = resT?.Versions;
                }
                else
                {
                    _clipCompareMessage = res.Error ?? "Failed to empty trash.";
                }
            }
            catch (Exception ex)
            {
                _clipCompareMessage = $"Empty trash failed: {ex.Message}";
            }
            finally
            {
                _promotingVersion = false;
                S.StateHasChanged();
            }
        }
    }
}
