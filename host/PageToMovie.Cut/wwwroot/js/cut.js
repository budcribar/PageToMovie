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
        const isMp4 = /\.mp4$/i.test(name);
        const isPointer = /\.current\.json$/i.test(name);
        if (!isMp4 && !isPointer) return;
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
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "+faststart",
            outName);
        return args;
    }

    async function deleteMemfs(ffmpeg, name) {
        try {
            await ffmpeg.deleteFile(name);
        } catch (err) {
            console.debug("Cut: memfs cleanup", name, err);
        }
    }

    async function prepareOneClip(c, index, total, onProgress) {
        const label = c.label || c.fileName || ("clip " + (index + 1));
        if (!c.url)
            return { error: "Selected take file is missing: " + label };
        onProgress?.(Math.round((index / total) * 50), "Preparing " + label + "…");
        const duration = Number(c.duration) || 0;
        const markIn = Number(c.markIn) || 0;
        const markOut = Number(c.markOut) || 0;
        const needTrim = duration > 0 && (markIn > 0.05 || (markOut > 0 && markOut < duration - 0.05));
        if (!needTrim)
            return { url: c.url, source: c.url };
        const trimmed = await cut.trimRangeAsync(c.url, markIn, markOut, onProgress);
        if (!trimmed.success)
            return { error: label + ": " + (trimmed.error || "trim failed") };
        return { url: trimmed.url, source: c.url };
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

        const prepared = [];
        const sourceUrls = [];
        for (let i = 0; i < clips.length; i++) {
            const one = await prepareOneClip(clips[i], i, clips.length, onProgress);
            if (one.error)
                return { success: false, error: one.error };
            sourceUrls.push(one.source);
            prepared.push(one.url);
        }

        onProgress?.(55, "Combining clips…");
        const concat = await api.concatVideosAsync(prepared, onProgress);
        if (!concat.success) return concat;

        const mixed = await mixOptionalAudio(api, concat.url, audioUrl, onProgress);
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
