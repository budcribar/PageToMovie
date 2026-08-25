using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Core.Utils;
using PageToMovie.Web.Services;

namespace PageToMovie.Web.Components.Pages;

public enum ReviewTab
{
    Review,
    Play,
    Share,
    Finish
}

public partial class Review
{
    /// <summary>List domain for the Review page. Owns related UI state and behavior.</summary>
    public sealed class ReviewListState
    {
        private readonly Review S;
        public ReviewListState(Review host) => S = host;

        internal ReviewTab? _activeTab = ReviewTab.Review;

        internal readonly HashSet<string> _expandedSceneGroups = new(StringComparer.OrdinalIgnoreCase);

        internal bool _sceneSortAsc = true;

        internal string _sceneSortBy = "number";

        internal List<SceneSummary> _scenes = new();

        internal SceneDetail? _selectedDetail;

        internal int? _selectedScene;

        internal bool _showActivity;


        internal void ToggleSceneSort(string column)
        {
            if (_sceneSortBy == column)
                _sceneSortAsc = !_sceneSortAsc;
            else
            {
                _sceneSortBy = column;
                _sceneSortAsc = true;
            }
        }

        internal string SortArrow(string column)
        {
            if (_sceneSortBy != column) return "⇅";
            return _sceneSortAsc ? "▲" : "▼";
        }


        internal static bool TryParseTab(string? raw, out ReviewTab tab)
        {
            tab = default;
            if (string.IsNullOrWhiteSpace(raw) || int.TryParse(raw, out _))
                return false;
            return Enum.TryParse(raw, ignoreCase: true, out tab) && Enum.IsDefined(tab);
        }

        internal async Task ApplyQueryTabAsync()
        {
            var uri = S.Nav.ToAbsoluteUri(S.Nav.Uri);
            if (!Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("tab", out var values))
                return;
            if (!TryParseTab(values.ToString(), out var tab))
                return;
            await ApplyTabAsync(tab);
        }

        internal async Task ApplyTabAsync(ReviewTab tab)
        {
            if (_activeTab == tab)
                return;
            _activeTab = tab;
            if (tab == ReviewTab.Play)
            {
                await S.Playback.PlayWipAsync();
            }
            else if (tab == ReviewTab.Share)
            {
                S.Share.PrepopulateDemoFields();
                await S.Share.RefreshYouTubeStatusAsync();
            }
        }

        internal async Task ToggleTabAsync(ReviewTab tab)
        {
            if (_activeTab == tab)
            {
                if (tab == ReviewTab.Play)
                {
                    await S.Playback.PlayWipAsync();
                    return;
                }
                _activeTab = null; // Toggle off / collapse card
            }
            else
            {
                await ApplyTabAsync(tab);
            }
        }


        internal Task SetTabReview() => ToggleTabAsync(ReviewTab.Review);

        internal Task SetTabShare() => ToggleTabAsync(ReviewTab.Share);


        internal bool IsSceneGroupExpanded(string rangeStr) => _expandedSceneGroups.Contains(rangeStr);


        internal void ToggleSceneGroupExpand(string rangeStr)
        {
            if (!_expandedSceneGroups.Add(rangeStr))
                _expandedSceneGroups.Remove(rangeStr);
        }


        internal void ToggleAllSceneGroups(bool expand)
        {
            _expandedSceneGroups.Clear();
            if (expand && S.AutoReview._movieReport?.GroupFeedback is { Count: > 0 } groups)
            {
                foreach (var g in groups)
                    _expandedSceneGroups.Add(g.SceneRange);
            }
        }


