/**
 * Standalone Cut — local folder + browser compose.
 * ffmpeg load / concat / probe / mix stay in PageToMovieFfmpeg (copied from Web).
 * Ops go through that helper's exclusive queue.
 */
(function () {
    const cut = {
        _root: null,
        _fallbackFiles: null,
        _trimSeq: 0,
        _ownedMovieUrl: null,
        _ownedPrefixUrls: [],
        _ownedTemps: new Set(),
        _activeInputs: new Set(),
        _pendingRevoke: new Set(),
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

    function isFsError(err) {
        if (!err)
            return false;
        const name = String(err.name || "");
        const msg = String(err.message || err);
        return name === "ErrnoError" || /FS error/i.test(msg) || /ErrnoError/i.test(msg);
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
            for (let i = 0; i < urls.length; i++) {
                const n = "cut_cat_" + seq + "_" + i + ".mp4";
                names.push(n);
                await writeMemfs(ffmpeg, n, await fetchInputBytes(api, urls[i], "Clip"));
            }
            await writeMemfs(ffmpeg, listName, names.map(function (n) { return "file '" + n + "'"; }).join("\n"));
            try {
                await ffmpeg.exec(["-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", listName]
                    .concat(h264EncodeArgs("aac"), [outName]));
            } catch (audioErr) {
                console.debug("Cut: concat audio missing", audioErr);
                try { await ffmpeg.deleteFile(outName); } catch (delErr) {
                    console.debug("Cut: concat out cleanup", delErr);
                }
                await ffmpeg.exec(["-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", listName]
                    .concat(h264EncodeArgs("an"), [outName]));
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

    function clampTrimWindow(startSec, endSec, total) {
        let start = Number(startSec) || 0;
        let end = Number(endSec);
        if (!Number.isFinite(end) || end <= 0)
            end = total > 0 ? total : 0;
        if (start < 0) start = 0;
        if (total > 0 && start > total) start = total;
        if (total > 0 && end > total) end = total;
        if (end <= start) end = start + 0.1;
        return { start: start, keep: Math.max(0.1, end - start) };
    }

    function buildTrimArgs(inName, outName, start, keep, silentAudio) {
        const args = ["-hide_banner", "-y"];
        if (start > 0.001) args.push("-ss", String(start));
        args.push("-i", inName);
        if (silentAudio)
            args.push("-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000");
        args.push("-t", String(keep),
            "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p");
        args.push.apply(args, h264EncodeArgs("aac"));
        if (silentAudio)
            args.push("-map", "0:v", "-map", "1:a", "-shortest");
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

    async function overlayTextsAsync(videoUrl, texts, onProgress) {
        const list = (texts || []).filter(function (t) { return t && String(t.text || "").trim(); });
        if (list.length === 0)
            return { success: true, url: videoUrl };
        const api = window.PageToMovieFfmpeg;
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
                    await ffmpeg.exec(args.concat(["-map", "0:a"], h264EncodeArgs("aac"), [outName]));
                } catch (audioErr) {
                    console.debug("Cut: overlay native audio missing", audioErr);
                    await ffmpeg.exec(args.concat(h264EncodeArgs("an"), [outName]));
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

    async function stillVideoAsync(pngUrl, seconds, onProgress, fadeSec) {
        const api = window.PageToMovieFfmpeg;
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
                await ffmpeg.exec([
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
    async function xfadeAsync(leftUrl, rightUrl, kind, onProgress, fadeSec) {
        const api = window.PageToMovieFfmpeg;
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
                const leftSec = probe.success && probe.seconds > 0 ? probe.seconds : 1;
                const fade = Number(fadeSec) > 0.05
                    ? Number(fadeSec)
                    : Math.min(CUT_XFADE_SEC, Math.max(CUT_XFADE_MIN_SEC, leftSec / 4));
                const offset = Math.max(0, leftSec - fade);
                const vgraph = "[0:v]scale=1280:720,setsar=1,format=yuv420p[v0];[1:v]scale=1280:720,setsar=1,format=yuv420p[v1];"
                    + "[v0][v1]xfade=transition=" + trans + ":duration=" + fade + ":offset=" + offset + ",format=yuv420p[v]";
                const aNorm = "[0:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a0];"
                    + "[1:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a1];";
                const graphs = [
                    vgraph + ";" + aNorm + "[a0][a1]acrossfade=d=" + fade + ":c1=tri:c2=tri[a]",
                    vgraph + ";" + aNorm + "[a0][a1]concat=n=2:v=0:a=1[a]",
                ];
                let encoded = false;
                for (const graph of graphs) {
                    try {
                        await ffmpeg.exec([
                            "-hide_banner", "-y", "-i", aName, "-i", bName,
                            "-filter_complex", graph,
                            "-map", "[v]", "-map", "[a]",
                        ].concat(h264EncodeArgs("aac"), [outName]));
                        encoded = true;
                        break;
                    } catch (audioErr) {
                        console.debug("Cut: xfade audio pass failed", audioErr);
                        try { await ffmpeg.deleteFile(outName); } catch (delErr) {
                            console.debug("Cut: xfade out cleanup", delErr);
                        }
                    }
                }
                if (!encoded)
                    return { success: false, error: "xfade audio failed" };
                const out = await ffmpeg.readFile(outName);
                const url = URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" }));
                noteTemp(url);
                return { success: true, url: url };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
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
            const black = await stillVideoAsync(blackPngUrl(), hold, onProgress);
            if (!black.success) return concatPinned(api, [leftUrl, rightUrl], onProgress);
            const mid = await concatPinned(api, [leftUrl, black.url], onProgress);
            if (!mid.success) return mid;
            return concatPinned(api, [mid.url, rightUrl], onProgress);
        }
        if (xfadeName(k)) {
            const faded = await xfadeAsync(leftUrl, rightUrl, k, onProgress);
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

    async function holdClipStillAsync(c, onProgress) {
        const look = c.card && c.card.text ? textStyle(c.card.style) : null;
        const png = look ? cardPngUrl(c.card.text, look) : blackPngUrl();
        const still = await stillVideoAsync(png, clipHoldSeconds(c), onProgress, look && look.fadeSec);
        if (!still.success)
            return { error: still.error || "Hold failed." };
        return { url: still.url, source: "" };
    }

    async function prepareWindowsAsync(c, index, total, onProgress) {
        const label = c.label || c.fileName || ("clip " + (index + 1));
        if (c.hold || !c.url) {
            onProgress?.(Math.round((index / Math.max(total, 1)) * 40), "Preparing " + label + "…");
            return holdClipStillAsync(c, onProgress);
        }
        onProgress?.(Math.round((index / Math.max(total, 1)) * 40), "Preparing " + label + "…");
        const windows = Array.isArray(c.windows) && c.windows.length > 0
            ? c.windows
            : [{ start: Number(c.markIn) || 0, end: Number(c.markOut) || 0 }];
        const urls = [];
        for (const w of windows) {
            const start = Number(w.start) || 0;
            const end = Number(w.end) || 0;
            const duration = Number(c.duration) || 0;
            const needTrim = duration <= 0 || start > 0.05 || (end > 0 && end < duration - 0.05);
            if (!needTrim) {
                urls.push(c.url);
                continue;
            }
            const trimmed = await cut.trimRangeAsync(c.url, start, end, onProgress);
            if (!trimmed.success)
                return { error: label + ": " + (trimmed.error || "trim failed") };
            urls.push(trimmed.url);
        }
        if (urls.length === 1)
            return { url: urls[0], source: c.url };
        const cat = await concatPinned(window.PageToMovieFfmpeg, urls, onProgress);
        if (!cat.success)
            return { error: label + ": " + (cat.error || "range join failed") };
        return { url: cat.url, source: c.url };
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
            filter: audio.filter || "",
            fallbackFilter: audio.fallbackFilter || "",
        };
    }

    async function placeMusicAsync(api, spec, onProgress) {
        const start = spec.start;
        const inn = spec.markIn;
        const outt = spec.markOut;
        const needsPlace = start > 0.02 || inn > 0.02 || outt > inn + 0.02;
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
                if (outt > inn + 0.02)
                    args.push("-t", String(Math.max(0.3, outt - inn)));
                const delayMs = Math.round(start * 1000);
                if (delayMs > 0)
                    args.push("-af", "adelay=" + delayMs + ":all=1");
                args.push("-c:a", "aac", "-b:a", "192k", outName);
                await ffmpeg.exec(args);
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
        const hold = spec && spec.markOut > spec.markIn ? spec.markOut - spec.markIn : 0;
        const volume = spec && Number.isFinite(spec.volume) ? spec.volume : 1;
        const fadeIn = spec && spec.fadeIn > 0 ? spec.fadeIn : 0;
        const fadeOut = spec && spec.fadeOut > 0 ? spec.fadeOut : 0;
        const start = spec && spec.start > 0 ? spec.start : 0;
        let chain = "volume=" + (Math.round(volume * 100) / 100);
        if (fadeIn > 0.001)
            chain += ",afade=t=in:st=" + start + ":d=" + fadeIn;
        if (fadeOut > 0.001) {
            const outAt = hold > 0.05 ? Math.max(start, start + hold - fadeOut) : start;
            chain += ",afade=t=out:st=" + outAt + ":d=" + fadeOut;
        }
        return {
            withVo: (spec && spec.filter) || ("[1:a]" + chain + ",apad[bg];[0:a][bg]amix=inputs=2:duration=first:dropout_transition=0[a]"),
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
            const args = [
                "-hide_banner", "-y", "-i", inVideo, "-i", inMusic,
                "-filter_complex", filters.withVo,
                "-map", "0:v", "-map", "[a]",
            ];
            if (durationSec > 0.05)
                args.push("-t", String(durationSec));
            args.push.apply(args, h264EncodeArgs("aac"));
            args.push(outName);
            try {
                await ffmpeg.exec(args);
            } catch (noVidAudio) {
                console.debug("Cut: mix video has no audio", noVidAudio);
                try { await ffmpeg.deleteFile(outName); } catch (delErr) {
                    console.debug("Cut: mix out cleanup", delErr);
                }
                const fallback = [
                    "-hide_banner", "-y", "-i", inVideo, "-i", inMusic,
                    "-filter_complex", filters.musicOnly,
                    "-map", "0:v", "-map", "[a]",
                ];
                if (durationSec > 0.05)
                    fallback.push("-t", String(durationSec));
                fallback.push.apply(fallback, h264EncodeArgs("aac"));
                fallback.push(outName);
                await ffmpeg.exec(fallback);
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

    async function mixOptionalAudio(api, videoUrl, audio, onProgress) {
        const spec = musicSpec(audio);
        if (!spec)
            return { success: true, url: videoUrl };
        const placed = await placeMusicAsync(api, spec, onProgress);
        if (!placed.success)
            return placed;
        return withPinnedUrls([videoUrl, placed.url], async function () {
            onProgress?.(80, "Mixing audio…");
            return mixMovieAudioAsync(api, videoUrl, placed.url, onProgress, spec);
        });
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
        if (cut._fallbackFiles) {
            const hit = cut._fallbackFiles.find(function (f) { return f.name === relativePath; });
            if (!hit) throw new Error("Clip is missing: " + relativePath);
            return hit;
        }
        if (!cut._root) throw new Error("No folder connected.");
        const fh = await fileHandleAt(cut._root, relativePath, false);
        return await fh.getFile();
    };

    cut.writeTextFileAsync = async function (relativePath, text) {
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
    cut.trimRangeAsync = async function (url, startSec, endSec, onProgress) {
        const api = window.PageToMovieFfmpeg;
        if (!api) return { success: false, error: "ffmpeg helper missing" };
        if (!url) return { success: false, error: "No URL" };
        return api._runExclusiveAsync(async function () {
            const load = await api.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error };
            const ffmpeg = api._ffmpeg;
            const seq = ++cut._trimSeq;
            const inName = "cut_in_" + seq + ".mp4";
            const outName = "cut_out_" + seq + ".mp4";
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
                    await ffmpeg.exec(buildTrimArgs(inName, outName, window.start, window.keep, false));
                } catch (audioErr) {
                    console.debug("Cut: trim native audio missing, pad silence", audioErr);
                    await ffmpeg.exec(buildTrimArgs(inName, outName, window.start, window.keep, true));
                }
                const out = await ffmpeg.readFile(outName);
                const blob = new Blob([out.buffer], { type: "video/mp4" });
                const urlOut = URL.createObjectURL(blob);
                noteTemp(urlOut);
                return { success: true, url: urlOut };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
                await deleteMemfs(ffmpeg, inName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    };

    function normalizeComposePlan(raw, jit) {
        if (Array.isArray(raw))
            return { clips: raw, scenes: [], joins: [], jit: !!jit };
        const plan = raw || {};
        return {
            clips: plan.clips || [],
            scenes: plan.scenes || [],
            joins: plan.joins || [],
            reuseMovieUrl: plan.reuseMovieUrl || "",
            reusePictureUrl: plan.reusePictureUrl || "",
            jit: !!jit,
        };
    }

    function emptyComposeResult(url, pictureUrl) {
        return {
            success: true,
            url: url,
            pictureUrl: pictureUrl || url,
            stitched: false,
            owned: false,
            scenes: [],
            joins: [],
            rebuiltScenes: [],
            rebuiltJoins: [],
        };
    }

    async function composeSceneClipsAsync(clips, scene, onProgress) {
        const api = window.PageToMovieFfmpeg;
        const first = Math.max(0, Number(scene.first) || 0);
        const count = Math.max(0, Number(scene.count) || 0);
        const slice = clips.slice(first, first + count);
        if (slice.length === 0)
            return { success: false, error: "Empty scene." };
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
                    cardPngUrl(c.card.text, look), c.card.seconds || 2, onProgress, look.fadeSec);
                if (!card.success)
                    return { success: false, error: card.error || "Card failed." };
                if (!acc)
                    acc = card.url;
                else {
                    const joined = await joinPairAsync(api, acc, card.url, pendingJoin, onProgress, pendingHold);
                    if (!joined.success) return joined;
                    acc = joined.url;
                }
                pendingJoin = "dip";
                pendingHold = 0;
            }
            const one = isHold
                ? await holdClipStillAsync(c, onProgress)
                : await prepareWindowsAsync(c, first + i, clips.length, onProgress);
            if (one.error)
                return { success: false, error: one.error };
            if (c.texts && c.texts.length > 0) {
                const over = await overlayTextsAsync(one.url, c.texts, onProgress);
                if (!over.success)
                    return { success: false, error: over.error || "Text overlay failed." };
                one.url = over.url;
            }
            if (!acc)
                acc = one.url;
            else {
                const joined = await joinPairAsync(api, acc, one.url, pendingJoin, onProgress, pendingHold);
                if (!joined.success) return joined;
                acc = joined.url;
            }
            pendingJoin = "cut";
            pendingHold = 0;
        }
        return { success: true, url: acc };
    }

    async function trimBodyAsync(url, seconds, inFade, outFade, onProgress) {
        const start = Math.max(0, Number(inFade) || 0);
        const total = Number(seconds) || 0;
        const end = Math.max(start + 0.1, total - (Number(outFade) || 0));
        if (start <= 0.05 && (total <= 0 || total - end <= 0.05))
            return { success: true, url: url };
        return cut.trimRangeAsync(url, start, end, onProgress);
    }

    async function ensureJoinUrlAsync(join, left, right, onProgress) {
        if (!join || !join.encodes)
            return { success: true, url: "" };
        if (join.url)
            return { success: true, url: join.url, cached: true };
        const kind = String(join.kind || "cut").toLowerCase();
        if (kind === "cuttoblack") {
            const hold = Math.max(0.3, Number(join.hold) || CUT_TO_BLACK_HOLD_SEC);
            const black = await stillVideoAsync(blackPngUrl(), hold, onProgress);
            if (!black.success) return black;
            join.url = black.url;
            return { success: true, url: black.url };
        }
        if (!xfadeName(kind))
            return { success: true, url: "" };
        const fade = Math.max(CUT_XFADE_MIN_SEC, Number(join.fade) || CUT_XFADE_SEC);
        const leftSec = Number(left.seconds) || 0;
        const tailStart = Math.max(0, leftSec - fade);
        const leftTail = await cut.trimRangeAsync(left.url, tailStart, leftSec > 0 ? leftSec : tailStart + fade, onProgress);
        if (!leftTail.success) return leftTail;
        const rightHead = await cut.trimRangeAsync(right.url, 0, fade, onProgress);
        if (!rightHead.success) return rightHead;
        const faded = await xfadeAsync(leftTail.url, rightHead.url, kind, onProgress, fade);
        if (!faded.success) return faded;
        join.url = faded.url;
        return faded;
    }

    async function stitchScenesAsync(api, sceneUrls, joins, onProgress) {
        const pieces = [];
        for (let i = 0; i < sceneUrls.length; i++) {
            const join = i < joins.length ? joins[i] : null;
            const prev = i > 0 ? joins[i - 1] : null;
            const inFade = prev && xfadeName(prev.kind) ? Number(prev.fade) || 0 : 0;
            const outFade = join && xfadeName(join.kind) ? Number(join.fade) || 0 : 0;
            const body = await trimBodyAsync(sceneUrls[i].url, sceneUrls[i].seconds, inFade, outFade, onProgress);
            if (!body.success) return body;
            pieces.push(body.url);
            if (!join || !join.encodes)
                continue;
            const next = sceneUrls[i + 1];
            if (!next)
                continue;
            const made = await ensureJoinUrlAsync(join, sceneUrls[i], next, onProgress);
            if (!made.success) return made;
            if (made.url)
                pieces.push(made.url);
        }
        onProgress?.(55, "Combining clips…");
        return concatPinned(api, pieces, onProgress);
    }

    async function composeWorkAsync(clipsOrPlan, audioUrl, dotNetRef, jit) {
        cut._aborted = false;
        const onProgress = asProgress(dotNetRef);
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
                const rebuiltScenes = [];
                const rebuiltJoins = [];
                const sceneUrls = [];
                const scenes = plan.scenes.length > 0
                    ? plan.scenes
                    : [{ scene: 1, first: 0, count: clips.length, seconds: 0, url: "" }];

                for (let i = 0; i < scenes.length; i++) {
                    if (composeStopped())
                        return { success: false, error: "Stopped." };
                    const scene = scenes[i];
                    if (scene.url) {
                        sceneUrls.push({ id: scene.scene, url: scene.url, seconds: scene.seconds });
                        continue;
                    }
                    onProgress?.(Math.round((i / Math.max(scenes.length, 1)) * 40), "Preparing scene…");
                    const built = await composeSceneClipsAsync(clips, scene, onProgress);
                    if (!built.success)
                        return built;
                    sceneUrls.push({ id: scene.scene, url: built.url, seconds: scene.seconds });
                    rebuiltScenes.push(scene.scene);
                }

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
                let picture;
                const joinsDirty = plan.joins.some(function (j) { return j && j.encodes && !j.url; });
                if (plan.reusePictureUrl && rebuiltScenes.length === 0 && !joinsDirty) {
                    picture = { success: true, url: plan.reusePictureUrl };
                } else {
                    picture = await stitchScenesAsync(api, sceneUrls, plan.joins, onProgress);
                    if (!picture.success) return picture;
                    plan.joins.forEach(function (j) {
                        if (j && j.encodes && j.url && !cachedJoins[j.from])
                            rebuiltJoins.push(j.from);
                    });
                }

                const mixed = await mixOptionalAudio(api, picture.url, audioUrl, onProgress);
                if (!mixed.success) return mixed;
                noteResult(mixed);
                onProgress?.(100, "Ready");
                if (plan.jit)
                    emitPrefix(dotNetRef, mixed.url, clips.length);
                return {
                    success: true,
                    url: mixed.url,
                    pictureUrl: picture.url,
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
