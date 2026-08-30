using Microsoft.JSInterop;
using PageToMovie.Core.Models;
using PageToMovie.Core.Utils;

namespace PageToMovie.Web.Services;

/// <summary>
/// Browser-side video concat via <c>PageToMovieFfmpeg</c> (ffmpeg.wasm).
/// Offloads multi-clip / multi-scene preview stitch from the API host.
/// </summary>
public sealed class ClientVideoStitchService
{
    private const string OptimizedConcatJs = "PageToMovieCut.concatVideosOptimizedAsync";
    private const string OptimizedConcatAndMixJs = "PageToMovieCut.concatAndMixVideosOptimizedAsync";
    private const string LegacyConcatJs = "PageToMovieFfmpeg.concatVideosAsync";

    private readonly IJSRuntime _js;
    private readonly EngineApiClient _engine;
    private readonly ClientMediaFolderService? _media;
    private readonly object _skipLock = new();

    /// <summary>Stable scene / clip-URL / segment index reused across Play clicks.</summary>
    internal PlayMediaIndex MediaIndex { get; } = new();

    /// <summary>Hit/miss counts for the last collect (tests + Play start-time notes).</summary>
    internal PlayMediaCollectStats LastCollectStats { get; } = new();

    public ClientVideoStitchService(
        IJSRuntime js,
        EngineApiClient engine,
        ClientMediaFolderService? media = null)
    {
        _js = js;
        _engine = engine;
        _media = media;
    }

    /// <summary>
    /// Ordered media URLs for the given scenes: prefer a fresh composite, else on-disk clips.
    /// </summary>
    public async Task<IReadOnlyList<string>> CollectSceneMediaUrlsAsync(
        string projectId,
        IReadOnlyList<int> sceneNumbers,
        IReadOnlyList<SceneSummary>? sceneList,
        IReadOnlySet<int>? staleScenes,
        CancellationToken ct = default)
    {
        // A server clip/composite fallback URL (ClipVideoUrl/CompositeVideoUrl) is only authorized when
        // BrowserMediaPath can append a fresh ?mt= media token. Ensure one exists BEFORE building URLs —
        // otherwise the raw ffmpeg fetch hits the authenticated endpoint with no token and gets a 401.
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        BeginSkippedCollect();
        var urls = new List<string>();
        foreach (var sn in sceneNumbers.Distinct().OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            await AppendSceneMediaUrlsAsync(urls, projectId, sn, sceneList, ct);
        }

        FinishSkippedCollect();
        return urls;
    }

    private async Task AppendSceneMediaUrlsAsync(
        List<string> urls,
        string projectId,
        int sn,
        IReadOnlyList<SceneSummary>? sceneList,
        CancellationToken ct)
    {
        var summary = sceneList?.FirstOrDefault(s => s.SceneNumber == sn);

        // Cached scene details when the list fingerprint still matches.
        var detail = await TryGetSceneDetailAsync(projectId, sn, summary, ct);

        var skipped = await TryAppendAllPlayableClipUrlsAsync(urls, projectId, sn, detail, ct);
        if (skipped.Count > 0)
        {
            NoteSkippedClips(skipped);
            return;
        }
        // skipped is empty here: either every planned clip resolved (already appended) or
        // the scene has no clip rows. Only the latter should fall through to composite.
        if ((detail?.Clips?.Count ?? 0) > 0)
            return;

        // Fallback: if no atomic clips on disk for this scene, use composite if available
        if (summary?.CompositeExists == true || detail?.CompositeExists == true)
            urls.Add(_engine.CompositeVideoUrl(projectId, sn));
    }

