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
    /// <summary>Clip editor dialog domain for the Scenes page.</summary>
    public sealed class ScenesClipForm
    {
        private readonly Scenes S;
        public ScenesClipForm(Scenes host) => S = host;

        internal int? _selectedClip;

        internal ClipSummary? _clip;

        internal (int Scene, int Clip)? _deleteClipTarget;

        internal ClipEditRequest? _clipEditor;

        internal bool _clipEditorIsNew;

        internal HashSet<string> _clipEditorCast = new(StringComparer.OrdinalIgnoreCase);

        internal void SelectClip(int? cn)
        {
            S._message = null; // clear any leftover completion message from a previous scene/action
            _selectedClip = cn;
            _clip = cn is int n
                ? S.List._detail?.Clips.FirstOrDefault(c => c.ClipNumber == n)
                : null;
            S.ClipVer._clipVersions = null;
            S.Playback._clipVideoUrl = null;
            if (cn is int cnv)
            {
                // Force new <video> mount so we never keep a previous composite/clip stream
                S.Playback._clipVideoKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Resolved once, not inline in markup — CacheBust() stamps the current second, so
                // calling it inline re-evaluates on every render (any SignalR/job-poll re-render
                // elsewhere on the page) and gives the <video> a new src each time, which makes the
                // browser reload the resource and restart playback — looks like looping.
                S.Playback._clipServerVideoUrl = S.List._detail is not null
                    ? Scenes.CacheBust(S.Engine.ClipVideoUrl(S._projectId, S.List._detail.SceneNumber, cnv))
                    : null;
                // Gate the <video> behind a loading spinner while we check for a newer local copy —
                // otherwise it renders immediately with the (possibly stale) server fallback src and
                // autoplays that before swapping to the fresh one once the check resolves.
                S.Playback._clipVideoLoading = S.MediaFolder.IsConnected;
                // Stop full-scene autoplay panel if open
                if (S.Playback._showScenePlayer && S.Playback._playingScene == S.List._detail?.SceneNumber)
                {
                    S.Playback._showScenePlayer = false;
                    S.Playback._playingScene = null;
                }
                if (S.List._detail is not null)
                    _ = S.Playback.LoadClipVideoAndTakesCountAsync(S.List._detail.SceneNumber, cnv);
            }
        }

        internal void OpenClipEditor(ClipSummary clip)
        {
            if (S.List._detail is null) return;
            _clipEditorIsNew = false;
            _clipEditor = new ClipEditRequest
            {
                ProjectId = S._projectId,
                Scene = S.List._detail.SceneNumber,
                Clip = clip.ClipNumber,
                VisualPrompt = clip.VisualPrompt,
                NegativePrompt = clip.NegativePrompt,
                Dialogue = clip.Dialogue,
                Speaker = clip.Speaker,
                Delivery = clip.Delivery,
                PronunciationHint = clip.PronunciationHint,
                PrimarySubject = clip.PrimarySubject,
                CharactersOnScreen = new List<string>(clip.CharactersOnScreen),
                ColorPalette = clip.ColorPalette,
                FilmStock = clip.FilmStock,
                DurationSeconds = clip.DurationSeconds,
            };
            _clipEditorCast = new HashSet<string>(clip.CharactersOnScreen, StringComparer.OrdinalIgnoreCase);
        }

        internal void OpenAddClipDialog()
        {
            if (S.List._detail is null) return;
            var nextClip = S.List._detail.Clips.Count == 0 ? 1 : S.List._detail.Clips.Max(c => c.ClipNumber) + 1;
            _clipEditorIsNew = true;
            _clipEditor = new ClipEditRequest
            {
                ProjectId = S._projectId,
                Scene = S.List._detail.SceneNumber,
                Clip = nextClip,
                DurationSeconds = 5,
            };
            _clipEditorCast = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal void CloseClipEditor() => _clipEditor = null;

        internal void ToggleClipEditorCast(string charKey, bool on)
        {
            if (on) _clipEditorCast.Add(charKey);
            else _clipEditorCast.Remove(charKey);
        }

        internal Task OnClipEditorCastToggled((string Key, bool On) args)
        {
            ToggleClipEditorCast(args.Key, args.On);
            return Task.CompletedTask;
        }

        internal async Task SaveClipEditorAsync()
        {
            if (_clipEditor is null || S.List._detail is null) return;

            // Mirror server rules for fast feedback (server still authoritative).
            var error = ValidateClipEditorFields();
            if (error is not null)
            {
                S._error = error;
                return;
            }

            S._busy = true;
            S._error = null;
            try
            {
                await PersistClipEditorAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }

        private string? ValidateClipEditorFields()
        {
            if (string.IsNullOrWhiteSpace(_clipEditor!.VisualPrompt))
                return "Visual prompt is required.";
            if (_clipEditor.DurationSeconds < 0 || _clipEditor.DurationSeconds > 12)
                return "Duration must be 0 (unset) or 3–12 seconds.";
            if (_clipEditor.DurationSeconds is > 0 and < 3)
                return "Duration must be at least 3s (or 0 to leave unset).";
            var dialogueError = ValidateClipDialogue();
            if (dialogueError is not null)
                return dialogueError;
            if (_clipEditorIsNew && (_clipEditor.Clip < 1 || _clipEditor.Clip > 200))
                return "Clip number must be between 1 and 200.";
            return null;
        }

        private string? ValidateClipDialogue()
        {
            var dlg = (_clipEditor!.Dialogue ?? "").Trim();
            var spk = (_clipEditor.Speaker ?? "").Trim();
            var del = (_clipEditor.Delivery ?? "").Trim();
            var delNone = del.Length == 0 || string.Equals(del, "none", StringComparison.OrdinalIgnoreCase);
            if (dlg.Length > 0 && spk.Length == 0)
                return "Dialogue needs a speaker. Pick who says the line, or clear the dialogue.";
            if (dlg.Length > 0 && delNone)
                return "Dialogue needs a delivery: Spoken (on camera), Voiceover (internal), or Off camera.";
            if (spk.Length > 0 && dlg.Length == 0)
                return "Speaker is set but dialogue is empty. Add the line, or set speaker to none.";
            return null;
        }

        private async Task PersistClipEditorAsync()
        {
            _clipEditor!.CharactersOnScreen = _clipEditorCast.ToList();
            var detail = S.List._detail!;
            if (_clipEditorIsNew)
            {
                await S.Engine.AddClipAsync(S._projectId, detail.SceneNumber, _clipEditor);
                S._message = $"Added S{detail.SceneNumber:D2}C{_clipEditor.Clip:D2} — generate its video when ready";
            }
            else
            {
                await S.Engine.UpdateClipAsync(S._projectId, detail.SceneNumber, _clipEditor.Clip, _clipEditor);
                S._message = $"Saved S{detail.SceneNumber:D2}C{_clipEditor.Clip:D2} — Regen the clip to re-render video/audio with the new fields";
            }
            try { await S.Engine.CommitProjectChangesAsync(S._projectId, $"Saved clip S{detail.SceneNumber:D2}C{_clipEditor.Clip:D2}"); }
            catch (Exception ex)
            {
                // Clip fields already saved; commit is best-effort for the uncommitted badge.
                System.Diagnostics.Debug.WriteLine(ex);
            }
            await S.RefreshUncommittedStatusAsync();
            _clipEditor = null;
            await S.List.LoadDetailAsync(detail.SceneNumber);
            var scenesDto = await S.Engine.GetScenesAsync(S._projectId);
            if (scenesDto?.Scenes is not null)
            {
                S.List._scenes = scenesDto.Scenes;
            }
            if (_selectedClip is int sel)
                _clip = S.List._detail!.Clips.FirstOrDefault(c => c.ClipNumber == sel);
        }

        internal void RequestDeleteClip(int scene, int clip) => _deleteClipTarget = (scene, clip);

        internal void CancelDeleteClip() => _deleteClipTarget = null;

        /// <summary>Scene-menu "Delete (N) selected clips…" — one confirm for the whole checked set.</summary>
        internal (int Scene, List<int> Clips)? _deleteClipsTarget;

        internal void RequestDeleteSelectedClips(int scene, IEnumerable<int> clips)
        {
            var list = clips.OrderBy(x => x).ToList();
            if (list.Count == 1) { _deleteClipTarget = (scene, list[0]); return; }
            if (list.Count > 0) _deleteClipsTarget = (scene, list);
        }

        internal void CancelDeleteClips() => _deleteClipsTarget = null;

        internal async Task ConfirmDeleteClipsAsync()
        {
            if (_deleteClipsTarget is not { } target) return;
            S._busy = true;
            S._error = null;
            try
            {
                foreach (var clip in target.Clips)
                    await S.Engine.DeleteClipAsync(S._projectId, target.Scene, clip);
                _deleteClipsTarget = null;
                if (_selectedClip is int sel && target.Clips.Contains(sel))
                {
                    _selectedClip = null;
                    _clip = null;
                }
                S.ClipSel._selectedClips.Clear();
                S._message = $"Deleted {target.Clips.Count} clip(s) from S{target.Scene:D2} — Play scene / Play WIP to refresh the assembled cut";
                await S.List.ReloadListAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }

        internal async Task ConfirmDeleteClipAsync()
        {
            if (_deleteClipTarget is not { } target) return;
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.DeleteClipAsync(S._projectId, target.Scene, target.Clip);
                _deleteClipTarget = null;
                if (_selectedClip == target.Clip)
                {
                    _selectedClip = null;
                    _clip = null;
                }
                S._message = $"Deleted S{target.Scene:D2}C{target.Clip:D2} — Play scene / Play WIP to refresh the assembled cut";
                await S.List.ReloadListAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }
    }
}
