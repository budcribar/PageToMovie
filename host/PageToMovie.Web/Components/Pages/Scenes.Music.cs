using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Domain: Music — partial methods/properties for the Scenes page
public partial class Scenes
{

    internal void OpenScoreMenu(int sceneNum) => _scoreMenuScene = sceneNum;


    internal void CloseScoreMenu() => _scoreMenuScene = null;


    internal async Task ScoreFromMenuAsync(int sceneNum)
    {
        _scoreMenuScene = null;
        await ScoreSceneBackgroundMusicAsync(sceneNum);
    }


    /// <summary>Catalog <c>supportsVocals</c> on the selected audio model — not provider id.</summary>
    internal bool SelectedAudioModelCanSing =>
        _audioModels.FirstOrDefault(m => string.Equals(m.Id, _selectedAudioModel, StringComparison.OrdinalIgnoreCase))
            ?.SupportsVocals == true;


    internal async Task LoadAudioModelsAsync()
    {
        try
        {
            var models = await Engine.GetSupportedModelsAsync();
            _audioModels = models.Where(m => m.Capability == "audio" &&
                !string.Equals(m.Id, "none", StringComparison.OrdinalIgnoreCase)).ToList();
            if (_audioModels.Count == 0)
                _audioModels.Add(new SupportedModelDto { Id = "fal-ai/stable-audio", DisplayName = "Stable Audio (Fal.ai)", Provider = "fal", Capability = "audio" });
        }
        catch { /* keep default single-entry list */ }
    }


    internal async Task ScoreSceneBackgroundMusicAsync(int sceneNum)
    {
        
        if (!Caps.MusicReady)
        {
            _error = Caps.MusicBlockedReason;
            return;
        }
if (string.IsNullOrWhiteSpace(_projectId)) return;

        var isVocal = _wantVocal && SelectedAudioModelCanSing;
        _busy = true;
        _scoringMusic = true;
        _error = null;
        _message = isVocal
            ? $"Queuing singing for Scene {sceneNum:D2}…"
            : $"Queuing background music for Scene {sceneNum:D2}…";
        StateHasChanged();

        try
        {
            await EnsureHubAsync();
            var started = await Engine.StartSceneMusicGenAsync(_projectId, sceneNum, _selectedAudioModel, isVocal);
            // Live progress card only — no duplicate "started" banner (same as scene gen).
            var jobs = await Engine.GetJobAsync();
            _job = jobs?.Job ?? started;
            if (!string.IsNullOrWhiteSpace(started?.JobId))
                _ = CompleteSceneMusicDownloadAsync(started.JobId, sceneNum);
        }
        catch (Exception ex)
        {
            _error = $"Music scoring failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            _scoringMusic = false;
            StateHasChanged();
        }
    }


