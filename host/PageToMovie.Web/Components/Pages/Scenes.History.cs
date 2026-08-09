using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

// Domain: History — partial methods/properties for the Scenes page
public partial class Scenes
{

    internal async Task OpenSceneHistoryAsync(int sceneNumber)
    {
        _historySceneNumber = sceneNumber;
        _showSceneHistory = true;
        _loadingHistory = true;
        _sceneRevertMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.GetSceneGitHistoryAsync(_projectId, sceneNumber);
            _sceneHistory = res?.History;
        }
        catch (Exception ex)
        {
            _error = $"Failed to load scene history: {ex.Message}";
        }
        finally
        {
            _loadingHistory = false;
            StateHasChanged();
        }
    }


    internal void CloseSceneHistory()
    {
        _showSceneHistory = false;
        _sceneHistory = null;
        _sceneRevertMessage = null;
    }


    internal void HideSceneHistory() => _showInlineSceneHistory = false;


    internal async Task OnSceneHistoryRestored()
    {
        // A snapshot was restored server-side — refresh the scene list/detail to reflect it.
        _showInlineSceneHistory = false;
        await SoftReloadAsync();
    }


    internal async Task RevertSceneToVersionAsync(int sceneNumber, string commitHash)
    {
        _revertingScene = true;
        _sceneRevertMessage = null;
        StateHasChanged();

        try
        {
            var res = await Engine.RevertSceneToCommitAsync(_projectId, sceneNumber, commitHash);
            if (res.Ok)
            {
                _sceneRevertMessage = $"Successfully reverted Scene {sceneNumber:D2} to version {commitHash[..Math.Min(8, commitHash.Length)]}.";
                if (_detail is not null && _detail.SceneNumber == sceneNumber)
                {
                    _detail = (await Engine.GetSceneDetailAsync(_projectId, sceneNumber))?.Scene;
                }
                var scenesDto = await Engine.GetScenesAsync(_projectId);
                if (scenesDto?.Scenes is not null)
                {
                    _scenes = scenesDto.Scenes;
                }
            }
            else
            {
                _sceneRevertMessage = res.Error ?? "Failed to revert scene.";
            }
        }
        catch (Exception ex)
        {
            _sceneRevertMessage = $"Revert failed: {ex.Message}";
        }
        finally
        {
            _revertingScene = false;
            StateHasChanged();
        }
    }

}