    private async Task<SceneDetail?> TryGetSceneDetailAsync(
        string projectId,
        int sn,
        SceneSummary? summary,
        CancellationToken ct)
    {
        var summaryFp = PlayMediaIndex.FingerprintSummary(summary);
        if (MediaIndex.TryGetSceneDetail(projectId, sn, summaryFp, out var cached))
        {
            LastCollectStats.AddDetailHit();
            return cached;
        }

        LastCollectStats.AddDetailMiss();
        try
        {
            var detail = (await _engine.GetSceneDetailAsync(projectId, sn, ct))?.Scene;
            if (detail is not null)
                MediaIndex.RememberSceneDetail(projectId, detail, summary);
            return detail;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prefetch scene JSON + clip indexes after Review has loaded the scene list,
    /// so the first Play does not walk every scene on click.
    /// </summary>
    public async Task WarmSceneIndexAsync(
        string projectId,
        IReadOnlyList<SceneSummary>? scenes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || scenes is not { Count: > 0 })
            return;

        MediaIndex.SyncSceneList(projectId, scenes);
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        var missing = scenes
            .Where(s => !MediaIndex.TryGetSceneDetail(
                projectId, s.SceneNumber, PlayMediaIndex.FingerprintSummary(s), out _))
            .ToList();
        if (missing.Count > 0)
        {
            await Parallel.ForEachAsync(
                missing,
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (summary, token) =>
                {
                    await TryGetSceneDetailAsync(projectId, summary.SceneNumber, summary, token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
        }

        if (MediaIndex.TryGetSceneGroup(projectId, scenes, out _))
        {
            LastCollectStats.AddGroupHit();
            return;
        }

        LastCollectStats.AddGroupMiss();
        var playable = scenes
            .Where(s => s.CompositeExists || s.ClipsOnDisk > 0 || s.ClipCount > 0)
            .OrderBy(s => s.SceneNumber)
            .Select(s => s.SceneNumber)
            .ToList();
        MediaIndex.RememberSceneGroup(projectId, scenes, playable);
    }

    /// <summary>
    /// Resolve every planned clip, or add none. A scene with a hole must not stitch the rest.
    /// Returns the labels of clips that were not playable (empty when all resolved or no clips).
    /// </summary>
    private async Task<List<string>> TryAppendAllPlayableClipUrlsAsync(
        List<string> urls, string projectId, int sn, SceneDetail? detail, CancellationToken ct)
    {
        var clips = detail?.Clips?.OrderBy(c => c.ClipNumber).ToList();
        if (clips is not { Count: > 0 })
            return new List<string>();

        var playable = await ResolvePlayableClipUrlsBatchAsync(
            projectId, sn, clips, includeServerFallback: true, ct).ConfigureAwait(false);
        var resolved = new List<string>(clips.Count);
        var missing = new List<string>();
        for (var i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            var url = playable[i];
            if (!string.IsNullOrEmpty(url))
                resolved.Add(url);
            else
                missing.Add(ScenePlayGate.FormatClipLabel(sn, c.ClipNumber));
        }

        if (missing.Count > 0)
            return missing;

        urls.AddRange(resolved);
        return missing;
    }

    /// <summary>
    /// Multi-scene collection variant of <see cref="CollectSceneMediaUrlsAsync"/> that mixes each
    /// scene's own locally-synced background music into that scene's segment before returning —
    /// callers then concat the returned per-scene URLs into the final multi-scene video via
    /// <see cref="ConcatAsync"/>. Mixing per scene (not on the final concatenated result) is
    /// required since different scenes have different music; a scene whose stitch or mix step
    /// fails is skipped rather than aborting the whole collection.
    /// </summary>
    public async Task<IReadOnlyList<string>> CollectAndMixSceneSegmentsAsync(
        string projectId,
        IReadOnlyList<int> sceneNumbers,
        IReadOnlyList<SceneSummary>? sceneList,
        IReadOnlySet<int>? staleScenes,
        CancellationToken ct = default)
    {
        var infos = await CollectAndMixSceneSegmentInfosAsync(
            projectId, sceneNumbers, sceneList, staleScenes, ct).ConfigureAwait(false);
        return infos.Select(s => s.Url).ToList();
    }

    /// <summary>
    /// The URL to play/save for a clip that has no local file. A video-extend clip's provider copy is
    /// the COMBINED video (previous clip + this one) — the server says how long that head is
    /// (ClipSummary.ProviderLeadInSeconds); slice it off here with ffmpeg.wasm so the previous clip's
    /// footage and lines never play twice. Returns a blob URL (caller may revoke) or the plain URL.
    /// </summary>
    public async Task<string> ResolveServerClipUrlAsync(string projectId, int sceneNumber, ClipSummary clip, CancellationToken ct = default)
    {
        var url = _engine.ClipVideoUrl(projectId, sceneNumber, clip.ClipNumber);
        if (clip.ProviderLeadInSeconds is not { } leadIn || leadIn <= 0.1)
            return url;
        try
        {
            var probe = await _js.InvokeAsync<JsProbeResult>("PageToMovieFfmpeg.probeDurationAsync", ct, url);
            if (probe is { Success: true, Seconds: > 0 } && probe.Seconds > leadIn + 0.1)
            {
                var slice = await _js.InvokeAsync<JsTrimTailResult>("PageToMovieFfmpeg.keepLastSecondsAsync", ct, url, probe.Seconds - leadIn, null);
                if (slice is { Success: true } && !string.IsNullOrWhiteSpace(slice.Url))
                    return slice.Url;
            }
        }
        catch { /* fall through: playing the combined copy is wrong but not worse than nothing */ }
        return url;
    }

    /// <summary>Why the last segment collection dropped a scene (stitch/fetch failure), or null.</summary>
    public string? LastCollectError { get; private set; }

    /// <summary>S## C## labels skipped during the last collect (holes / unreachable).</summary>
    public IReadOnlyList<string> LastSkippedClipLabels { get; private set; } = Array.Empty<string>();

    private readonly List<string> _skippedClipLabels = new();

    private void BeginSkippedCollect()
    {
        lock (_skipLock)
        {
            LastCollectError = null;
            _skippedClipLabels.Clear();
            LastSkippedClipLabels = Array.Empty<string>();
        }
        LastCollectStats.Reset();
    }

    private void NoteSkippedClips(IEnumerable<string> labels)
    {
        lock (_skipLock)
        {
            _skippedClipLabels.AddRange(
                labels
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.Ordinal)
                    .Where(label => !_skippedClipLabels.Contains(label, StringComparer.Ordinal)));
            LastSkippedClipLabels = _skippedClipLabels.ToList();
        }
    }

    private void FinishSkippedCollect()
    {
        lock (_skipLock)
        {
            LastSkippedClipLabels = _skippedClipLabels.ToList();
            if (_skippedClipLabels.Count > 0 && string.IsNullOrWhiteSpace(LastCollectError))
                LastCollectError = FormatMissingClipPlayError(_skippedClipLabels, _media?.IsConnected == true);
        }
    }

    private void NoteCollectError(string error)
    {
        lock (_skipLock)
            LastCollectError = error;
    }

    /// <summary>On-disk clip URLs for one scene (ordered).</summary>
    /// <remarks>
    /// <see cref="ClipSummary.OnDisk"/> is true for a local-folder <c>.client.json</c> marker as well
    /// as a real server mp4. Blindly adding <see cref="EngineApiClient.ClipVideoUrl"/> then lets
    /// browser stitch fetch a 404. Prefer the local blob; only fall back to the server URL when
    /// that endpoint is actually reachable.
    /// </remarks>
    public async Task<IReadOnlyList<string>> CollectClipUrlsAsync(
        string projectId,
        int sceneNumber,
        SceneDetail? detail = null,
        CancellationToken ct = default,
        bool includeServerFallback = true,
        IEnumerable<int>? clipNumbers = null,
        bool requireAllPlannedClips = false)
    {
        BeginSkippedCollect();
        // Ensure a fresh ?mt= media token before any ClipVideoUrl fallback (see CollectSceneMediaUrlsAsync).
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        if (detail is null)
            detail = await TryGetSceneDetailAsync(projectId, sceneNumber, summary: null, ct).ConfigureAwait(false);
        else
            MediaIndex.RememberSceneDetail(projectId, detail);
        if (detail?.Clips is null || detail.Clips.Count == 0)
            return Array.Empty<string>();

        var clipSet = clipNumbers?.ToHashSet();
        var list = new List<string>();
        var missing = new List<string>();
        IEnumerable<ClipSummary> query;
        if (clipSet is { Count: > 0 })
        {
            query = clipSet.OrderBy(n => n).Select(n =>
                detail.Clips.FirstOrDefault(c => c.ClipNumber == n)
                ?? new ClipSummary { ClipNumber = n, OnDisk = false });
        }
        else if (requireAllPlannedClips)
        {
            query = detail.Clips.OrderBy(c => c.ClipNumber);
        }
        else
        {
            query = detail.Clips.Where(c => c.OnDisk).OrderBy(c => c.ClipNumber);
        }

        var rows = query.ToList();
        var resolved = await ResolvePlayableClipUrlsBatchAsync(
            projectId, sceneNumber, rows, includeServerFallback, ct).ConfigureAwait(false);
        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.IsNullOrEmpty(resolved[i]))
                list.Add(resolved[i]!);
            else
                missing.Add(ScenePlayGate.FormatClipLabel(sceneNumber, rows[i].ClipNumber));
        }

        if (missing.Count > 0)
        {
            NoteSkippedClips(missing);
            LastCollectError = FormatMissingClipPlayError(missing, _media?.IsConnected == true);
        }
        return list;
    }

    private async Task<string?[]> ResolvePlayableClipUrlsBatchAsync(
        string projectId,
        int sceneNumber,
        IReadOnlyList<ClipSummary> rows,
        bool includeServerFallback,
        CancellationToken ct)
    {
        var resolved = new string?[rows.Count];
        var serverCandidates = new List<(int Index, string Url, string Fingerprint)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var clipRow = rows[i];
            var currentRel = _media is not null
                ? await _media.ResolveCurrentTakeRelativePathAsync(projectId, sceneNumber, clipRow.ClipNumber)
                    .ConfigureAwait(false)
                : null;
            var takeFp = PlayMediaIndex.FingerprintClip(clipRow, currentRel);
            if (MediaIndex.TryGetClipUrl(projectId, sceneNumber, clipRow.ClipNumber, takeFp, out var cached)
                && !string.IsNullOrEmpty(cached))
            {
                LastCollectStats.AddClipUrlHit();
                resolved[i] = cached;
                continue;
            }

            LastCollectStats.AddClipUrlMiss();
            var local = await TryLocalClipBlobUrlAsync(projectId, sceneNumber, clipRow, currentRel)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(local))
            {
                resolved[i] = local;
                MediaIndex.RememberClipUrl(projectId, sceneNumber, clipRow.ClipNumber, takeFp, local);
                continue;
            }
            if (!includeServerFallback)
                continue;
            var server = await ResolveServerClipUrlAsync(projectId, sceneNumber, clipRow, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(server))
                serverCandidates.Add((i, server, takeFp));
        }

