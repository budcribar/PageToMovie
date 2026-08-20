using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Localization;
using PageToMovie.Web.Services;

using PageToMovie.Core.Utils;
namespace PageToMovie.Web.Components.Pages;

public partial class Scenes
{
    /// <summary>Clip regen + video-edit domain for the Scenes page.</summary>
    public sealed class ScenesClipRegen
    {
        private readonly Scenes S;
        public ScenesClipRegen(Scenes host) => S = host;

        internal bool _showVideoEditPrompt;

        internal string _videoEditPromptText = "";

        internal string _preferredVideoEditor = "ClipChamp";

        internal async Task OpenInExternalEditorAsync(int? sceneNumber = null, int? clipNumber = null)
        {
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                var res = await S.Engine.OpenInExternalEditorAsync(S._projectId, sceneNumber, clipNumber, _preferredVideoEditor);
                if (res.Ok)
                {
                    S._message = $"🎬 Opened video in {res.Editor ?? _preferredVideoEditor}.";
                }
                else if (!string.IsNullOrWhiteSpace(res.VideoUrl))
                {
                    var cleanPid = CommonRegex.Replace(S._projectId, @"[^\w\.-]", "_");
                    string fileName;
                    if (sceneNumber is int sn)
                    {
                        fileName = clipNumber is int cn
                            ? $"{cleanPid}_S{sn:D2}C{cn:D2}.mp4"
                            : $"{cleanPid}_S{sn:D2}_composite.mp4";
                    }
                    else
                    {
                        fileName = $"{cleanPid}_full.mp4";
                    }
                    S._message = $"🎬 Downloaded video to your PC — open {fileName} in {res.Editor ?? _preferredVideoEditor}.";
                    try
                    {
                        await S.JS.InvokeVoidAsync("eval", $"const a=document.createElement('a');a.href='{res.VideoUrl}';a.download='{fileName}';document.body.appendChild(a);a.click();document.body.removeChild(a);");
                    }
                    catch { /* ignore */ }
                }
                else
                {
                    S._error = res.Error ?? "Could not open external video editor.";
                }
            }
            catch (Exception ex)
            {
                S._error = ex.Message;
            }
            finally
            {
                S._busy = false;
            }
        }

        internal async Task EnsurePredecessorsUploadedAsync(List<(int Scene, int Clip)> targets)
        {
            if (!S.MediaFolder.IsConnected || string.IsNullOrEmpty(S._projectId) || targets.Count == 0) return;

            // Cache scene detail per scene number — targets can span scenes (multi-scene batch),
            // and S.List._detail may be loaded for a different scene than the one we're checking (or null,
            // when this runs from the scene-list page rather than a scene-detail view).
            var detailCache = new Dictionary<int, SceneDetail?>();

            // Ensure required character and location reference plates are uploaded from local media folder.
            await EnsureReferencePlatesUploadedAsync(targets, detailCache);

            // Video-extend continuity (see FilmJobService.GenerateOneClipAsync + ClientMediaFolderService.
            // PrepareExtendSourceAsync): resolved once per batch, not per target, since it's the same
            // active project setting for every clip here.
            var extendModel = await ResolveActiveVideoExtendModelAsync();

            foreach (var (sn, cn) in targets)
            {
                if (ShouldSkipPredecessor(cn, sn, targets)) continue;
                var sceneDetail = await GetSceneDetailCachedAsync(sn, detailCache);
                await UploadPredecessorIfMissingAsync(sn, cn - 1, sceneDetail);
                await PrepareExtendIfNeededAsync(sn, cn, sceneDetail, extendModel);
            }
        }

        private async Task EnsureReferencePlatesUploadedAsync(
            List<(int Scene, int Clip)> targets, Dictionary<int, SceneDetail?> detailCache)
        {
            var charactersToUpload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var locationsToUpload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sn in targets.Select(t => t.Scene).Distinct())
            {
                var d = await GetSceneDetailCachedAsync(sn, detailCache);
                if (d is null) continue;
                CollectLocationKey(d, locationsToUpload);
                CollectCharacterKeys(d, charactersToUpload);
            }

            foreach (var locKey in locationsToUpload)
                await UploadLocationPlateIfAvailableLocallyAsync(locKey);

