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
    internal sealed class ScenesClipVersions
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
                _clipVersions = res?.Versions;
                _selectedCompareVersionId = _clipVersions?.FirstOrDefault(v => !v.IsCurrent)?.VersionId ?? _clipVersions?.FirstOrDefault()?.VersionId;
                await S.RefreshCompareVideoUrlsAsync();

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
                    await S.RefreshCompareVideoUrlsAsync();
                    if (S._detail is not null && S._detail.SceneNumber == sceneNumber)
                    {
                        S._detail = (await S.Engine.GetSceneDetailAsync(S._projectId, sceneNumber))?.Scene;
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

        internal async Task SoftDeleteClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
        {
            _promotingVersion = true;
            _clipCompareMessage = null;
            S.StateHasChanged();

            try
            {
                var res = await S.Engine.SoftDeleteClipVersionAsync(S._projectId, sceneNumber, clipNumber, versionId);
                if (res.Ok)
                {
                    _clipCompareMessage = "Take deleted. You can restore it from the Trash Bin below.";
                    var resV = await S.Engine.GetClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _clipVersions = resV?.Versions;
                    var resT = await S.Engine.GetTrashClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _trashVersions = resT?.Versions;
                }
                else
                {
                    _clipCompareMessage = res.Error ?? "Failed to delete take.";
                }
            }
            catch (Exception ex)
            {
                _clipCompareMessage = $"Delete failed: {ex.Message}";
            }
            finally
            {
                _promotingVersion = false;
                S.StateHasChanged();
            }
        }

        internal async Task RestoreClipVersionAsync(int sceneNumber, int clipNumber, string versionId)
        {
            _promotingVersion = true;
            _clipCompareMessage = null;
            S.StateHasChanged();

            try
            {
                var res = await S.Engine.RestoreClipVersionAsync(S._projectId, sceneNumber, clipNumber, versionId);
                if (res.Ok)
                {
                    _clipCompareMessage = "Take restored from Trash Bin.";
                    var resV = await S.Engine.GetClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _clipVersions = resV?.Versions;
                    var resT = await S.Engine.GetTrashClipVersionsAsync(S._projectId, sceneNumber, clipNumber);
                    _trashVersions = resT?.Versions;
                }
                else
                {
                    _clipCompareMessage = res.Error ?? "Failed to restore take.";
                }
            }
            catch (Exception ex)
            {
                _clipCompareMessage = $"Restore failed: {ex.Message}";
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
