using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Util;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class SimpleVoice
{
    internal enum Phase { Pick, Record, Done, Movie }

    internal Phase _phase = Phase.Pick;
    internal List<ForkableStoryDto> _forkableStories = new();
    internal bool _storiesLoading = true;
    internal string? _storiesError;
    internal bool _voiceReady; // narrator has a cloned voice (freshly captured or already applied) → show "make movie"
    internal bool _dubbing;
    internal string? _dubStatus;
    internal string? _dubbedUrl;
    internal string? _dubSummary;
    internal string? _projectId;
    internal string? _projectLabel;
    internal string _narratorKey = "Character_Narrator";
    /// <summary>True when the story has no character confidently identified as the narrator — the
    /// user must pick which speaking character to voice instead of us silently guessing one.</summary>
    internal bool _needsCharacterPick;
    internal List<CharacterSummary> _narratorCandidates = new();
    internal bool _busy;
    internal string? _error;
    internal string? _message;

    /// <summary>Child phase components mutate host state; ensure shell re-renders (phase switch).</summary>
    internal void Notify() => StateHasChanged();

    protected override async Task OnInitializedAsync()
    {
        try { await Session.EnsureHydratedAsync(); }
        catch { /* list is public */ }

        await LoadStoriesAsync();

        if (Session.IsLoggedIn
            && ActiveProject.IsSimpleVoice
            && !string.IsNullOrEmpty(ActiveProject.ProjectId))
        {
            _projectId = ActiveProject.ProjectId;
            _projectLabel = ActiveProject.Label;
            _phase = Phase.Record;
            await EnsureVoiceModelAsync();
            await ResolveNarratorKeyAsync();
            if (!_needsCharacterPick)
                await RefreshSampleStateAsync();
        }
    }

    internal async Task LoadStoriesAsync()
    {
        _storiesLoading = true;
        _storiesError = null;
        Notify();
        try
        {
            _forkableStories = await Engine.ListForkableProjectsAsync();
        }
        catch (Exception ex)
        {
            _forkableStories = new();
            _storiesError = TimeoutOrFail(ex);
        }
        finally
        {
            _storiesLoading = false;
            Notify();
        }
    }

    private static string TimeoutOrFail(Exception ex) =>
        ex is OperationCanceledException or TaskCanceledException or TimeoutException
            ? "Stories took too long to load. Try again."
            : "Could not load stories. Try again.";

    internal async Task SelectStoryAsync(ForkableStoryDto story)
    {
        if (!Session.IsLoggedIn)
        {
            Nav.NavigateTo("login?returnUrl=/simple-voice");
            return;
        }
        _busy = true;
        _error = null;
        _message = null;
        try
        {
            // Make the user's own copy of the film (fork), then set up the voice step on it.
            var fork = await Engine.ForkProjectAsync(story.Id);
            var forkId = fork?.Id;
            if (string.IsNullOrEmpty(forkId))
                throw new InvalidOperationException("Could not open that story.");

            try { await Engine.SetStudioPathAsync(forkId, ProjectStudioPaths.SimpleVoice); }
            catch { /* older API */ }
            await ActiveProject.SelectAsync(Engine, forkId, fork?.Title ?? story.Title, studioPath: ProjectStudioPaths.SimpleVoice);

            _projectId = forkId;
            _projectLabel = fork?.Title ?? story.Title;
            _voiceReady = false;
            _dubbedUrl = null;
            _phase = Phase.Record;
            await EnsureVoiceModelAsync();
            await ResolveNarratorKeyAsync();
            if (!_needsCharacterPick)
                await RefreshSampleStateAsync();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _busy = false;
            Notify();
        }
    }

    internal void BackToPick()
    {
        _phase = Phase.Pick;
        _voiceReady = false;
        _error = null;
        _message = null;
        Notify();
    }

    private async Task ResolveNarratorKeyAsync()
    {
        _narratorKey = "Character_Narrator";
        _needsCharacterPick = false;
        _narratorCandidates = new();
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            var dto = await Engine.GetCharactersAsync(_projectId);
            var list = dto?.Characters ?? new();
            if (list.Count == 0) return;

            var confident = list.FirstOrDefault(c =>
                                (c.Key?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false)
                                || (c.DisplayName?.Contains("narrator", StringComparison.OrdinalIgnoreCase) ?? false))
                            ?? list.FirstOrDefault(c => c.VoiceOnly);
            if (!string.IsNullOrEmpty(confident?.Key))
            {
                _narratorKey = confident.Key;
                return;
            }

            // No character reads as "the narrator" — don't silently guess the first character in the
            // list (that could be anyone). Let the user pick who they want to voice instead.
            //
            // Deliberately NOT filtered to c.Speaks: that flag means "has a quoted-dialogue cue in
            // the screenplay", but pure third-person narration (e.g. a picture book with no spoken
            // lines at all) has no such character — filtering on it left every character excluded,
            // which silently fell through to the same list[0] guess this branch exists to avoid.
            _narratorCandidates = list;
            _needsCharacterPick = true;
        }
        catch
        {
            // Seed will be created on upload if cast not built yet.
        }
    }

    /// <summary>User picked who to voice when no narrator could be confidently identified.</summary>
    internal async Task PickCharacterAsync(CharacterSummary c)
    {
        if (string.IsNullOrEmpty(c.Key)) return;
        _narratorKey = c.Key;
        _needsCharacterPick = false;
        _error = null;
        await EnsureVoiceModelAsync();
        await RefreshSampleStateAsync();
        Notify();
    }

    private async Task EnsureVoiceModelAsync()
    {
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            var cfgDto = await Engine.GetConfigAsync(_projectId);
            var map = cfgDto?.Config;
            string voice = "none";
            if (map is not null && map.TryGetValue("voice_model_name", out var el)
                && el.ValueKind == System.Text.Json.JsonValueKind.String)
                voice = el.GetString() ?? "none";
            if (!string.IsNullOrWhiteSpace(voice)
                && !voice.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !voice.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                return;
            var defaultClone = SupportedModelCatalog.FirstEnabledVoiceCloneModelId();
            if (string.IsNullOrWhiteSpace(defaultClone))
                return;
            await Engine.SaveConfigAsync(_projectId, new Dictionary<string, object?>
            {
                ["voice_model_name"] = defaultClone,
            });
        }
        catch { /* non-fatal */ }
    }

    private async Task RefreshSampleStateAsync()
    {
        _voiceReady = false;
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            var dto = await Engine.GetCharactersAsync(_projectId);
            var c = dto?.Characters?.FirstOrDefault(x =>
                string.Equals(x.Key, _narratorKey, StringComparison.OrdinalIgnoreCase));
            // Already-cloned narrator (a provider voice id exists) → skip capture, go to "make movie".
            if (c is not null && !string.IsNullOrEmpty(c.VoiceProviderVoiceId))
            {
                _voiceReady = true;
                _phase = Phase.Done;
            }
        }
        catch { /* ignore */ }
    }

    // Fired by <VoiceCaptureStep> once the cloned voice is built + applied — advance to "make movie".
    internal async Task OnVoiceReadyAsync()
    {
        _voiceReady = true;
        _phase = Phase.Done;
        _error = null;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Not happy with the take? Drop back into the recorder for the same character — a
    /// fresh <VoiceCaptureStep> instance re-runs its own phrase prep and replaces the sample.</summary>
    internal void BeginReRecord()
    {
        _voiceReady = false;
        _error = null;
        _message = null;
        Notify();
    }

    internal void BackToVoiceFromMovie()
    {
        _phase = Phase.Done;
        _dubbedUrl = null;
        _error = null;
        Notify();
    }

    internal async Task MakeMovieAsync()
    {
        if (string.IsNullOrEmpty(_projectId)) return;
        // Clips + synthesized audio live in the browser media folder — it must be connected.
        if (!MediaFolder.IsConnected)
        {
            var connected = await MediaFolder.ConnectFolderAsync();
            if (!connected && !MediaFolder.IsConnected)
            {
                _error = "Connect your media folder so we can build your movie.";
                Notify();
                return;
            }
        }
        _phase = Phase.Movie; // step 3
        _dubbing = true;
        _busy = true;
        _error = null;
        _message = null;
        _dubbedUrl = null;
        _dubStatus = "Starting…";
        Notify();
        try
        {
            var res = await VoiceSub.DubMovieInMyVoiceAsync(
                _projectId,
                charKey: _narratorKey,
                onProgress: s => { _dubStatus = s; _ = InvokeAsync(StateHasChanged); });
            _dubSummary = $"{res.ClipsDubbed} scene(s) in your voice"
                          + (res.ClipsFailed > 0 ? $" · {res.ClipsFailed} skipped" : "");
            if (res.Ok && !string.IsNullOrWhiteSpace(res.DownloadUrl))
                _dubbedUrl = res.DownloadUrl;
            else
                _error = res.Error ?? "Could not make your movie.";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _dubbing = false;
            _busy = false;
            _dubStatus = null;
            Notify();
        }
    }

    internal async Task DownloadDubbedAsync()
    {
        if (string.IsNullOrWhiteSpace(_dubbedUrl)) return;
        var name = string.IsNullOrWhiteSpace(_projectLabel) ? "movie" : _projectLabel;
        await VoiceSub.DownloadAsync(_dubbedUrl, $"{name}-in-my-voice.mp4");
    }

    internal async Task SwitchToFullStudioAsync()
    {
        if (!string.IsNullOrEmpty(_projectId))
        {
            try
            {
                await Engine.SetStudioPathAsync(_projectId, ProjectStudioPaths.Full);
                ActiveProject.Set(ActiveProject.ProjectId, ActiveProject.Label, ActiveProject.ParentProjectId, ProjectStudioPaths.Full);
            }
            catch { /* ignore */ }
        }
        Nav.NavigateTo("characters");
    }
}
