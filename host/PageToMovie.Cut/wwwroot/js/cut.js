/**
 * Standalone Cut — local folder + browser compose.
 * ffmpeg load / concat / probe / mix stay in PageToMovieFfmpeg (copied from Web).
 * Ops go through that helper's exclusive queue.
 */
window.PageToMovieCut = {
    _root: null,
    _fallbackFiles: null,
    _trimSeq: 0,

    supportsDirectoryPicker: function () {
        return { supported: typeof window.showDirectoryPicker === "function" };
    },

    pickFolderAsync: async function () {
        if (typeof window.showDirectoryPicker !== "function") {
            return { success: false, error: "This browser cannot pick a folder. Use Chrome or Edge, or choose MP4 files instead." };
        }
        try {
            this._root = await window.showDirectoryPicker({ mode: "readwrite" });
            this._fallbackFiles = null;
            return { success: true, folderName: this._root.name };
        } catch (err) {
            if (err && err.name === "AbortError")
                return { success: false, error: "Folder selection cancelled." };
            return { success: false, error: (err && err.message) || "Folder selection failed." };
        }
    },

    pickMp4FilesAsync: function () {
        const self = this;
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
                self._root = null;
                self._fallbackFiles = files;
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
    },

    listMediaFilesAsync: async function () {
        if (this._fallbackFiles) {
            return {
                success: true,
                files: this._fallbackFiles.map(function (f) {
                    return { fileName: f.name, relativePath: f.name, sizeBytes: f.size };
                }),
            };
        }
        if (!this._root)
            return { success: false, error: "No folder connected.", files: [] };
        try {
            const files = [];
            await this._walkDirAsync(this._root, "", 0, files);
            return { success: true, files: files };
        } catch (err) {
            return { success: false, error: (err && err.message) || "Could not read the folder.", files: [] };
        }
    },

    _walkDirAsync: async function (dir, rel, depth, files) {
        if (depth > 8) return;
        for await (const [name, handle] of dir.entries()) {
            if (!name || name.startsWith(".")) continue;
            const path = rel ? (rel + "/" + name) : name;
            if (handle.kind === "directory") {
                await this._walkDirAsync(handle, path, depth + 1, files);
                continue;
            }
            if (handle.kind !== "file") continue;
            const isMp4 = /\.mp4$/i.test(name);
            const isPointer = /\.current\.json$/i.test(name);
            if (!isMp4 && !isPointer) continue;
            try {
                const file = await handle.getFile();
                const entry = {
                    fileName: name,
                    relativePath: path,
                    sizeBytes: file ? file.size : 0,
                };
                if (isPointer && file)
                    entry.text = await file.text();
                files.push(entry);
            } catch (_) { /* skip unreadable */ }
        }
    },

    _resolveFileAsync: async function (relativePath) {
        if (this._fallbackFiles) {
            const hit = this._fallbackFiles.find(function (f) { return f.name === relativePath; });
            if (!hit) throw new Error("Clip is missing: " + relativePath);
            return hit;
        }
        if (!this._root) throw new Error("No folder connected.");
        const parts = String(relativePath || "").replaceAll("\\", "/").split("/").filter(Boolean);
        if (parts.length === 0) throw new Error("Clip is missing.");
        let dir = this._root;
        for (let i = 0; i < parts.length - 1; i++)
            dir = await dir.getDirectoryHandle(parts[i], { create: false });
        const fh = await dir.getFileHandle(parts[parts.length - 1], { create: false });
        return await fh.getFile();
    },

    writeTextFileAsync: async function (relativePath, text) {
        if (this._fallbackFiles)
            return { success: false, error: "Folder write needs Pick folder (not loose files)." };
        if (!this._root)
            return { success: false, error: "No folder connected." };
        try {
            const parts = String(relativePath || "").replaceAll("\\", "/").split("/").filter(Boolean);
            if (parts.length === 0)
                return { success: false, error: "Missing path." };
            let dir = this._root;
            for (let i = 0; i < parts.length - 1; i++)
                dir = await dir.getDirectoryHandle(parts[i], { create: false });
            const fh = await dir.getFileHandle(parts[parts.length - 1], { create: true });
            const w = await fh.createWritable();
            await w.write(String(text ?? ""));
            await w.close();
            return { success: true };
        } catch (err) {
            return { success: false, error: (err && err.message) || "Could not save current take." };
        }
    },

    getFileBlobUrlAsync: async function (relativePath) {
        try {
            const file = await this._resolveFileAsync(relativePath);
            if (!file || file.size <= 0)
                return { success: false, error: "Clip is missing or empty: " + relativePath };
            const url = URL.createObjectURL(file);
            return { success: true, url: url, sizeBytes: file.size };
        } catch (err) {
            return { success: false, error: (err && err.message) || ("Clip is missing: " + relativePath) };
        }
    },

    createBlobUrlFromStream: async function (streamRef, mime) {
        try {
            const buf = await streamRef.arrayBuffer();
            const blob = new Blob([buf], { type: mime || "application/octet-stream" });
            return { success: true, url: URL.createObjectURL(blob) };
        } catch (err) {
            return { success: false, error: (err && err.message) || "Could not read the file." };
        }
    },

    revokeBlobUrl: function (url) {
        if (typeof url === "string" && url.startsWith("blob:")) {
            try { URL.revokeObjectURL(url); } catch (_) { /* */ }
        }
    },

    readMediaDuration: function (el) {
        if (!el) return 0;
        const d = el.duration;
        return (typeof d === "number" && isFinite(d) && d > 0) ? d : 0;
    },

    downloadUrlAs: function (url, fileName) {
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName || "movie.mp4";
        document.body.appendChild(a);
        a.click();
        a.remove();
        return { success: true };
    },

    _asProgress: function (dotNetRef) {
        if (!dotNetRef) return null;
        return function (pct, msg) {
            try { dotNetRef.invokeMethodAsync("Report", Math.round(pct || 0), msg || ""); } catch (_) { /* disposed */ }
        };
    },

    /**
     * Trim [startSec, endSec) using the same encode args as PageToMovieFfmpeg.encodeSliceAsync /
     * _trimKeepSecondsAsync. Serialized on the shared ffmpeg queue.
     */
    trimRangeAsync: async function (url, startSec, endSec, onProgress) {
        const api = window.PageToMovieFfmpeg;
        if (!api) return { success: false, error: "ffmpeg helper missing" };
        if (!url) return { success: false, error: "No URL" };
        const self = this;
        return api._runExclusiveAsync(async function () {
            const load = await api.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error };
            const ffmpeg = api._ffmpeg;
            const seq = ++self._trimSeq;
            const inName = "cut_in_" + seq + ".mp4";
            const outName = "cut_out_" + seq + ".mp4";
            try {
                if (typeof onProgress === "function") onProgress(12, "Loading clip…");
                const data = await api._safeFetchFile(url);
                await ffmpeg.writeFile(inName, data);
                if (typeof onProgress === "function") onProgress(30, "Probing duration…");
                const probe = await api._probeDurationMemfsAsync(inName);
                const total = probe.success && probe.seconds > 0 ? probe.seconds : 0;
                let start = Number(startSec) || 0;
                let end = Number(endSec);
                if (!(end > 0) && total > 0) end = total;
                if (start < 0) start = 0;
                if (total > 0 && start > total) start = total;
                if (total > 0 && end > total) end = total;
                if (end <= start) end = start + 0.1;
                const keep = Math.max(0.1, end - start);
                if (typeof onProgress === "function") onProgress(55, "Trimming…");
                const args = ["-hide_banner", "-y"];
                if (start > 0.001) args.push("-ss", String(start));
                args.push("-i", inName, "-t", String(keep),
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
                    "-c:a", "aac", "-b:a", "128k",
                    "-movflags", "+faststart",
                    outName);
                await ffmpeg.exec(args);
                const out = await ffmpeg.readFile(outName);
                const blob = new Blob([out.buffer], { type: "video/mp4" });
                return { success: true, url: URL.createObjectURL(blob) };
            } catch (err) {
                return { success: false, error: (err && err.message) || String(err) };
            } finally {
                try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                try { await ffmpeg.deleteFile(outName); } catch (_) { /* */ }
            }
        });
    },

    exportMovieAsync: async function (clips, audioUrl, dotNetRef) {
        const onProgress = this._asProgress(dotNetRef);
        const api = window.PageToMovieFfmpeg;
        if (!api) return { success: false, error: "ffmpeg helper missing" };
        if (!clips || clips.length === 0)
            return { success: false, error: "No clips to export." };

        const prepared = [];
        for (let i = 0; i < clips.length; i++) {
            const c = clips[i];
            const label = c.label || c.fileName || ("clip " + (i + 1));
            if (!c.url)
                return { success: false, error: "Clip is missing: " + label };
            if (typeof onProgress === "function")
                onProgress(Math.round((i / clips.length) * 50), "Preparing " + label + "…");
            const duration = Number(c.duration) || 0;
            const markIn = Number(c.markIn) || 0;
            const markOut = Number(c.markOut) || 0;
            const needTrim = duration > 0 && (markIn > 0.05 || (markOut > 0 && markOut < duration - 0.05));
            if (needTrim) {
                const trimmed = await this.trimRangeAsync(c.url, markIn, markOut, onProgress);
                if (!trimmed.success)
                    return { success: false, error: label + ": " + (trimmed.error || "trim failed") };
                prepared.push(trimmed.url);
            } else {
                prepared.push(c.url);
            }
        }

        if (typeof onProgress === "function") onProgress(55, "Combining clips…");
        const concat = await api.concatVideosAsync(prepared, onProgress);
        if (!concat.success) return concat;
        let outUrl = concat.url;

        if (audioUrl) {
            if (typeof onProgress === "function") onProgress(80, "Mixing audio…");
            let mixed = await api.mixSceneAudioAsync(outUrl, audioUrl, 22, onProgress);
            if (!mixed.success)
                mixed = await api.replaceVideoAudioAsync(outUrl, audioUrl, onProgress);
            if (!mixed.success) return mixed;
            outUrl = mixed.url;
        }

        if (typeof onProgress === "function") onProgress(96, "Saving movie.mp4…");
        this.downloadUrlAs(outUrl, "movie.mp4");
        if (typeof onProgress === "function") onProgress(100, "Done");
        return { success: true, url: outUrl };
    },
};