    /// <summary>
    /// SignalR normally delivers each generated asset to the media-folder service. Waiting for the
    /// terminal job here closes the gap when that transient event is missed, and gives the operator
    /// a normal browser download even when no folder has been connected.
    /// </summary>
    internal async Task CompleteSceneMusicDownloadAsync(string jobId, int sceneNum)
    {
        try
        {
            var final = await Engine.WaitForJobTerminalAsync(jobId, timeout: TimeSpan.FromMinutes(20));
            if (final is null ||
                !string.Equals(final.Status, "done", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(final.ClientMediaUrl) ||
                string.IsNullOrWhiteSpace(final.ClientRelativePath))
                return;

            var clientUrl = final.ClientMediaUrl;
            var clientRelativePath = final.ClientRelativePath;

            // Keep the project-owned copy when a local media folder is available. This is safe if
            // the regular JobUpdated handler already saved it because the service de-duplicates paths.
            await MediaFolder.SaveJobMediaAsync(final);

            var fileName = Path.GetFileName(clientRelativePath);
            await JS.InvokeVoidAsync("PageToMovieMedia.downloadFromUrlAsync", clientUrl, fileName);
            await InvokeAsync(() =>
            {
                _message = $"Background music for Scene {sceneNum:D2} downloaded.";
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            await InvokeAsync(() =>
            {
                _error = $"Music download failed: {ex.Message}";
                StateHasChanged();
            });
        }
    }


    internal async Task OpenMusicCompareAsync(int sceneNumber)
    {
        _compareMusicSceneNumber = sceneNumber;
        _showMusicCompare = true;
        _loadingMusicVersions = true;
        _musicCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
            _musicVersions = res?.Versions;
            var trash = await Engine.GetTrashMusicVersionsAsync(_projectId, sceneNumber);
            _musicTrashVersions = trash?.Versions;
            _showMusicTrash = false;
            await RefreshMusicCompareUrlsAsync();
        }
        catch (Exception ex)
        {
            _error = $"Failed to load audio takes: {ex.Message}";
        }
        finally
        {
            _loadingMusicVersions = false;
            StateHasChanged();
        }
    }


    internal void CloseMusicCompare()
    {
        _showMusicCompare = false;
        _musicVersions = null;
        _musicTrashVersions = null;
        _showMusicTrash = false;
        _musicCompareUrls.Clear();
        _musicCompareMessage = null;
    }


    internal async Task RefreshMusicCompareUrlsAsync()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (_musicVersions is { Count: > 0 })
        {
            foreach (var v in _musicVersions)
            {
                var urls = new List<string>();
                foreach (var relPath in v.RelativePaths)
                {
                    var url = await MediaFolder.GetLocalBlobUrlAsync(_projectId, relPath);
                    if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
                }
                map[v.TakeId] = urls;
            }
        }
        _musicCompareUrls = map;
        StateHasChanged();
    }


    internal async Task PromoteMusicVersionAsync(int sceneNumber, string takeId)
    {
        _promotingMusicVersion = true;
        _musicCompareMessage = null;
        StateHasChanged();

        try
        {
            var target = _musicVersions?.FirstOrDefault(v => string.Equals(v.TakeId, takeId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                _musicCompareMessage = "Take not found.";
                return;
            }

            // Copy the chosen take's bytes to the active path first (archives whatever's currently
            // active under its own take id in the process — same mechanism a fresh generation uses),
            // then flip which sidecar the server considers active.
            var current = _musicVersions?.FirstOrDefault(v => v.IsCurrent);
            var archiveTakeId = current?.TakeId is { Length: > 0 } cid ? cid : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

            var copied = await MediaFolder.PromoteMusicTakeAsync(_projectId, target, archiveTakeId);
            if (!copied)
            {
                _musicCompareMessage = "Failed to copy audio locally — is your media folder connected?";
                return;
            }

            var res = await Engine.PromoteMusicVersionAsync(_projectId, sceneNumber, takeId);
            if (res.Ok)
            {
                _musicCompareMessage = $"Promoted take {takeId} to active.";
                var resV = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
                _musicVersions = resV?.Versions;
                await RefreshMusicCompareUrlsAsync();
                if (_detail is not null && _detail.SceneNumber == sceneNumber)
                {
                    _detail = (await Engine.GetSceneDetailAsync(_projectId, sceneNumber))?.Scene;
                }
            }
            else
            {
                _musicCompareMessage = res.Error ?? "Failed to promote audio take.";
            }
        }
        catch (Exception ex)
        {
            _musicCompareMessage = $"Promote failed: {ex.Message}";
        }
        finally
        {
            _promotingMusicVersion = false;
            StateHasChanged();
        }
    }


    internal async Task SoftDeleteMusicVersionAsync(int sceneNumber, string takeId)
    {
        _musicCompareMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.SoftDeleteMusicVersionAsync(_projectId, sceneNumber, takeId);
            if (res.Ok)
            {
                _musicCompareMessage = $"Deleted take {takeId}.";
                var resV = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
                _musicVersions = resV?.Versions;
                var resT = await Engine.GetTrashMusicVersionsAsync(_projectId, sceneNumber);
                _musicTrashVersions = resT?.Versions;
                await RefreshMusicCompareUrlsAsync();
            }
            else
            {
                _musicCompareMessage = res.Error ?? "Failed to delete audio take.";
            }
        }
        catch (Exception ex)
        {
            _musicCompareMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            StateHasChanged();
        }
    }


    internal async Task RestoreMusicVersionAsync(int sceneNumber, string takeId)
    {
        _promotingMusicVersion = true;
        _musicCompareMessage = null;
        StateHasChanged();
        try
        {
            var res = await Engine.RestoreMusicVersionAsync(_projectId, sceneNumber, takeId);
            if (res.Ok)
            {
                _musicCompareMessage = "Take restored.";
                var versions = await Engine.GetMusicVersionsAsync(_projectId, sceneNumber);
                _musicVersions = versions?.Versions;
                var trash = await Engine.GetTrashMusicVersionsAsync(_projectId, sceneNumber);
                _musicTrashVersions = trash?.Versions;
                await RefreshMusicCompareUrlsAsync();
            }
            else
            {
                _musicCompareMessage = res.Error ?? "Failed to restore audio take.";
            }
        }
        catch (Exception ex)
        {
            _musicCompareMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            _promotingMusicVersion = false;
            StateHasChanged();
        }
    }

}
