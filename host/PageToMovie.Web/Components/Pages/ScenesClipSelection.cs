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
    /// <summary>Clip multi-select / sort domain for the Scenes page.</summary>
    public sealed class ScenesClipSelection
    {
        private readonly Scenes S;
        public ScenesClipSelection(Scenes host) => S = host;

        /// <summary>Clip table: when true, sort by duration; else keep plan order (clip number).</summary>
        internal bool _clipSortByDuration;

        internal bool _clipSortAscending = true;

        internal string DurationSortArrow
        {
            get
            {
                if (!_clipSortByDuration) return "⇅";
                return _clipSortAscending ? "▲" : "▼";
            }
        }

        /// <summary>Multi-select clip numbers within the currently open scene's clip table, for batch regen.</summary>
        internal readonly HashSet<int> _selectedClips = new();

        internal void ToggleClipDurationSort()
        {
            if (_clipSortByDuration)
                _clipSortAscending = !_clipSortAscending;
            else
            {
                _clipSortByDuration = true;
                _clipSortAscending = true;
            }
        }

        /// <summary>Clips in open scene, optionally sorted by actual/plan duration.</summary>
        internal IEnumerable<ClipSummary> SortedDetailClips
        {
            get
            {
                if (S.List._detail?.Clips is null)
                    return Array.Empty<ClipSummary>();
                if (!_clipSortByDuration)
                    return S.List._detail.Clips.OrderBy(c => c.ClipNumber);
                static double Dur(ClipSummary c) =>
                    c.ActualDurationSeconds ?? (c.DurationSeconds > 0 ? c.DurationSeconds : 0);
                return _clipSortAscending
                    ? S.List._detail.Clips.OrderBy(Dur).ThenBy(c => c.ClipNumber)
                    : S.List._detail.Clips.OrderByDescending(Dur).ThenBy(c => c.ClipNumber);
            }
        }

        /// <summary>
        /// True when this exact clip is the one currently being (re)generated — the server updates
        /// the job's Scene/Clip to whichever item it's actively working on, for both single-clip
        /// regen (kind "scene") and multi-select batch regen (kind "batch"). Used to avoid showing
        /// a stale "on disk" pill or letting Play open the file mid-overwrite.
        /// </summary>
        internal bool IsClipGenBusy(int clipNumber)
        {
            if (S.List._detail is null) return false;
            var sn = S.List._detail.SceneNumber;
            if (S.Gen._pendingRegenScene == sn) return true;

            bool Affects(JobSnapshot j) =>
                (string.Equals(j.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)) &&
                ScenesGeneration.IsScenesWorkflowJob(j.Kind) &&
                j.Scene == sn && j.Clip == clipNumber;

            if (S.Gen._job is not null && Affects(S.Gen._job))
                return true;
            return S.Gen._myJobs.Any(Affects);
        }

        /// <summary>
        /// Clip N (N>1) needs clip N-1 on disk — Imagine continues from the previous video.
        /// </summary>
        internal bool PreviousClipMissing(int clipNumber)
        {
            if (clipNumber <= 1 || S.List._detail is null) return false;
            var prev = S.List._detail.Clips.FirstOrDefault(c => c.ClipNumber == clipNumber - 1);
            return prev is null || !prev.OnDisk;
        }

        /// <summary>Select clips in the open scene that are not on disk yet.</summary>
        internal void SelectMissingClips()
        {
            if (S.List._detail is null) return;
            _selectedClips.Clear();
            foreach (var c in S.List._detail.Clips.Where(c => !c.OnDisk))
                _selectedClips.Add(c.ClipNumber);
        }

        internal void ToggleClipSelect(int cn, bool on)
        {
            if (on) _selectedClips.Add(cn);
            else _selectedClips.Remove(cn);
        }

        internal void ClearClipSelection() => _selectedClips.Clear();

        internal bool AllClipsSelected =>
            S.List._detail is { Clips.Count: > 0 } && S.List._detail.Clips.All(c => _selectedClips.Contains(c.ClipNumber));

        internal void ToggleSelectAllClips(bool on)
        {
            if (S.List._detail is null) return;
            if (on)
            {
                foreach (var c in S.List._detail.Clips)
                    _selectedClips.Add(c.ClipNumber);
            }
            else
            {
                _selectedClips.Clear();
            }
        }

        internal double? EstimateSelectedClipsCostUsd()
        {
            if (S.List._costReport is null || S.List._detail is null) return null;
            var row = S.List._costReport.Scenes.FirstOrDefault(r => r.Scene == S.List._detail.SceneNumber);
            if (row is null || row.ClipsTotal <= 0) return null;
            // Approximate: whole-scene draft cost spread evenly per clip (force-regen ignores on-disk state).
            return row.AllDraftUsd / row.ClipsTotal * _selectedClips.Count;
        }

        internal int EstimateSelectedClips()
        {
            if (S.List._scenes is null) return 0;
            // Generate always fills missing only — estimate remaining work on selected scenes.
            return S.List._scenes
                .Where(x => S.List._selected.Contains(x.SceneNumber))
                .Sum(s => Math.Max(0, s.ClipCount - s.ClipsOnDisk));
        }
    }
}