        internal async Task LoadAsync()
        {
            S._busy = true;
            S._error = null;
            try
            {
                await S.Playback.LoadPreferredVideoEditorAsync();
                var scenes = await S.Engine.GetScenesAsync(S._projectId);
                _scenes = scenes?.Scenes ?? new();
                var log = await S.Engine.GetEditLogAsync(S._projectId);
                S.AutoReview._entries = log?.EditLog?.Entries ?? new();
                var revs = await S.Engine.GetClipReviewsAsync(S._projectId);
                S.AutoReview._reviews = revs?.Reviews ?? new();
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs._job = jobs?.Job;
                await S.Playback.RefreshWipMetaAsync();
                await S.Share.RefreshYouTubeStatusAsync();
                var movieRes = await S.Engine.GetMovieReviewReportAsync(S._projectId);
                S.AutoReview._movieReport = movieRes?.Report;
                if (_selectedScene is int sn)
                    await LoadSelectedDetailAsync(sn);
                await S.Playback.RefreshLocalPlayableAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task SoftLoadAsync()
        {
            try
            {
                var log = await S.Engine.GetEditLogAsync(S._projectId);
                S.AutoReview._entries = log?.EditLog?.Entries ?? new();
                var revs = await S.Engine.GetClipReviewsAsync(S._projectId);
                S.AutoReview._reviews = revs?.Reviews ?? new();
                var scenes = await S.Engine.GetScenesAsync(S._projectId);
                _scenes = scenes?.Scenes ?? new();
                await S.Playback.RefreshWipMetaAsync();
                if (_selectedScene is int snSelected)
                    await TryLoadDraftsForSceneAsync(snSelected);
                if (_selectedScene is int sn)
                    await LoadSelectedDetailAsync(sn);
                await S.Playback.RefreshLocalPlayableAsync();
            }
            catch { /* ignore */ }
        }


        internal async Task TryLoadDraftsForSceneAsync(int scene)
        {
            var n = ClipCountFor(scene);
            for (var c = 1; c <= n; c++)
            {
                try
                {
                    var d = await S.Engine.GetClipAutoReviewDraftAsync(S._projectId, scene, c);
                    if (d is not null)
                        S.AutoReview._drafts[ReviewAutoReview.ClipKey(scene, c)] = d;
                }
                catch { /* optional */ }
            }
        }


        internal async Task LoadSelectedDetailAsync(int sn)
        {
            try
            {
                var dto = await S.Engine.GetSceneDetailAsync(S._projectId, sn);
                _selectedDetail = dto?.Scene;
            }
            catch
            {
                _selectedDetail = null;
            }
        }


        /// <summary>Same-scene click closes the clip-review card (Clips link / S-badge toggle).</summary>
        internal static int? ToggleSelectedScene(int? current, int scene) =>
            current == scene ? null : scene;

        internal void DismissClipReview()
        {
            _selectedScene = null;
            _selectedDetail = null;
            S.Playback.ClearClipPlayError();
            S.AutoReview.CloseApplyPanel();
        }

        internal async Task SelectSceneAsync(int scene)
        {
            if (ToggleSelectedScene(_selectedScene, scene) is null)
            {
                DismissClipReview();
                return;
            }

            _selectedScene = scene;
            S.AutoReview.CloseApplyPanel();
            await LoadSelectedDetailAsync(scene);
            await TryLoadDraftsForSceneAsync(scene);
        }


        internal int ClipCountFor(int scene) =>
            _scenes.FirstOrDefault(s => s.SceneNumber == scene)?.ClipCount ?? 0;


        internal int ClipCountOnDisk(int scene) =>
            _scenes.FirstOrDefault(s => s.SceneNumber == scene)?.ClipsOnDisk ?? 0;


        internal bool SceneHasComposite(int scene) =>
            _scenes.FirstOrDefault(s => s.SceneNumber == scene)?.CompositeExists == true;


        internal bool ClipOnDisk(int scene, int clip)
        {
            if (_selectedDetail is { } d && d.SceneNumber == scene)
            {
                var c = d.Clips.FirstOrDefault(x => x.ClipNumber == clip);
                if (c is not null) return c.OnDisk;
            }
            // Fall back: this clip only — a sibling hole must not hide a present clip.
            var s = _scenes.FirstOrDefault(x => x.SceneNumber == scene);
            if (s is null) return false;
            return ScenePlayGate.IsClipPlayableFromSceneMissingList(clip, s.ClipsMissingServerVideo);
        }

        /// <summary>
        /// Per-clip Play enablement: current take (local folder or server MP4 /
        /// <c>.current.json</c>), not a sidecar marker and not "the rest of the
        /// scene is complete." <c>SizeBytes</c> 0 / unknown does not disable.
        /// </summary>
        internal static bool DecideClipIsPlayable(
            long? detailSizeBytes,
            IReadOnlyList<int>? missingServerVideo,
            int clip,
            bool hasLocalVideo)
        {
            if (hasLocalVideo)
                return true;
            if (detailSizeBytes is long size)
                return ScenePlayGate.IsClipPlayable(size);
            return ScenePlayGate.IsClipPlayableFromSceneMissingList(clip, missingServerVideo);
        }

        internal bool ClipIsPlayable(int scene, int clip)
        {
            var hasLocal = S.Playback.HasCachedLocalVideo(scene, clip);
            if (_selectedDetail is { } d && d.SceneNumber == scene)
            {
                var c = d.Clips.FirstOrDefault(x => x.ClipNumber == clip);
                if (c is not null)
                    return DecideClipIsPlayable(c.SizeBytes, null, clip, hasLocal);
            }

            var s = _scenes.FirstOrDefault(x => x.SceneNumber == scene);
            if (s is null) return hasLocal;
            return DecideClipIsPlayable(null, s.ClipsMissingServerVideo, clip, hasLocal);
        }


        internal async Task ApproveAsync(int scene)
        {
            S._busy = true;
            S._error = null;
            try
            {
                await S.Jobs.EnsureHubAsync();
                // Approve is review state only — Play stitches in the browser (no server remux).
                await S.Engine.ApproveSceneAsync(S._projectId, scene, S.AutoReview._note);
                S._message = $"Approved S{scene:D2}";
                await SoftLoadAsync();
                var jobs = await S.Engine.GetJobAsync();
                S.Jobs._job = jobs?.Job;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }

    }
}
