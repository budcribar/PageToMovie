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

    private string StripActive
    {
        get
        {
            if (_phase == Phase.Movie) return "film";
            if (_phase is Phase.Record or Phase.Done) return "cast";
            return "book";
        }
    }

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
    internal string _voiceCharacterLabel = "Narrator";
    /// <summary>True when the story has no character confidently identified as the narrator — the
    /// user must pick which speaking character to voice instead of us silently guessing one.</summary>
    internal bool _needsCharacterPick;
    internal List<CharacterSummary> _narratorCandidates = new();
    internal bool _busy;
    internal string? _error;
    internal string? _message;
    private bool _sessionReady;
    private EngineApiClient? _boundEngine;

    /// <summary>
    /// Queue a render without waiting. Safe from OnInitializedAsync / LoadStoriesAsync.
    /// Never replace this with <c>await InvokeAsync(StateHasChanged)</c> during init —
    /// the renderer cannot process InvokeAsync until OnInitializedAsync returns, which
    /// deadlocks ("Loading stories…" forever; the 8s timeout never appears).
    /// </summary>
    internal void Notify()
    {
        _ = _storiesLoading; // instance-bound for S2325 (Blazor partial hides StateHasChanged)
        try
        {
            StateHasChanged();
        }
        catch (InvalidOperationException)
        {
            // No renderer (unit tests).
        }
    }

    /// <summary>Test seam so LoadStoriesAsync can run without a Blazor renderer.</summary>
    internal void BindEngine(EngineApiClient engine) => _boundEngine = engine;

    /// <summary>
    /// Marshal a paint onto the renderer after OnAfterRender has returned
    /// (hydrate/resume continuation). Safe to await InvokeAsync here.
    /// Do not call this from OnInitializedAsync or LoadStoriesAsync — that deadlocks.
    /// </summary>
    internal virtual async Task PaintAsync()
    {
        _ = _storiesLoading;
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (InvalidOperationException)
        {
            // No renderer (unit tests).
        }
    }

    /// <summary>
    /// Public catalog — do not wait on JS session hydrate. Login forceLoad lands here before
    /// interactive JS is safe; awaiting sessionStorage in OnInitializedAsync hangs forever
    /// ("Loading stories…") because the try/catch never sees a throw.
    /// </summary>
    protected override Task OnInitializedAsync() => LoadStoriesAsync();

    /// <summary>
    /// Restore session after first interactive render (JS available), then optionally resume
    /// an in-progress simple-voice project. Must return immediately: Blazor defers further
    /// renders of this component until OnAfterRenderAsync completes.
    /// </summary>
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _sessionReady)
            return Task.CompletedTask;
        _sessionReady = true;
        _ = ResumeSessionAfterPaintAsync();
        return Task.CompletedTask;
    }

    private async Task ResumeSessionAfterPaintAsync()
    {
        try
        {
            try { await Session.EnsureHydratedAsync(); }
            catch { /* JS session restore; the public list does not need it */ }
            await TryResumeExistingProjectAsync();
        }
        catch
        {
            /* resume is best-effort; never block the story list */
        }
        await PaintAsync();
    }

    internal async Task LoadStoriesAsync()
    {
        _storiesLoading = true;
        _storiesError = null;
        // Sync StateHasChanged only. Awaiting PaintAsync/InvokeAsync here deadlocks
        // OnInitializedAsync: the renderer is busy running this method and cannot
        // process InvokeAsync until it returns, so the HTTP call never starts.
        Notify();
        try
        {
            var (stories, error) = await (_boundEngine ?? Engine).ListForkableProjectsAsync();
            _forkableStories = stories;
            _storiesError = error;
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

    internal static string TimeoutOrFail(Exception ex) =>
        ex is OperationCanceledException or TaskCanceledException or TimeoutException
            ? EngineApiClient.ForkableStoriesTimeoutMessage
            : EngineApiClient.ForkableStoriesFailMessage;

    internal static bool ShouldResumeRecordPhase(bool isLoggedIn, bool isSimpleVoiceProject, string? projectId) =>
        isLoggedIn && isSimpleVoiceProject && !string.IsNullOrEmpty(projectId);

    private async Task TryResumeExistingProjectAsync()
    {
        if (!ShouldResumeRecordPhase(Session.IsLoggedIn, ActiveProject.IsSimpleVoice, ActiveProject.ProjectId))
            return;
        _projectId = ActiveProject.ProjectId;
        _projectLabel = ActiveProject.Label;
        _phase = Phase.Record;
        await EnsureVoiceModelAsync();
        await ResolveNarratorKeyAsync();
        if (!_needsCharacterPick)
            await RefreshSampleStateAsync();
    }

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
            await ActiveProject.SelectAsync(
                Engine,
                forkId,
                fork?.Title ?? story.Title,
                parentProjectId: fork?.ParentProjectId ?? story.Id,
                studioPath: ProjectStudioPaths.SimpleVoice);

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
        _voiceCharacterLabel = "Narrator";
        _needsCharacterPick = false;
        _narratorCandidates = new();
        if (string.IsNullOrEmpty(_projectId)) return;
        try
        {
            var dto = await Engine.GetCharactersAsync(_projectId);
            var list = dto?.Characters ?? new();
            if (list.Count == 0) return;

            var speakers = list.Where(c => c.Speaks).ToList();
            if (speakers.Count == 0)
                speakers = list.Where(c => !c.IsGroup).ToList();
            if (speakers.Count == 0)
                speakers = list;

            if (speakers.Count == 1)
            {
                ApplyCharacter(speakers[0]);
                return;
            }

            var already = speakers.FirstOrDefault(c => !string.IsNullOrEmpty(c.VoiceProviderVoiceId));
            if (already is not null)
            {
                ApplyCharacter(already);
                _narratorCandidates = speakers;
                return;
            }

            // Mary has Teacher + Mary (+ kids). Always let the user pick who to replace.
            _narratorCandidates = speakers;
            _needsCharacterPick = true;
        }
        catch
        {
            // Seed will be created on upload if cast not built yet.
        }
    }

    internal void ApplyCharacter(CharacterSummary c)
    {
        _narratorKey = c.Key ?? "";
        _voiceCharacterLabel = string.IsNullOrWhiteSpace(c.DisplayName)
            ? CastKindClassifier.StripPrefix(c.Key)
            : c.DisplayName;
    }

    /// <summary>User picked who to voice when no narrator could be confidently identified.</summary>
    internal async Task PickCharacterAsync(CharacterSummary c)
    {
        if (string.IsNullOrEmpty(c.Key)) return;
        ApplyCharacter(c);
        _needsCharacterPick = false;
        _error = null;
        await EnsureVoiceModelAsync();
        await RefreshSampleStateAsync();
        Notify();
    }

    internal void ChangeCharacter()
    {
        _voiceReady = false;
        _needsCharacterPick = _narratorCandidates.Count > 0;
        _error = null;
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
