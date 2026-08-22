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
    };

    function messageOf(err, fallback) {
        return err?.message || fallback;
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
            dir = await dir.getDirectoryHandle(part, { create: false });
        return dir.getFileHandle(parts.at(-1), { create: !!create });
    }

    function asProgress(dotNetRef) {
        if (!dotNetRef) return undefined;
        return function (pct, msg) {
            try {
                dotNetRef.invokeMethodAsync("Report", Math.round(pct || 0), msg || "");
            } catch (err) {
                console.debug("Cut: progress sink gone", err);
            }
        };
    }

    async function collectMediaFile(handle, name, path, files) {
        if (handle.kind !== "file") return;
            const keep = /\.mp4$/i.test(name)
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

    function buildTrimArgs(inName, outName, start, keep) {
        const args = ["-hide_banner", "-y"];
        if (start > 0.001) args.push("-ss", String(start));
        args.push("-i", inName, "-t", String(keep),
            "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "+faststart",
            outName);
        return args;
    }

    function xfadeName(kind) {
        const k = String(kind || "cut").toLowerCase();
        if (k === "dissolve") return "fade";
        if (k === "fadewhite") return "fadewhite";
        if (k === "dip" || k === "fadein" || k === "fadeout") return "fadeblack";
        return "";
    }

    function cardPngUrl(text) {
        const canvas = document.createElement("canvas");
        canvas.width = 1280;
        canvas.height = 720;
        const ctx = canvas.getContext("2d");
        ctx.fillStyle = "#000000";
        ctx.fillRect(0, 0, 1280, 720);
        ctx.fillStyle = "#f1f3f7";
        ctx.font = "48px sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText(String(text || "Scene"), 640, 360, 1100);
        return canvas.toDataURL("image/png");
    }

    async function stillVideoAsync(pngUrl, seconds, onProgress) {
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
                const data = await api._safeFetchFile(pngUrl);
                await ffmpeg.writeFile(inName, data);
                await ffmpeg.exec([
                    "-hide_banner", "-y", "-loop", "1", "-i", inName, "-t", String(hold),
                    "-vf", "scale=1280:720,setsar=1",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                    "-an", "-movflags", "+faststart",
                    outName,
                ]);
                const out = await ffmpeg.readFile(outName);
                return { success: true, url: URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" })) };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
                await deleteMemfs(ffmpeg, inName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    }

    async function xfadeAsync(leftUrl, rightUrl, kind, onProgress) {
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
                await ffmpeg.writeFile(aName, await api._safeFetchFile(leftUrl));
                await ffmpeg.writeFile(bName, await api._safeFetchFile(rightUrl));
                const probe = await api._probeDurationMemfsAsync(aName);
                const leftSec = probe.success && probe.seconds > 0 ? probe.seconds : 1;
                const fade = Math.min(0.5, Math.max(0.2, leftSec / 4));
                const offset = Math.max(0, leftSec - fade);
                const graph = "[0:v]scale=1280:720,setsar=1[v0];[1:v]scale=1280:720,setsar=1[v1];"
                    + "[v0][v1]xfade=transition=" + trans + ":duration=" + fade + ":offset=" + offset + "[v]";
                await ffmpeg.exec([
                    "-hide_banner", "-y", "-i", aName, "-i", bName,
                    "-filter_complex", graph, "-map", "[v]", "-an",
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
                    "-movflags", "+faststart",
                    outName,
                ]);
                const out = await ffmpeg.readFile(outName);
                return { success: true, url: URL.createObjectURL(new Blob([out.buffer], { type: "video/mp4" })) };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
                await deleteMemfs(ffmpeg, aName);
                await deleteMemfs(ffmpeg, bName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    }

    async function joinPairAsync(api, leftUrl, rightUrl, kind, onProgress) {
        const k = String(kind || "cut").toLowerCase();
        if (k === "cuttoblack") {
            const hold = await stillVideoAsync(cardPngUrl(""), 0.4, onProgress);
            if (!hold.success) return api.concatVideosAsync([leftUrl, rightUrl], onProgress);
            const mid = await api.concatVideosAsync([leftUrl, hold.url], onProgress);
            if (!mid.success) return mid;
            return api.concatVideosAsync([mid.url, rightUrl], onProgress);
        }
        if (xfadeName(k)) {
            const faded = await xfadeAsync(leftUrl, rightUrl, k, onProgress);
            if (faded.success) return faded;
        }
        return api.concatVideosAsync([leftUrl, rightUrl], onProgress);
    }

    async function prepareWindowsAsync(c, index, total, onProgress) {
        const label = c.label || c.fileName || ("clip " + (index + 1));
        if (!c.url)
            return { error: "Selected take file is missing: " + label };
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
        const cat = await window.PageToMovieFfmpeg.concatVideosAsync(urls, onProgress);
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

    async function mixOptionalAudio(api, videoUrl, audioUrl, onProgress) {
        if (!audioUrl)
            return { success: true, url: videoUrl };
        onProgress?.(80, "Mixing audio…");
        let mixed = await api.mixSceneAudioAsync(videoUrl, audioUrl, 22, onProgress);
        if (!mixed.success)
            mixed = await api.replaceVideoAudioAsync(videoUrl, audioUrl, onProgress);
        return mixed;
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
        if (typeof url === "string" && url.startsWith("blob:")) {
            try {
                URL.revokeObjectURL(url);
            } catch (err) {
                console.debug("Cut: revoke skipped", err);
            }
        }
    };

    cut.readMediaDuration = function (el) {
        const d = el?.duration;
        return (typeof d === "number" && Number.isFinite(d) && d > 0) ? d : 0;
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
                const data = await api._safeFetchFile(url);
                await ffmpeg.writeFile(inName, data);
                onProgress?.(30, "Probing duration…");
                const probe = await api._probeDurationMemfsAsync(inName);
                const total = probe.success && probe.seconds > 0 ? probe.seconds : 0;
                const window = clampTrimWindow(startSec, endSec, total);
                onProgress?.(55, "Trimming…");
                await ffmpeg.exec(buildTrimArgs(inName, outName, window.start, window.keep));
                const out = await ffmpeg.readFile(outName);
                const blob = new Blob([out.buffer], { type: "video/mp4" });
                return { success: true, url: URL.createObjectURL(blob) };
            } catch (err) {
                return { success: false, error: messageOf(err, String(err)) };
            } finally {
                await deleteMemfs(ffmpeg, inName);
                await deleteMemfs(ffmpeg, outName);
            }
        });
    };

    cut.composeMovieAsync = async function (clips, audioUrl, dotNetRef) {
        const onProgress = asProgress(dotNetRef);
        const api = window.PageToMovieFfmpeg;
        if (!api) return { success: false, error: "ffmpeg helper missing" };
        if (!clips || clips.length === 0)
            return { success: false, error: "No clips to export." };

        const items = [];
        const sourceUrls = [];
        for (let i = 0; i < clips.length; i++) {
            const c = clips[i];
            if (c.card?.text) {
                const card = await stillVideoAsync(cardPngUrl(c.card.text), c.card.seconds || 2, onProgress);
                if (!card.success)
                    return { success: false, error: card.error || "Card failed." };
                items.push({ url: card.url, joinOut: "dip" });
            }
            const one = await prepareWindowsAsync(c, i, clips.length, onProgress);
            if (one.error)
                return { success: false, error: one.error };
            sourceUrls.push(one.source);
            items.push({ url: one.url, joinOut: c.joinOut || "cut" });
        }

        onProgress?.(55, "Combining clips…");
        let acc = items[0].url;
        for (let i = 1; i < items.length; i++) {
            const joined = await joinPairAsync(api, acc, items[i].url, items[i - 1].joinOut, onProgress);
            if (!joined.success) return joined;
            acc = joined.url;
        }

        const mixed = await mixOptionalAudio(api, acc, audioUrl, onProgress);
        if (!mixed.success) return mixed;

        onProgress?.(100, "Ready");
        return { success: true, url: mixed.url, owned: !sourceUrls.includes(mixed.url) };
    };

    cut.exportMovieAsync = async function (clips, audioUrl, dotNetRef) {
        const r = await cut.composeMovieAsync(clips, audioUrl, dotNetRef);
        if (!r.success) return r;
        cut.downloadUrlAs(r.url, "movie.mp4");
        return r;
    };

    cut.previewMovieAsync = async function (clips, audioUrl, dotNetRef) {
        const r = await cut.composeMovieAsync(clips, audioUrl, dotNetRef);
        if (!r.success) return r;
        if (cut._ownedMovieUrl && cut._ownedMovieUrl !== r.url)
            cut.revokeBlobUrl(cut._ownedMovieUrl);
        cut._ownedMovieUrl = r.owned ? r.url : null;
        return r;
    };

    window.PageToMovieCut = cut;
})();
