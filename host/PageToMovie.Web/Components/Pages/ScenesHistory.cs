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
    /// <summary>History domain for the Scenes page. Owns related UI state and behavior.</summary>
    internal sealed class ScenesHistory
    {

    private readonly Scenes S;
    public ScenesHistory(Scenes host) => S = host;



    internal bool _showSceneHistory;


    internal int _historySceneNumber;


    internal bool _loadingHistory;


    internal bool _revertingScene;


    internal string? _sceneRevertMessage;


    internal List<SceneCommitHistoryItem>? _sceneHistory;



    // ---- Inline scene VERSION history panel (SceneVersionHistory component, P3) — separate from
    // the git-commit history modal above; distinct state so the two panels never collide. ----
    internal bool _showInlineSceneHistory;



    internal async Task OpenSceneHistoryAsync(int sceneNumber)
    {
        _historySceneNumber = sceneNumber;
        _showSceneHistory = true;
        _loadingHistory = true;
        _sceneRevertMessage = null;
        S.StateHasChanged();

        try
        {
            var res = await S.Engine.GetSceneGitHistoryAsync(S._projectId, sceneNumber);
            _sceneHistory = res?.History;
        }
        catch (Exception ex)
        {
            S._error = $"Failed to load scene history: {ex.Message}";
        }
        finally
        {
            _loadingHistory = false;
            S.StateHasChanged();
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
        await S.SoftReloadAsync();
    }



    internal async Task RevertSceneToVersionAsync(int sceneNumber, string commitHash)
    {
        _revertingScene = true;
        _sceneRevertMessage = null;
        S.StateHasChanged();

        try
        {
            var res = await S.Engine.RevertSceneToCommitAsync(S._projectId, sceneNumber, commitHash);
            if (res.Ok)
            {
                _sceneRevertMessage = $"Successfully reverted Scene {sceneNumber:D2} to version {commitHash[..Math.Min(8, commitHash.Length)]}.";
                if (S._detail is not null && S._detail.SceneNumber == sceneNumber)
                {
                    S._detail = (await S.Engine.GetSceneDetailAsync(S._projectId, sceneNumber))?.Scene;
                }
                var scenesDto = await S.Engine.GetScenesAsync(S._projectId);
                if (scenesDto?.Scenes is not null)
                {
                    S._scenes = scenesDto.Scenes;
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
            S.StateHasChanged();
        }
    }


    }
}
