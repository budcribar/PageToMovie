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

public partial class Review
{
    /// <summary>AutoReview domain for the Review page. Owns related UI state and behavior.</summary>
    internal sealed class ReviewAutoReview
    {
        private readonly Review S;
        public ReviewAutoReview(Review host) => S = host;

        /// <summary>SxxCyy → draft from last auto-review.</summary>
            internal readonly Dictionary<string, ClipAutoReviewDraft> _drafts = new(StringComparer.OrdinalIgnoreCase);

        internal string? _editKey;

        internal List<EditRow>? _editRows;

        internal List<EditLogEntry> _entries = new();

        internal bool _isReviewing;

        internal MovieAutoReviewReport? _movieReport;

        internal string _note = "";

        internal int _reviewProgressPct;

        internal string _reviewProgressStatus = "";

        internal Dictionary<string, string> _reviews = new();


        internal IEnumerable<SceneSummary> SortedReviewScenes
        {
            get
            {
                return S._sceneSortBy switch
                {
                    "duration" => S._sceneSortAsc
                        ? S._scenes.OrderBy(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0)
                        : S._scenes.OrderByDescending(s => s.ActualDurationSeconds ?? s.PlannedDurationSeconds ?? 0),
                    _ => S._sceneSortAsc
                        ? S._scenes.OrderBy(s => s.SceneNumber)
                        : S._scenes.OrderByDescending(s => s.SceneNumber),
                };
            }
        }


