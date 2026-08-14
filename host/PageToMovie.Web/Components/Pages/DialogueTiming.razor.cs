using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public partial class DialogueTiming
{

    private static readonly System.Globalization.CultureInfo Ci = System.Globalization.CultureInfo.InvariantCulture;

    [Parameter, SupplyParameterFromQuery(Name = "scene")]
    public int? SceneParam { get; set; }

    private string? _projectId;
    private List<ProjectInfo> _projects = new();
    internal bool _loading = true;
    internal bool _busy;
    private string? _error;
    internal string? _status;

    private List<EngineApiClient.DialogueSceneLinesDto> _sceneLines = new();
    private int _scene;
    private DialogueTimingDoc? _doc;
    private DialogueTimingScene? _current;

    private readonly HashSet<DialogueTimingRow> _editing = new();
    internal bool _dirty;

    private readonly Dictionary<int, string> _sceneUrls = new();

    private string AnalyzeButtonLabel
    {
        get
        {
            if (_busy) return "Analyzing…";
            if (_current is null) return "Analyze scene (speech-to-text)";
            return "Re-analyze scene";
        }
    }

    private static string TimingRowClass(DialogueTimingRow row)
    {
        if (row.Reviewed) return "dt-row dt-ok";
        if (string.IsNullOrEmpty(row.ScriptText)) return "dt-row dt-extra";
        if (row.MatchScore >= 0.7) return "dt-row";
        return "dt-row dt-warn";
    }

    private static string HeardWordClass(HashSet<string> scriptSet, string text)
    {
        if (scriptSet.Count == 0) return "dt-w";
        return scriptSet.Contains(NormTok(text)) ? "dt-w dt-hit" : "dt-w dt-extra-w";
    }

    protected override async Task OnInitializedAsync()
    {
        (_projects, _projectId, _error) = await ScenePlaybackSupport.ResolveProjectSelectionAsync(Session, Engine, ActiveProject);

        _loading = false;
        if (!string.IsNullOrEmpty(_projectId))
            await LoadProjectAsync();
    }

    private async Task SelectProjectAsync(ChangeEventArgs e)
    {
        _projectId = e.Value?.ToString();
        await LoadProjectAsync();
    }

    private async Task LoadProjectAsync()
    {
        _sceneLines = new(); _doc = null; _current = null; _scene = 0; _sceneUrls.Clear();
        _editing.Clear(); _dirty = false;
        _error = null; _status = null;
        if (string.IsNullOrEmpty(_projectId)) { StateHasChanged(); return; }
        try
        {
            _sceneLines = await Engine.GetDialogueLinesAsync(_projectId);
            _doc = await Engine.GetDialogueTimingAsync(_projectId);
            if (_sceneLines.Count > 0)
            {
                _scene = _sceneLines[0].Scene;
                // Honor ?scene=N from a link (e.g. the Scenes editor) when that scene has dialogue.
                if (SceneParam is int sp && _sceneLines.Any(s => s.Scene == sp))
                    _scene = sp;
                _current = _doc?.Scenes.FirstOrDefault(s => s.Scene == _scene);
            }
        }
        catch (Exception ex) { _error = ex.Message; }
        StateHasChanged();
    }

    private Task SelectSceneAsync(ChangeEventArgs e)
    {
        _scene = int.TryParse(e.Value?.ToString(), out var n) ? n : 0;
        _current = _doc?.Scenes.FirstOrDefault(s => s.Scene == _scene);
        _editing.Clear(); _dirty = false;
        _status = null;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private bool HasCached(int scene) => _doc?.Scenes.Any(s => s.Scene == scene) == true;

    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrEmpty(_projectId) || _scene <= 0) return;
        var lines = _sceneLines.FirstOrDefault(s => s.Scene == _scene)?.Lines ?? new();
        if (lines.Count == 0) { _status = "No script lines for this scene."; return; }

        _busy = true; _error = null; _status = "Starting…";
        StateHasChanged();
        try
        {
            var scene = await Timing.AnalyzeSceneAsync(
                _projectId, _scene, lines, s => { _status = s; _ = InvokeAsync(StateHasChanged); });
            if (scene is null) { _status = "Couldn't load that scene's clips."; return; }

            await Engine.SaveDialogueTimingSceneAsync(_projectId, scene);
            _current = scene;
            _editing.Clear(); _dirty = false;
            _doc ??= new DialogueTimingDoc { ProjectId = _projectId };
            _doc.Scenes.RemoveAll(s => s.Scene == _scene);
            _doc.Scenes.Add(scene);
            _status = "Done.";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; StateHasChanged(); }
    }

    private void ToggleEdit(DialogueTimingRow row)
    {
        if (!_editing.Add(row)) _editing.Remove(row);
    }

    internal void MarkDirty() => _dirty = true;

    // Recompute the match % live as the script/heard text or window is edited.
    private void OnRowChanged(DialogueTimingRow row)
    {
        row.MatchScore = Math.Round(Overlap(row.ScriptText, row.HeardText), 3);
        _dirty = true;
    }

    // Re-run speech-to-text over this row's (possibly edited) window and refresh heard text + timings.
    private async Task ReSttAsync(DialogueTimingRow row)
    {
        if (string.IsNullOrEmpty(_projectId) || row.WindowEndSec <= row.WindowStartSec) return;
        _busy = true; _status = "Re-running speech-to-text…"; StateHasChanged();
        try
        {
            var url = await GetSceneUrlAsync(_scene);
            if (string.IsNullOrEmpty(url)) { _status = "Couldn't load the scene."; return; }
            var audio = await Js.InvokeAsync<byte[]>(
                "PageToMovieFfmpeg.extractAudioSegmentAsync", url, row.WindowStartSec, row.WindowEndSec);
            if (audio is null || audio.Length < 256) { _status = "No audio in that window."; return; }

            var transcript = await Engine.TranscribeSegmentAsync(audio, "segment.wav");
            row.HeardText = (transcript?.Text ?? "").Trim();
            row.Words = (transcript?.Words ?? new())
                .Where(tw => !string.IsNullOrWhiteSpace(tw.Text) &&
                             !string.Equals(tw.Type, "spacing", StringComparison.OrdinalIgnoreCase))
                .Select(tw => new VoiceCaptureWord { Text = tw.Text.Trim(), StartSec = Math.Max(0, tw.Start), EndSec = Math.Max(0, tw.End) })
                .ToList();
            row.MatchScore = Math.Round(Overlap(row.ScriptText, row.HeardText), 3);
            _dirty = true; _status = "Updated.";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; StateHasChanged(); }
    }

    private async Task SaveSceneAsync()
    {
        if (string.IsNullOrEmpty(_projectId) || _current is null) return;
        _busy = true; StateHasChanged();
        try
        {
            if (await Engine.SaveDialogueTimingSceneAsync(_projectId, _current))
            {
                _doc ??= new DialogueTimingDoc { ProjectId = _projectId };
                _doc.Scenes.RemoveAll(s => s.Scene == _current.Scene);
                _doc.Scenes.Add(_current);
                _dirty = false; _status = "Saved.";
            }
            else _error = "Save failed.";
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _busy = false; StateHasChanged(); }
    }

    private static double Overlap(string? script, string? heard)
    {
        var e = SplitWords(script).Select(NormTok).Where(s => s.Length > 0).ToList();
        if (e.Count == 0) return 0;
        var h = new HashSet<string>(SplitWords(heard).Select(NormTok).Where(s => s.Length > 0));
        return (double)e.Count(w => h.Contains(w)) / e.Count;
    }

    private async Task PlayWindowAsync(DialogueTimingRow row)
    {
        if (string.IsNullOrEmpty(_projectId) || row.WindowEndSec <= row.WindowStartSec) return;
        try
        {
            var url = await GetSceneUrlAsync(_scene);
            if (string.IsNullOrEmpty(url)) return;
            var res = await Js.InvokeAsync<ExtractUrlResult>(
                "PageToMovieFfmpeg.extractAudioSegmentToUrlAsync", url, row.WindowStartSec, row.WindowEndSec);
            if (res is { Success: true } && !string.IsNullOrEmpty(res.Url))
                await Js.InvokeAsync<bool>("PageToMovieFfmpeg.playAudioAsync", res.Url);
        }
        catch { /* ignore playback errors */ }
    }

    private Task<string?> GetSceneUrlAsync(int scene) =>
        ScenePlaybackSupport.GetSceneUrlAsync(Stitch, _projectId, scene, _sceneUrls);

    private static List<string> SplitWords(string? t) =>
        string.IsNullOrWhiteSpace(t) ? new() : t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static HashSet<string> TokenSet(string? t) =>
        new(SplitWords(t).Select(NormTok).Where(s => s.Length > 0));

    private static string NormTok(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Pretty(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "" : key.Replace("Character_", "").Replace('_', ' ').Trim();

    private sealed class ExtractUrlResult { public bool Success { get; set; } public string? Url { get; set; } public string? Error { get; set; } }
}
