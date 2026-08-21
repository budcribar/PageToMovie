/**
 * Client media folder: store gen MP4s on the user's disk (File System Access API).
 * Server keeps hashes + metadata only.
 */
window.PageToMovieMedia = {
    _root: null, // FileSystemDirectoryHandle
    _blobUrls: {},

    supportsDirectoryPicker: function () {
        return typeof window.showDirectoryPicker === "function";
    },

    hasFolder: function () {
        return !!this._root;
    },

    folderName: function () {
        return this.getFullPath() || (this._root ? this._root.name : null);
    },

    getFullPath: function () {
        return localStorage.getItem("ptm-media-fullpath") || null;
    },

    setFullPath: function (path) {
        if (path) {
            localStorage.setItem("ptm-media-fullpath", path);
        } else {
            localStorage.removeItem("ptm-media-fullpath");
        }
    },

    /**
     * Status + a short body snippet so a 502 JSON error is visible in LastStatus
     * without Railway logs. Consumes the response body.
     */
    httpErrorText: async function (res) {
        let body = "";
        try {
            body = (await res.text()).replace(/\s+/g, " ").trim().slice(0, 400);
        } catch (_) { /* */ }
        const status = "HTTP " + res.status + (res.statusText ? " " + res.statusText : "");
        return body ? (status + " " + body) : status;
    },

    /**
     * Prompt once for a media root folder (session). Prefer same folder as export.
     */
    connectFolderAsync: async function () {
        if (!this.supportsDirectoryPicker()) {
            return { success: false, error: "This browser does not support folder access (use Chrome or Edge)." };
        }
        try {
            this._root = await window.showDirectoryPicker({ mode: "readwrite" });
            if (window.PageToMovieExport)
                window.PageToMovieExport._directoryHandle = this._root;
            const fullPath = this._resolvePickerFullPath(this._root);
            if (fullPath)
                this.setFullPath(fullPath);
            await this._saveHandleToDbAsync(this._root);
            return { success: true, folderName: this._root.name, fullPath: fullPath || this.getFullPath() || null };
        } catch (err) {
            if (err?.name === "AbortError")
                return { success: false, error: "Folder selection cancelled." };
            return { success: false, error: err?.message || "Folder selection failed." };
        }
    },

    _resolvePickerFullPath: function (handle) {
        try {
            if (typeof handle.path === "string" && handle.path)
                return handle.path;
            if (typeof handle.fullPath === "string" && handle.fullPath)
                return handle.fullPath;
        } catch (e) { if (e) { /* ignore: handle.path/fullPath may throw */ } }
        const prev = this.getFullPath();
        if (!prev) return null;
        const parts = prev.split(/[\\/]/).filter(Boolean);
        const leaf = parts.at(-1) ?? "";
        if (leaf?.toLowerCase() === String(handle.name).toLowerCase())
            return prev;
        try { localStorage.removeItem("ptm-media-fullpath"); } catch (_) { /* ignore */ }
        return null;
    },

    /**
     * IndexedDB-backed persistence of the actual FileSystemDirectoryHandle (structured-cloneable,
     * unlike localStorage which can only hold the folder's name string). This is what makes a real
     * 1-click reconnect possible: showDirectoryPicker() always needs a fresh user gesture and has no
     * way to pre-select a remembered folder, but re-requesting permission on an already-held handle
     * does not re-open the OS folder-browser dialog.
     */
    _dbPromise: null,
    _openDbAsync: function () {
        if (this._dbPromise) return this._dbPromise;
        this._dbPromise = new Promise((resolve, reject) => {
            const req = indexedDB.open("ptm-media", 1);
            req.onupgradeneeded = () => { req.result.createObjectStore("handles"); };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
        return this._dbPromise;
    },
    _saveHandleToDbAsync: async function (handle) {
        try {
            const db = await this._openDbAsync();
            await new Promise((resolve, reject) => {
                const tx = db.transaction("handles", "readwrite");
                tx.objectStore("handles").put(handle, "root");
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        } catch (err) {
            console.warn("Could not persist media folder handle for reconnect:", err);
        }
    },
    _loadHandleFromDbAsync: async function () {
        try {
            const db = await this._openDbAsync();
            return await new Promise((resolve, reject) => {
                const tx = db.transaction("handles", "readonly");
                const req = tx.objectStore("handles").get("root");
                req.onsuccess = () => resolve(req.result || null);
                req.onerror = () => reject(req.error);
            });
        } catch (e) {
            if (e) { /* ignore: IndexedDB unavailable */ }
            return null;
        }
    },

    /**
     * Silent reconnect attempt on page load: no dialog, no user gesture required. Succeeds only if
     * a handle was previously persisted AND the browser still grants readwrite permission on it
     * without asking (permission grants often do not survive a full page reload, in which case this
     * returns reason:"prompt" so the caller can offer a 1-click "Reconnect" button that calls
     * reconnectAsync() from a real click handler).
     */
    tryReconnectAsync: async function () {
        if (this._root) return { success: true, folderName: this._root.name, silent: true };
        const handle = await this._loadHandleFromDbAsync();
        if (!handle) return { success: false, reason: "none" };
        try {
            const perm = await handle.queryPermission({ mode: "readwrite" });
            if (perm === "granted") {
                this._root = handle;
                if (window.PageToMovieExport) window.PageToMovieExport._directoryHandle = handle;
                return { success: true, folderName: handle.name, silent: true };
            }
            return { success: false, reason: perm === "denied" ? "denied" : "prompt", folderName: handle.name };
        } catch (err) {
            return { success: false, reason: "error", error: err.message || String(err) };
        }
    },

    /**
     * Re-grant permission on the previously-chosen folder from a real user gesture (button click).
     * No folder-browser dialog — just a permission re-grant on the same handle.
     */
    reconnectAsync: async function () {
        const handle = await this._loadHandleFromDbAsync();
        if (!handle) return { success: false, error: "No remembered folder to reconnect to" };
        try {
            const perm = await handle.requestPermission({ mode: "readwrite" });
            if (perm !== "granted")
                return { success: false, error: "Permission was not granted" };
            this._root = handle;
            if (window.PageToMovieExport) window.PageToMovieExport._directoryHandle = handle;
            return { success: true, folderName: handle.name };
        } catch (err) {
            return { success: false, error: err.message || "Reconnect failed" };
        }
    },

    /**
     * Split relativePath on "/" and create/resolve each directory segment under this._root, e.g.
     * {root}/{projectId}/assets/video/... — the project-id segment is just part of the string the
     * caller (ClientMediaFolderService) already built; this function has no project-id concept of
     * its own, it just walks whatever path it's given.
     */
    _ensurePathAsync: async function (relativePath) {
        if (!this._root) throw new Error("Media folder not connected");
        const parts = relativePath.replaceAll("\\", "/").split("/").filter(Boolean);
        let dir = this._root;
        for (let i = 0; i < parts.length - 1; i++) {
            dir = await dir.getDirectoryHandle(parts[i], { create: true });
        }
        const fileName = parts[parts.length - 1];
        return { dir, fileName };
    },

    /**
     * If relativePath is a clip video (assets/video/scene_SS_clip_CC.mp4) and a file already
     * exists there, move it to assets/video/history/scene_SS_clip_CC_{timestamp}.mp4 before it
     * gets overwritten, so ClipPromptCompareViewer has a previous version to compare against.
     * Best-effort: any failure here must never block the actual save.
     */
    _archiveClipHistoryAsync: async function (relativePath) {
        try {
            // Leading group tolerates a project-id prefix (e.g. "alice/Buster/assets/video/...")
            // ahead of the clip filename — preserved below so the archived copy lands under the
            // same project's history folder instead of the shared root's.
            const m = /^(.+\/)?assets\/video\/(scene_\d+_clip_\d+)\.mp4$/i.exec(relativePath.replaceAll("\\", "/"));
            if (!m) return;
            const { dir, fileName } = await this._ensurePathAsync(relativePath);
            let existing;
            try { existing = await dir.getFileHandle(fileName, { create: false }); }
            catch (_) { return; } // nothing to archive yet
            const file = await existing.getFile();
            if (!file || file.size < 1024) return;

            const prefix = m[1] || "";
            const historyPath = `${prefix}assets/video/history/${m[2]}_${Date.now()}.mp4`;
            const { dir: histDir, fileName: histName } = await this._ensurePathAsync(historyPath);
            const buf = await file.arrayBuffer();
            const wh = await histDir.getFileHandle(histName, { create: true });
            const w = await wh.createWritable();
            await w.write(buf);
            await w.close();
        } catch (err) {
            console.warn("clip history archive skipped:", err);
        }
    },

    /**
     * If relativePath is a scene music segment (assets/music/scene_SS_seg_NN.wav) and a file already
     * exists there, move it to assets/music/history/scene_SS_seg_NN_{takeId}.wav before it gets
     * overwritten. Unlike _archiveClipHistoryAsync, the archive stamp is the caller-supplied takeId
     * (one id shared by every segment of the same generation run — see JobSnapshot.MusicTakeId) rather
     * than a fresh Date.now() per call, so a multi-segment take's files archive together under one
     * comparable id instead of scattering across whatever moment each segment happened to finish.
     * Falls back to Date.now() if no takeId is given. Best-effort: failure never blocks the save.
     */
    _archiveMusicSegmentAsync: async function (relativePath, takeId) {
        try {
            const m = /^(.+\/)?assets\/music\/(scene_\d+_seg_\d+)\.wav$/i.exec(relativePath.replaceAll("\\", "/"));
            if (!m) return;
            const { dir, fileName } = await this._ensurePathAsync(relativePath);
            let existing;
            try { existing = await dir.getFileHandle(fileName, { create: false }); }
            catch (_) { return; } // nothing to archive yet
            const file = await existing.getFile();
            if (!file || file.size < 1024) return;

            const prefix = m[1] || "";
            const stamp = takeId || Date.now();
            const historyPath = `${prefix}assets/music/history/${m[2]}_${stamp}.wav`;
            const { dir: histDir, fileName: histName } = await this._ensurePathAsync(historyPath);
            const buf = await file.arrayBuffer();
            const wh = await histDir.getFileHandle(histName, { create: true });
            const w = await wh.createWritable();
            await w.write(buf);
            await w.close();
        } catch (err) {
            console.warn("music segment archive skipped:", err);
        }
    },

    /**
     * Download from same-origin proxy (or any URL, incl. blob:) and write under media folder.
     * Pure I/O — callers that want silence-trim run PageToMovieFfmpeg.analyzeSilenceAsync /
     * encodeSliceAsync themselves first and pass the resulting blob: URL in as `url`.
     * Returns sha256 hex + size.
     * @param {string} url
     * @param {string} relativePath
     * @param {(p:number,msg:string)=>void} [onProgress]
     * @param {string} [takeId] for scene music segments — see _archiveMusicSegmentAsync.
     */
    saveFromUrlAsync: async function (url, relativePath, onProgress, takeId) {
        const denied = await this._ensureRootConnectedAsync();
        if (denied) return denied;
        try {
            await this._archiveClipHistoryAsync(relativePath);
            await this._archiveMusicSegmentAsync(relativePath, takeId);
            const report = (p, m) => typeof onProgress === "function" && onProgress(p, m);
            report(5, "Downloading clip…");
            const res = await fetch(url, { credentials: "same-origin" });
            if (!res.ok) return { success: false, error: "Download failed " + await this.httpErrorText(res) };
            const buf = await res.arrayBuffer();

            report(60, "Hashing…");
            const sha = await this._sha256Hex(buf);
            report(85, "Writing folder…");
            const key = await this._writeFileAndRevokeBlobAsync(relativePath, buf);
            report(100, "Saved");
            return {
                success: true,
                sha256: sha,
                sizeBytes: buf.byteLength,
                relativePath: key,
                folderName: this._root.name,
            };
        } catch (err) {
            console.error("saveFromUrlAsync", err);
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * Fetch a (typically blob:) URL and POST its bytes straight to a server upload endpoint via
     * FormData — used for video-extend continuity uploads (see ClientMediaFolderService /
     * trimTailAsync) so a locally-trimmed clip never has to round-trip through Blazor interop as
     * a byte[] just to reach the server.
     *
     * uploadUrl must already carry the short-lived media token (?mt=) — the same query auth
     * media GET URLs use. This helper cannot send Authorization; a bare /upload POST 403s
     * under RequireLogin (ProjectAccessMiddleware treats the caller as the anonymous default).
     */
    uploadUrlToServerAsync: async function (url, uploadUrl) {
        try {
            const res = await fetch(url, { credentials: "same-origin" });
            if (!res.ok) return { success: false, error: "Fetch failed " + await this.httpErrorText(res) };
            const blob = await res.blob();
            const form = new FormData();
            form.append("video", blob, "upload.mp4");
            const up = await fetch(uploadUrl, { method: "POST", body: form, credentials: "same-origin" });
            if (!up.ok) {
                let text = "";
                try { text = await up.text(); } catch (_) { /* */ }
                return { success: false, error: "Upload failed HTTP " + up.status + " " + text };
            }
            return { success: true };
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * Save a generated asset through the browser's normal download mechanism. This remains a
     * fallback for cases where the File System Access folder is unavailable or a live job event
     * arrives after the page's media-folder listener was attached.
     */
    downloadFromUrlAsync: async function (url, fileName) {
        const res = await fetch(url, { credentials: "same-origin" });
        if (!res.ok) throw new Error("Download failed HTTP " + res.status);
        const blobUrl = URL.createObjectURL(await res.blob());
        try {
            const a = document.createElement("a");
            a.href = blobUrl;
            a.download = fileName || "background-music.wav";
            a.style.display = "none";
            document.body.appendChild(a);
            a.click();
            a.remove();
        } finally {
            window.setTimeout(() => URL.revokeObjectURL(blobUrl), 1000);
        }
    },

    /** Revoke an arbitrary blob: URL (e.g. one handed back by PageToMovieFfmpeg.encodeSliceAsync). */
    revokeUrl: function (url) {
        try { URL.revokeObjectURL(url); } catch (_) { /* */ }
    },

    /** Build a blob: URL from base64 audio/video (TTS speak response). */
    blobUrlFromBase64: function (base64, mime) {
        if (!base64) return null;
        try {
            const bin = atob(base64);
            const bytes = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) bytes[i] = bin.codePointAt(i);
            const blob = new Blob([bytes], { type: mime || "audio/mpeg" });
            return URL.createObjectURL(blob);
        } catch (e) {
            console.error("blobUrlFromBase64 failed", e);
            return null;
        }
    },

    /**
     * Read a project-relative file as a blob: URL for &lt;video&gt; / stitch. Cached per path for
     * the session; pass forceRefresh=true (after a staleness check via statLocalFileAsync
     * decides the cached URL no longer matches what's on disk) to revoke and recreate it.
     */
    getBlobUrlAsync: async function (relativePath, forceRefresh) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const key = relativePath.replaceAll("\\", "/");
            if (!forceRefresh && this._blobUrls[key]) {
                return { success: true, url: this._blobUrls[key] };
            }
            const fh = await this._resolveFileHandleAsync(relativePath);
            if (!fh) return { success: false, error: "Not found in media folder" };
            const file = await fh.getFile();
            if (!file || file.size < 1024)
                return { success: false, error: "File missing or empty" };
            if (this._blobUrls[key]) {
                try { URL.revokeObjectURL(this._blobUrls[key]); } catch (_) { /* */ }
            }
            const url = URL.createObjectURL(file);
            this._blobUrls[key] = url;
            return { success: true, url: url, sizeBytes: file.size };
        } catch (err) {
            return { success: false, error: err.message || "Not found in media folder" };
        }
    },

    /**
     * Shared file-handle resolution for getBytesAsync/getBlobUrlAsync/statLocalFileAsync:
     * exact relative-path match first, falling back to a "scene_XX_clip_YY*" prefix search
     * (newest by mtime) for take-suffixed filenames the caller doesn't know the exact name of.
     * Was duplicated verbatim in two places; centralized so future kinds (audio tracks, etc.)
     * get the same fallback for free instead of a third copy-paste.
     */
    _resolveFileHandleAsync: async function (relativePath) {
        const { dir, fileName } = await this._ensurePathAsync(relativePath);
        try {
            return await dir.getFileHandle(fileName, { create: false });
        } catch (_) {
            return await this._bestPrefixFileHandleAsync(dir, fileName);
        }
    },

    _bestPrefixFileHandleAsync: async function (dir, fileName) {
        const m = fileName.match(/^(scene_\d+_clip_\d+)/i);
        if (!m) return null;
        const prefix = m[1].toLowerCase();
        const ext = (fileName.match(/\.[a-z0-9]+$/i) || [".mp4"])[0].toLowerCase();
        let bestFh = null;
        let bestMtime = 0;
        for await (const entry of dir.values()) {
            const nameLower = entry.name.toLowerCase();
            if (entry.kind !== "file" || !nameLower.startsWith(prefix) || !nameLower.endsWith(ext))
                continue;
            try {
                const f = await entry.getFile();
                if (f && f.size >= 1024 && f.lastModified > bestMtime) {
                    bestMtime = f.lastModified;
                    bestFh = entry;
                }
            } catch (e) { if (e) { /* skip unreadable entry */ } }
        }
        return bestFh;
    },

    /**
     * Replay one server-side renumber onto the local folder: copy bytes from → to, then remove
     * the old entry. Exact-name only (no prefix fallback — a rename must never grab a cousin
     * take file). No-op success when the source is absent or the target already exists, so
     * replaying a manifest entry twice is harmless.
     */
    renameFileAsync: async function (fromPath, toPath) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const { dir: fromDir, fileName: fromName } = await this._ensurePathAsync(fromPath);
            let fromFh;
            try { fromFh = await fromDir.getFileHandle(fromName, { create: false }); }
            catch (_) { return { success: true, skipped: "source missing" }; }
            const { dir: toDir, fileName: toName } = await this._ensurePathAsync(toPath);
            try {
                await toDir.getFileHandle(toName, { create: false });
                return { success: true, skipped: "target exists" };
            } catch (err) { if (err) { /* target free — proceed */ } }
            const file = await fromFh.getFile();
            const buf = await file.arrayBuffer();
            const wh = await toDir.getFileHandle(toName, { create: true });
            const w = await wh.createWritable();
            await w.write(buf);
            await w.close();
            await fromDir.removeEntry(fromName);
            return { success: true };
        } catch (err) {
            return { success: false, error: err.message || "rename failed" };
        }
    },

    /** Delete one file if present (server-side renumber invalidated it, e.g. a scene composite). */
    deleteFileAsync: async function (relativePath) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const { dir, fileName } = await this._ensurePathAsync(relativePath);
            try { await dir.removeEntry(fileName); } catch (_) { /* already gone */ }
            return { success: true };
        } catch (err) {
            return { success: false, error: err.message || "delete failed" };
        }
    },

    /**
     * Read a project-relative file (e.g. assets/video/scene_01_clip_01.mp4) as byte array.
     */
    getBytesAsync: async function (relativePath, minBytes) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const fh = await this._resolveFileHandleAsync(relativePath);
            if (!fh) return { success: false, error: "Not found in media folder" };
            const file = await fh.getFile();
            // Clips used 1KB floor; voice samples can be shorter — allow 1 byte default when minBytes=0.
            const floor = (minBytes === 0 || minBytes) ? minBytes : 1024;
            if (!file || file.size < floor)
                return { success: false, error: "File missing or empty" };
            const buf = await file.arrayBuffer();
            return { success: true, bytes: new Uint8Array(buf), sizeBytes: file.size };
        } catch (err) {
            return { success: false, error: err.message || "Not found in media folder" };
        }
    },

    /**
     * Cheap metadata-only check — no blob URL created, no bytes read. Callers (C#) use this to
     * decide whether a local file is still current before trusting it, without the cost/side
     * effects of getBlobUrlAsync.
     */
    statLocalFileAsync: async function (relativePath) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const fh = await this._resolveFileHandleAsync(relativePath);
            if (!fh) return { success: false, error: "Not found in media folder" };
            const file = await fh.getFile();
            // Only truly empty files count as absent. A 1 KB threshold here reported valid small
            // media (sidecars, tiny images) as missing, so the sync re-downloaded them every visit.
            if (!file || file.size <= 0)
                return { success: false, error: "File missing or empty" };
            return { success: true, sizeBytes: file.size, lastModifiedMs: file.lastModified };
        } catch (err) {
            return { success: false, error: err.message || "Not found in media folder" };
        }
    },

    sha256LocalFileAsync: async function (relativePath) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const fh = await this._resolveFileHandleAsync(relativePath);
            if (!fh) return { success: false, error: "Not found in media folder" };
            const file = await fh.getFile();
            if (!file || file.size <= 0)
                return { success: false, error: "File missing or empty" };
            const buf = await file.arrayBuffer();
            const sha = await this._sha256Hex(buf);
            return { success: true, sha256: sha, sizeBytes: file.size };
        } catch (err) {
            return { success: false, error: err.message || "Not found in media folder" };
        }
    },

    /**
     * List archived previous versions of one clip (newest first), written by
     * _archiveClipHistoryAsync. Each entry's relativePath already includes dirPrefix and can be
     * passed straight to getBlobUrlAsync as-is.
     * @param {string} dirPrefix literal path up to and including "assets/video/history", e.g.
     *   "alice/Buster/assets/video/history" — built by the caller, not assembled in here.
     * @returns {{ success:boolean, entries?: { relativePath:string, timestampMs:number }[], error?:string }}
     */
    listClipHistoryAsync: async function (dirPrefix, scene, clip) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const prefix = `scene_${String(scene).padStart(2, "0")}_clip_${String(clip).padStart(2, "0")}_`;
            const parts = dirPrefix.replaceAll("\\", "/").split("/").filter(Boolean);
            let histDir = this._root;
            for (const part of parts) {
                try { histDir = await histDir.getDirectoryHandle(part, { create: false }); }
                catch (_) { return { success: true, entries: [] }; }
            }

            const entries = [];
            for await (const [name, handle] of histDir.entries()) {
                if (handle.kind !== "file" || !name.startsWith(prefix) || !name.endsWith(".mp4")) continue;
                const ts = Number.parseInt(name.slice(prefix.length, -4), 10);
                if (!Number.isFinite(ts)) continue;
                entries.push({ relativePath: `${dirPrefix}/${name}`, timestampMs: ts });
            }
            entries.sort((a, b) => b.timestampMs - a.timestampMs);
            return { success: true, entries };
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    revokeBlobUrls: function () {
        for (const k of Object.keys(this._blobUrls)) {
            try { URL.revokeObjectURL(this._blobUrls[k]); } catch (_) { /* */ }
        }
        this._blobUrls = {};
    },

    /**
     * Hash a blob: URL (stitched export) for registry + demo.
     */
    hashBlobUrlAsync: async function (blobUrl) {
        try {
            const res = await fetch(blobUrl);
            const buf = await res.arrayBuffer();
            const sha = await this._sha256Hex(buf);
            return { success: true, sha256: sha, sizeBytes: buf.byteLength };
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    saveBlobUrlAsync: async function (blobUrl, relativePath) {
        if (!this._root) {
            const c = await this.connectFolderAsync();
            if (!c.success) return c;
        }
        try {
            const res = await fetch(blobUrl);
            const buf = await res.arrayBuffer();
            const sha = await this._sha256Hex(buf);
            const { dir, fileName } = await this._ensurePathAsync(relativePath);
            const fh = await dir.getFileHandle(fileName, { create: true });
            const w = await fh.createWritable();
            await w.write(buf);
            await w.close();
            return {
                success: true,
                sha256: sha,
                sizeBytes: buf.byteLength,
                relativePath: relativePath.replaceAll("\\", "/"),
            };
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * Copy a file already in the media folder to another path within it — used to promote an
     * archived audio take back to active (the bytes never leave the browser, unlike
     * saveFromUrlAsync which downloads from a server proxy URL). Archives whatever's currently at
     * toRelativePath first, same as any other write to that path.
     */
    copyLocalFileAsync: async function (fromRelativePath, toRelativePath, takeId) {
        if (!this._root) return { success: false, error: "Media folder not connected" };
        try {
            const fh = await this._resolveFileHandleAsync(fromRelativePath);
            if (!fh) return { success: false, error: "Source file not found in media folder" };
            const file = await fh.getFile();
            const buf = await file.arrayBuffer();

            await this._archiveClipHistoryAsync(toRelativePath);
            await this._archiveMusicSegmentAsync(toRelativePath, takeId);

            const sha = await this._sha256Hex(buf);
            const { dir, fileName } = await this._ensurePathAsync(toRelativePath);
            const wfh = await dir.getFileHandle(fileName, { create: true });
            const w = await wfh.createWritable();
            await w.write(buf);
            await w.close();

            const key = toRelativePath.replaceAll("\\", "/");
            if (this._blobUrls[key]) {
                try { URL.revokeObjectURL(this._blobUrls[key]); } catch (_) { /* */ }
                delete this._blobUrls[key];
            }
            return { success: true, sha256: sha, sizeBytes: buf.byteLength };
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    _sha256Hex: async function (arrayBuffer) {
        const digest = await crypto.subtle.digest("SHA-256", arrayBuffer);
        const bytes = new Uint8Array(digest);
        return Array.from(bytes).map(b => b.toString(16).padStart(2, "0")).join("");
    },

    /**
     * Write bytes into the media folder and drop any cached blob URL for that path.
     * @returns {Promise<string>} normalized relative path
     */
    _writeFileAndRevokeBlobAsync: async function (relativePath, buf) {
        const { dir, fileName } = await this._ensurePathAsync(relativePath);
        const fh = await dir.getFileHandle(fileName, { create: true });
        const w = await fh.createWritable();
        await w.write(buf);
        await w.close();
        const key = relativePath.replaceAll("\\", "/");
        if (this._blobUrls[key]) {
            try { URL.revokeObjectURL(this._blobUrls[key]); } catch (_) { /* */ }
            delete this._blobUrls[key];
        }
        return key;
    },

    _ensureRootConnectedAsync: async function () {
        if (!this._root) {
            const c = await this.connectFolderAsync();
            if (!c.success) return c;
        }
        return null;
    },

    /**
     * Hash + write a buffer, returning the same success shape as saveBytesAsync.
     * @param {ArrayBuffer|Uint8Array} buf
     * @param {string} relativePath
     * @param {ArrayBuffer} shaSource
     */
    _commitBufferAsync: async function (buf, relativePath, shaSource) {
        const sha = await this._sha256Hex(shaSource);
        const key = await this._writeFileAndRevokeBlobAsync(relativePath, buf);
        return {
            success: true,
            sha256: sha,
            sizeBytes: buf.byteLength,
            relativePath: key,
        };
    },

    /**
     * Write raw bytes into the media folder (preferred for large MP4/MP3 from zip import).
     * @param {Uint8Array|ArrayBuffer} bytes
     * @param {string} relativePath e.g. "owner/slug/assets/video/scene_01_clip_01.mp4"
     */
    saveBytesAsync: async function (bytes, relativePath) {
        const denied = await this._ensureRootConnectedAsync();
        if (denied) return denied;
        try {
            const buf = bytes instanceof Uint8Array
                ? bytes
                : new Uint8Array(bytes);
            return await this._commitBufferAsync(
                buf, relativePath,
                buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength));
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * Write raw bytes (base64) into the media folder at relativePath.
     * Used for voice-clone samples (same client-first storage as MP3/MP4).
     */
    saveBytesBase64Async: async function (base64, relativePath) {
        const denied = await this._ensureRootConnectedAsync();
        if (denied) return denied;
        try {
            const bin = atob(base64);
            const buf = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) buf[i] = bin.codePointAt(i);
            return await this._commitBufferAsync(buf, relativePath, buf.buffer);
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * List audio files under optional prefix (relative to media root).
     * @returns {{ success:boolean, files?: { relativePath:string, name:string, sizeBytes:number }[], error?:string }}
     */
    /**
     * List audio files under optional prefix (relative to media root).
     * @returns {{ success:boolean, files?: { relativePath:string, name:string, sizeBytes:number }[], error?:string }}
     */
    listAudioFilesAsync: async function (prefix) {
        if (!this._root) return { success: false, error: "Media folder not connected", files: [] };
        const audioExt = new Set([".webm", ".mp3", ".wav", ".m4a", ".ogg", ".aac", ".flac"]);
        try {
            const { dir, base } = await this._descendPrefixAsync(prefix);
            const files = await this._collectFilesByExtAsync(dir, base, audioExt, false);
            files.sort((a, b) => a.relativePath.localeCompare(b.relativePath));
            return { success: true, files };
        } catch (err) {
            return { success: false, error: err.message || String(err), files: [] };
        }
    },

    /**
     * List media files under a project id folder (or any relative prefix).
     * Used by admin full-project export merge (MP4/MP3/etc. live on the client).
     * @param {string} prefix e.g. "owner/slug" project id
     * @returns {{ success:boolean, files?: { relativePath:string, sizeBytes:number }[], error?:string }}
     */
    listMediaTreeAsync: async function (prefix) {
        if (!this._root) return { success: false, error: "Media folder not connected", files: [] };
        const mediaExt = new Set([
            ".mp4", ".webm", ".mov", ".mkv", ".m4v",
            ".mp3", ".wav", ".m4a", ".ogg", ".aac", ".flac", ".opus",
            ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ]);
        try {
            const { dir, base } = await this._descendPrefixAsync(prefix);
            const files = await this._collectFilesByExtAsync(dir, base, mediaExt, true);
            files.sort((a, b) => a.relativePath.localeCompare(b.relativePath));
            return { success: true, files };
        } catch (err) {
            return { success: false, error: err.message || String(err), files: [] };
        }
    },

    _descendPrefixAsync: async function (prefix) {
        let dir = this._root;
        let base = "";
        if (prefix && String(prefix).trim()) {
            const parts = String(prefix).replaceAll("\\", "/").split("/").filter(Boolean);
            for (const part of parts) {
                dir = await dir.getDirectoryHandle(part, { create: false });
                base = base ? `${base}/${part}` : part;
            }
        }
        return { dir, base };
    },

    _collectFilesByExtAsync: async function (dir, relBase, extSet, requireSize) {
        const files = [];
        const walk = async (cur, base) => {
            for await (const [name, handle] of cur.entries()) {
                if (name.startsWith(".") || name === "node_modules") continue;
                const rel = base ? `${base}/${name}` : name;
                if (handle.kind === "directory") {
                    await walk(handle, rel);
                    continue;
                }
                await this._tryPushMatchingFileAsync(files, handle, name, rel, extSet, requireSize);
            }
        };
        await walk(dir, relBase);
        return files;
    },

    _fileExtLower: function (name) {
        const lower = name.toLowerCase();
        const dot = lower.lastIndexOf(".");
        return dot >= 0 ? lower.slice(dot) : "";
    },

    _tryPushMatchingFileAsync: async function (files, handle, name, rel, extSet, requireSize) {
        if (handle.kind !== "file") return;
        if (!extSet.has(this._fileExtLower(name))) return;
        try {
            const f = await handle.getFile();
            if (requireSize && (!f || f.size <= 0)) return;
            files.push({ relativePath: rel.replaceAll("\\", "/"), name, sizeBytes: f.size });
        } catch (e) { if (e) { /* skip unreadable file */ } }
    },
};
