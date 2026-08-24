/**
 * Standalone Cut — local folder + browser compose.
 * ffmpeg load / concat / probe / mix stay in PageToMovieFfmpeg (copied from Web).
 * Ops go through that helper's exclusive queue.
 */
(function () {
    const cut = {
        _root: null,
        _fallbackFiles: null,
        _debugFolder: null,
        _trimSeq: 0,
        _ownedMovieUrl: null,
        _ownedPrefixUrls: [],
        _ownedTemps: new Set(),
        _activeInputs: new Set(),
        _pendingRevoke: new Set(),
        _lastDebugError: null,
        _lastComposeMetrics: null,
        _progressRef: null,
        _aborted: false,
        _composeGate: Promise.resolve(),
        _playClock: {
            mode: "idle",
            timelineStart: 0,
            localStart: 0,
            localEnd: 0,
            pxPerSec: 36,
            totalSec: 0,
            timelineSec: 0,
            holdSec: null,
            liveText: true,
        },
        _playSurfaces: { clip: null, movie: null, front: null },
        _playSwapSeq: 0,
        _textOverlayEl: null,
        _textCues: [],
    };

    /** Matches CutComposeContract.CutToBlackHoldSeconds — black hold, not a card. */
    const CUT_TO_BLACK_HOLD_SEC = 0.4;
    /** Matches CutComposeContract.XfadeSeconds / XfadeMinSeconds. */
    const CUT_XFADE_SEC = 0.5;
    const CUT_XFADE_MIN_SEC = 0.2;
    /** Matches CutFfmpegEncode / CutComposeContract WMP-safe H.264. */
    const CUT_H264_PRESET = "ultrafast";
    const CUT_H264_CRF = "23";
    const CUT_H264_PIX = "yuv420p";
    const CUT_H264_PROFILE = "main";
    const CUT_AAC_RATE = "128k";
    const FFMPEG_WORKER_MIN = 1;
    const FFMPEG_WORKER_MAX = 4;
    const FFMPEG_WORKER_STORAGE_KEY = "pagetomovie.cut.ffmpegWorkers";
    const FFMPEG_STITCH_WORKER_STORAGE_KEY = "pagetomovie.cut.ffmpegStitchWorkers";
    const FFMPEG_CLIP_WORKER_STORAGE_KEY = "pagetomovie.cut.ffmpegClipWorkers";

    function clampWorkerCount(value) {
        const parsed = Math.trunc(Number(value));
        if (!Number.isFinite(parsed)) return FFMPEG_WORKER_MIN;
        return Math.min(FFMPEG_WORKER_MAX, Math.max(FFMPEG_WORKER_MIN, parsed));
    }

    function queryFlag(name) {
        try {
            const value = new URLSearchParams(window.location.search).get(name);
            return value === "1" || String(value || "").toLowerCase() === "true";
        } catch (_) {
            return false;
        }
    }

    function requestedWorkerCount() {
        try {
            const query = new URLSearchParams(window.location.search).get("ffmpegWorkers");
            if (query !== null)
                return clampWorkerCount(query);
            return clampWorkerCount(window.localStorage.getItem(FFMPEG_WORKER_STORAGE_KEY) || 1);
        } catch (_) {
            return FFMPEG_WORKER_MIN;
        }
    }

    function requestedStitchWorkerCount() {
        try {
            const query = new URLSearchParams(window.location.search).get("ffmpegStitchWorkers");
            if (query !== null)
                return clampWorkerCount(query);
            return clampWorkerCount(window.localStorage.getItem(FFMPEG_STITCH_WORKER_STORAGE_KEY) || 1);
        } catch (_) {
            return FFMPEG_WORKER_MIN;
        }
    }

    function requestedClipWorkerCount() {
        try {
            const query = new URLSearchParams(window.location.search).get("ffmpegClipWorkers");
            if (query !== null)
                return clampWorkerCount(query);
            return clampWorkerCount(window.localStorage.getItem(FFMPEG_CLIP_WORKER_STORAGE_KEY) || 1);
        } catch (_) {
            return FFMPEG_WORKER_MIN;
        }
    }

    cut.setFfmpegWorkerCount = function (value) {
        const count = clampWorkerCount(value);
        try { window.localStorage.setItem(FFMPEG_WORKER_STORAGE_KEY, String(count)); } catch (_) { }
        return count;
    };

    cut.setFfmpegStitchWorkerCount = function (value) {
        const count = clampWorkerCount(value);
        try { window.localStorage.setItem(FFMPEG_STITCH_WORKER_STORAGE_KEY, String(count)); } catch (_) { }
        return count;
    };

    cut.setFfmpegClipWorkerCount = function (value) {
        const count = clampWorkerCount(value);
        try { window.localStorage.setItem(FFMPEG_CLIP_WORKER_STORAGE_KEY, String(count)); } catch (_) { }
        return count;
    };

    cut.getFfmpegWorkerConfig = function () {
        return {
            requested: requestedWorkerCount(),
            stitchRequested: requestedStitchWorkerCount(),
            clipRequested: requestedClipWorkerCount(),
            min: FFMPEG_WORKER_MIN,
            max: FFMPEG_WORKER_MAX,
            forceFresh: queryFlag("ffmpegFresh"),
            combinedConcatMix: queryFlag("ffmpegCombined"),
            flatClipPipeline: queryFlag("ffmpegFlat"),
        };
    };

    cut.getLastComposeMetrics = function () {
        return cut._lastComposeMetrics;
    };

    try {
        document.documentElement.dataset.cutFfmpegConfig = JSON.stringify(cut.getFfmpegWorkerConfig());
    } catch (_) { }

    function isFsError(err) {
        if (!err)
            return false;
        const name = String(err.name || "");
        const msg = String(err.message || err);
        return name === "ErrnoError"
            || /FS error/i.test(msg)
            || /ErrnoError/i.test(msg)
            || /memory access out of bounds/i.test(msg);
    }

    /** Keep in sync with CutComposeContract.BrowserWorkingFileError. */
    function fsUserMessage() {
        return "Could not finish the movie file. Stop playback, then try Make movie again.";
    }

    function messageOf(err, fallback) {
        if (isFsError(err))
            return fsUserMessage();
        return err?.message || fallback;
    }

    async function fetchInputBytes(api, url, label) {
        if (!url)
            throw new Error(label + " is missing.");
        try {
            const data = await api._safeFetchFile(url);
            if (!data || !data.length)
                throw new Error(label + " could not be read. Try Make movie again.");
            return data;
        } catch (err) {
            if (isFsError(err))
                throw new Error(fsUserMessage());
            const msg = String(err && err.message ? err.message : err || "");
            if (/failed to fetch|NetworkError|ERR_FILE_NOT_FOUND/i.test(msg))
                throw new Error(label + " is no longer available. Try Make movie again.");
            throw err;
        }
    }

    async function sweepCutMemfs(ffmpeg) {
        if (!ffmpeg || typeof ffmpeg.listDir !== "function")
            return;
        let entries = [];
        try {
            entries = await ffmpeg.listDir("/");
        } catch (err) {
            console.debug("Cut: memfs list", err);
            return;
        }
        for (let i = 0; i < entries.length; i++) {
            const entry = entries[i];
            const name = entry && typeof entry === "object" ? entry.name : entry;
            if (typeof name !== "string" || name === "." || name === "..")
                continue;
            if (name.indexOf("cut_") !== 0)
                continue;
            await deleteMemfs(ffmpeg, name);
        }
    }

    async function writeMemfs(ffmpeg, name, data) {
        if (!data || data.length === 0)
            throw new Error("A clip or soundtrack could not be read. Try Make movie again.");
        await deleteMemfs(ffmpeg, name);
        try {
            await ffmpeg.writeFile(name, data);
        } catch (err) {
            if (!isFsError(err))
                throw err;
            await sweepCutMemfs(ffmpeg);
            await deleteMemfs(ffmpeg, name);
            await ffmpeg.writeFile(name, data);
        }
    }

    async function execChecked(ffmpeg, args, label) {
        const code = await ffmpeg.exec(args);
        if (typeof code === "number" && code !== 0)
            throw new Error((label || "ffmpeg command failed") + " (exit " + code + ")");
        return code;
    }

    async function resetFfmpegWorker(api) {
        if (!api)
            return;
        try {
            if (api._ffmpeg && typeof api._ffmpeg.terminate === "function")
                api._ffmpeg.terminate();
        } catch (err) {
            console.debug("Cut: ffmpeg reset", err);
        }
        api._ffmpeg = null;
        api._loaded = false;
        api._loading = null;
    }

    async function drainComposeAsync() {
        try {
            await cut._composeGate;
        } catch (_) { /* previous compose already surfaced */ }
        const api = window.PageToMovieFfmpeg;
        if (api && api._lock) {
            try { await api._lock; } catch (_) { /* exclusive queue drained */ }
        }
        if (api && api._ffmpeg)
            await sweepCutMemfs(api._ffmpeg);
    }

    function splitRelPath(relativePath) {
        return String(relativePath || "").replaceAll("\\", "/").split("/").filter(Boolean);
    }

    async function fileHandleAt(root, relativePath, create) {
        const parts = splitRelPath(relativePath);
        if (parts.length === 0)
            throw new Error("Clip is missing.");
        let dir = root;
        for (const part of parts.slice(0, -1))
            dir = await dir.getDirectoryHandle(part, { create: !!create });
        return dir.getFileHandle(parts.at(-1), { create: !!create });
    }

    function invokeQuiet(dotNetRef, method) {
        if (!dotNetRef || typeof dotNetRef.invokeMethodAsync !== "function")
            return;
        if (dotNetRef._id === undefined || dotNetRef._id === null)
            return;
        const args = Array.prototype.slice.call(arguments, 2);
        try {
            const pending = dotNetRef.invokeMethodAsync.apply(dotNetRef, [method].concat(args));
            if (pending && typeof pending.catch === "function")
                pending.catch(function () { /* disposed DotNetObjectReference */ });
        } catch (_) {
            // Disposed or gone — progress/time is optional.
        }
    }

    function invokeCompose(dotNetRef, method) {
        if (cut._aborted || cut._progressRef !== dotNetRef)
            return;
        invokeQuiet.apply(null, arguments);
    }

    function asProgress(dotNetRef) {
        if (!dotNetRef) return undefined;
        cut._progressRef = dotNetRef;
        return function (pct, msg) {
            invokeCompose(dotNetRef, "Report", Math.round(pct || 0), msg || "");
        };
    }

    function emitPrefix(dotNetRef, url, clipCount) {
        if (!dotNetRef || !url) return;
        invokeCompose(dotNetRef, "OnPrefix", url, clipCount);
    }

    function noteTemp(url) {
        if (typeof url === "string" && url.startsWith("blob:"))
            cut._ownedTemps.add(url);
    }

    function noteResult(r) {
        if (r && r.success && r.url && !r.single)
            noteTemp(r.url);
        return r;
    }

    function pinUrl(url) {
        if (typeof url === "string" && url.length > 0)
            cut._activeInputs.add(url);
    }

    function unpinUrl(url) {
        cut._activeInputs.delete(url);
    }

    function isPinnedPrefix(url) {
        return url === cut._ownedMovieUrl || cut._ownedPrefixUrls.indexOf(url) >= 0;
    }

    /** Same rules as CutBlobLifetime.CanRevoke — never revoke an in-flight concat/JIT URL. */
    function canRevokeTemp(url) {
        if (typeof url !== "string" || !url.startsWith("blob:"))
            return false;
        if (cut._activeInputs.has(url))
            return false;
        if (isPinnedPrefix(url))
            return false;
        return cut._ownedTemps.has(url);
    }

    function actuallyRevoke(url) {
        try {
            URL.revokeObjectURL(url);
        } catch (err) {
            console.debug("Cut: revoke skipped", err);
        }
        cut._ownedTemps.delete(url);
        cut._pendingRevoke.delete(url);
        cut._ownedPrefixUrls = cut._ownedPrefixUrls.filter(function (u) { return u !== url; });
        if (cut._ownedMovieUrl === url)
            cut._ownedMovieUrl = null;
    }

    function releaseTempUrl(url) {
        if (typeof url !== "string" || !url.startsWith("blob:") || !cut._ownedTemps.has(url))
            return;
        if (isPinnedPrefix(url))
            return;
        if (cut._activeInputs.has(url)) {
            cut._pendingRevoke.add(url);
            return;
        }
        actuallyRevoke(url);
    }

    function flushPendingRevokes() {
        Array.from(cut._pendingRevoke).forEach(function (url) {
            if (!cut._activeInputs.has(url))
                actuallyRevoke(url);
        });
    }

    async function withPinnedUrls(urls, fn) {
        const list = (urls || []).filter(function (u) { return typeof u === "string" && u.length > 0; });
        list.forEach(pinUrl);
        try {
            return await fn();
        } finally {
            list.forEach(unpinUrl);
            flushPendingRevokes();
        }
    }

    function keepPrefixUrl(url) {
        if (typeof url !== "string" || !url.startsWith("blob:")) return;
        if (cut._ownedPrefixUrls.indexOf(url) >= 0) return;
        cut._ownedPrefixUrls.push(url);
        while (cut._ownedPrefixUrls.length > 3) {
            const old = cut._ownedPrefixUrls[0];
            if (cut._activeInputs.has(old) || old === cut._ownedMovieUrl)
                break;
            cut._ownedPrefixUrls.shift();
            if (canRevokeTemp(old))
                actuallyRevoke(old);
        }
    }

    function replaceOwnedMovie(url, owned) {
        const prev = cut._ownedMovieUrl;
        cut._ownedMovieUrl = owned ? url : null;
        if (prev && prev !== url) {
            cut._ownedPrefixUrls = cut._ownedPrefixUrls.filter(function (u) { return u !== prev; });
            if (canRevokeTemp(prev))
                actuallyRevoke(prev);
        }
    }

    function composeStopped() {
        return !!cut._aborted;
    }

    async function concatPinned(api, urls, onProgress) {
        return withPinnedUrls(urls, async function () {
            const list = (urls || []).filter(Boolean);
            if (list.length === 0)
                return { success: false, error: "No clips to combine." };
            if (list.length === 1)
                return { success: true, url: list[0], single: true };
            return noteResult(await concatEncodeAsync(api, list, onProgress));
        });
    }

    async function concatEncodeOnce(api, ffmpeg, urls, seq) {
        const names = [];
        const outName = "cut_cat_" + seq + ".mp4";
        const listName = "cut_cat_" + seq + ".txt";
        try {
            let outputSec = 0;
            const durations = [];
            for (let i = 0; i < urls.length; i++) {
                const n = "cut_cat_" + seq + "_" + i + ".mp4";
                names.push(n);
                await writeMemfs(ffmpeg, n, await fetchInputBytes(api, urls[i], "Clip"));
                const probe = await api._probeDurationMemfsAsync(n);
                const seconds = probe.success && Number(probe.seconds) > 0
                    ? Number(probe.seconds) : 0;
                durations.push(seconds);
                outputSec += seconds;
            }
            const list = [];
            for (let i = 0; i < names.length; i++) {
                list.push("file '" + names[i] + "'");
                if (durations[i] > 0.001)
                    list.push("duration " + durations[i]);
            }
            await writeMemfs(ffmpeg, listName, list.join("\n"));
            const input = ["-hide_banner", "-y", "-fflags", "+genpts", "-f", "concat", "-safe", "0", "-i", listName];
            const cap = outputSec > 0.05 ? ["-t", String(outputSec)] : [];
            try {
                await execChecked(ffmpeg, input.concat(
                    ["-vf", "setpts=PTS-STARTPTS", "-af", "asetpts=PTS-STARTPTS"],
                    h264EncodeArgs("aac"), cap, [outName]));
            } catch (audioErr) {
                console.debug("Cut: concat audio missing", audioErr);
                try { await ffmpeg.deleteFile(outName); } catch (delErr) {
                    console.debug("Cut: concat out cleanup", delErr);
                }
                await execChecked(ffmpeg, input.concat(
                    ["-vf", "setpts=PTS-STARTPTS"],
                    h264EncodeArgs("an"), cap, [outName]));
            }
            const out = await ffmpeg.readFile(outName);
            const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
            noteTemp(url);
            return { success: true, url: url };
        } finally {
            for (let i = 0; i < names.length; i++)
                await deleteMemfs(ffmpeg, names[i]);
            await deleteMemfs(ffmpeg, listName);
            await deleteMemfs(ffmpeg, outName);
        }
    }

    async function concatEncodeAsync(api, urls, onProgress) {
        return api._runExclusiveAsync(async function () {
            let load = await api.ensureLoadedAsync(onProgress);
            if (!load.success)
                return { success: false, error: load.error };
            const seq = ++cut._trimSeq;
            try {
                return await concatEncodeOnce(api, api._ffmpeg, urls, seq);
            } catch (err) {
                if (!isFsError(err))
                    return { success: false, error: messageOf(err, "Combine failed.") };
                await resetFfmpegWorker(api);
                load = await api.ensureLoadedAsync(onProgress);
                if (!load.success)
                    return { success: false, error: load.error || fsUserMessage() };
                try {
                    return await concatEncodeOnce(api, api._ffmpeg, urls, seq);
                } catch (retry) {
                    return { success: false, error: messageOf(retry, fsUserMessage()) };
                }
            }
        });
    }

    async function concatVideoRemuxOnce(api, ffmpeg, urls, seq) {
        const names = [];
        const outName = "cut_vcat_" + seq + ".mp4";
        const listName = "cut_vcat_" + seq + ".txt";
        try {
            let outputSec = 0;
            const durations = [];
            for (let i = 0; i < urls.length; i++) {
                const name = "cut_vcat_" + seq + "_" + i + ".mp4";
                names.push(name);
                await writeMemfs(ffmpeg, name, await fetchInputBytes(api, urls[i], "Clip"));
                const probe = await api._probeDurationMemfsAsync(name);
                const seconds = probe.success && Number(probe.seconds) > 0
                    ? Number(probe.seconds) : 0;
                durations.push(seconds);
                outputSec += seconds;
            }
            const list = [];
            for (let i = 0; i < names.length; i++) {
                list.push("file '" + names[i] + "'");
                if (durations[i] > 0.001)
                    list.push("duration " + durations[i]);
            }
            await writeMemfs(ffmpeg, listName, list.join("\n"));
            const cap = outputSec > 0.05 ? ["-t", String(outputSec)] : [];
            await execChecked(ffmpeg, [
                "-hide_banner", "-y", "-fflags", "+genpts",
                "-f", "concat", "-safe", "0", "-i", listName,
                "-map", "0:v:0", "-c:v", "copy", "-an",
                "-avoid_negative_ts", "make_zero", "-movflags", "+faststart",
            ].concat(cap, [outName]));
            const out = await ffmpeg.readFile(outName);
            const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
            noteTemp(url);
            return { success: true, url: url, seconds: outputSec };
        } finally {
            for (const name of names)
                await deleteMemfs(ffmpeg, name);
            await deleteMemfs(ffmpeg, listName);
            await deleteMemfs(ffmpeg, outName);
        }
    }

    async function concatVideoRemuxAsync(api, urls, onProgress) {
        return api._runExclusiveAsync(async function () {
            await resetFfmpegWorker(api);
            let load = await api.ensureLoadedAsync(onProgress);
            if (!load.success)
                return { success: false, error: load.error };
            const seq = ++cut._trimSeq;
            try {
                return await concatVideoRemuxOnce(api, api._ffmpeg, urls, seq);
            } catch (err) {
                if (!isFsError(err))
                    return { success: false, error: messageOf(err, "Video stitch failed.") };
                await resetFfmpegWorker(api);
                load = await api.ensureLoadedAsync(onProgress);
                if (!load.success)
                    return { success: false, error: load.error || fsUserMessage() };
                try {
                    return await concatVideoRemuxOnce(api, api._ffmpeg, urls, seq);
                } catch (retry) {
                    return { success: false, error: messageOf(retry, fsUserMessage()) };
                }
            }
        });
    }

    async function collectMediaFile(handle, name, path, files) {
        if (handle.kind !== "file") return;
            const keep = /\.mp4$/i.test(name)
                || /\.(mp3|wav|m4a|aac)$/i.test(name) // CutMusicPersist.IsAudioFileName
                || /\.current\.json$/i.test(name)
                || /\.clip\.json$/i.test(name)
                || /^cut\.project\.json$/i.test(name);
            if (!keep) return;
            const isPointer = !/\.mp4$/i.test(name);
        try {
            const file = await handle.getFile();
            const entry = {
                fileName: name,
                relativePath: path,
                sizeBytes: file?.size ?? 0,
            };
            if (isPointer && file)
                entry.text = await file.text();
            files.push(entry);
        } catch (err) {
            console.debug("Cut: skip unreadable file", path, err);
        }
    }

    async function walkDirAsync(dir, rel, depth, files) {
        if (depth > 8) return;
        for await (const [name, handle] of dir.entries()) {
            if (!name || name.startsWith(".")) continue;
            const path = rel ? (rel + "/" + name) : name;
            if (handle.kind === "directory") {
                await walkDirAsync(handle, path, depth + 1, files);
                continue;
            }
            await collectMediaFile(handle, name, path, files);
        }
    }

    function h264EncodeArgs(audio) {
        const args = [
            "-c:v", "libx264", "-preset", CUT_H264_PRESET, "-crf", CUT_H264_CRF,
            "-pix_fmt", CUT_H264_PIX, "-profile:v", CUT_H264_PROFILE,
        ];
        if (audio === "aac")
            args.push("-c:a", "aac", "-b:a", CUT_AAC_RATE);
        else if (audio === "an")
            args.push("-an");
        args.push("-movflags", "+faststart");
        return args;
    }

    function audioRemuxArgs() {
        return [
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", CUT_AAC_RATE,
            "-movflags", "+faststart",
        ];
    }

    function clampTrimWindow(startSec, endSec, total) {
        let start = Number(startSec) || 0;
        let end = Number(endSec);
        if (!Number.isFinite(end) || end <= 0)
            end = total > 0 ? total : 0;
        if (start < 0) start = 0;
        if (total > 0 && start > total) start = total;
        if (total > 0 && end > total) end = total;
        if (total > 0 && end - start < 0.1)
            start = Math.max(0, end - 0.1);
        if (end <= start) end = start + 0.1;
        return { start: start, keep: Math.max(0.1, end - start) };
    }

    function buildTrimArgs(inName, outName, start, keep, silentAudio) {
        const args = ["-hide_banner", "-y"];
        args.push("-i", inName);
        if (silentAudio)
            args.push("-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
        // Accurate output seek keeps a decoded video frame in short transition tails.
        // It must follow every input or FFmpeg applies it to the next input instead.
        if (start > 0.001) args.push("-ss", String(start));
        args.push("-t", String(keep),
            // Every prepared clip must begin at PTS zero. Otherwise the concat
            // demuxer preserves each source's trim offset as a gap: seeking
            // lands on black frames and late scenes (notably credits) vanish.
            "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p,setpts=PTS-STARTPTS",
            "-af", "asetpts=PTS-STARTPTS");
        args.push.apply(args, h264EncodeArgs("aac"));
        if (silentAudio)
            args.push("-map", "0:v:0", "-map", "1:a:0", "-shortest");
        else
            // Requiring native audio makes a silent source fail this pass and
            // enter the padded-silence retry, keeping concat stream layouts equal.
            args.push("-map", "0:v:0", "-map", "0:a:0");
        args.push(outName);
        return args;
    }

    function xfadeName(kind) {
        const k = String(kind || "cut").toLowerCase();
        if (k === "dissolve") return "fade";
        if (k === "fadewhite") return "fadewhite";
        if (k === "dip" || k === "fadein" || k === "fadeout") return "fadeblack";
        return "";
    }

    function cssFontOf(raw) {
        if (raw && raw.cssFont)
            return String(raw.cssFont);
        const name = String((raw && (raw.font || raw.fontFamily)) || "").toLowerCase();
        if (name === "arial") return "Arial, Helvetica, sans-serif";
        if (name === "georgia") return "Georgia, 'Times New Roman', serif";
        if (name === "impact") return "Impact, Haettenschweiler, sans-serif";
        if (name === "courier" || name === "courier new") return "'Courier New', Courier, monospace";
        return "sans-serif";
    }

    function textAlignOf(raw) {
        const a = String((raw && raw.align) || "").toLowerCase();
        if (a === "left" || a === "right")
            return a;
        return "center";
    }

    function textStyle(raw) {
        const fontPx = Number(raw && raw.fontPx) > 0 ? Number(raw.fontPx) : 48;
        const y = Number(raw && raw.y) > 0 ? Number(raw.y) : 360;
        const align = textAlignOf(raw);
        const xRaw = Number(raw && raw.x);
        const x = Number.isFinite(xRaw) && xRaw > 0
            ? xRaw
            : (align === "left" ? 96 : align === "right" ? 1184 : 640);
        const fadeSec = Number(raw && raw.fadeSec);
        return {
            fontPx: fontPx,
            color: (raw && raw.color) || "#ffffff",
            y: y,
            x: x,
            align: align,
            cssFont: cssFontOf(raw),
            bar: !!(raw && raw.bar),
            fadeSec: Number.isFinite(fadeSec) && fadeSec > 0 ? fadeSec : 0,
        };
    }

    function drawStyledText(ctx, text, style, plate) {
        if (plate === "black") {
            ctx.fillStyle = "#000000";
            ctx.fillRect(0, 0, 1280, 720);
        } else {
            ctx.clearRect(0, 0, 1280, 720);
        }
        const label = String(text || "").trim();
        if (!label)
            return;
        ctx.font = style.fontPx + "px " + (style.cssFont || "sans-serif");
        ctx.textAlign = style.align || "center";
        ctx.textBaseline = "middle";
        const x = Number(style.x) > 0 ? Number(style.x) : 640;
        if (style.bar) {
            const width = Math.min(1100, Math.max(220, ctx.measureText(label).width + 64));
            const h = Math.round(style.fontPx * 1.35);
            let barX = x - width / 2;
            if (style.align === "left")
                barX = x;
            else if (style.align === "right")
                barX = x - width;
            ctx.fillStyle = "rgba(0, 0, 0, 0.55)";
            ctx.fillRect(barX, style.y - h / 2, width, h);
        }
        ctx.fillStyle = style.color;
        ctx.fillText(label, x, style.y, 1100);
    }

    function cardPngUrl(text, style) {
        const canvas = document.createElement("canvas");
        canvas.width = 1280;
        canvas.height = 720;
        const ctx = canvas.getContext("2d");
        drawStyledText(ctx, text, textStyle(style), "black");
        return canvas.toDataURL("image/png");
    }

    function blackPngUrl() {
        return cardPngUrl("", null);
    }

    function overlayPngUrl(text, style) {
        const canvas = document.createElement("canvas");
        canvas.width = 1280;
        canvas.height = 720;
        const ctx = canvas.getContext("2d");
        drawStyledText(ctx, text || "Title", textStyle(style), "clear");
        return canvas.toDataURL("image/png");
    }

    async function overlayTextsAsync(videoUrl, texts, onProgress, apiOverride) {
        const list = (texts || []).filter(function (t) { return t && String(t.text || "").trim(); });
        if (list.length === 0)
            return { success: true, url: videoUrl };
        const api = apiOverride || window.PageToMovieFfmpeg;
        if (!api) return { success: false, error: "ffmpeg helper missing" };
        if (!videoUrl) return { success: false, error: "No URL" };
        return api._runExclusiveAsync(async function () {
            const load = await api.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error };
            const ffmpeg = api._ffmpeg;
            const seq = ++cut._trimSeq;
            const inName = "cut_ov_in_" + seq + ".mp4";
            const outName = "cut_ov_out_" + seq + ".mp4";
            const pngs = [];
            try {
                const data = await withPinnedUrls([videoUrl], function () { return fetchInputBytes(api, videoUrl, "Picture"); });
                await writeMemfs(ffmpeg, inName, data);
                let graph = "[0:v]scale=1280:720,setsar=1[v0]";
                let last = "v0";
                const args = ["-hide_banner", "-y", "-i", inName];
                for (let i = 0; i < list.length; i++) {
                    const pngName = "cut_ov_" + seq + "_" + i + ".png";
                    const look = textStyle(list[i].style);
                    pngs.push(pngName);
                    await writeMemfs(ffmpeg, pngName, await fetchInputBytes(api, overlayPngUrl(list[i].text, look), "Title"));
                    const start = Math.max(0, Number(list[i].start) || 0);
                    const hold = Math.max(0.3, Number(list[i].seconds) || 2);
                    const end = start + hold;
                    const fade = Math.min(look.fadeSec, hold / 3);
                    const next = "v" + (i + 1);
                    const src = String(i + 1);
                    if (fade > 0.05) {
                        args.push("-itsoffset", String(start), "-loop", "1", "-t", String(hold), "-i", pngName);
                        graph += ";[" + src + ":v]format=rgba,fade=t=in:st=0:d=" + fade
                            + ":alpha=1,fade=t=out:st=" + (hold - fade) + ":d=" + fade + ":alpha=1[ov" + i + "]";
                        graph += ";[" + last + "][ov" + i + "]overlay=0:0:eof_action=pass[" + next + "]";
                    } else {
                        args.push("-i", pngName);
                        graph += ";[" + last + "][" + src + ":v]overlay=0:0:enable='gte(t," + start + ")*lte(t," + end + ")'[" + next + "]";
                    }
                    last = next;
                }
                args.push("-filter_complex", graph, "-map", "[" + last + "]");
                try {
                    await execChecked(ffmpeg, args.concat(["-map", "0:a"], h264EncodeArgs("aac"), [outName]));
                } catch (audioErr) {
                    console.debug("Cut: overlay native audio missing", audioErr);
                    await execChecked(ffmpeg, args.concat(h264EncodeArgs("an"), [outName]));
                }
                const out = await ffmpeg.readFile(outName);
                const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
                noteTemp(url);
                return { success: true, url: url };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
                await deleteMemfs(ffmpeg, inName);
                await deleteMemfs(ffmpeg, outName);
                for (const name of pngs)
                    await deleteMemfs(ffmpeg, name);
            }
        });
    }

    async function stillVideoAsync(pngUrl, seconds, onProgress, fadeSec, apiOverride) {
        const api = apiOverride || window.PageToMovieFfmpeg;
        if (!api) return { success: false, error: "ffmpeg helper missing" };
        const hold = Math.max(0.3, Number(seconds) || 2);
        return api._runExclusiveAsync(async function () {
            const load = await api.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error };
            const ffmpeg = api._ffmpeg;
            const seq = ++cut._trimSeq;
            const inName = "cut_card_" + seq + ".png";
            const outName = "cut_card_" + seq + ".mp4";
            try {
                const data = await fetchInputBytes(api, pngUrl, "Card");
                await writeMemfs(ffmpeg, inName, data);
                const fade = Math.min(Math.max(0, Number(fadeSec) || 0), hold / 3);
                let vf = "scale=1280:720,setsar=1";
                if (fade > 0.05)
                    vf += ",fade=t=in:st=0:d=" + fade + ",fade=t=out:st=" + (hold - fade) + ":d=" + fade;
                vf += ",format=yuv420p";
                await execChecked(ffmpeg, [
                    "-hide_banner", "-y", "-loop", "1", "-i", inName, "-t", String(hold),
                    "-vf", vf,
                ].concat(h264EncodeArgs("an"), [outName]));
                const out = await ffmpeg.readFile(outName);
                const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
                noteTemp(url);
                return { success: true, url: url };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
                await deleteMemfs(ffmpeg, inName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    }

    /**
     * Visual dissolve/dip. Must keep native clip audio.
     * Scene-change default is Dissolve. Mapping video only with `-an` strips VO
     * from the accumulator; later concat cannot bring it back.
     */
    async function xfadeAsync(leftUrl, rightUrl, kind, onProgress, fadeSec, apiOverride) {
        const api = apiOverride || window.PageToMovieFfmpeg;
        const trans = xfadeName(kind);
        if (!api || !trans) return { success: false, error: "no xfade" };
        return api._runExclusiveAsync(async function () {
            const load = await api.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error };
            const ffmpeg = api._ffmpeg;
            const seq = ++cut._trimSeq;
            const aName = "cut_xf_a_" + seq + ".mp4";
            const bName = "cut_xf_b_" + seq + ".mp4";
            const outName = "cut_xf_o_" + seq + ".mp4";
            const logTail = [];
            const logHandler = function (event) {
                const message = event && event.message ? String(event.message) : "";
                if (!message) return;
                logTail.push(message);
                if (logTail.length > 20) logTail.shift();
            };
            if (typeof ffmpeg.on === "function")
                ffmpeg.on("log", logHandler);
            try {
                const pair = await withPinnedUrls([leftUrl, rightUrl], async function () {
                    return [
                        await fetchInputBytes(api, leftUrl, "Clip"),
                        await fetchInputBytes(api, rightUrl, "Clip"),
                    ];
                });
                await writeMemfs(ffmpeg, aName, pair[0]);
                await writeMemfs(ffmpeg, bName, pair[1]);
                const probe = await api._probeDurationMemfsAsync(aName);
                const rightProbe = await api._probeDurationMemfsAsync(bName);
                const leftSec = probe.success && probe.seconds > 0 ? probe.seconds : 1;
                const rightSec = rightProbe.success && rightProbe.seconds > 0 ? rightProbe.seconds : 1;
                const fade = Number(fadeSec) > 0.05
                    ? Number(fadeSec)
                    : Math.min(CUT_XFADE_SEC, Math.max(CUT_XFADE_MIN_SEC, leftSec / 4));
                const offset = Math.max(0, leftSec - fade);
                const outputSec = Math.max(0.1, offset + rightSec);
                const vgraph = "[0:v]scale=1280:720,setsar=1,fps=30,settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p[v0];"
                    + "[1:v]scale=1280:720,setsar=1,fps=30,settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p[v1];"
                    + "[v0][v1]xfade=transition=" + trans + ":duration=" + fade + ":offset=" + offset + ",format=yuv420p[v]";
                const aNorm = "[0:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a0];"
                    + "[1:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a1];";
                const graphs = [
                    vgraph + ";" + aNorm + "[a0][a1]acrossfade=d=" + fade + ":c1=tri:c2=tri[a]",
                    vgraph + ";[0:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad[a]",
                    vgraph + ";[1:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad[a]",
                ];
                let encoded = false;
                for (const graph of graphs) {
                    try {
                        await execChecked(ffmpeg, [
                            "-hide_banner", "-y", "-i", aName, "-i", bName,
                            "-filter_complex", graph,
                            "-map", "[v]", "-map", "[a]",
                        ].concat(h264EncodeArgs("aac"), ["-t", String(outputSec), outName]));
                        encoded = true;
                        break;
                    } catch (audioErr) {
                        console.debug("Cut: xfade audio pass failed", audioErr);
                        try { await ffmpeg.deleteFile(outName); } catch (delErr) {
                            console.debug("Cut: xfade out cleanup", delErr);
                        }
                    }
                }
                if (!encoded) {
                    try {
                        await execChecked(ffmpeg, [
                            "-hide_banner", "-y", "-i", aName, "-i", bName,
                            "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
                            "-filter_complex", vgraph,
                            "-map", "[v]", "-map", "2:a",
                        ].concat(h264EncodeArgs("aac"), ["-t", String(outputSec), outName]));
                        encoded = true;
                    } catch (silentErr) {
                        console.debug("Cut: xfade silence pass failed", silentErr);
                    }
                }
                if (!encoded) {
                    cut._lastDebugError = {
                        kind: kind, leftSec: leftSec, rightSec: rightSec,
                        fade: fade, offset: offset, logTail: logTail,
                    };
                    console.error("Cut: xfade failed", JSON.stringify(cut._lastDebugError));
                    const detail = cut._debugFolder && logTail.length > 0
                        ? " Debug: " + logTail.slice(-8).join(" | ")
                        : "";
                    return { success: false, error: "Transition could not be rendered." + detail };
                }
                const out = await ffmpeg.readFile(outName);
                const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
                noteTemp(url);
                return { success: true, url: url };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
                if (typeof ffmpeg.off === "function")
                    ffmpeg.off("log", logHandler);
                await deleteMemfs(ffmpeg, aName);
                await deleteMemfs(ffmpeg, bName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    }

    async function joinPairAsync(api, leftUrl, rightUrl, kind, onProgress, holdSec) {
        const k = String(kind || "cut").toLowerCase();
        if (k === "cuttoblack") {
            const hold = Math.max(0.3, Number(holdSec) || CUT_TO_BLACK_HOLD_SEC);
            const black = await stillVideoAsync(blackPngUrl(), hold, onProgress, 0, api);
            if (!black.success) return concatPinned(api, [leftUrl, rightUrl], onProgress);
            let mid = null;
            try {
                mid = await concatPinned(api, [leftUrl, black.url], onProgress);
                if (!mid.success) return mid;
                return await concatPinned(api, [mid.url, rightUrl], onProgress);
            } finally {
                releaseTempUrl(black.url);
                if (mid && mid.success)
                    releaseTempUrl(mid.url);
            }
        }
        if (xfadeName(k)) {
            const faded = await xfadeAsync(leftUrl, rightUrl, k, onProgress, 0, api);
            if (faded.success) return faded;
        }
        return concatPinned(api, [leftUrl, rightUrl], onProgress);
    }

    function clipHoldSeconds(c) {
        const inn = Number(c && c.markIn) || 0;
        const outt = Number(c && c.markOut) || 0;
        if (outt > inn)
            return outt - inn;
        const duration = Number(c && c.duration) || 0;
        return Math.max(0.3, duration || 2);
    }

    async function holdClipStillAsync(c, onProgress, api) {
        const look = c.card && c.card.text ? textStyle(c.card.style) : null;
        const png = look ? cardPngUrl(c.card.text, look) : blackPngUrl();
        const still = await stillVideoAsync(png, clipHoldSeconds(c), onProgress, look && look.fadeSec, api);
        if (!still.success)
            return { error: still.error || "Hold failed." };
        return { url: still.url, source: "" };
    }

    async function prepareWindowsAsync(c, index, total, onProgress, api) {
        const label = c.label || c.fileName || ("clip " + (index + 1));
        if (c.hold || !c.url) {
            onProgress?.(Math.round((index / Math.max(total, 1)) * 40), "Preparing " + label + "…");
            return holdClipStillAsync(c, onProgress, api);
        }
        onProgress?.(Math.round((index / Math.max(total, 1)) * 40), "Preparing " + label + "…");
        const windows = Array.isArray(c.windows) && c.windows.length > 0
            ? c.windows
            : [{ start: Number(c.markIn) || 0, end: Number(c.markOut) || 0 }];
        const urls = [];
        for (const w of windows) {
            const start = Number(w.start) || 0;
            const end = Number(w.end) || 0;
            // Always normalize dimensions and audio layout. Passing an
            // untouched silent credits MP4 into an audio-bearing concat can
            // make the final scene disappear even when no trim is requested.
            const trimmed = await trimRangeWithApiAsync(api, c.url, start, end, onProgress);
            if (!trimmed.success)
                return { error: label + ": " + (trimmed.error || "trim failed") };
            urls.push(trimmed.url);
        }
        if (urls.length === 1)
            return { url: urls[0], source: c.url };
        let cat = null;
        try {
            cat = await concatPinned(api, urls, onProgress);
            if (!cat.success)
                return { error: label + ": " + (cat.error || "range join failed") };
            return { url: cat.url, source: c.url };
        } finally {
            urls.forEach(function (url) {
                if (!cat || !cat.success || url !== cat.url)
                    releaseTempUrl(url);
            });
        }
    }

    async function deleteMemfs(ffmpeg, name) {
        try {
            await ffmpeg.deleteFile(name);
        } catch (err) {
            console.debug("Cut: memfs cleanup", name, err);
        }
    }

    function musicSpec(audio) {
        if (!audio)
            return null;
        if (typeof audio === "string")
            return { url: audio, start: 0, markIn: 0, markOut: 0 };
        const url = audio.url || "";
        if (!url)
            return null;
        const volume = Number(audio.volume);
        return {
            url: url,
            start: Math.max(0, Number(audio.start) || 0),
            markIn: Math.max(0, Number(audio.markIn) || 0),
            markOut: Math.max(0, Number(audio.markOut) || 0),
            volume: Number.isFinite(volume) ? Math.max(0, Math.min(1, volume)) : 1,
            fadeIn: Math.max(0, Number(audio.fadeIn) || 0),
            fadeOut: Math.max(0, Number(audio.fadeOut) || 0),
            playbackRate: Math.max(0.5, Math.min(2, Number(audio.playbackRate) || 1)),
            noiseSuppression: audio.noiseSuppression === true,
            introBlack: Math.max(0, Math.min(60, Number(audio.introBlack) || 0)),
            prepareFilter: audio.prepareFilter || "",
            filter: audio.filter || "",
            fallbackFilter: audio.fallbackFilter || "",
        };
    }

    async function placeMusicAsync(api, spec, onProgress) {
        const start = spec.start;
        const inn = spec.markIn;
        const outt = spec.markOut;
        const needsPlace = start > 0.02 || inn > 0.02 || outt > inn + 0.02 || !!spec.prepareFilter;
        if (!needsPlace)
            return { success: true, url: spec.url };
        return api._runExclusiveAsync(async function () {
            const load = await api.ensureLoadedAsync(onProgress);
            if (!load.success)
                return { success: false, error: load.error };
            const ffmpeg = api._ffmpeg;
            const seq = ++cut._trimSeq;
            const inName = "cut_mus_in_" + seq + ".bin";
            const outName = "cut_mus_out_" + seq + ".m4a";
            try {
                const data = await withPinnedUrls([spec.url], function () { return fetchInputBytes(api, spec.url, "Soundtrack"); });
                await writeMemfs(ffmpeg, inName, data);
                const args = ["-hide_banner", "-y"];
                if (inn > 0.02)
                    args.push("-ss", String(inn));
                args.push("-i", inName);
                const audioFilters = [];
                if (outt > inn + 0.02) {
                    // Trim the source before adding timeline silence. Using
                    // output -t here cuts a late-start track off inside its
                    // leading delay, before any music can be heard.
                    audioFilters.push("atrim=duration=" + String(Math.max(0.3, outt - inn)));
                    audioFilters.push("asetpts=PTS-STARTPTS");
                }
                if (spec.prepareFilter)
                    audioFilters.push(spec.prepareFilter);
                const delayMs = Math.round(start * 1000);
                if (delayMs > 0)
                    audioFilters.push("adelay=" + delayMs + ":all=1");
                if (audioFilters.length > 0)
                    args.push("-af", audioFilters.join(","));
                args.push("-c:a", "aac", "-b:a", "192k", outName);
                await execChecked(ffmpeg, args);
                const out = await ffmpeg.readFile(outName);
                const url = URL.createObjectURL(new Blob([out.buffer], { type: "audio/mp4" }));
                noteTemp(url);
                return { success: true, url: url };
            } catch (err) {
                return { success: false, error: messageOf(err, "Could not place music.") };
            } finally {
                await deleteMemfs(ffmpeg, inName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    }

    function mixFiltersOf(spec) {
        const sourceHold = spec && spec.markOut > spec.markIn ? spec.markOut - spec.markIn : 0;
        const rate = spec && spec.playbackRate > 0 ? spec.playbackRate : 1;
        const hold = sourceHold / rate;
        const volume = spec && Number.isFinite(spec.volume) ? spec.volume : 1;
        const fadeIn = spec && spec.fadeIn > 0 ? spec.fadeIn : 0;
        const fadeOut = spec && spec.fadeOut > 0 ? spec.fadeOut : 0;
        const start = spec && spec.start > 0 ? spec.start : 0;
        const introBlack = spec && spec.introBlack > 0 ? spec.introBlack : 0;
        let chain = "volume=" + (Math.round(volume * 100) / 100);
        if (fadeIn > 0.001)
            chain += ",afade=t=in:st=" + start + ":d=" + fadeIn;
        if (fadeOut > 0.001) {
            const outAt = hold > 0.05 ? Math.max(start, start + hold - fadeOut) : start;
            chain += ",afade=t=out:st=" + outAt + ":d=" + fadeOut;
        }
        return {
            withVo: (spec && spec.filter) || ("[1:a]" + chain + ",apad[bg];"
                + (introBlack > 0
                    ? "[0:a]adelay=" + String(Math.round(introBlack * 1000)) + ":all=1[vo];[vo]"
                    : "[0:a]")
                + "[bg]amix=inputs=2:duration=longest:dropout_transition=0[a]"),
            musicOnly: (spec && spec.fallbackFilter) || ("[1:a]" + chain + ",apad[a]"),
        };
    }

    async function mixMovieAudioOnce(api, ffmpeg, videoUrl, musicUrl, spec, seq) {
        const inVideo = "cut_mix_v_" + seq + ".mp4";
        const inMusic = "cut_mix_m_" + seq + ".m4a";
        const outName = "cut_mix_o_" + seq + ".mp4";
        const filters = mixFiltersOf(spec);
        try {
            await writeMemfs(ffmpeg, inVideo, await fetchInputBytes(api, videoUrl, "Picture"));
            await writeMemfs(ffmpeg, inMusic, await fetchInputBytes(api, musicUrl, "Soundtrack"));
            const probe = await api._probeDurationMemfsAsync(inVideo);
            const durationSec = probe.success && probe.seconds > 0 ? probe.seconds : 0;
            const musicProbe = await api._probeDurationMemfsAsync(inMusic);
            const musicSec = musicProbe.success && musicProbe.seconds > 0 ? musicProbe.seconds : 0;
            const introBlack = spec && spec.introBlack > 0 ? spec.introBlack : 0;
            const pictureEndSec = durationSec + introBlack;
            const outputSec = Math.max(pictureEndSec, musicSec);
            const freezeSec = durationSec > 0.05 ? Math.max(0, musicSec - pictureEndSec) : 0;
            const extendPicture = introBlack > 0.05 || freezeSec > 0.05;
            const videoFilter = extendPicture
                ? ";[0:v]tpad=start_mode=add:start_duration=" + String(introBlack)
                    + ":color=black:stop_mode=clone:stop_duration=" + String(freezeSec)
                    + ",format=yuv420p[v]"
                : "";
            const args = [
                "-hide_banner", "-y", "-i", inVideo, "-i", inMusic,
                "-filter_complex", filters.withVo + videoFilter,
                "-map", extendPicture ? "[v]" : "0:v", "-map", "[a]",
            ];
            if (outputSec > 0.05)
                args.push("-t", String(outputSec));
            args.push.apply(args, extendPicture ? h264EncodeArgs("aac") : audioRemuxArgs());
            args.push(outName);
            try {
                await execChecked(ffmpeg, args);
            } catch (noVidAudio) {
                console.debug("Cut: mix video has no audio", noVidAudio);
                try { await ffmpeg.deleteFile(outName); } catch (delErr) {
                    console.debug("Cut: mix out cleanup", delErr);
                }
                const fallback = [
                    "-hide_banner", "-y", "-i", inVideo, "-i", inMusic,
                    "-filter_complex", filters.musicOnly + videoFilter,
                    "-map", extendPicture ? "[v]" : "0:v", "-map", "[a]",
                ];
                if (outputSec > 0.05)
                    fallback.push("-t", String(outputSec));
                fallback.push.apply(fallback, extendPicture ? h264EncodeArgs("aac") : audioRemuxArgs());
                fallback.push(outName);
                await execChecked(ffmpeg, fallback);
            }
            const out = await ffmpeg.readFile(outName);
            const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
            noteTemp(url);
            return { success: true, url: url };
        } finally {
            await deleteMemfs(ffmpeg, inVideo);
            await deleteMemfs(ffmpeg, inMusic);
            await deleteMemfs(ffmpeg, outName);
        }
    }

    async function mixMovieAudioAsync(api, videoUrl, musicUrl, onProgress, spec) {
        return api._runExclusiveAsync(async function () {
            let load = await api.ensureLoadedAsync(onProgress);
            if (!load.success)
                return { success: false, error: load.error };
            const seq = ++cut._trimSeq;
            try {
                return await mixMovieAudioOnce(api, api._ffmpeg, videoUrl, musicUrl, spec, seq);
            } catch (err) {
                if (!isFsError(err))
                    return { success: false, error: messageOf(err, "Could not mix audio.") };
                await resetFfmpegWorker(api);
                load = await api.ensureLoadedAsync(onProgress);
                if (!load.success)
                    return { success: false, error: load.error || fsUserMessage() };
                try {
                    return await mixMovieAudioOnce(api, api._ffmpeg, videoUrl, musicUrl, spec, seq);
                } catch (retry) {
                    return { success: false, error: messageOf(retry, fsUserMessage()) };
                }
            }
        });
    }

    async function mixOptionalAudio(api, videoUrl, audio, onProgress, metrics) {
        const spec = musicSpec(audio);
        if (!spec)
            return { success: true, url: videoUrl };
        const prepareStarted = performance.now();
        const placed = await placeMusicAsync(api, spec, onProgress);
        if (metrics) metrics.musicPrepareMs = Math.round(performance.now() - prepareStarted);
        if (!placed.success)
            return placed;
        try {
            return await withPinnedUrls([videoUrl, placed.url], async function () {
                onProgress?.(80, "Mixing audio…");
                const mixStarted = performance.now();
                try {
                    return await mixMovieAudioAsync(api, videoUrl, placed.url, onProgress, spec);
                } finally {
                    if (metrics) metrics.mixMs = Math.round(performance.now() - mixStarted);
                }
            });
        } finally {
            if (placed.url !== spec.url)
                releaseTempUrl(placed.url);
        }
    }

    cut.supportsDirectoryPicker = function () {
        return { supported: typeof window.showDirectoryPicker === "function" };
    };

    cut.pickFolderAsync = async function () {
        if (typeof window.showDirectoryPicker !== "function") {
            return { success: false, error: "This browser cannot pick a folder. Use Chrome or Edge, or choose MP4 files instead." };
        }
        try {
            cut._root = await window.showDirectoryPicker({ mode: "readwrite" });
            cut._fallbackFiles = null;
            cut._debugFolder = null;
            return { success: true, folderName: cut._root.name };
        } catch (err) {
            if (err?.name === "AbortError")
                return { success: false, error: "Folder selection cancelled." };
            return { success: false, error: messageOf(err, "Folder selection failed.") };
        }
    };

    cut.clickFileInput = function (host) {
        const input = host && host.querySelector ? host.querySelector("input[type=\"file\"]") : null;
        if (input)
            input.click();
    };

    cut.pickMp4FilesAsync = function () {
        return new Promise(function (resolve) {
            const input = document.createElement("input");
            input.type = "file";
            input.accept = "video/mp4,.mp4";
            input.multiple = true;
            input.addEventListener("change", function () {
                const files = Array.from(input.files || []);
                if (files.length === 0) {
                    resolve({ success: false, error: "No files selected." });
                    return;
                }
                cut._root = null;
                cut._fallbackFiles = files;
                cut._debugFolder = null;
                resolve({
                    success: true,
                    folderName: "Selected files",
                    files: files.map(function (f) {
                        return { fileName: f.name, relativePath: f.name, sizeBytes: f.size };
                    }),
                });
            });
            input.click();
        });
    };

    cut.listMediaFilesAsync = async function () {
        if (cut._debugFolder)
            return { success: true, files: cut._debugFolder.files };
        if (cut._fallbackFiles) {
            return {
                success: true,
                files: cut._fallbackFiles.map(function (f) {
                    return { fileName: f.name, relativePath: f.name, sizeBytes: f.size };
                }),
            };
        }
        if (!cut._root)
            return { success: false, error: "No folder connected.", files: [] };
        try {
            const files = [];
            await walkDirAsync(cut._root, "", 0, files);
            return { success: true, files: files };
        } catch (err) {
            return { success: false, error: messageOf(err, "Could not read the folder."), files: [] };
        }
    };

    cut._resolveFileAsync = async function (relativePath) {
        if (cut._debugFolder) {
            const hit = cut._debugFolder.files.find(function (f) { return f.relativePath === relativePath; });
            if (!hit) throw new Error("Clip is missing: " + relativePath);
            const response = await fetch(debugFileUrl(cut._debugFolder.baseUrl, relativePath));
            if (!response.ok) throw new Error("Clip is missing: " + relativePath);
            return await response.blob();
        }
        if (cut._fallbackFiles) {
            const hit = cut._fallbackFiles.find(function (f) { return f.name === relativePath; });
            if (!hit) throw new Error("Clip is missing: " + relativePath);
            return hit;
        }
        if (!cut._root) throw new Error("No folder connected.");
        const fh = await fileHandleAt(cut._root, relativePath, false);
        return await fh.getFile();
    };

    function debugFileUrl(baseUrl, relativePath) {
        const encoded = String(relativePath || "").split("/").map(encodeURIComponent).join("/");
        return new URL(encoded, baseUrl).href;
    }

    cut.loadDebugFolderAsync = async function (manifestUrl) {
        try {
            const response = await fetch(manifestUrl, { cache: "no-store" });
            if (!response.ok)
                throw new Error("Debug folder manifest returned " + response.status + ".");
            const manifest = await response.json();
            const baseUrl = new URL(manifest.baseUrl || "./", window.location.href).href;
            const rows = Array.isArray(manifest.files) ? manifest.files : [];
            const files = await Promise.all(rows.map(async function (row) {
                const relativePath = String(row.relativePath || "");
                const url = debugFileUrl(baseUrl, relativePath);
                if (row.missing === true) {
                    return {
                        fileName: relativePath.split("/").pop(),
                        relativePath: relativePath,
                        sizeBytes: Number(row.sizeBytes) || 1,
                    };
                }
                if (row.text === true) {
                    const textResponse = await fetch(url, { cache: "no-store" });
                    if (!textResponse.ok)
                        throw new Error("Debug file is missing: " + relativePath);
                    const text = await textResponse.text();
                    return { fileName: relativePath.split("/").pop(), relativePath: relativePath, sizeBytes: text.length, text: text };
                }
                const head = await fetch(url, { method: "HEAD", cache: "no-store" });
                if (!head.ok)
                    throw new Error("Debug file is missing: " + relativePath);
                return {
                    fileName: relativePath.split("/").pop(),
                    relativePath: relativePath,
                    sizeBytes: Number(head.headers.get("content-length")) || Number(row.sizeBytes) || 1,
                };
            }));
            cut._root = null;
            cut._fallbackFiles = null;
            cut._debugFolder = { baseUrl: baseUrl, files: files };
            return { success: true, folderName: manifest.folderName || "Debug folder", files: files };
        } catch (err) {
            cut._debugFolder = null;
            return { success: false, error: messageOf(err, "Could not load debug folder."), files: [] };
        }
    };

    cut.writeTextFileAsync = async function (relativePath, text) {
        if (cut._debugFolder)
            return { success: false, error: "Debug folders are read-only." };
        if (cut._fallbackFiles)
            return { success: false, error: "Folder write needs Pick folder (not loose files)." };
        if (!cut._root)
            return { success: false, error: "No folder connected." };
        try {
            const fh = await fileHandleAt(cut._root, relativePath, true);
            const w = await fh.createWritable();
            await w.write(String(text ?? ""));
            await w.close();
            return { success: true };
        } catch (err) {
            return { success: false, error: messageOf(err, "Could not save current take.") };
        }
    };

    cut.writeBlobUrlFileAsync = async function (relativePath, url) {
        if (cut._debugFolder)
            return { success: false, error: "Debug folders are read-only." };
        if (cut._fallbackFiles)
            return { success: false, error: "Folder write needs Pick folder (not loose files)." };
        if (!cut._root)
            return { success: false, error: "No folder connected." };
        if (!url)
            return { success: false, error: "No movie to save." };
        try {
            const data = await fetch(url).then(function (r) { return r.arrayBuffer(); });
            const fh = await fileHandleAt(cut._root, relativePath, true);
            const w = await fh.createWritable();
            await w.write(data);
            await w.close();
            return { success: true };
        } catch (err) {
            return { success: false, error: messageOf(err, "Could not save movie.mp4.") };
        }
    };

    cut.getFileBlobUrlAsync = async function (relativePath) {
        try {
            const file = await cut._resolveFileAsync(relativePath);
            if (!file || file.size <= 0)
                return { success: false, error: "Clip is missing or empty: " + relativePath };
            return { success: true, url: URL.createObjectURL(file), sizeBytes: file.size };
        } catch (err) {
            return { success: false, error: messageOf(err, "Clip is missing: " + relativePath) };
        }
    };

    cut.createBlobUrlFromStream = async function (streamRef, mime) {
        try {
            const buf = await streamRef.arrayBuffer();
            const blob = new Blob([buf], { type: mime || "application/octet-stream" });
            return { success: true, url: URL.createObjectURL(blob) };
        } catch (err) {
            return { success: false, error: messageOf(err, "Could not read the file.") };
        }
    };

    cut.revokeBlobUrl = function (url) {
        if (typeof url !== "string" || !url.startsWith("blob:"))
            return;
        if (cut._activeInputs.has(url)) {
            cut._pendingRevoke.add(url);
            return;
        }
        actuallyRevoke(url);
    };

    cut.abortCompose = async function () {
        cut._aborted = true;
        cut._progressRef = null;
        if (window.PageToMovieFfmpeg)
            window.PageToMovieFfmpeg._onProgress = null;
        await drainComposeAsync();
    };

    cut.prepareExportAsync = async function () {
        await drainComposeAsync();
        return { success: true };
    };

    cut.readMediaDuration = function (el) {
        const d = el?.duration;
        return (typeof d === "number" && Number.isFinite(d) && d > 0) ? d : 0;
    };

    function validateMediaUrl(url, requireVideo) {
        return new Promise(function (resolve) {
            if (!url) {
                resolve({ success: false, error: "Media URL is missing.", duration: 0, width: 0, height: 0 });
                return;
            }
            const media = document.createElement(requireVideo ? "video" : "audio");
            media.preload = "auto";
            media.muted = true;
            let settled = false;
            let metadataReady = false;
            let timer = 0;
            const finish = function (result) {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                try {
                    media.removeAttribute("src");
                    media.load();
                } catch (err) {
                    console.debug("Cut: media validation cleanup", err);
                }
                resolve(result);
            };
            const facts = function () {
                const duration = Number.isFinite(media.duration) && media.duration > 0 ? media.duration : 0;
                return {
                    duration: duration,
                    width: requireVideo ? Number(media.videoWidth) || 0 : 0,
                    height: requireVideo ? Number(media.videoHeight) || 0 : 0,
                };
            };
            media.onloadedmetadata = function () {
                metadataReady = true;
                const f = facts();
                if (f.duration <= 0) {
                    finish({ success: false, error: "Media has no positive duration.", ...f });
                    return;
                }
                if (requireVideo && (f.width <= 0 || f.height <= 0))
                    finish({ success: false, error: "The file has no video stream.", ...f });
            };
            // loadeddata means the browser decoded the first media frame. For a
            // video take this is stronger than trusting MP4 boxes or file size.
            media.onloadeddata = function () {
                const f = facts();
                if (!metadataReady || f.duration <= 0) return;
                if (requireVideo && (f.width <= 0 || f.height <= 0)) return;
                finish({ success: true, error: "", ...f });
            };
            media.onerror = function () {
                finish({ success: false, error: requireVideo
                    ? "The video stream could not be decoded."
                    : "The audio stream could not be decoded.", ...facts() });
            };
            timer = setTimeout(function () {
                finish({ success: false, error: requireVideo
                    ? "Timed out while decoding the first video frame."
                    : "Timed out while decoding the audio stream.", ...facts() });
            }, 10000);
            media.src = url;
        });
    }

    cut.validateVideoUrl = function (url) { return validateMediaUrl(url, true); };
    cut.validateAudioUrl = function (url) { return validateMediaUrl(url, false); };

    cut.probeUrlDuration = function (url) {
        return new Promise(function (resolve) {
            if (!url) {
                resolve(0);
                return;
            }
            const v = document.createElement("video");
            v.preload = "metadata";
            v.muted = true;
            const done = function (d) {
                try {
                    v.removeAttribute("src");
                    v.load();
                } catch (err) {
                    console.debug("Cut: probe cleanup", err);
                }
                resolve(d);
            };
            v.onloadedmetadata = function () {
                const d = v.duration;
                done((typeof d === "number" && Number.isFinite(d) && d > 0) ? d : 0);
            };
            v.onerror = function () { done(0); };
            setTimeout(function () { done(0); }, 8000);
            v.src = url;
        });
    };

    cut.seekMedia = function (el, seconds) {
        if (!el) return;
        const t = Number(seconds);
        if (!Number.isFinite(t) || t < 0) return;
        try {
            el.currentTime = t;
        } catch (err) {
            console.debug("Cut: seek", err);
        }
    };

    cut.bindTimeUpdate = function (el, dotNetRef) {
        return cut.bindPlayback(el, dotNetRef);
    };

    function playSurfaceList() {
        const s = cut._playSurfaces || {};
        return [s.clip, s.movie].filter(Boolean);
    }

    function unbindOne(el) {
        if (!el) return;
        if (el._cutTimeHandler)
            el.removeEventListener("timeupdate", el._cutTimeHandler);
        if (el._cutEndedHandler)
            el.removeEventListener("ended", el._cutEndedHandler);
        el._cutTimeHandler = null;
        el._cutEndedHandler = null;
    }

    cut.unbindPlayback = function (el) {
        const list = playSurfaceList();
        if (el && list.indexOf(el) < 0)
            list.push(el);
        for (let i = 0; i < list.length; i++)
            unbindOne(list[i]);
    };

    function formatPlayClock(seconds) {
        let s = Number(seconds);
        if (!Number.isFinite(s) || s < 0)
            s = 0;
        const m = Math.floor(s / 60);
        const rem = s - m * 60;
        return m + ":" + rem.toFixed(2).padStart(5, "0");
    }

    function hideTextOverlay() {
        const el = cut._textOverlayEl;
        if (el)
            el.classList.add("is-off");
    }

    cut.bindPlayClock = function (pxPerSec, totalSec) {
        const px = Number(pxPerSec);
        const total = Number(totalSec);
        if (Number.isFinite(px) && px > 0)
            cut._playClock.pxPerSec = px;
        if (Number.isFinite(total) && total >= 0)
            cut._playClock.totalSec = total;
    };

    cut.setPlayClockWindow = function (mode, timelineStart, localStart, localEnd) {
        cut._playClock.mode = mode || "idle";
        cut._playClock.timelineStart = Number(timelineStart) || 0;
        cut._playClock.localStart = Number(localStart) || 0;
        cut._playClock.localEnd = Number(localEnd) || 0;
    };

    cut.timelineFromMedia = function (localSec) {
        const c = cut._playClock;
        const t = Number(localSec) || 0;
        if (c.mode === "movie")
            return Math.max(0, t);
        return c.timelineStart + Math.max(0, t - c.localStart);
    };

    cut.holdPlayhead = function (timelineSec) {
        const t = Math.max(0, Number(timelineSec) || 0);
        cut._playClock.holdSec = t;
        cut._playClock.timelineSec = t;
        cut.paintPlayhead(t);
    };

    cut.readTimelineSec = function () {
        const front = cut._playSurfaces && cut._playSurfaces.front;
        if (front && !front.paused && typeof front.currentTime === "number") {
            const live = cut.timelineFromMedia(front.currentTime);
            if (Number.isFinite(live) && live >= 0)
                return live;
        }
        const held = cut._playClock.holdSec;
        if (typeof held === "number" && Number.isFinite(held) && held >= 0)
            return held;
        const last = cut._playClock.timelineSec;
        return Number.isFinite(last) && last >= 0 ? last : 0;
    };

    cut.paintPlayhead = function (timelineSec) {
        const t = Math.max(0, Number(timelineSec) || 0);
        cut._playClock.timelineSec = t;
        const needle = document.querySelector("[data-testid=\"cut-tl-playhead\"]");
        if (needle)
            needle.style.left = (t * cut._playClock.pxPerSec) + "px";
        const clock = document.querySelector(".cut-tl-clock");
        if (clock)
            clock.textContent = formatPlayClock(t) + " / " + formatPlayClock(cut._playClock.totalSec);
        cut.paintTextOverlay(t);
    };

    function showOnlyPlaySurface(el) {
        const s = cut._playSurfaces || {};
        const all = [s.clip, s.movie];
        for (let i = 0; i < all.length; i++) {
            const v = all[i];
            if (!v || !v.classList) continue;
            v.classList.toggle("is-off", v !== el);
        }
        s.front = el || null;
        cut.setLiveTextOverlay(!!el && el !== s.movie);
    }

    cut.setPreviewSurface = function (movieEl, clipEl, showMovie) {
        if (movieEl)
            cut._playSurfaces.movie = movieEl;
        if (clipEl)
            cut._playSurfaces.clip = clipEl;
        showOnlyPlaySurface(showMovie ? (movieEl || cut._playSurfaces.movie) : (clipEl || cut._playSurfaces.clip));
    };

    cut.bindPlaySurfaces = function (clipEl, movieEl) {
        const s = cut._playSurfaces;
        if (clipEl) s.clip = clipEl;
        if (movieEl) s.movie = movieEl;
    };

    cut.resetPlaySurfaces = function () {
        cut._playSwapSeq++;
        cut._playSurfaces.front = null;
    };

    cut.pausePlaySurfaces = function () {
        const list = playSurfaceList();
        for (let i = 0; i < list.length; i++)
            cut.pauseVideo(list[i]);
    };

    cut.setLiveTextOverlay = function (on) {
        cut._playClock.liveText = !!on;
        if (!on)
            hideTextOverlay();
    };

    cut.setTextCues = function (el, cues) {
        cut._textOverlayEl = el || null;
        cut._textCues = Array.isArray(cues) ? cues : [];
    };

    cut.paintTextOverlay = function (timelineSec) {
        const el = cut._textOverlayEl;
        if (!el)
            return;
        if (!cut._playClock.liveText) {
            el.classList.add("is-off");
            return;
        }
        const t = Number(timelineSec) || 0;
        const cues = cut._textCues || [];
        let cue = null;
        for (let i = cues.length - 1; i >= 0; i--) {
            const c = cues[i];
            const start = Number(c.startSec) || 0;
            const end = Number(c.endSec) || 0;
            if (t >= start && t < end) {
                cue = c;
                break;
            }
        }
        const line = el.querySelector(".cut-text-overlay-line");
        if (!cue || !line) {
            el.classList.add("is-off");
            return;
        }
        el.classList.remove("is-off");
        const start = Number(cue.startSec) || 0;
        const end = Number(cue.endSec) || 0;
        const fade = Number(cue.fadeSec) || 0;
        const hold = Math.max(0, end - start);
        let opacity = 1;
        if (fade > 0.05 && hold > 0) {
            const edge = Math.min(fade, hold / 3);
            if (t < start + edge)
                opacity = Math.max(0, (t - start) / edge);
            else if (t > end - edge)
                opacity = Math.max(0, (end - t) / edge);
        }
        const fontPx = Number(cue.fontPx) || 48;
        const y = Number(cue.y);
        const align = textAlignOf(cue);
        const stage = el.parentElement;
        const scale = stage && stage.clientWidth > 0 ? stage.clientWidth / 1280 : 1;
        line.textContent = String(cue.text || "");
        line.style.fontSize = Math.max(12, fontPx * scale) + "px";
        line.style.fontFamily = cssFontOf(cue);
        line.style.color = cue.colorHex || "#ffffff";
        line.style.top = ((Number.isFinite(y) ? y : 360) / 720 * 100) + "%";
        line.style.textAlign = align;
        if (align === "left") {
            line.style.left = "7%";
            line.style.right = "auto";
            line.style.transform = "translate(0, -50%)";
        } else if (align === "right") {
            line.style.left = "auto";
            line.style.right = "7%";
            line.style.transform = "translate(0, -50%)";
        } else {
            line.style.left = "50%";
            line.style.right = "auto";
            line.style.transform = "translate(-50%, -50%)";
        }
        line.style.opacity = String(opacity);
        line.classList.toggle("has-bar", !!cue.bar);
    };

    cut.readCurrentTime = function (el) {
        const front = cut._playSurfaces && cut._playSurfaces.front;
        const v = front || el;
        const t = v && typeof v.currentTime === "number" ? v.currentTime : 0;
        return Number.isFinite(t) && t > 0 ? t : 0;
    };

    function bindOnePlayback(el, dotNetRef) {
        if (!el || !dotNetRef) return;
        unbindOne(el);
        el._cutAdvanceSent = false;
        el._cutTimeHandler = function () {
            if (cut._playSurfaces.front && el !== cut._playSurfaces.front)
                return;
            if (el.paused)
                return;
            const local = el.currentTime || 0;
            const painted = cut.timelineFromMedia(local);
            const hold = cut._playClock.holdSec;
            if (typeof hold === "number" && Math.abs(painted - hold) > 0.2 && el.seeking)
                return;
            cut.paintPlayhead(painted);
            if (typeof hold === "number" && Math.abs(painted - hold) <= 0.2)
                cut._playClock.holdSec = null;
            if (cut._playClock.mode === "native"
                && cut._playClock.localEnd > 0
                && local >= cut._playClock.localEnd - 0.04) {
                if (!el._cutAdvanceSent) {
                    el._cutAdvanceSent = true;
                    invokeQuiet(dotNetRef, "OnEnded");
                }
            }
        };
        el._cutEndedHandler = function () {
            if (cut._playSurfaces.front && el !== cut._playSurfaces.front)
                return;
            invokeQuiet(dotNetRef, "OnEnded");
        };
        el.addEventListener("timeupdate", el._cutTimeHandler);
        el.addEventListener("ended", el._cutEndedHandler);
    }

    cut.bindPlayback = function (el, dotNetRef) {
        if (!dotNetRef) return { success: false };
        cut.unbindPlayback(el);
        const list = playSurfaceList();
        if (el && list.indexOf(el) < 0)
            list.push(el);
        for (let i = 0; i < list.length; i++)
            bindOnePlayback(list[i], dotNetRef);
        if (!cut._playSurfaces.front && el)
            cut._playSurfaces.front = el;
        return { success: true };
    };

    cut.elementRect = function (el) {
        if (!el || typeof el.getBoundingClientRect !== "function")
            return { x: 0, y: 0, width: 0, height: 0 };
        const r = el.getBoundingClientRect();
        return { x: r.left, y: r.top, width: r.width, height: r.height };
    };

    cut.setPointerCapture = function (el, pointerId) {
        if (!el || typeof el.setPointerCapture !== "function") return;
        try {
            el.setPointerCapture(pointerId);
        } catch (err) {
            console.debug("Cut: pointer capture", err);
        }
    };

    function waitSeeked(video, t) {
        return new Promise(function (resolve) {
            const finish = function () {
                video.removeEventListener("seeked", onSeeked);
                video.removeEventListener("error", onErr);
                clearTimeout(timer);
                resolve();
            };
            const onSeeked = function () { finish(); };
            const onErr = function () { finish(); };
            const timer = setTimeout(finish, 900);
            video.addEventListener("seeked", onSeeked);
            video.addEventListener("error", onErr);
            try {
                video.currentTime = t;
            } catch (err) {
                finish();
            }
        });
    }

    cut.captureFilmstrip = async function (url, startSec, endSec, count) {
        if (!url)
            return { success: false, frames: [] };
        const n = Math.max(1, Math.min(10, Number(count) || 4));
        const video = document.createElement("video");
        video.muted = true;
        video.preload = "auto";
        video.playsInline = true;
        const frames = [];
        try {
            await new Promise(function (resolve, reject) {
                video.onloadeddata = function () { resolve(); };
                video.onerror = function () { reject(new Error("filmstrip load")); };
                setTimeout(function () { reject(new Error("filmstrip timeout")); }, 8000);
                video.src = url;
            });
            const duration = (typeof video.duration === "number" && Number.isFinite(video.duration))
                ? video.duration
                : 0;
            let start = Number(startSec);
            let end = Number(endSec);
            if (!Number.isFinite(start) || start < 0) start = 0;
            if (!Number.isFinite(end) || end <= start)
                end = duration > start ? duration : start + 0.1;
            if (duration > 0) {
                if (start > duration) start = Math.max(0, duration - 0.05);
                if (end > duration) end = duration;
            }
            const canvas = document.createElement("canvas");
            canvas.width = 96;
            canvas.height = 54;
            const ctx = canvas.getContext("2d");
            const span = Math.max(0.05, end - start);
            for (let i = 0; i < n; i++) {
                const t = start + ((i + 0.5) / n) * span;
                await waitSeeked(video, t);
                if (ctx) {
                    ctx.fillStyle = "#111";
                    ctx.fillRect(0, 0, canvas.width, canvas.height);
                    try {
                        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
                    } catch (err) {
                        console.debug("Cut: filmstrip frame", err);
                    }
                    frames.push(canvas.toDataURL("image/jpeg", 0.62));
                }
            }
            return { success: frames.length > 0, frames: frames };
        } catch (err) {
            console.debug("Cut: filmstrip", err);
            return { success: false, frames: frames };
        } finally {
            try {
                video.removeAttribute("src");
                video.load();
            } catch (err) {
                console.debug("Cut: filmstrip cleanup", err);
            }
        }
    };

    cut.playVideo = function (el) {
        if (!el || typeof el.play !== "function") return;
        const playing = el.play();
        if (playing && typeof playing.catch === "function") {
            playing.catch(function (err) {
                console.debug("Cut: autoplay needs a click", err);
            });
        }
    };

    cut.pauseVideo = function (el) {
        if (!el || typeof el.pause !== "function") return;
        try {
            el.pause();
        } catch (err) {
            console.debug("Cut: pause", err);
        }
    };

    function playUrlOf(video) {
        if (!video) return "";
        return video.currentSrc || video.src || video.getAttribute("src") || "";
    }

    function incomingPlayEl(surface) {
        const s = cut._playSurfaces || {};
        if (surface === "movie")
            return s.movie;
        return s.clip;
    }

    function preparePlayAt(video, url, seconds, waitMs) {
        return new Promise(function (resolve) {
            if (!video || !url) {
                resolve(false);
                return;
            }
            const t = Number(seconds);
            const seek = Number.isFinite(t) && t >= 0 ? t : 0;
            const timeoutMs = Number(waitMs) > 0 ? Number(waitMs) : 1500;
            let settled = false;
            let timer = 0;
            const finish = function (ok) {
                if (settled) return;
                settled = true;
                video.removeEventListener("seeked", onSeeked);
                video.removeEventListener("loadedmetadata", onMeta);
                video.removeEventListener("loadeddata", onMeta);
                video.removeEventListener("error", onErr);
                clearTimeout(timer);
                resolve(!!ok);
            };
            const onErr = function () { finish(false); };
            const onSeeked = function () {
                if (typeof video.requestVideoFrameCallback === "function") {
                    video.requestVideoFrameCallback(function () { finish(true); });
                    return;
                }
                finish(video.readyState >= 2);
            };
            const onMeta = function () {
                try {
                    if (Math.abs((video.currentTime || 0) - seek) < 0.02 && video.readyState >= 2) {
                        finish(true);
                        return;
                    }
                    video.currentTime = seek;
                } catch (err) {
                    finish(false);
                }
            };
            timer = setTimeout(function () { finish(video.readyState >= 2); }, timeoutMs);
            video.addEventListener("seeked", onSeeked);
            video.addEventListener("error", onErr);
            video.muted = true;
            video.preload = "auto";
            const same = playUrlOf(video) === url;
            if (same && video.readyState >= 1) {
                onMeta();
                return;
            }
            video.addEventListener("loadedmetadata", onMeta);
            video.addEventListener("loadeddata", onMeta);
            video.src = url;
        });
    }

    function swapPlayTo(incoming, url, seconds, play) {
        if (!incoming || !url)
            return Promise.resolve({ success: false });
        const seq = play ? ++cut._playSwapSeq : cut._playSwapSeq;
        incoming._cutAdvanceSent = false;
        const maxTries = play ? 3 : 1;
        const waitMs = play ? 6000 : 1500;
        const attempt = function (triesLeft) {
            return preparePlayAt(incoming, url, seconds, waitMs).then(function (ok) {
                if (play && seq !== cut._playSwapSeq)
                    return { success: false };
                if (!play) {
                    if (incoming !== cut._playSurfaces.front)
                        cut.pauseVideo(incoming);
                    return { success: !!ok };
                }
                const hasFrame = ok || incoming.readyState >= 2;
                if (!hasFrame && cut._playSurfaces.front) {
                    if (triesLeft > 1)
                        return attempt(triesLeft - 1);
                    return { success: false };
                }
                const outgoing = cut._playSurfaces.front;
                if (outgoing && outgoing !== incoming)
                    cut.pauseVideo(outgoing);
                incoming.muted = false;
                cut.playVideo(incoming);
                showOnlyPlaySurface(incoming);
                return { success: true };
            });
        };
        return attempt(maxTries);
    }

    cut.primeUrlAt = function (url, seconds, surface) {
        const incoming = incomingPlayEl(surface || "native");
        if (!incoming || incoming === cut._playSurfaces.front)
            return;
        swapPlayTo(incoming, url, seconds, false);
    };

    cut.playUrlAt = function (el, url, seconds) {
        if (!url) return Promise.resolve({ success: false });
        const s = cut._playSurfaces || {};
        const surface = (el && el === s.movie) ? "movie" : "native";
        const incoming = incomingPlayEl(surface) || el;
        if (!incoming) return Promise.resolve({ success: false });
        const t = Number(seconds);
        const seek = Number.isFinite(t) && t >= 0 ? t : 0;
        if (incoming === s.front && playUrlOf(incoming) === url && incoming.readyState >= 1) {
            incoming._cutAdvanceSent = false;
            try {
                incoming.currentTime = seek;
            } catch (err) {
                console.debug("Cut: playUrlAt seek", err);
            }
            cut.playVideo(incoming);
            return Promise.resolve({ success: true });
        }
        return swapPlayTo(incoming, url, seconds, true);
    };

    cut.downloadUrlAs = function (url, fileName) {
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName || "movie.mp4";
        document.body.appendChild(a);
        a.click();
        a.remove();
        return { success: true };
    };

    /**
     * Trim [startSec, endSec) using the same encode args as PageToMovieFfmpeg.encodeSliceAsync /
     * _trimKeepSecondsAsync. Serialized on the shared ffmpeg queue.
     */
    async function trimRangeWithApiAsync(api, url, startSec, endSec, onProgress) {
        if (!api) return { success: false, error: "ffmpeg helper missing" };
        if (!url) return { success: false, error: "No URL" };
        return api._runExclusiveAsync(async function () {
            const load = await api.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error };
            const ffmpeg = api._ffmpeg;
            const seq = ++cut._trimSeq;
            const inName = "cut_in_" + seq + ".mp4";
            const outName = "cut_out_" + seq + ".mp4";
            const logTail = [];
            const logHandler = function (event) {
                const message = event && event.message ? String(event.message) : "";
                if (!message) return;
                logTail.push(message);
                if (logTail.length > 24) logTail.shift();
            };
            if (typeof ffmpeg.on === "function")
                ffmpeg.on("log", logHandler);
            try {
                onProgress?.(12, "Loading clip…");
                const data = await withPinnedUrls([url], function () { return fetchInputBytes(api, url, "Clip"); });
                await writeMemfs(ffmpeg, inName, data);
                onProgress?.(30, "Probing duration…");
                const probe = await api._probeDurationMemfsAsync(inName);
                const total = probe.success && probe.seconds > 0 ? probe.seconds : 0;
                const window = clampTrimWindow(startSec, endSec, total);
                onProgress?.(55, "Trimming…");
                try {
                    await execChecked(ffmpeg, buildTrimArgs(inName, outName, window.start, window.keep, false));
                } catch (audioErr) {
                    console.debug("Cut: trim native audio missing, pad silence", audioErr);
                    await execChecked(ffmpeg, buildTrimArgs(inName, outName, window.start, window.keep, true));
                }
                const out = await ffmpeg.readFile(outName);
                const blob = new Blob([out.buffer], { type: "video/mp4" });
                const urlOut = URL.createObjectURL(blob);
                noteTemp(urlOut);
                return { success: true, url: urlOut };
            } catch (err) {
                const detail = cut._debugFolder && logTail.length > 0
                    ? " Debug: " + logTail.slice(-10).join(" | ")
                    : "";
                return { success: false, error: messageOf(err, String(err)) + detail };
            } finally {
                if (typeof ffmpeg.off === "function")
                    ffmpeg.off("log", logHandler);
                await deleteMemfs(ffmpeg, inName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    }

    cut.trimRangeAsync = async function (url, startSec, endSec, onProgress) {
        return trimRangeWithApiAsync(window.PageToMovieFfmpeg, url, startSec, endSec, onProgress);
    };

    function normalizeComposePlan(raw, jit) {
        if (Array.isArray(raw))
            return { clips: raw, scenes: [], joins: [], jit: !!jit };
        const plan = raw || {};
        const forceFresh = queryFlag("ffmpegFresh");
        return {
            clips: plan.clips || [],
            scenes: (plan.scenes || []).map(function (scene) {
                return forceFresh ? Object.assign({}, scene, { url: "" }) : scene;
            }),
            joins: (plan.joins || []).map(function (join) {
                return forceFresh ? Object.assign({}, join, { url: "" }) : join;
            }),
            reuseMovieUrl: forceFresh ? "" : (plan.reuseMovieUrl || ""),
            reusePictureUrl: forceFresh ? "" : (plan.reusePictureUrl || ""),
            jit: !!jit,
        };
    }

    function emptyComposeResult(url, pictureUrl) {
        return {
            success: true,
            url: url,
            pictureUrl: pictureUrl || url,
            pictureReusable: true,
            stitched: false,
            owned: false,
            scenes: [],
            joins: [],
            rebuiltScenes: [],
            rebuiltJoins: [],
        };
    }

    async function prepareSceneClipAsync(api, c, index, total, onProgress) {
        const isHold = !!(c.hold || !c.url);
        const one = isHold
            ? await holdClipStillAsync(c, onProgress, api)
            : await prepareWindowsAsync(c, index, total, onProgress, api);
        if (one.error)
            return one;
        if (c.texts && c.texts.length > 0) {
            const beforeOverlay = one.url;
            const over = await overlayTextsAsync(one.url, c.texts, onProgress, api);
            if (!over.success) {
                releaseTempUrl(beforeOverlay);
                return { error: over.error || "Text overlay failed." };
            }
            one.url = over.url;
            if (beforeOverlay !== one.url)
                releaseTempUrl(beforeOverlay);
        }
        return one;
    }

    async function composeSceneClipsAsync(api, clips, scene, onProgress) {
        const first = Math.max(0, Number(scene.first) || 0);
        const count = Math.max(0, Number(scene.count) || 0);
        const slice = clips.slice(first, first + count);
        if (slice.length === 0)
            return { success: false, error: "Empty scene." };
        const hasInlineCards = slice.some(function (c) {
            return c.card && c.card.text && !(c.hold || !c.url);
        });
        if (!hasInlineCards) {
            const pieces = [];
            let combined = null;
            try {
                for (let i = 0; i < slice.length; i++) {
                    if (composeStopped())
                        return { success: false, error: "Stopped." };
                    const one = await prepareSceneClipAsync(api, slice[i], first + i, clips.length, onProgress);
                    if (one.error)
                        return { success: false, error: one.error };
                    pieces.push(one.url);
                }
                if (pieces.length > 1)
                    await resetFfmpegWorker(api);
                combined = await concatPinned(api, pieces, onProgress);
                return combined;
            } finally {
                pieces.forEach(function (url) {
                    if (!combined || !combined.success || url !== combined.url)
                        releaseTempUrl(url);
                });
            }
        }
        let acc = null;
        let pendingJoin = "cut";
        let pendingHold = 0;
        for (let i = 0; i < slice.length; i++) {
            if (composeStopped())
                return { success: false, error: "Stopped." };
            const c = slice[i];
            const isHold = !!(c.hold || !c.url);
            if (c.card && c.card.text && !isHold) {
                const look = textStyle(c.card.style);
                const card = await stillVideoAsync(
                    cardPngUrl(c.card.text, look), c.card.seconds || 2, onProgress, look.fadeSec, api);
                if (!card.success)
                    return { success: false, error: card.error || "Card failed." };
                if (!acc)
                    acc = card.url;
                else {
                    const previous = acc;
                    const joined = await joinPairAsync(api, acc, card.url, pendingJoin, onProgress, pendingHold);
                    if (!joined.success) return joined;
                    acc = joined.url;
                    if (previous !== acc)
                        releaseTempUrl(previous);
                    if (card.url !== acc)
                        releaseTempUrl(card.url);
                }
                pendingJoin = "dip";
                pendingHold = 0;
            }
            const one = await prepareSceneClipAsync(api, c, first + i, clips.length, onProgress);
            if (one.error)
                return { success: false, error: one.error };
            if (!acc)
                acc = one.url;
            else {
                const previous = acc;
                const joined = await joinPairAsync(api, acc, one.url, pendingJoin, onProgress, pendingHold);
                if (!joined.success) return joined;
                acc = joined.url;
                if (previous !== acc)
                    releaseTempUrl(previous);
                if (one.url !== acc)
                    releaseTempUrl(one.url);
            }
            pendingJoin = "cut";
            pendingHold = 0;
        }
        return { success: true, url: acc };
    }

    function isolatedFfmpegApi(base, workerIndex) {
        const api = Object.create(base);
        api._ffmpeg = null;
        api._loaded = false;
        api._loading = null;
        api._blobUrl = null;
        api._lock = Promise.resolve();
        api._onProgress = null;
        api._silenceSessions = {};
        api._silenceSessionSeq = 0;
        api._trimTailSeq = 0;
        api._trimHeadSeq = 0;
        api._poolWorkerIndex = workerIndex;
        api._log = function (msg) {
            if (typeof msg === "string" && msg.trim())
                console.debug("[PageToMovieFfmpeg pool " + workerIndex + "]", msg);
        };
        return api;
    }

    function terminateIsolatedApi(api, base) {
        if (!api || api === base) return;
        try {
            if (api._ffmpeg && typeof api._ffmpeg.terminate === "function")
                api._ffmpeg.terminate();
        } catch (err) {
            console.debug("Cut: pool worker terminate", err);
        }
        api._ffmpeg = null;
        api._loaded = false;
        api._loading = null;
    }

    async function prepareScenesSerialAsync(api, clips, scenes, sceneUrls, rebuiltScenes, onProgress) {
        for (let i = 0; i < scenes.length; i++) {
            if (sceneUrls[i]) continue;
            if (composeStopped())
                return { success: false, error: "Stopped." };
            const scene = scenes[i];
            onProgress?.(Math.round((rebuiltScenes.length / Math.max(scenes.length, 1)) * 40), "Preparing scene…");
            const built = await composeSceneClipsAsync(api, clips, scene, onProgress);
            if (!built.success)
                return built;
            const seconds = await measuredSceneSecondsAsync(api, built.url, scene.seconds);
            sceneUrls[i] = { id: scene.scene, url: built.url, seconds: seconds };
            rebuiltScenes.push(scene.scene);
        }
        return { success: true };
    }

    async function prepareScenesWithPoolAsync(base, clips, scenes, onProgress, metrics) {
        const sceneUrls = new Array(scenes.length);
        const dirtyIndexes = [];
        for (let i = 0; i < scenes.length; i++) {
            const scene = scenes[i];
            if (!scene.url) {
                dirtyIndexes.push(i);
                continue;
            }
            const seconds = await measuredSceneSecondsAsync(base, scene.url, scene.seconds);
            sceneUrls[i] = { id: scene.scene, url: scene.url, seconds: seconds };
        }

        const requested = requestedWorkerCount();
        const effective = Math.min(requested, Math.max(1, dirtyIndexes.length));
        metrics.requestedWorkers = requested;
        metrics.effectiveWorkers = effective;
        metrics.dirtyScenes = dirtyIndexes.length;
        if (dirtyIndexes.length === 0)
            return { success: true, sceneUrls: sceneUrls, rebuiltScenes: [] };

        if (effective === 1) {
            const rebuiltScenes = [];
            const started = performance.now();
            const serial = await prepareScenesSerialAsync(base, clips, scenes, sceneUrls, rebuiltScenes, onProgress);
            metrics.scenePrepareMs = Math.round(performance.now() - started);
            return Object.assign(serial, { sceneUrls: sceneUrls, rebuiltScenes: rebuiltScenes });
        }

        const apis = [base];
        for (let i = 1; i < effective; i++)
            apis.push(isolatedFfmpegApi(base, i + 1));
        let cursor = 0;
        let completed = 0;
        const rebuiltScenes = [];
        const started = performance.now();
        const runs = apis.map(async function (api) {
            while (true) {
                const position = cursor++;
                if (position >= dirtyIndexes.length) return;
                if (composeStopped()) throw new Error("Stopped.");
                const index = dirtyIndexes[position];
                const scene = scenes[index];
                const built = await composeSceneClipsAsync(api, clips, scene, onProgress);
                if (!built.success)
                    throw new Error(built.error || "Scene could not be rendered.");
                const seconds = await measuredSceneSecondsAsync(api, built.url, scene.seconds);
                sceneUrls[index] = { id: scene.scene, url: built.url, seconds: seconds };
                rebuiltScenes.push(scene.scene);
                completed++;
                onProgress?.(Math.round((completed / dirtyIndexes.length) * 40),
                    "Preparing scenes with " + effective + " workers…");
            }
        });

        const settled = await Promise.allSettled(runs);
        metrics.scenePrepareMs = Math.round(performance.now() - started);
        const rejected = settled.find(function (result) { return result.status === "rejected"; });
        for (let i = 1; i < apis.length; i++)
            terminateIsolatedApi(apis[i], base);
        if (!rejected) {
            rebuiltScenes.sort(function (a, b) { return Number(a) - Number(b); });
            return { success: true, sceneUrls: sceneUrls, rebuiltScenes: rebuiltScenes };
        }
        if (composeStopped())
            return { success: false, error: "Stopped.", sceneUrls: sceneUrls, rebuiltScenes: rebuiltScenes };

        metrics.fellBackToOne = true;
        metrics.fallbackReason = String(rejected.reason && rejected.reason.message || rejected.reason || "Worker failed");
        console.warn("Cut: FFmpeg pool failed; retrying with one worker", metrics.fallbackReason);
        for (const index of dirtyIndexes) {
            if (sceneUrls[index] && sceneUrls[index].url)
                releaseTempUrl(sceneUrls[index].url);
            sceneUrls[index] = null;
        }
        rebuiltScenes.length = 0;
        await resetFfmpegWorker(base);
        onProgress?.(0, "Parallel render failed — retrying safely with 1 worker…");
        const serial = await prepareScenesSerialAsync(base, clips, scenes, sceneUrls, rebuiltScenes, onProgress);
        return Object.assign(serial, { sceneUrls: sceneUrls, rebuiltScenes: rebuiltScenes });
    }

    async function prepareFlatClipsSerialAsync(api, clips, clipUrls, onProgress) {
        for (let i = 0; i < clips.length; i++) {
            if (composeStopped()) return { success: false, error: "Stopped." };
            const one = await prepareSceneClipAsync(api, clips[i], i, clips.length, onProgress);
            if (one.error) return { success: false, error: one.error };
            const seconds = await measuredSceneSecondsAsync(api, one.url, Number(clips[i].duration) || 0);
            clipUrls[i] = { id: i + 1, url: one.url, seconds: seconds };
            onProgress?.(Math.round(((i + 1) / clips.length) * 40), "Preparing clips…");
        }
        return { success: true };
    }

    async function prepareFlatClipsWithPoolAsync(base, clips, onProgress, metrics) {
        const clipUrls = new Array(clips.length);
        const requested = requestedClipWorkerCount();
        const effective = Math.min(requested, Math.max(1, clips.length));
        metrics.clipRequestedWorkers = requested;
        metrics.clipEffectiveWorkers = effective;
        const started = performance.now();
        if (effective === 1) {
            const serial = await prepareFlatClipsSerialAsync(base, clips, clipUrls, onProgress);
            metrics.clipPrepareMs = Math.round(performance.now() - started);
            return Object.assign(serial, { clipUrls: clipUrls });
        }

        const apis = [base];
        for (let i = 1; i < effective; i++)
            apis.push(isolatedFfmpegApi(base, 200 + i + 1));
        let cursor = 0;
        let completed = 0;
        const runs = apis.map(async function (api) {
            while (true) {
                const index = cursor++;
                if (index >= clips.length) return;
                if (composeStopped()) throw new Error("Stopped.");
                const one = await prepareSceneClipAsync(api, clips[index], index, clips.length, onProgress);
                if (one.error) throw new Error(one.error);
                const seconds = await measuredSceneSecondsAsync(api, one.url, Number(clips[index].duration) || 0);
                clipUrls[index] = { id: index + 1, url: one.url, seconds: seconds };
                completed++;
                onProgress?.(Math.round((completed / clips.length) * 40),
                    "Preparing clips with " + effective + " workers…");
            }
        });
        const settled = await Promise.allSettled(runs);
        metrics.clipPrepareMs = Math.round(performance.now() - started);
        const rejected = settled.find(function (result) { return result.status === "rejected"; });
        for (let i = 1; i < apis.length; i++)
            terminateIsolatedApi(apis[i], base);
        if (!rejected)
            return { success: true, clipUrls: clipUrls };
        for (const item of clipUrls) {
            if (item && item.url) releaseTempUrl(item.url);
        }
        if (composeStopped()) return { success: false, error: "Stopped.", clipUrls: clipUrls };
        metrics.clipFellBackToOne = true;
        metrics.clipFallbackReason = String(
            rejected.reason && rejected.reason.message || rejected.reason || "Worker failed");
        await resetFfmpegWorker(base);
        onProgress?.(0, "Parallel clip render failed — retrying safely with 1 worker…");
        clipUrls.fill(null);
        const serial = await prepareFlatClipsSerialAsync(base, clips, clipUrls, onProgress);
        return Object.assign(serial, { clipUrls: clipUrls });
    }

    function flatJoinsOf(scenes, joins, clipCount) {
        const flat = new Array(Math.max(0, clipCount - 1)).fill(null);
        for (let i = 0; i < scenes.length; i++) {
            const scene = scenes[i];
            const boundary = (Number(scene.first) || 0) + (Number(scene.count) || 0) - 1;
            if (boundary >= 0 && boundary < flat.length && i < joins.length)
                flat[boundary] = joins[i];
        }
        return flat;
    }

    async function trimBodyAsync(api, url, seconds, inFade, outFade, onProgress) {
        const start = Math.max(0, Number(inFade) || 0);
        const total = Number(seconds) || 0;
        const end = Math.max(start + 0.1, total - (Number(outFade) || 0));
        if (start <= 0.05 && (total <= 0 || total - end <= 0.05))
            return { success: true, url: url };
        return trimRangeWithApiAsync(api, url, start, end, onProgress);
    }

    async function measuredSceneSecondsAsync(api, url, plannedSeconds) {
        const planned = Math.max(0, Number(plannedSeconds) || 0);
        if (!api || typeof api.probeDurationAsync !== "function" || !url)
            return planned;
        const probe = await api.probeDurationAsync(url);
        return probe && probe.success && Number(probe.seconds) > 0
            ? Number(probe.seconds)
            : planned;
    }

    async function ensureJoinUrlAsync(api, join, left, right, onProgress) {
        if (!join || !join.encodes)
            return { success: true, url: "" };
        if (join.url)
            return { success: true, url: join.url, cached: true };
        const kind = String(join.kind || "cut").toLowerCase();
        if (kind === "cuttoblack") {
            const hold = Math.max(0.3, Number(join.hold) || CUT_TO_BLACK_HOLD_SEC);
            const black = await stillVideoAsync(blackPngUrl(), hold, onProgress, 0, api);
            if (!black.success) return black;
            join.url = black.url;
            return { success: true, url: black.url };
        }
        if (!xfadeName(kind))
            return { success: true, url: "" };
        const fade = Math.max(CUT_XFADE_MIN_SEC, Number(join.fade) || CUT_XFADE_SEC);
        const leftSec = Number(left.seconds) || 0;
        const tailStart = Math.max(0, leftSec - fade);
        const leftTail = await trimRangeWithApiAsync(
            api, left.url, tailStart, leftSec > 0 ? leftSec : tailStart + fade, onProgress);
        if (!leftTail.success) return leftTail;
        const rightHead = await trimRangeWithApiAsync(api, right.url, 0, fade, onProgress);
        if (!rightHead.success) {
            releaseTempUrl(leftTail.url);
            return rightHead;
        }
        try {
            const faded = await xfadeAsync(leftTail.url, rightHead.url, kind, onProgress, fade, api);
            if (faded.success) {
                join.url = faded.url;
                return faded;
            }
            // A dissolve is optional presentation, never a reason to lose the
            // movie. Preserve both edge windows as a hard cut when the browser
            // FFmpeg build cannot complete xfade/acrossfade.
            const hardCut = await concatPinned(api, [leftTail.url, rightHead.url], onProgress);
            if (!hardCut.success)
                return faded;
            join.url = hardCut.url;
            return hardCut;
        } finally {
            releaseTempUrl(leftTail.url);
            releaseTempUrl(rightHead.url);
        }
    }

    function assembleStitchPieces(sceneUrls, joins, bodyUrls, joinUrls) {
        const pieces = [];
        const transientBodies = [];
        for (let i = 0; i < sceneUrls.length; i++) {
            const bodyUrl = bodyUrls[i] || sceneUrls[i].url;
            pieces.push(bodyUrl);
            if (bodyUrl !== sceneUrls[i].url)
                transientBodies.push(bodyUrl);
            if (joinUrls[i])
                pieces.push(joinUrls[i]);
        }
        return { success: true, pieces: pieces, transientBodies: transientBodies };
    }

    async function prepareStitchPiecesSerialAsync(api, sceneUrls, joins, onProgress) {
        const bodyUrls = new Array(sceneUrls.length);
        const joinUrls = new Array(joins.length);
        for (let i = 0; i < sceneUrls.length; i++) {
            if (composeStopped())
                return { success: false, error: "Stopped." };
            const join = i < joins.length ? joins[i] : null;
            const prev = i > 0 ? joins[i - 1] : null;
            const inFade = prev && xfadeName(prev.kind) ? Number(prev.fade) || 0 : 0;
            const outFade = join && xfadeName(join.kind) ? Number(join.fade) || 0 : 0;
            const body = await trimBodyAsync(api, sceneUrls[i].url, sceneUrls[i].seconds, inFade, outFade, onProgress);
            if (!body.success) return body;
            bodyUrls[i] = body.url;
            if (!join || !join.encodes || !sceneUrls[i + 1])
                continue;
            const made = await ensureJoinUrlAsync(api, join, sceneUrls[i], sceneUrls[i + 1], onProgress);
            if (!made.success) return made;
            joinUrls[i] = made.url || "";
        }
        return assembleStitchPieces(sceneUrls, joins, bodyUrls, joinUrls);
    }

    function releaseStitchAttempt(sceneUrls, joins, bodyUrls, dirtyJoinIndexes) {
        for (let i = 0; i < bodyUrls.length; i++) {
            if (bodyUrls[i] && bodyUrls[i] !== sceneUrls[i].url)
                releaseTempUrl(bodyUrls[i]);
        }
        for (const index of dirtyJoinIndexes) {
            const join = joins[index];
            if (join && join.url) {
                releaseTempUrl(join.url);
                join.url = "";
            }
        }
    }

    async function concatAndMixOnce(api, ffmpeg, urls, musicUrl, spec, seq) {
        const names = [];
        const listName = "cut_onepass_" + seq + ".txt";
        const musicName = "cut_onepass_music_" + seq + ".m4a";
        const outName = "cut_onepass_out_" + seq + ".mp4";
        try {
            let pictureSec = 0;
            const durations = [];
            for (let i = 0; i < urls.length; i++) {
                const name = "cut_onepass_" + seq + "_" + i + ".mp4";
                names.push(name);
                await writeMemfs(ffmpeg, name, await fetchInputBytes(api, urls[i], "Clip"));
                const probe = await api._probeDurationMemfsAsync(name);
                const seconds = probe.success && Number(probe.seconds) > 0 ? Number(probe.seconds) : 0;
                durations.push(seconds);
                pictureSec += seconds;
            }
            const list = [];
            for (let i = 0; i < names.length; i++) {
                list.push("file '" + names[i] + "'");
                if (durations[i] > 0.001)
                    list.push("duration " + durations[i]);
            }
            await writeMemfs(ffmpeg, listName, list.join("\n"));
            await writeMemfs(ffmpeg, musicName, await fetchInputBytes(api, musicUrl, "Soundtrack"));
            const musicProbe = await api._probeDurationMemfsAsync(musicName);
            const musicSec = musicProbe.success && Number(musicProbe.seconds) > 0
                ? Number(musicProbe.seconds) : 0;
            const introBlack = spec && spec.introBlack > 0 ? spec.introBlack : 0;
            const pictureEndSec = pictureSec + introBlack;
            const outputSec = Math.max(pictureEndSec, musicSec);
            const freezeSec = pictureSec > 0.05 ? Math.max(0, musicSec - pictureEndSec) : 0;
            let videoFilter = "[0:v]setpts=PTS-STARTPTS";
            if (introBlack > 0.05 || freezeSec > 0.05) {
                videoFilter += ",tpad=start_mode=add:start_duration=" + String(introBlack)
                    + ":color=black:stop_mode=clone:stop_duration=" + String(freezeSec);
            }
            videoFilter += ",format=yuv420p[v]";
            const filters = mixFiltersOf(spec);
            const input = [
                "-hide_banner", "-y", "-fflags", "+genpts",
                "-f", "concat", "-safe", "0", "-i", listName,
                "-i", musicName,
            ];
            const output = ["-map", "[v]", "-map", "[a]"];
            if (outputSec > 0.05)
                output.push("-t", String(outputSec));
            output.push.apply(output, h264EncodeArgs("aac"));
            output.push(outName);
            try {
                await execChecked(ffmpeg, input.concat(
                    ["-filter_complex", filters.withVo + ";" + videoFilter], output));
            } catch (noNativeAudio) {
                console.debug("Cut: one-pass concat has no native audio", noNativeAudio);
                try { await ffmpeg.deleteFile(outName); } catch (delErr) {
                    console.debug("Cut: one-pass cleanup", delErr);
                }
                await execChecked(ffmpeg, input.concat(
                    ["-filter_complex", filters.musicOnly + ";" + videoFilter], output));
            }
            const outProbe = await api._probeDurationMemfsAsync(outName);
            const actualSec = outProbe.success && Number(outProbe.seconds) > 0
                ? Number(outProbe.seconds) : 0;
            if (outputSec > 0.1 && actualSec + 0.25 < outputSec)
                throw new Error("Combined movie ended early.");
            const out = await ffmpeg.readFile(outName);
            const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
            noteTemp(url);
            return { success: true, url: url, pictureSeconds: pictureSec, outputSeconds: outputSec };
        } finally {
            for (const name of names)
                await deleteMemfs(ffmpeg, name);
            await deleteMemfs(ffmpeg, listName);
            await deleteMemfs(ffmpeg, musicName);
            await deleteMemfs(ffmpeg, outName);
        }
    }

    async function concatAndMixAsync(api, urls, musicUrl, spec, onProgress) {
        return api._runExclusiveAsync(async function () {
            await resetFfmpegWorker(api);
            let load = await api.ensureLoadedAsync(onProgress);
            if (!load.success)
                return { success: false, error: load.error };
            const seq = ++cut._trimSeq;
            try {
                return await concatAndMixOnce(api, api._ffmpeg, urls, musicUrl, spec, seq);
            } catch (err) {
                if (!isFsError(err))
                    return { success: false, error: messageOf(err, "Combined concat and mix failed.") };
                await resetFfmpegWorker(api);
                load = await api.ensureLoadedAsync(onProgress);
                if (!load.success)
                    return { success: false, error: load.error || fsUserMessage() };
                try {
                    return await concatAndMixOnce(api, api._ffmpeg, urls, musicUrl, spec, seq);
                } catch (retry) {
                    return { success: false, error: messageOf(retry, fsUserMessage()) };
                }
            }
        });
    }

    async function prepareStitchPiecesWithPoolAsync(base, sceneUrls, joins, onProgress, metrics) {
        const requested = requestedStitchWorkerCount();
        const tasks = [];
        const bodyUrls = new Array(sceneUrls.length);
        const joinUrls = new Array(joins.length);
        const dirtyJoinIndexes = [];
        for (let i = 0; i < sceneUrls.length; i++) {
            const index = i;
            const join = index < joins.length ? joins[index] : null;
            const prev = index > 0 ? joins[index - 1] : null;
            const inFade = prev && xfadeName(prev.kind) ? Number(prev.fade) || 0 : 0;
            const outFade = join && xfadeName(join.kind) ? Number(join.fade) || 0 : 0;
            tasks.push(async function (api) {
                const body = await trimBodyAsync(api, sceneUrls[index].url, sceneUrls[index].seconds,
                    inFade, outFade, onProgress);
                if (!body.success) throw new Error(body.error || "Scene body could not be trimmed.");
                bodyUrls[index] = body.url;
            });
            if (join && join.encodes && sceneUrls[index + 1]) {
                if (join.url) {
                    joinUrls[index] = join.url;
                } else {
                    dirtyJoinIndexes.push(index);
                    tasks.push(async function (api) {
                        const made = await ensureJoinUrlAsync(
                            api, join, sceneUrls[index], sceneUrls[index + 1], onProgress);
                        if (!made.success) throw new Error(made.error || "Transition could not be rendered.");
                        joinUrls[index] = made.url || "";
                    });
                }
            }
        }

        const effective = Math.min(requested, Math.max(1, tasks.length));
        if (metrics) {
            metrics.stitchRequestedWorkers = requested;
            metrics.stitchEffectiveWorkers = effective;
            metrics.stitchTasks = tasks.length;
        }
        const started = performance.now();
        if (effective === 1) {
            const serial = await prepareStitchPiecesSerialAsync(base, sceneUrls, joins, onProgress);
            if (metrics) metrics.stitchPrepareMs = Math.round(performance.now() - started);
            return serial;
        }

        const apis = [base];
        for (let i = 1; i < effective; i++)
            apis.push(isolatedFfmpegApi(base, 100 + i + 1));
        let cursor = 0;
        let completed = 0;
        const runs = apis.map(async function (api) {
            while (true) {
                const position = cursor++;
                if (position >= tasks.length) return;
                if (composeStopped()) throw new Error("Stopped.");
                await tasks[position](api);
                completed++;
                onProgress?.(40 + Math.round((completed / tasks.length) * 15),
                    "Preparing transitions with " + effective + " workers…");
            }
        });
        const settled = await Promise.allSettled(runs);
        if (metrics) metrics.stitchPrepareMs = Math.round(performance.now() - started);
        const rejected = settled.find(function (result) { return result.status === "rejected"; });
        for (let i = 1; i < apis.length; i++)
            terminateIsolatedApi(apis[i], base);
        if (!rejected)
            return assembleStitchPieces(sceneUrls, joins, bodyUrls, joinUrls);
        if (composeStopped()) {
            releaseStitchAttempt(sceneUrls, joins, bodyUrls, dirtyJoinIndexes);
            return { success: false, error: "Stopped." };
        }

        if (metrics) {
            metrics.stitchFellBackToOne = true;
            metrics.stitchFallbackReason = String(
                rejected.reason && rejected.reason.message || rejected.reason || "Worker failed");
        }
        console.warn("Cut: FFmpeg stitch pool failed; retrying with one worker",
            metrics ? metrics.stitchFallbackReason : rejected.reason);
        releaseStitchAttempt(sceneUrls, joins, bodyUrls, dirtyJoinIndexes);
        await resetFfmpegWorker(base);
        onProgress?.(40, "Parallel transition render failed — retrying safely with 1 worker…");
        return prepareStitchPiecesSerialAsync(base, sceneUrls, joins, onProgress);
    }

    async function stitchScenesAsync(api, sceneUrls, joins, onProgress, metrics) {
        const prepared = await prepareStitchPiecesWithPoolAsync(api, sceneUrls, joins, onProgress, metrics);
        if (!prepared.success) return prepared;
        const pieces = prepared.pieces;
        const transientBodies = prepared.transientBodies;
        onProgress?.(55, "Combining clips…");
        let combined = null;
        const concatStarted = performance.now();
        try {
            let expectedSec = 0;
            for (const url of pieces)
                expectedSec += await measuredSceneSecondsAsync(api, url, 0);
            // Scene preparation and xfade passes fragment ffmpeg.wasm's fixed
            // heap. Start the largest concat in a fresh worker so it does not
            // fail near completion with "memory access out of bounds".
            await resetFfmpegWorker(api);
            combined = await concatPinned(api, pieces, onProgress);
            if (!combined.success)
                return combined;
            const actualSec = await measuredSceneSecondsAsync(api, combined.url, 0);
            if (expectedSec > 0.1 && actualSec + 0.25 < expectedSec) {
                const lastScene = sceneUrls.length > 0 ? sceneUrls[sceneUrls.length - 1] : null;
                const lastJoin = joins.length > 0 ? joins[joins.length - 1] : null;
                const lastKind = String(lastJoin && lastJoin.kind || "").toLowerCase();
                if (lastScene && lastScene.url && xfadeName(lastKind)) {
                    await resetFfmpegWorker(api);
                    const appended = await xfadeAsync(
                        combined.url, lastScene.url, lastKind, onProgress,
                        Math.max(CUT_XFADE_MIN_SEC, Number(lastJoin.fade) || CUT_XFADE_SEC));
                    if (appended.success) {
                        releaseTempUrl(combined.url);
                        combined = appended;
                        return combined;
                    }
                }
                const nativeAudio = combined.url;
                const video = await concatVideoRemuxAsync(api, pieces, onProgress);
                if (!video.success)
                    return video;
                const repaired = await mixMovieAudioAsync(api, video.url, nativeAudio, onProgress, {
                    start: 0, markIn: 0, markOut: 0, volume: 1,
                    fadeIn: 0, fadeOut: 0, playbackRate: 1,
                });
                releaseTempUrl(video.url);
                if (!repaired.success) {
                    releaseTempUrl(nativeAudio);
                    return repaired;
                }
                releaseTempUrl(nativeAudio);
                combined = repaired;
            }
            return combined;
        } finally {
            if (metrics) metrics.concatMs = Math.round(performance.now() - concatStarted);
            transientBodies.forEach(function (url) {
                if (!combined || !combined.success || url !== combined.url)
                    releaseTempUrl(url);
            });
        }
    }

    async function validateCombinedResultAsync(combined, metrics) {
        if (!combined || !combined.success) return combined;
        const validationStarted = performance.now();
        const checks = await Promise.all([
            cut.validateVideoUrl(combined.url),
            cut.validateAudioUrl(combined.url),
        ]);
        if (metrics)
            metrics.combinedValidationMs = Math.round(performance.now() - validationStarted);
        if (checks[0].success && checks[1].success) {
            if (metrics) metrics.combinedValidated = true;
            return combined;
        }
        const validationError = !checks[0].success
            ? checks[0].error || "Combined video stream could not be decoded."
            : checks[1].error || "Combined audio stream could not be decoded.";
        releaseTempUrl(combined.url);
        return { success: false, error: validationError };
    }

    async function stitchAndMixScenesAsync(api, sceneUrls, joins, audio, onProgress, metrics) {
        const spec = musicSpec(audio);
        if (!spec)
            return { success: false, error: "Soundtrack is missing." };
        const prepared = await prepareStitchPiecesWithPoolAsync(api, sceneUrls, joins, onProgress, metrics);
        if (!prepared.success) return prepared;
        const prepareStarted = performance.now();
        const placed = await placeMusicAsync(api, spec, onProgress);
        if (metrics) metrics.musicPrepareMs = Math.round(performance.now() - prepareStarted);
        if (!placed.success) {
            prepared.transientBodies.forEach(releaseTempUrl);
            return placed;
        }
        let combined = null;
        const combinedStarted = performance.now();
        try {
            onProgress?.(55, "Combining clips and mixing audio…");
            combined = await withPinnedUrls(prepared.pieces.concat([placed.url]), function () {
                return concatAndMixAsync(api, prepared.pieces, placed.url, spec, onProgress);
            });
            combined = await validateCombinedResultAsync(combined, metrics);
            if (metrics) {
                metrics.combinedMs = Math.round(performance.now() - combinedStarted);
                metrics.combinedUsed = !!combined.success;
            }
            return combined;
        } finally {
            prepared.transientBodies.forEach(function (url) {
                if (!combined || !combined.success || url !== combined.url)
                    releaseTempUrl(url);
            });
            if (placed.url !== spec.url)
                releaseTempUrl(placed.url);
        }
    }

    function flatPipelineEligible(clips, scenes, audio, metrics) {
        return !!metrics.flatRequested && !!metrics.combinedRequested && !!musicSpec(audio)
            && scenes.length > 0
            && !clips.some(function (clip) {
                return clip && clip.card && clip.card.text && !(clip.hold || !clip.url);
            });
    }

    async function composeFlatClipsAndMixAsync(api, clips, scenes, joins, audio, onProgress, metrics) {
        const spec = musicSpec(audio);
        const preparedClips = await prepareFlatClipsWithPoolAsync(api, clips, onProgress, metrics);
        if (!preparedClips.success) return preparedClips;
        const clipUrls = preparedClips.clipUrls;
        const flatJoins = flatJoinsOf(scenes, joins, clips.length);
        const prepared = await prepareStitchPiecesWithPoolAsync(api, clipUrls, flatJoins, onProgress, metrics);
        if (!prepared.success) {
            clipUrls.forEach(function (item) { if (item && item.url) releaseTempUrl(item.url); });
            return prepared;
        }
        const prepareStarted = performance.now();
        const placed = await placeMusicAsync(api, spec, onProgress);
        metrics.musicPrepareMs = Math.round(performance.now() - prepareStarted);
        if (!placed.success) {
            prepared.transientBodies.forEach(releaseTempUrl);
            clipUrls.forEach(function (item) { if (item && item.url) releaseTempUrl(item.url); });
            return placed;
        }
        let combined = null;
        const combinedStarted = performance.now();
        try {
            onProgress?.(55, "Combining prepared clips and mixing audio…");
            combined = await withPinnedUrls(prepared.pieces.concat([placed.url]), function () {
                return concatAndMixAsync(api, prepared.pieces, placed.url, spec, onProgress);
            });
            combined = await validateCombinedResultAsync(combined, metrics);
            metrics.combinedMs = Math.round(performance.now() - combinedStarted);
            metrics.combinedUsed = !!combined.success;
            metrics.flatUsed = !!combined.success;
            return combined;
        } finally {
            prepared.transientBodies.forEach(function (url) {
                if (!combined || !combined.success || url !== combined.url)
                    releaseTempUrl(url);
            });
            clipUrls.forEach(function (item) {
                if (item && item.url && (!combined || item.url !== combined.url))
                    releaseTempUrl(item.url);
            });
            if (placed.url !== spec.url)
                releaseTempUrl(placed.url);
        }
    }

    async function composeWorkAsync(clipsOrPlan, audioUrl, dotNetRef, jit) {
        cut._aborted = false;
        const onProgress = asProgress(dotNetRef);
        const composeStarted = performance.now();
        const metrics = {
            requestedWorkers: requestedWorkerCount(),
            effectiveWorkers: 1,
            dirtyScenes: 0,
            stitchRequestedWorkers: requestedStitchWorkerCount(),
            stitchEffectiveWorkers: 1,
            stitchTasks: 0,
            forceFresh: queryFlag("ffmpegFresh"),
            fellBackToOne: false,
            fallbackReason: "",
            stitchFellBackToOne: false,
            stitchFallbackReason: "",
            scenePrepareMs: 0,
            stitchPrepareMs: 0,
            concatMs: 0,
            musicPrepareMs: 0,
            mixMs: 0,
            combinedRequested: queryFlag("ffmpegCombined"),
            combinedUsed: false,
            combinedFellBack: false,
            combinedFallbackReason: "",
            combinedMs: 0,
            combinedValidated: false,
            combinedValidationMs: 0,
            flatRequested: queryFlag("ffmpegFlat"),
            flatUsed: false,
            flatFellBack: false,
            flatFallbackReason: "",
            clipRequestedWorkers: requestedClipWorkerCount(),
            clipEffectiveWorkers: 1,
            clipPrepareMs: 0,
            clipFellBackToOne: false,
            clipFallbackReason: "",
            totalMs: 0,
        };
        cut._lastComposeMetrics = metrics;
        try {
            const api = window.PageToMovieFfmpeg;
            if (!api) return { success: false, error: "ffmpeg helper missing" };
            const plan = normalizeComposePlan(clipsOrPlan, jit);
            const clips = plan.clips;
            if (!clips || clips.length === 0)
                return { success: false, error: "No clips to export." };
            if (plan.reuseMovieUrl)
                return emptyComposeResult(plan.reuseMovieUrl, plan.reusePictureUrl || plan.reuseMovieUrl);

            const spec = musicSpec(audioUrl);
            const sourceUrls = clips.map(function (c) { return c && c.url; }).filter(Boolean);
            if (spec)
                sourceUrls.push(spec.url);
            if (plan.reusePictureUrl)
                sourceUrls.push(plan.reusePictureUrl);
            plan.scenes.forEach(function (s) { if (s && s.url) sourceUrls.push(s.url); });
            plan.joins.forEach(function (j) { if (j && j.url) sourceUrls.push(j.url); });

            return await withPinnedUrls(sourceUrls, async function () {
                let rebuiltScenes = [];
                const rebuiltJoins = [];
                const scenes = plan.scenes.length > 0
                    ? plan.scenes
                    : [{ scene: 1, first: 0, count: clips.length, seconds: 0, url: "" }];
                if (!plan.reusePictureUrl && flatPipelineEligible(clips, scenes, audioUrl, metrics)) {
                    const cachedFlatJoins = {};
                    plan.joins.forEach(function (join) {
                        if (join && join.encodes && join.url) cachedFlatJoins[join.from] = true;
                    });
                    const flat = await composeFlatClipsAndMixAsync(
                        api, clips, scenes, plan.joins, audioUrl, onProgress, metrics);
                    if (flat.success) {
                        plan.joins.forEach(function (join) {
                            if (join && join.encodes && join.url && !cachedFlatJoins[join.from])
                                rebuiltJoins.push(join.from);
                        });
                        noteResult(flat);
                        onProgress?.(100, "Ready");
                        if (plan.jit) emitPrefix(dotNetRef, flat.url, clips.length);
                        return {
                            success: true, url: flat.url, pictureUrl: "", pictureReusable: false,
                            stitched: true, owned: true, scenes: [],
                            joins: plan.joins.filter(function (join) {
                                return join && join.encodes && join.url;
                            }).map(function (join) { return { id: join.from, url: join.url }; }),
                            rebuiltScenes: [], rebuiltJoins: rebuiltJoins,
                        };
                    }
                    metrics.flatFellBack = true;
                    metrics.flatFallbackReason = flat.error || "Flat clip pipeline failed.";
                    console.warn("Cut: flat clip pipeline failed; retrying scene pipeline",
                        metrics.flatFallbackReason);
                    await resetFfmpegWorker(api);
                    onProgress?.(0, "Fast clip pipeline failed — retrying proven scene pipeline…");
                }
                const prepared = await prepareScenesWithPoolAsync(api, clips, scenes, onProgress, metrics);
                if (!prepared.success)
                    return prepared;
                const sceneUrls = prepared.sceneUrls;
                rebuiltScenes = prepared.rebuiltScenes;

                if (plan.jit) {
                    let firstDirty = scenes.length;
                    for (let i = 0; i < scenes.length; i++) {
                        if (!scenes[i].url) {
                            firstDirty = i;
                            break;
                        }
                    }
                    if (firstDirty > 0) {
                        const lead = await stitchScenesAsync(api, sceneUrls.slice(0, firstDirty), plan.joins.slice(0, firstDirty), onProgress);
                        if (lead.success) {
                            let covered = 0;
                            for (let i = 0; i < firstDirty; i++)
                                covered += Number(scenes[i].count) || 0;
                            keepPrefixUrl(lead.url);
                            emitPrefix(dotNetRef, lead.url, covered);
                        }
                    }
                }

                if (composeStopped())
                    return { success: false, error: "Stopped." };

                const cachedJoins = {};
                plan.joins.forEach(function (j) {
                    if (j && j.encodes && j.url)
                        cachedJoins[j.from] = true;
                });
                let picture = null;
                let mixed = null;
                let pictureReusable = true;
                const joinsDirty = plan.joins.some(function (j) { return j && j.encodes && !j.url; });
                const combinedEligible = metrics.combinedRequested && !!spec
                    && !plan.reusePictureUrl;
                if (combinedEligible) {
                    const onePass = await stitchAndMixScenesAsync(
                        api, sceneUrls, plan.joins, audioUrl, onProgress, metrics);
                    if (onePass.success) {
                        mixed = onePass;
                        pictureReusable = false;
                    } else {
                        metrics.combinedFellBack = true;
                        metrics.combinedFallbackReason = onePass.error || "Combined pass failed.";
                        console.warn("Cut: combined concat/mix failed; retrying two-pass pipeline",
                            metrics.combinedFallbackReason);
                        await resetFfmpegWorker(api);
                        onProgress?.(40, "Combined pass failed — retrying proven export path…");
                    }
                }

                if (!mixed) {
                    if (plan.reusePictureUrl && rebuiltScenes.length === 0 && !joinsDirty) {
                        picture = { success: true, url: plan.reusePictureUrl };
                    } else {
                        picture = await stitchScenesAsync(api, sceneUrls, plan.joins, onProgress, metrics);
                        if (!picture.success) return picture;
                    }
                    mixed = await mixOptionalAudio(api, picture.url, audioUrl, onProgress, metrics);
                }
                if (!mixed.success) return mixed;
                plan.joins.forEach(function (j) {
                    if (j && j.encodes && j.url && !cachedJoins[j.from])
                        rebuiltJoins.push(j.from);
                });
                noteResult(mixed);
                onProgress?.(100, "Ready");
                if (plan.jit)
                    emitPrefix(dotNetRef, mixed.url, clips.length);
                return {
                    success: true,
                    url: mixed.url,
                    pictureUrl: picture ? picture.url : "",
                    pictureReusable: pictureReusable,
                    stitched: true,
                    owned: true,
                    scenes: sceneUrls.map(function (s) { return { id: s.id, url: s.url }; }),
                    joins: plan.joins.filter(function (j) { return j && j.encodes && j.url; })
                        .map(function (j) { return { id: j.from, url: j.url }; }),
                    rebuiltScenes: rebuiltScenes,
                    rebuiltJoins: rebuiltJoins,
                };
            });
        } finally {
            metrics.totalMs = Math.round(performance.now() - composeStarted);
            try {
                document.documentElement.dataset.cutComposeMetrics = JSON.stringify(metrics);
            } catch (_) { }
            console.info("Cut: compose metrics", JSON.stringify(metrics));
            if (cut._progressRef === dotNetRef)
                cut._progressRef = null;
        }
    }

    cut.composeMovieAsync = async function (clips, audioUrl, dotNetRef, jit) {
        const run = function () { return composeWorkAsync(clips, audioUrl, dotNetRef, jit); };
        const done = cut._composeGate.then(run, run);
        cut._composeGate = done.then(function () {}, function () {});
        return done;
    };

    cut.exportMovieAsync = async function (clips, audioUrl, dotNetRef) {
        await drainComposeAsync();
        const r = await cut.composeMovieAsync(clips, audioUrl, dotNetRef);
        if (!r.success) return r;
        cut.downloadUrlAs(r.url, "movie.mp4");
        return r;
    };

    cut.previewMovieAsync = async function (clips, audioUrl, dotNetRef) {
        const r = await cut.composeMovieAsync(clips, audioUrl, dotNetRef, false);
        if (!r.success) return r;
        replaceOwnedMovie(r.url, !!r.owned);
        return r;
    };

    cut.previewMovieJitAsync = async function (clips, audioUrl, dotNetRef) {
        const r = await cut.composeMovieAsync(clips, audioUrl, dotNetRef, true);
        if (!r.success) return r;
        replaceOwnedMovie(r.url, !!r.owned);
        return r;
    };

    window.PageToMovieCut = cut;
})();