        // Range probes are independent network operations. Run them together so scene startup
        // costs one probe round-trip instead of one round-trip per clip.
        await Parallel.ForEachAsync(
            serverCandidates,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (candidate, token) =>
            {
                if (await _engine.MediaUrlReachableAsync(candidate.Url, token).ConfigureAwait(false))
                {
                    resolved[candidate.Index] = candidate.Url;
                    MediaIndex.RememberClipUrl(
                        projectId, sceneNumber, rows[candidate.Index].ClipNumber,
                        candidate.Fingerprint, candidate.Url);
                }
            }).ConfigureAwait(false);

        return resolved;
    }

    /// <summary>
    /// The clip the operator is looking at on the Film page. <c>.current.json</c> decides which
    /// take that is — the same pointer <c>ScenesPlayback</c> resolves — so Play and the clip
    /// player never disagree about what "this clip" means.
    /// <para>The scene row's file name is a fallback, not the answer. It comes from the server,
    /// and the two drift by design in the cases the promote path already names out loud: promoted
    /// on the server while the media folder was disconnected, or while it refused the pointer
    /// write. Reading the row first meant Review played one take and the Film page showed another
    /// for the same slot.</para>
    /// </summary>
    private async Task<string?> TryLocalClipBlobUrlAsync(
        string projectId,
        int sceneNumber,
        ClipSummary clipRow,
        string? currentTakeRel = null)
    {
        if (_media is null)
            return null;

        var currentRel = currentTakeRel
            ?? await _media.ResolveCurrentTakeRelativePathAsync(projectId, sceneNumber, clipRow.ClipNumber)
                .ConfigureAwait(false);

        foreach (var candidate in ClipPathCandidates(currentRel, clipRow))
        {
            var url = await _media.GetLocalBlobUrlAsync(projectId, candidate).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        return null;
    }

    /// <summary>
    /// Where Play looks for a clip, in order. <c>.current.json</c> decides which take that is —
    /// the same pointer the Film page's clip player resolves — so the two never disagree about
    /// what "this clip" means.
    /// <para>The scene row's file name is a fallback, not the answer. It comes from the server,
    /// and the two drift in the cases the promote path already names out loud: promoted on the
    /// server while the media folder was disconnected, or while it refused the pointer write.
    /// Reading the row first meant Review played one take while the Film page showed another.
    /// The canonical <c>scene_SS_clip_CC.mp4</c> name is never a candidate: it is a leftover
    /// alias, not a take.</para>
    /// </summary>
    internal static IEnumerable<string> ClipPathCandidates(string? currentTakeRel, ClipSummary clipRow)
    {
        if (!string.IsNullOrWhiteSpace(currentTakeRel))
            yield return currentTakeRel;
        if (!string.IsNullOrWhiteSpace(clipRow.FileName)
            && !ClipTakeNaming.IsCanonicalClipName(clipRow.FileName))
            yield return $"{ClipTakeNaming.AssetsVideoPrefix}/{clipRow.FileName}";
    }

    /// <summary>Operator-facing copy when a clip has no local blob and no server mp4.</summary>
    internal static string FormatMissingClipPlayError(IReadOnlyList<string> missingKeys, bool mediaFolderConnected)
    {
        var listed = string.Join(", ", missingKeys);
        if (mediaFolderConnected)
            return $"{listed} could not be read from your local media folder.";
        return $"{listed} cannot be played. Connect your local media folder if the clips are on this computer, or generate them again.";
    }

    public async Task<ClientStitchResult> ConcatAsync(
        IReadOnlyList<string> urls,
        CancellationToken ct = default)
    {
        if (urls is null || urls.Count == 0)
            return ClientStitchResult.Fail("No video URLs to combine");

        if (urls.Count == 1)
        {
            // Still hash so film_build gets a studio.sha256 for single-scene projects.
            string? sha = null;
            long? blen = null;
            try
            {
                var hash = await _js.InvokeAsync<JsHashResult>(
                    "PageToMovieFfmpeg.hashUrlAsync", ct, urls[0]);
                if (hash is { Success: true })
                {
                    sha = hash.Sha256;
                    blen = hash.ByteLength;
                }
            }
            catch { /* non-fatal */ }
            return ClientStitchResult.Ok(urls[0], count: 1, single: true, sha, blen);
        }

        var optimized = await TryConcatWithJsAsync(OptimizedConcatJs, urls, ct);
        if (optimized.HasPlayableUrl)
            return optimized;

        // The shared pool already retries with one worker. Keep the original implementation as a
        // compatibility/recovery path for old cached clients and browser-specific codec failures.
        var legacy = await TryConcatWithJsAsync(LegacyConcatJs, urls, ct);
        return legacy;
    }

    private async Task<ClientStitchResult> TryConcatWithJsAsync(
        string identifier,
        IReadOnlyList<string> urls,
        CancellationToken ct)
    {
        try
        {
            var raw = await _js.InvokeAsync<JsConcatResult>(identifier, ct, (object)urls.ToArray());
            if (raw is null)
                return ClientStitchResult.Fail("No response from browser stitch");
            if (!raw.Success)
                return ClientStitchResult.Fail(raw.Error ?? "Browser stitch failed");
            if (raw.Url is not { Length: > 0 } stitchedUrl)
                return ClientStitchResult.Fail("Stitch produced no video URL");
            return ClientStitchResult.Ok(
                stitchedUrl,
                raw.Count > 0 ? raw.Count : urls.Count,
                raw.Single,
                raw.Sha256,
                raw.ByteLength);
        }
        catch (JSException jex)
        {
            return ClientStitchResult.Fail(jex.Message);
        }
        catch (Exception ex)
        {
            return ClientStitchResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Scene-clip preview stitch used by Scenes and Review playback: empty-url fail,
    /// combining status, revoke the previous preview blob, then concat. Page-specific
    /// empty messages and player flags stay at the call site via <paramref name="emptyError"/>
    /// and <paramref name="onFail"/>.
    /// </summary>
    public async Task<string?> TryConcatSceneClipsAsync(
        IReadOnlyList<string> urls,
        string emptyError,
        Action<string> setStatus,
        Action<string> onFail,
        CancellationToken ct = default)
    {
        if (urls.Count == 0)
        {
            onFail(emptyError);
            return null;
        }

        setStatus(urls.Count == 1 ? "Loading…" : $"Combining {urls.Count} clips…");
        await RevokePreviewUrlAsync();
        var result = await ConcatAsync(urls, ct);
        if (!result.HasPlayableUrl)
        {
            onFail(result.StitchError);
            return null;
        }

        return result.Url;
    }

    /// <summary>
    /// Same as <see cref="CollectAndMixSceneSegmentsAsync"/> but keeps scene numbers + relative paths
    /// for film_build EDL registration after full-film stitch.
    /// </summary>
    public async Task<IReadOnlyList<ClientWipSegment>> CollectAndMixSceneSegmentInfosAsync(
        string projectId,
        IReadOnlyList<int> sceneNumbers,
        IReadOnlyList<SceneSummary>? sceneList,
        IReadOnlySet<int>? staleScenes,
        CancellationToken ct = default)
    {
        var ordered = sceneNumbers.Distinct().OrderBy(x => x).ToList();
        var segments = new List<ClientWipSegment>(ordered.Count);
        BeginSkippedCollect();
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        SyncPlayableSceneGroup(projectId, sceneList, ordered);

        // Resolve clip URLs for every scene together so workers are not waiting on a
        // serial GetSceneDetail walk. Mix stays sequential (ffmpeg compose gate).
        var urlsByIndex = await ResolveSceneClipUrlListsAsync(projectId, ordered, sceneList, ct)
            .ConfigureAwait(false);

        for (var i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var segment = await MixOrReuseSceneSegmentAsync(
                projectId, ordered[i], urlsByIndex[i], sceneList, ct).ConfigureAwait(false);
            if (segment is not null)
                segments.Add(segment);
        }
        FinishSkippedCollect();
        return segments;
    }

    private void SyncPlayableSceneGroup(
        string projectId,
        IReadOnlyList<SceneSummary>? sceneList,
        IReadOnlyList<int> ordered)
    {
        if (sceneList is not { Count: > 0 })
            return;

        MediaIndex.SyncSceneList(projectId, sceneList);
        if (MediaIndex.TryGetSceneGroup(projectId, sceneList, out _))
        {
            LastCollectStats.AddGroupHit();
            return;
        }

        LastCollectStats.AddGroupMiss();
        MediaIndex.RememberSceneGroup(projectId, sceneList, ordered);
    }

    private async Task<List<string>[]> ResolveSceneClipUrlListsAsync(
        string projectId,
        IReadOnlyList<int> ordered,
        IReadOnlyList<SceneSummary>? sceneList,
        CancellationToken ct)
    {
        var urlsByIndex = new List<string>[ordered.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, ordered.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (index, token) =>
            {
                var sceneUrls = new List<string>();
                await AppendSceneMediaUrlsAsync(sceneUrls, projectId, ordered[index], sceneList, token)
                    .ConfigureAwait(false);
                urlsByIndex[index] = sceneUrls;
            }).ConfigureAwait(false);
        return urlsByIndex;
    }

    private async Task<ClientWipSegment?> MixOrReuseSceneSegmentAsync(
        string projectId,
        int sceneNumber,
        IReadOnlyList<string>? sceneUrls,
        IReadOnlyList<SceneSummary>? sceneList,
        CancellationToken ct)
    {
        if (sceneUrls is not { Count: > 0 })
            return null;

        var summary = sceneList?.FirstOrDefault(s => s.SceneNumber == sceneNumber);
        MediaIndex.TryGetSceneDetail(
            projectId, sceneNumber, PlayMediaIndex.FingerprintSummary(summary), out var detail);
        var segmentFp = PlayMediaIndex.FingerprintSegment(summary, detail, sceneUrls.Count);
        if (MediaIndex.TryGetSceneSegment(projectId, sceneNumber, segmentFp, out var cached)
            && cached is not null)
        {
            LastCollectStats.AddSegmentHit();
            return cached;
        }

        LastCollectStats.AddSegmentMiss();
        var sceneUrl = await ConcatAndMixSceneAsync(projectId, sceneUrls, sceneNumber, ct: ct);
        if (string.IsNullOrWhiteSpace(sceneUrl))
        {
            // Remember why: "no clips" and "could not stitch the clips" are different problems.
            var why = LastCollectError ?? "browser stitch failed";
            NoteCollectError(
                why.StartsWith("S", StringComparison.Ordinal) && why.Contains(" C", StringComparison.Ordinal)
                    ? why
                    : $"S{sceneNumber:D2}: {why}");
            return null;
        }

        var segment = new ClientWipSegment
        {
            SceneNumber = sceneNumber,
            Url = sceneUrl,
            RelativeSrc = $"assets/video/scene_{sceneNumber:D2}.mp4",
        };
        MediaIndex.RememberSceneSegment(projectId, sceneNumber, segmentFp, segment);
        return segment;
    }

    /// <summary>
    /// After a full-film browser stitch: probe segment + total durations, ensure studio sha256,
    /// POST film_build.v1 to the API (non-fatal on failure).
    /// </summary>
    public async Task<(bool Ok, string? FilmId, string? Error)> RegisterFilmBuildAfterWipStitchAsync(
        string projectId,
        IReadOnlyList<ClientWipSegment> segments,
        ClientStitchResult stitch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || stitch.Url is null || !stitch.Success)
            return (false, null, "No stitch result");
        if (segments.Count == 0)
            return (false, null, "No segments");

        try
        {
            var sha = stitch.Sha256;
            long? byteLen = stitch.ByteLength;
            if (string.IsNullOrWhiteSpace(sha))
            {
                var hash = await _js.InvokeAsync<JsHashResult>("PageToMovieFfmpeg.hashUrlAsync", ct, stitch.Url);
                if (hash is { Success: true } && !string.IsNullOrWhiteSpace(hash.Sha256))
                {
                    sha = hash.Sha256;
                    byteLen = hash.ByteLength ?? byteLen;
                }
            }

            if (string.IsNullOrWhiteSpace(sha))
                return (false, null, "Could not hash stitched video");

            double t = 0;
            var edl = new List<object>();
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var dur = await ProbeDurationSecondsAsync(seg.Url, ct);
                if (dur <= 0) dur = 0.1; // keep monotonic timeline
                edl.Add(new
                {
                    index = i,
                    scene = seg.SceneNumber,
                    clip = (int?)null,
                    take = (int?)null,
                    tStart = t,
                    tEnd = t + dur,
                    src = seg.RelativeSrc,
                    srcSha256 = (string?)null,
                    sidecar = (string?)null,
                });
                t += dur;
            }

            // Prefer probed total; fall back to sum of segments
            var total = await ProbeDurationSecondsAsync(stitch.Url, ct);
            if (total <= 0) total = t;

            var body = new
            {
                studioSha256 = sha,
                durationSeconds = total,
                byteLength = byteLen,
                studioPath = "assets/movie_wip.mp4",
                assemblyWhere = "client",
                segments = edl,
            };

            var resp = await _engine.RegisterFilmBuildAsync(projectId, body, ct);
            return resp;
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<double> ProbeDurationSecondsAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;
        try
        {
            var probe = await _js.InvokeAsync<JsProbeResult>(
                "PageToMovieFfmpeg.probeDurationAsync", ct, url);
            return probe is { Success: true } && probe.Seconds > 0 ? probe.Seconds : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Layers a scene's locally-synced background-music segments under a video URL (volume
    /// ducking + fade-out), the client-side replacement for the old server ffmpeg mix. Returns
    /// <paramref name="videoUrl"/> unchanged if no music segments are synced locally for the scene.
    /// </summary>
    public async Task<string> MixSceneMusicAsync(
        string projectId,
        string videoUrl,
        int sceneNumber,
        int volumePercent = 20,
        CancellationToken ct = default)
    {
        if (_media is null || string.IsNullOrWhiteSpace(videoUrl))
            return videoUrl;

        var musicUrl = await ResolveSceneMusicUrlAsync(projectId, sceneNumber);
        return string.IsNullOrWhiteSpace(musicUrl)
            ? videoUrl
            : await MixResolvedMusicAsync([videoUrl], videoUrl, musicUrl, volumePercent, ct);
    }

    private async Task<string?> ConcatAndMixSceneAsync(
        string projectId,
        IReadOnlyList<string> videoUrls,
        int sceneNumber,
        int volumePercent = 20,
        CancellationToken ct = default)
    {
        var musicUrl = await ResolveSceneMusicUrlAsync(projectId, sceneNumber);
        if (string.IsNullOrWhiteSpace(musicUrl))
        {
            var concat = await ConcatAsync(videoUrls, ct);
            if (concat.HasPlayableUrl)
                return concat.Url;
            LastCollectError = concat.StitchError;
            return null;
        }

        try
        {
            var optimized = await _js.InvokeAsync<JsConcatResult>(
                OptimizedConcatAndMixJs,
                ct,
                (object)videoUrls.ToArray(),
                musicUrl,
                volumePercent);
            if (optimized is { Success: true, Url: { Length: > 0 } optimizedUrl })
                return optimizedUrl;
        }
        catch
        {
            // Shared fast path is optional at runtime; use the proven two-pass path below.
        }

        var fallback = await ConcatAsync(videoUrls, ct);
        if (!fallback.HasPlayableUrl)
        {
            LastCollectError = fallback.StitchError;
            return null;
        }
        return await MixResolvedMusicAsync(videoUrls, fallback.Url!, musicUrl, volumePercent, ct, tryOptimized: false);
    }

    private async Task<string?> ResolveSceneMusicUrlAsync(string projectId, int sceneNumber)
    {
        if (_media is null)
            return null;
        var segmentUrls = await _media.GetSceneMusicSegmentUrlsAsync(projectId, sceneNumber);
        if (segmentUrls.Count == 0)
            return null;
        if (segmentUrls.Count == 1)
            return segmentUrls[0];
        try
        {
            var concat = await _js.InvokeAsync<JsConcatResult>(
                "PageToMovieFfmpeg.concatAudioSegmentsAsync", (object)segmentUrls.ToArray());
            return concat is { Success: true, Url: { Length: > 0 } concatUrl } ? concatUrl : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> MixResolvedMusicAsync(
        IReadOnlyList<string> sourceVideoUrls,
        string videoUrl,
        string musicUrl,
        int volumePercent,
        CancellationToken ct,
        bool tryOptimized = true)
    {
        if (tryOptimized)
        {
            try
            {
                var optimized = await _js.InvokeAsync<JsConcatResult>(
                    OptimizedConcatAndMixJs,
                    ct,
                    (object)sourceVideoUrls.ToArray(),
                    musicUrl,
                    volumePercent);
                if (optimized is { Success: true, Url: { Length: > 0 } optimizedUrl })
                    return optimizedUrl;
            }
            catch
            {
                // Keep best-effort music behavior when the shared renderer is unavailable.
            }
        }

        try
        {
            var mixed = await _js.InvokeAsync<JsConcatResult>(
                "PageToMovieFfmpeg.mixSceneAudioAsync", ct, videoUrl, musicUrl, volumePercent);
            return mixed is { Success: true, Url: { Length: > 0 } mixedUrl } ? mixedUrl : videoUrl;
        }
        catch
        {
            return videoUrl; // mixing is best-effort — never block playback on a music failure
        }
    }

    public async Task RevokePreviewUrlAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("PageToMovieFfmpeg.revokePreviewUrl");
        }
        catch
        {
            // optional
        }
    }

    /// <summary>Browser duration probe (ffmpeg.wasm).</summary>
    /// <summary>Keep the last <paramref name="keepSeconds"/> of a video as a blob URL (ffmpeg.wasm); null on failure.</summary>
    public async Task<string?> TrimTailAsync(string url, double keepSeconds, CancellationToken ct = default)
    {
        try
        {
            var r = await _js.InvokeAsync<JsTrimTailResult>("PageToMovieFfmpeg.keepLastSecondsAsync", ct, url, keepSeconds, null);
            return r is { Success: true } && !string.IsNullOrWhiteSpace(r.Url) ? r.Url : null;
        }
        catch { return null; }
    }

    public async Task<double?> ProbeDurationAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var raw = await _js.InvokeAsync<JsProbeResult>(
                "PageToMovieFfmpeg.probeDurationAsync", ct, url);
            return raw is { Success: true, Seconds: > 0 } ? raw.Seconds : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sample JPEG frames for one clip (and previous tail when clip &gt; 1) for auto-review upload.
    /// Prefers local media-folder blobs, else authenticated clip proxy URLs.
    /// </summary>
    public async Task<(IReadOnlyList<ClipAutoReviewClientFrame> Frames, string? Error)> SampleAutoReviewFramesAsync(
        string projectId,
        int scene,
        int clip,
        CancellationToken ct = default)
    {
        var frames = new List<ClipAutoReviewClientFrame>();
        try
        {
            await AppendPreviousClipTailFramesAsync(projectId, scene, clip, frames, ct);

            var curUrl = await ResolveClipUrlAsync(projectId, scene, clip, ct);
            if (string.IsNullOrWhiteSpace(curUrl))
                return (frames, $"No video URL for S{scene:D2}C{clip:D2} (connect media folder or ensure clip exists).");

            var cur = await ExtractFramesRawAsync(curUrl, mode: "span", count: 3, ct);
            if (!cur.Success || cur.Frames is null || cur.Frames.Count == 0)
                return (frames, cur.Error ?? "Could not sample frames from current clip");

            AppendLabeledFrames(frames, cur.Frames, "CURRENT_CLIP");

            if (frames.Count == 0)
                return (frames, "No frames produced");
            return (frames, null);
        }
        catch (Exception ex)
        {
            return (frames, ex.Message);
        }
    }

    private async Task AppendPreviousClipTailFramesAsync(
        string projectId, int scene, int clip, List<ClipAutoReviewClientFrame> frames, CancellationToken ct)
    {
        if (clip > 1)
        {
            var prevUrl = await ResolveClipUrlAsync(projectId, scene, clip - 1, ct);
            if (!string.IsNullOrWhiteSpace(prevUrl))
            {
                var prev = await ExtractFramesRawAsync(prevUrl, mode: "tail", count: 3, ct);
                if (prev.Success && prev.Frames is { Count: > 0 })
                    AppendLabeledFrames(frames, prev.Frames, "PREVIOUS_CLIP_TAIL");
            }
        }
    }

    private static void AppendLabeledFrames(
        List<ClipAutoReviewClientFrame> frames, IReadOnlyList<JsFrameItem> source, string label)
    {
        foreach (var f in source)
        {
            if (string.IsNullOrWhiteSpace(f.Base64)) continue;
            frames.Add(new ClipAutoReviewClientFrame
            {
                Label = label,
                Mime = string.IsNullOrWhiteSpace(f.Mime) ? "image/jpeg" : f.Mime,
                Base64 = f.Base64,
            });
        }
    }

    /// <summary>Resolve a clip's playable URL: local media-folder blob when synced, else clip proxy.</summary>
    public async Task<string?> ResolveClipUrlAsync(
        string projectId, int scene, int clip, CancellationToken ct)
    {
        var rel = _media is not null
            ? await _media.ResolveCurrentTakeRelativePathAsync(projectId, scene, clip)
            : null;
        if (_media is not null && !string.IsNullOrWhiteSpace(rel))
        {
            var local = await _media.GetLocalBlobUrlAsync(projectId, rel);
            if (!string.IsNullOrWhiteSpace(local))
                return local;
        }
        // No local copy → fall back to the authenticated clip proxy; ensure a fresh ?mt= token first.
        await _engine.EnsureMediaAccessAsync(ct).ConfigureAwait(false);
        return _engine.ClipVideoUrl(projectId, scene, clip);
    }

    public async Task<JsFramesResult> ExtractFramesRawAsync(
        string url, string mode, int count, CancellationToken ct = default)
    {
        try
        {
            var raw = await _js.InvokeAsync<JsFramesResult>(
                "PageToMovieFfmpeg.extractFramesAsync",
                ct,
                url,
                new { mode, count, maxWidth = 640, quality = 5 });
            return raw ?? new JsFramesResult { Success = false, Error = "No response from frame extract" };
        }
        catch (Exception ex)
        {
            return new JsFramesResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// ffmpeg credits generator: canvas → ffmpeg.wasm, then the same take pipeline as any
    /// other clip (take_NN; current take is .current.json). No catalog video client.
    /// </summary>
    public async Task<(bool Ok, string? Error)> RenderAndStoreCreditsClipAsync(
        ProjectClipRef clipRef, double durationSeconds,
        int width, int height, int fps, CancellationToken ct = default)
    {
        var projectId = clipRef.ProjectId;
        var scene = clipRef.Scene;
        var clip = clipRef.Clip;
        try
        {
            var content = await _engine.GetCreditsContentAsync(projectId, ct).ConfigureAwait(false);
            // ffmpeg.wasm can wedge (worker never answers). Bound the render so the page says so instead
            // of sitting on "Waiting…" forever; 3 minutes is generous for a few seconds of canvas video.
            using var renderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            renderCts.CancelAfter(TimeSpan.FromMinutes(3));
            JsCreditsResult? res;
            try
            {
                res = await _js.InvokeAsync<JsCreditsResult>(
                "PageToMovieFfmpeg.renderCreditsClipAsync", renderCts.Token, new
                {
                    title = content?.Title ?? "The End",
                    author = content?.Author ?? "",
                    softwareName = content?.SoftwareName ?? "PageToMovie",
                    siteUrl = content?.SiteUrl ?? "pagetomovie.com",
                    width,
                    height,
                    fps,
                    durationSec = durationSeconds <= 0 ? 5 : durationSeconds,
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (false, "Credits card render timed out in the browser (ffmpeg.wasm did not answer within 3 minutes). Reload the page and try again.");
            }
            if (res is not { Success: true } || string.IsNullOrEmpty(res.Mp4Base64))
                return (false, res?.Error ?? "Credits card render failed");

            var bytes = Convert.FromBase64String(res.Mp4Base64);

            // ffmpeg generator → same take persist as any other clip (take_NN + .current.json).
            var uploaded = await _engine.UploadClipWithResultAsync(
                    projectId, scene, clip, bytes, kind: "credits", seconds: durationSeconds, ct)
                .ConfigureAwait(false);
            if (!uploaded.Ok)
                return (false, $"Rendered the credits card but could not save it: {uploaded.Error}");

            var take = uploaded.Take > 0 ? uploaded.Take : ClipTakeNaming.ParseTakeNumber(uploaded.RelativePath);
            if (take <= 0) take = 1;
            await SaveCreditsTakeLocallyAsync(projectId, scene, clip, take, bytes, ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task SaveCreditsTakeLocallyAsync(
        string projectId, int scene, int clip, int take, byte[] bytes, CancellationToken ct)
    {
        if (_media is null)
            return;
        var takeRel = ClipTakeNaming.TakeRelativePath(scene, clip, take);
        await SaveAndRegisterCreditsFileAsync(projectId, scene, clip, takeRel, bytes, ct).ConfigureAwait(false);
    }

    private async Task SaveAndRegisterCreditsFileAsync(
        string projectId, int scene, int clip, string relPath, byte[] bytes, CancellationToken ct)
    {
        if (_media is null)
            return;
        var (savedOk, _, sha256, sizeBytes, _) =
            await _media.SaveBytesAsync(projectId, relPath, bytes).ConfigureAwait(false);
        if (!savedOk || string.IsNullOrWhiteSpace(sha256))
            return;
        try
        {
            await _engine.RegisterMediaAsync(projectId, new MediaRegisterRequest
            {
                RelativePath = relPath,
                Sha256 = sha256,
                SizeBytes = sizeBytes,
                Kind = "credits",
                Scene = scene,
                Clip = clip,
            }, ct).ConfigureAwait(false);
        }
        catch { /* local save is enough; register is best-effort */ }
    }

    private sealed class JsCreditsResult
    {
        public bool Success { get; set; } = false;
        public string? Mp4Base64 { get; set; } = null;
        public long ByteLength { get; set; } = 0;
        public string? Error { get; set; } = null;
    }

    private sealed class JsConcatResult
    {
        public bool Success { get; set; } = false;
        public string? Url { get; set; } = null;
        public string? Error { get; set; } = null;
        public int Count { get; set; } = 0;
        public bool Single { get; set; } = false;
        public string? Sha256 { get; set; } = null;
        public long? ByteLength { get; set; } = null;
    }

    private sealed class JsProbeResult
    {
        public bool Success { get; set; } = false;
        public double Seconds { get; set; } = 0;
        public string? Error { get; set; } = null;
    }

    private sealed class JsTrimTailResult
    {
        public bool Success { get; set; } = false;
        public string? Url { get; set; } = null;
        public double SourceDurationSec { get; set; } = 0;
        public double KeptSec { get; set; } = 0;
        public string? Error { get; set; } = null;
    }

    public sealed class JsFramesResult
    {
        public bool Success { get; set; } = false;
        public string? Error { get; set; } = null;
        public List<JsFrameItem>? Frames { get; set; } = null;
    }

    public sealed class JsFrameItem
    {
        public string? Base64 { get; set; } = null;
        public string? Mime { get; set; } = null;
    }
}

public sealed class ClientStitchResult
{
    public bool Success { get; init; }
    public string? Url { get; init; }
    public string? Error { get; init; }
    public int Count { get; init; }
    public bool Single { get; init; }
    /// <summary>SHA-256 of stitched bytes when computed in the browser.</summary>
    public string? Sha256 { get; init; }
    public long? ByteLength { get; init; }

    public static ClientStitchResult Ok(
        string url, int count = 1, bool single = false, string? sha256 = null, long? byteLength = null) =>
        new()
        {
            Success = true,
            Url = url,
            Count = count,
            Single = single,
            Sha256 = sha256,
            ByteLength = byteLength,
        };

    public static ClientStitchResult Fail(string error) =>
        new() { Success = false, Error = error };

    /// <summary>True when concat produced a playable URL.</summary>
    public bool HasPlayableUrl => Success && !string.IsNullOrWhiteSpace(Url);

    /// <summary>Operator-facing stitch failure; default when the browser returned no detail.</summary>
    public string StitchError => Error ?? "Browser stitch failed";
}

/// <summary>One scene segment used in a full-film client stitch (URL + path for film_build EDL).</summary>
public sealed class ClientWipSegment
{
    public int SceneNumber { get; init; }
    public string Url { get; init; } = "";
    public string RelativeSrc { get; init; } = "";
}

public sealed class JsHashResult
{
    public bool Success { get; set; } = false;
    public string? Sha256 { get; set; } = null;
    public long? ByteLength { get; set; } = null;
    public string? Error { get; set; } = null;
}