            foreach (var charKey in charactersToUpload)
                await UploadCharacterPlateIfAvailableLocallyAsync(charKey);
        }

        private static void CollectLocationKey(SceneDetail d, HashSet<string> locationsToUpload)
        {
            if (!string.IsNullOrWhiteSpace(d.PrimaryLocationId))
                locationsToUpload.Add(d.PrimaryLocationId);
        }

        private static void CollectCharacterKeys(SceneDetail d, HashSet<string> charactersToUpload)
        {
            if (d.Clips is null) return;
            foreach (var ch in d.Clips
                .Where(c => c.CharactersOnScreen is not null)
                .SelectMany(c => c.CharactersOnScreen)
                .Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                charactersToUpload.Add(ch);
            }
        }

        private async Task UploadLocationPlateIfAvailableLocallyAsync(string locKey)
        {
            var candidates = ProjectAssetNaming.LocationRefFileNameCandidates(locKey);
            foreach (var cand in candidates)
            {
                var rel = $"assets/locations/{cand}";
                var (found, size) = await S.MediaFolder.StatLocalFileAsync(S._projectId, rel);
                if (!found || size < 64) continue;

                var bytes = await S.MediaFolder.ReadLocalBytesAsync($"{S._projectId}/{rel}", minBytes: 64);
                if (bytes is not null && bytes.Length >= 64)
                {
                    await using var ms = new MemoryStream(bytes);
                    await S.Engine.UploadLocationRefAsync(S._projectId, locKey, ms, cand);
                    break;
                }
            }
        }

        private async Task UploadCharacterPlateIfAvailableLocallyAsync(string charKey)
        {
            var candidates = ProjectAssetNaming.CharacterRefFileCandidates(charKey);
            foreach (var cand in candidates)
            {
                var rel = $"assets/characters/{cand}";
                var (found, size) = await S.MediaFolder.StatLocalFileAsync(S._projectId, rel);
                if (!found || size < 64) continue;

                var bytes = await S.MediaFolder.ReadLocalBytesAsync($"{S._projectId}/{rel}", minBytes: 64);
                if (bytes is not null && bytes.Length >= 64)
                {
                    await using var ms = new MemoryStream(bytes);
                    await S.Engine.UploadCharacterRefAsync(S._projectId, charKey, ms, cand);
                    break;
                }
            }
        }

        private static bool ShouldSkipPredecessor(int cn, int sn, List<(int Scene, int Clip)> targets) =>
            cn <= 1 || targets.Any(t => t.Scene == sn && t.Clip == cn - 1);

        private async Task<SceneDetail?> GetSceneDetailCachedAsync(int sn, Dictionary<int, SceneDetail?> cache)
        {
            if (cache.TryGetValue(sn, out var cached)) return cached;
            var d = S.List._detail?.SceneNumber == sn ? S.List._detail : (await S.Engine.GetSceneDetailAsync(S._projectId, sn))?.Scene;
            cache[sn] = d;
            return d;
        }

        private async Task UploadPredecessorIfMissingAsync(int sn, int prevClipNum, SceneDetail? sceneDetail)
        {
            // OnDisk alone isn't enough here: it's also true when only the .client.json marker
            // exists (clip synced to the client, then pruned off server disk) — SizeBytes is 0 in
            // that case since there are no real bytes for the video-extend gate to find and copy.
            var prevSummary = sceneDetail?.Clips?.FirstOrDefault(c => c.ClipNumber == prevClipNum);
            var serverHasRealBytes = prevSummary?.OnDisk == true && prevSummary.SizeBytes >= 1024;
            if (serverHasRealBytes) return;

            var localBytes = await S.MediaFolder.GetClipBytesAsync(S._projectId, sn, prevClipNum);
            if (localBytes is not { Length: >= 1024 }) return;

            S._message = $"Uploading local predecessor S{sn:D2}C{prevClipNum:D2} to server…";
            S.StateHasChanged();
            await S.Engine.UploadClipAsync(S._projectId, sn, prevClipNum, localBytes);
        }

        private async Task PrepareExtendIfNeededAsync(int sn, int cn, SceneDetail? sceneDetail, double? extendModel)
        {
            if (extendModel is not { } maxInputSeconds) return;
            var wantsExtend = string.Equals(
                sceneDetail?.Clips?.FirstOrDefault(c => c.ClipNumber == cn)?.Continuation,
                "extend_previous", StringComparison.OrdinalIgnoreCase);
            if (!wantsExtend) return;
            // Best-effort: a false return just means no extend-source file appears on the
            // server, so it falls back to today's fresh-gen behavior — never blocks generation.
            await S.MediaFolder.PrepareExtendSourceAsync(S._projectId, sn, cn, maxInputSeconds);
        }

        /// <summary>Active project video model's max input length for a real video-extend call, or
        /// null when the model doesn't support real continuity (today: only Grok's video model does)
        /// or lookup fails.</summary>
        internal async Task<double?> ResolveActiveVideoExtendModelAsync()
        {
            try
            {
                var cfg = await S.Engine.GetConfigAsync(S._projectId);
                var modelId = cfg?.Config is { } c && c.TryGetValue("model_name", out var el) &&
                              el.ValueKind == System.Text.Json.JsonValueKind.String
                    ? el.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(modelId)) return null;

                var models = await S.Engine.GetSupportedModelsAsync(capability: "video");
                var entry = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
                if (entry is not { SupportsVideoContinue: true }) return null;

                return entry.AbsMaxClipDurationSeconds ?? entry.MaxClipDurationSeconds ?? 15;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Clip numbers in scene <paramref name="sn"/> not yet on server disk (or synced-only) —
        /// used to pre-check predecessors before an "only missing" generation batch.</summary>
        internal async Task<List<(int Scene, int Clip)>> MissingClipTargetsAsync(int sn)
        {
            var detail = S.List._detail?.SceneNumber == sn ? S.List._detail : (await S.Engine.GetSceneDetailAsync(S._projectId, sn))?.Scene;
            return detail?.Clips?.Where(c => !c.OnDisk).Select(c => (Scene: sn, Clip: c.ClipNumber)).ToList()
                ?? new List<(int Scene, int Clip)>();
        }

        internal bool _showRegenClipsConfirm;
        internal void CloseRegenClipsConfirm() => _showRegenClipsConfirm = false;

        /// <summary>"Regen selected clips" button: confirm (which clips, estimate) then regenerate — same
        /// shape as Generate; replaces the per-row Regen buttons.</summary>
        internal async Task OpenRegenClipsConfirmAsync()
        {
            if (S.List._detail is null) return;
            // Credits are rendered in the browser — no server job, no estimate, no confirm: just re-render.
            if (S.Gen.IsCreditsSceneNum(S.List._detail.SceneNumber))
            {
                await S.Gen.GenerateCreditsEntryAsync(S.List._detail.SceneNumber);
                return;
            }
            if (S.ClipSel._selectedClips.Count == 0)
            {
                S._error = "No clips selected — check the clip(s) to regenerate in the first column.";
                return;
            }
            S._error = null;
            _showRegenClipsConfirm = true;
        }

        internal async Task ConfirmRegenClipsAsync()
        {
            _showRegenClipsConfirm = false;
            await RegenSelectedClipsAsync();
        }

        /// <summary>List-rate estimate for the selected clips: the scene's all-draft cost / its clip count × selected.</summary>
        internal double EstimateSelectedClipsCostUsd()
        {
            var sn = S.List._detail?.SceneNumber;
            var row = S.List._costReport?.Scenes.FirstOrDefault(r => r.Scene == sn);
            if (row is null || row.ClipsTotal <= 0) return 0;
            return row.AllDraftUsd / row.ClipsTotal * S.ClipSel._selectedClips.Count;
        }

        internal int SelectedClipsWithPlanLint() =>
            S.List._detail?.Clips.Count(c => S.ClipSel._selectedClips.Contains(c.ClipNumber) && c.PlanLint.Count > 0) ?? 0;

        internal async Task RegenSelectedClipsAsync()
        {
            if (S.List._detail is null || S.ClipSel._selectedClips.Count == 0) return;
            if (S.Gen.IsCreditsSceneNum(S.List._detail.SceneNumber))
            {
                await S.Gen.GenerateCreditsEntryAsync(S.List._detail.SceneNumber);
                return;
            }
            if (!await S.Gen.EnsureMediaFolderForVideoAsync()) return;
            var sn = S.List._detail.SceneNumber;
            S._busy = true;
            S._error = null;
            S._message = null;
            S.Gen._pendingRegenScene = sn;
            try
            {
                var targets = S.ClipSel._selectedClips.OrderBy(c => c).Select(c => (Scene: sn, Clip: c)).ToList();
                await S.Gen.EnsureHubAsync();
                await EnsurePredecessorsUploadedAsync(targets);
                S.Gen.OpenJobModal();
                S.Gen._job = await S.Engine.StartClipBatchGenAsync(S._projectId, targets, resolution: S.Gen._genResolution);
                S._message = $"Regenerating {targets.Count} clip(s) in S{sn:D2} @ {S.Gen._genResolution}…";
                S.ClipSel._selectedClips.Clear();
                S.StateHasChanged();
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; S.Gen._pendingRegenScene = null; }
        }

        /// <summary>
        /// A local file at a clip's canonical relative path is not necessarily the *current* version
        /// — a later regen/promote may have happened without this browser open to catch the
        /// auto-save. Every call site that trusts a local clip copy (playback, dialogue
        /// re-verification upload) should gate on this against the server's currently-registered
        /// size rather than assuming presence means current. Returns null on any lookup failure —
        /// callers then fall back to their own "trust local unconditionally" or "use server" default.
        /// </summary>
        internal async Task<long?> ResolveExpectedClipSizeAsync(int scene, int clip)
        {
            try
            {
                var status = await S.Engine.GetClipMediaStatusAsync(S._projectId, scene, clip);
                if (status is { Ok: true })
                {
                    if (status.OnServer) return status.ServerSizeBytes;
                    if (status.OnClient) return status.ClientSizeBytes;
                    return null;
                }
            }
            catch { /* best effort — falls back to unconditional local trust */ }
            return null;
        }

        /// <summary>Force re-generate a single clip (onlyMissing: false).</summary>
        internal async Task RegenClipAsync(int sn, int cn)
        {
            // Credits are rendered deterministically client-side — never sent to the video model.
            if (S.Gen.IsCreditsSceneNum(sn)) { await S.Gen.GenerateCreditsEntryAsync(sn); return; }
            if (!S.List.CastReady)
            {
                S._error = S.List.CastBlockedTitle;
                return;
            }
            if (!await S.Gen.EnsureMediaFolderForVideoAsync()) return;

            S._busy = true;
            S._error = null;
            S._message = null;
            S.Gen._pendingRegenScene = sn;
            S.Gen.OpenJobModal();
            try
            {
                await S.Gen.EnsureHubAsync();
                await EnsurePredecessorsUploadedAsync(new List<(int Scene, int Clip)> { (sn, cn) });
                await S.Engine.StartSceneGenAsync(S._projectId, sn, onlyMissing: false, clip: cn, resolution: S.Gen._genResolution, takeTrigger: VideoTakeKinds.UserRegen);
                S._message = $"Regenerating S{sn:D2}C{cn:D2} @ {S.Gen._genResolution}…";
                S.Gen._pendingTakeReasonScene = sn;
                S.Gen._pendingTakeReasonClip = cn;
                var jobs = await S.Engine.GetJobAsync();
                S.Gen._job = jobs?.Job;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; S.Gen._pendingRegenScene = null; }
        }

        /// <summary>xAI's edit input cap — see MaxVideoEditInputSeconds's doc comment (client hint;
        /// RunVideoEditAsync is the authoritative server-side check).</summary>
        internal static bool ClipExceedsEditDurationCap(ClipSummary clip) =>
            (clip.ActualDurationSeconds ?? clip.DurationSeconds) > Scenes.MaxVideoEditInputSeconds + 0.01;

        internal void OpenVideoEditPrompt()
        {
            _videoEditPromptText = "";
            _showVideoEditPrompt = true;
        }

        internal void CloseVideoEditPrompt() => _showVideoEditPrompt = false;

        internal async Task SubmitVideoEditAsync()
        {
            if (S.List._detail is null || S.ClipForm._clip is null || string.IsNullOrWhiteSpace(_videoEditPromptText))
                return;

            var sn = S.List._detail.SceneNumber;
            var cn = S.ClipForm._clip.ClipNumber;
            _showVideoEditPrompt = false;
            S._busy = true;
            S._error = null;
            S._message = null;
            try
            {
                await S.Gen.EnsureHubAsync();
                await S.Engine.StartVideoEditAsync(S._projectId, sn, cn, _videoEditPromptText.Trim());
                S._message = $"Editing S{sn:D2}C{cn:D2}…";
                var jobs = await S.Engine.GetJobAsync();
                S.Gen._job = jobs?.Job;
            }
            catch (Exception ex) { S._error = ex.Message; }
            finally { S._busy = false; }
        }
    }
}