        internal async Task ReviewAsync(int scene, int clip, string status)
        {
            S._busy = true;
            S._error = null;
            try
            {
                await S.Engine.ReviewClipAsync(S._projectId, scene, clip, status, _note);
                S._message = $"Marked S{scene:D2}C{clip:D2} {status}";
                _note = "";
                await S.SoftLoadAsync();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal static string ClipKey(int scene, int clip) => $"S{scene:D2}C{clip:D2}";


        internal bool IsAutoReviewRunning(int scene, int clip) =>
            S._job is not null &&
            (S._job.Status is "running" or "queued") &&
            (
                (string.Equals(S._job.Kind, "clip-auto-review", StringComparison.OrdinalIgnoreCase) &&
                 S._job.Scene == scene &&
                 S._job.Clip == clip)
                ||
                (string.Equals(S._job.Kind, "clip-auto-review-batch", StringComparison.OrdinalIgnoreCase) &&
                 (S._job.Scene is null || S._job.Scene == scene))
            );


        internal ClipAutoReviewDraft? GetLocalDraft(int scene, int clip) =>
            _drafts.TryGetValue(ClipKey(scene, clip), out var d) ? d : null;


        internal bool HasIncludedEdits() =>
            _editRows is { Count: > 0 } && _editRows.Any(r => r.Include && !string.IsNullOrWhiteSpace(r.Value));


        internal async Task StartAutoReviewAsync(int scene, int clip)
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            CloseApplyPanel();
            try
            {
                await S.EnsureHubAsync();
                S._message = $"Sampling frames S{scene:D2}C{clip:D2}…";
                S.StateHasChanged();
                var (frames, sampleErr) = await S.Stitch.SampleAutoReviewFramesAsync(S._projectId, scene, clip);
                if (!string.IsNullOrWhiteSpace(sampleErr) || frames.Count == 0)
                    throw new InvalidOperationException(sampleErr ?? "No frames sampled");

                S._message = $"Uploading {frames.Count} frame(s) · reviewing S{scene:D2}C{clip:D2}…";
                S.StateHasChanged();
                var started = await S.Engine.StartClipAutoReviewAsync(S._projectId, scene, clip, frames);
                S._job = started;
                if (S._job is null)
                {
                    var jobs = await S.Engine.GetJobAsync();
                    S._job = jobs?.Job;
                }
                S._message = $"Reviewing S{scene:D2}C{clip:D2}…";
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal async Task<IReadOnlyList<string>> ResolveSceneUrlsForReviewAsync(int scene)
        {
            var summary = S._scenes.FirstOrDefault(s => s.SceneNumber == scene);
            if (summary?.CompositeExists == true)
            {
                return new List<string> { S.Engine.CompositeVideoUrl(S._projectId, scene) };
            }
            return await S.Stitch.CollectClipUrlsAsync(S._projectId, scene);
        }


        internal async Task StartFullMovieReviewAsync()
        {
            if (S._busy || S.JobRunning || _isReviewing) return;
            S._busy = true;
            _isReviewing = true;
            _reviewProgressPct = 5;
            _reviewProgressStatus = "Initializing frame sampling across scenes…";
            S._error = null;
            S._message = null;
            S.StateHasChanged();
            try
            {
                var keyframes = new List<MovieAutoReviewKeyframe>();
                var scenesToReview = S._scenes.OrderBy(x => x.SceneNumber).ToList();

                for (var i = 0; i < scenesToReview.Count; i++)
                {
                    var s = scenesToReview[i];
                    _reviewProgressPct = 5 + (int)(55.0 * (i + 1) / Math.Max(1, scenesToReview.Count));
                    _reviewProgressStatus = $"Sampling visual frames for Scene {s.SceneNumber} ({i + 1}/{scenesToReview.Count})…";
                    S.StateHasChanged();

                    var urls = await ResolveSceneUrlsForReviewAsync(s.SceneNumber);
                    if (urls is { Count: > 0 })
                    {
                        foreach (var url in urls.Take(2))
                        {
                            try
                            {
                                var framesResult = await S.Stitch.ExtractFramesRawAsync(url, mode: "span", count: 2);
                                if (framesResult.Success && framesResult.Frames is { Count: > 0 })
                                {
                                    foreach (var f in framesResult.Frames)
                                    {
                                        if (string.IsNullOrWhiteSpace(f.Base64)) continue;
                                        keyframes.Add(new MovieAutoReviewKeyframe
                                        {
                                            SceneNumber = s.SceneNumber,
                                            Label = $"SCENE_{s.SceneNumber:D2}",
                                            Base64 = f.Base64,
                                            Mime = string.IsNullOrWhiteSpace(f.Mime) ? "image/jpeg" : f.Mime
                                        });
                                    }
                                }
                            }
                            catch { /* fall through */ }
                        }
                    }
                }

                if (keyframes.Count == 0)
                {
                    S._error = "No video clips available to sample for movie review. Generate scene clips first.";
                    return;
                }

                _reviewProgressPct = 75;
                _reviewProgressStatus = $"Evaluating 6 categories (Continuity, Character, Lighting, Pacing, Dialogue, Music) across {keyframes.Count} sampled keyframes with Vision AI…";
                S.StateHasChanged();

                var envelope = await S.Engine.ReviewMovieAsync(S._projectId, keyframes);
                if (envelope?.Report is not null)
                {
                    _movieReport = envelope.Report;
                    _reviewProgressPct = 100;
                    _reviewProgressStatus = "Full movie AI review ready!";
                    S._message = $"Full movie AI review ready — Score: {_movieReport.OverallScore}/10 ({_movieReport.Verdict})";
                }
                else
                {
                    S._error = "Failed to generate full movie review report.";
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                _isReviewing = false;
                S._busy = false;
                S.StateHasChanged();
            }
        }


        internal async Task StartBatchAutoReviewAsync()
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            CloseApplyPanel();
            try
            {
                await S.EnsureHubAsync();
                // Client-orchestrated: sample frames per clip, then single authenticated review job.
                var targets = new List<(int Scene, int Clip)>();
                foreach (var s in S._scenes.OrderBy(x => x.SceneNumber))
                {
                    if (s.ClipsOnDisk <= 0 && s.ClipCount <= 0) continue;
                    // Prefer detail when selected; otherwise use summary counts
                    SceneDetail? detail = null;
                    if (S._selectedScene == s.SceneNumber)
                        detail = S._selectedDetail;
                    else
                    {
                        try
                        {
                            detail = (await S.Engine.GetSceneDetailAsync(S._projectId, s.SceneNumber))?.Scene;
                        }
                        catch { /* fall through */ }
                    }

                    if (detail?.Clips is { Count: > 0 })
                    {
                        foreach (var c in detail.Clips.Where(c => c.OnDisk).OrderBy(c => c.ClipNumber))
                            targets.Add((s.SceneNumber, c.ClipNumber));
                    }
                    else if (s.ClipsOnDisk > 0)
                    {
                        for (var c = 1; c <= Math.Max(s.ClipCount, s.ClipsOnDisk); c++)
                        {
                            if (S.ClipOnDisk(s.SceneNumber, c))
                                targets.Add((s.SceneNumber, c));
                        }
                    }
                }

                // onlyMissing: skip clips that already have a draft
                var todo = new List<(int Scene, int Clip)>();
                foreach (var (scene, clip) in targets)
                {
                    if (_drafts.ContainsKey(ClipKey(scene, clip)))
                        continue;
                    try
                    {
                        var existing = await S.Engine.GetClipAutoReviewDraftAsync(S._projectId, scene, clip);
                        if (existing is not null)
                        {
                            _drafts[ClipKey(scene, clip)] = existing;
                            continue;
                        }
                    }
                    catch { /* treat as missing */ }
                    todo.Add((scene, clip));
                }

                if (todo.Count == 0)
                {
                    S._message = "Batch auto-review: nothing to do (no missing drafts)";
                    return;
                }

                var ok = 0;
                var failed = 0;
                for (var i = 0; i < todo.Count; i++)
                {
                    var (scene, clip) = todo[i];
                    S._message = $"Auto-review {i + 1}/{todo.Count}: sampling S{scene:D2}C{clip:D2}…";
                    S.StateHasChanged();
                    try
                    {
                        var (frames, sampleErr) = await S.Stitch.SampleAutoReviewFramesAsync(S._projectId, scene, clip);
                        if (!string.IsNullOrWhiteSpace(sampleErr) || frames.Count == 0)
                            throw new InvalidOperationException(sampleErr ?? "No frames");

                        S._message = $"Auto-review {i + 1}/{todo.Count}: reviewing S{scene:D2}C{clip:D2} ({frames.Count} frames)…";
                        S.StateHasChanged();
                        var started = await S.Engine.StartClipAutoReviewAsync(S._projectId, scene, clip, frames);
                        S._job = started;
                        var snap = await S.Engine.WaitForJobTerminalAsync(
                            jobId: started?.JobId,
                            timeout: TimeSpan.FromMinutes(6));
                        S._job = snap ?? started;
                        if (snap is not null &&
                            string.Equals(snap.Status, "done", StringComparison.OrdinalIgnoreCase))
                        {
                            ok++;
                            try
                            {
                                var d = await S.Engine.GetClipAutoReviewDraftAsync(S._projectId, scene, clip);
                                if (d is not null)
                                    _drafts[ClipKey(scene, clip)] = d;
                            }
                            catch { /* optional */ }
                        }
                        else
                        {
                            failed++;
                            var err = snap?.Error ?? snap?.Message ?? "job failed";
                            S._error = $"S{scene:D2}C{clip:D2}: {err}";
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        S._error = $"S{scene:D2}C{clip:D2}: {ex.Message}";
                    }
                }

                try { await S.Engine.GetReviewIndexAsync(S._projectId, rebuild: true); } catch { /* optional */ }
                await S.SoftLoadAsync();
                S._message = $"Batch auto-review done: {ok} ok, {failed} failed of {todo.Count}";
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }


        internal void OpenApplyPanel(int scene, int clip)
        {
            var draft = GetLocalDraft(scene, clip);
            if (draft is null) return;
            _editKey = ClipKey(scene, clip);
            _editRows = draft.Suggestions.Select(s => new EditRow
            {
                Layer = s.Layer,
                Field = s.Field,
                CharKey = s.CharKey,
                Label = string.IsNullOrWhiteSpace(s.Label) ? s.Field : s.Label,
                CurrentValue = s.CurrentValue ?? "",
                Value = s.SuggestedValue ?? "",
                Rationale = s.Rationale,
                Include = s.IncludeByDefault,
            }).ToList();
            // If no structured suggestions but fail, still allow empty panel + manual regen
            if (_editRows.Count == 0 &&
                string.Equals(draft.Suggestion, "fail", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(draft.Note))
            {
                _editRows.Add(new EditRow
                {
                    Layer = "clip",
                    Field = "visual_prompt",
                    Label = "Clip note (add to visual prompt yourself or leave unchecked)",
                    CurrentValue = "",
                    Value = draft.Note,
                    Include = false,
                    Rationale = "AI did not propose a full field rewrite — edit or leave unchecked.",
                });
            }
        }


        internal void CloseApplyPanel()
        {
            _editKey = null;
            _editRows = null;
        }


        internal async Task ApplyAndRegenAsync(int scene, int clip)
        {
            if (_editRows is null) return;
            var items = _editRows
                .Where(r => r.Include && !string.IsNullOrWhiteSpace(r.Value))
                .Select(r => new ClipAutoReviewApplyItem
                {
                    Layer = r.Layer,
                    Field = r.Field,
                    CharKey = r.CharKey,
                    Value = r.Value.Trim(),
                })
                .ToList();

            S._busy = true;
            S._error = null;
            try
            {
                if (items.Count > 0)
                {
                    await S.Engine.ApplyClipAutoReviewAsync(S._projectId, scene, clip, items);
                    S._message = $"Saved {items.Count} change(s) — regenerating S{scene:D2}C{clip:D2}…";
                }
                else
                {
                    S._message = $"Regenerating S{scene:D2}C{clip:D2} (no field changes)…";
                }

                await S.EnsureHubAsync();
                await S.Engine.StartSceneGenAsync(S._projectId, scene, onlyMissing: false, clip: clip);
                var jobs = await S.Engine.GetJobAsync();
                S._job = jobs?.Job;
                CloseApplyPanel();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }

    }
}
