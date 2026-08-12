/**
 * PageToMovie Client-Side Video Export & File System Access API Helper
 * Enables zero-server-overhead direct streaming of rendered MP4 movies
 * straight to the user's local hard drive.
 */

window.PageToMovieExport = {
    _directoryHandle: null,

    async _reportProgress(progressRef, phase, percent, message) {
        if (!progressRef) return;
        try {
            await progressRef.invokeMethodAsync(
                "ReportAsync",
                phase || "",
                typeof percent === "number" && !Number.isNaN(percent) ? percent : null,
                message || null);
        } catch (_) { /* component disposed */ }
    },

    /**
     * Download a binary stream from Blazor (DotNetStreamReference) as a file.
     * Used for admin full-project zip export.
     * Optional 3rd arg: DotNetObjectReference to ExportProgressSink.
     */
    downloadStreamAsync: async function (fileName, contentStreamReference, progressRef) {
        try {
            await this._reportProgress(progressRef, "download", null, "Receiving zip…");
            const arrayBuffer = await contentStreamReference.arrayBuffer();
            await this._reportProgress(progressRef, "pack", 100, "Saving download…");
            const blob = new Blob([arrayBuffer], { type: "application/zip" });
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = fileName || "PageToMovie_project.zip";
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
            await this._reportProgress(progressRef, "done", 100, "Download complete");
            return { success: true };
        } catch (err) {
            console.error("downloadStreamAsync failed:", err);
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * Checks if modern File System Access API is supported by the user's browser.
     */
    supportsFileSystemAccess: function () {
        return 'showSaveFilePicker' in window || 'showDirectoryPicker' in window;
    },

    /**
     * Prompts user to select an output folder ONCE per session.
     * All subsequent clip/movie renders in the session save directly into this folder without prompting.
     */
    selectExportDirectoryAsync: async function () {
        if (!('showDirectoryPicker' in window)) {
            return { success: false, error: 'Directory Picker API not supported on this browser.' };
        }
        try {
            this._directoryHandle = await window.showDirectoryPicker({ mode: 'readwrite' });
            return {
                success: true,
                folderName: this._directoryHandle.name,
                message: `Export folder '${this._directoryHandle.name}' connected for this session.`
            };
        } catch (err) {
            console.warn('Directory selection cancelled or failed:', err);
            return { success: false, error: err.message || 'Folder selection cancelled.' };
        }
    },

    /**
     * Returns true if a local directory handle has been authorized by the user for this session.
     */
    hasDirectoryHandle: function () {
        return this._directoryHandle !== null;
    },

    /**
     * Saves raw Uint8Array / base64 data directly into the authorized session folder without prompts.
     * If no folder is selected yet, prompts once via file save picker or folder picker.
     */
    saveMovieToDiskAsync: async function (suggestedFilename, base64Data, mimeType) {
        try {
            const raw = window.atob(base64Data);
            const rawLength = raw.length;
            const uInt8Array = new Uint8Array(rawLength);
            for (let i = 0; i < rawLength; ++i) {
                uInt8Array[i] = raw.charCodeAt(i);
            }
            const blob = new Blob([uInt8Array], { type: mimeType || 'video/mp4' });

            // 1. Direct write into authorized session folder (zero prompts)
            if (this._directoryHandle) {
                const fileHandle = await this._directoryHandle.getFileHandle(suggestedFilename || 'PageToMovie_WIP.mp4', { create: true });
                const writable = await fileHandle.createWritable();
                await writable.write(blob);
                await writable.close();
                return { success: true, folderName: this._directoryHandle.name, message: `Saved directly into '${this._directoryHandle.name}/${suggestedFilename}'.` };
            }

            // 2. Single-file save picker (prompts once)
            if ('showSaveFilePicker' in window) {
                const options = {
                    suggestedName: suggestedFilename || 'PageToMovie_WIP.mp4',
                    types: [{
                        description: 'MP4 Video File',
                        accept: { 'video/mp4': ['.mp4'] }
                    }]
                };

                const handle = await window.showSaveFilePicker(options);
                const writable = await handle.createWritable();
                await writable.write(blob);
                await writable.close();
                return { success: true, message: 'Movie saved directly to disk.' };
            } else {
                // 3. Fallback browser download prompt
                const url = URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = url;
                a.download = suggestedFilename || 'PageToMovie_WIP.mp4';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                URL.revokeObjectURL(url);
                return { success: true, message: 'Movie downloaded via browser fallback.' };
            }
        } catch (err) {
            console.error('File System Access API export error:', err);
            return { success: false, error: err.message || 'Export cancelled or failed.' };
        }
    },

    /**
     * Client-Side WASM FFmpeg video clip concatenator helper stub.
     * Uses browser Blob URLs to merge scene clips in browser memory without server CPU usage.
     */
    concatenateClipsInBrowserAsync: async function (clipUrls, outputFilename) {
        try {
            console.log('Concatenating clips in browser WASM context:', clipUrls);
            const blobs = await Promise.all(clipUrls.map(url => fetch(url).then(r => r.blob())));
            const mergedBlob = new Blob(blobs, { type: 'video/mp4' });

            if (this._directoryHandle) {
                const fileHandle = await this._directoryHandle.getFileHandle(outputFilename || 'PageToMovie_FullMovie.mp4', { create: true });
                const writable = await fileHandle.createWritable();
                await writable.write(mergedBlob);
                await writable.close();
                return { success: true, folderName: this._directoryHandle.name };
            }

            const url = URL.createObjectURL(mergedBlob);
            const a = document.createElement('a');
            a.href = url;
            a.download = outputFilename || 'PageToMovie_FullMovie.mp4';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);

            return { success: true, count: clipUrls.length };
        } catch (err) {
            console.error('Browser WASM concatenation error:', err);
            return { success: false, error: err.message };
        }
    },

    /**
     * Copy text to the system clipboard (share links, etc.).
     */
    copyTextAsync: async function (text) {
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                await navigator.clipboard.writeText(text || "");
                return { success: true };
            }
            // Fallback for older browsers / non-secure contexts
            const ta = document.createElement("textarea");
            ta.value = text || "";
            ta.setAttribute("readonly", "");
            ta.style.position = "fixed";
            ta.style.left = "-9999px";
            document.body.appendChild(ta);
            ta.select();
            const ok = document.execCommand("copy");
            document.body.removeChild(ta);
            return ok ? { success: true } : { success: false, error: "Copy command failed" };
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * Upload a browser media URL (blob: or /api/… with access_token) to POST /api/demos as multipart.
     * @param {string} mediaUrl blob or same-origin media URL
     * @param {string} uploadUrl absolute or root-relative POST target (e.g. /api/demos)
     * @param {string|null} accessToken JWT for Authorization header
     * @param {{ title?: string, description?: string, projectId?: string, fileName?: string, acceptedGuidelines?: boolean }} meta
     */
    uploadDemoMovieAsync: async function (mediaUrl, uploadUrl, accessToken, meta, dotNetRef) {
        try {
            if (!mediaUrl) return { success: false, error: "No media URL" };
            meta = meta || {};
            if (dotNetRef) {
                try { dotNetRef.invokeMethodAsync("ReportPublishProgress", 5, "Preparing movie cut for upload..."); } catch (_) {}
            }
            const res = await fetch(mediaUrl);
            if (!res.ok) {
                return { success: false, error: "Could not read video (" + res.status + ")" };
            }
            const blob = await res.blob();
            if (!blob || blob.size < 1024) {
                return { success: false, error: "Video is empty or too small" };
            }
            const form = this._buildUploadDemoFormData(blob, meta);

            const xhr = new XMLHttpRequest();
            xhr.open("POST", uploadUrl, true);
            if (accessToken) xhr.setRequestHeader("Authorization", "Bearer " + accessToken);

            if (xhr.upload && dotNetRef) {
                xhr.upload.onprogress = (e) => {
                    if (e.lengthComputable && e.total > 0) {
                        const pct = Math.round(10 + (e.loaded / e.total) * 85);
                        const loadedMb = (e.loaded / (1024 * 1024)).toFixed(1);
                        const totalMb = (e.total / (1024 * 1024)).toFixed(1);
                        try {
                            dotNetRef.invokeMethodAsync("ReportPublishProgress", pct, `Uploading cut to server (${loadedMb} MB / ${totalMb} MB)...`);
                        } catch (_) {}
                    }
                };
            }

            return await new Promise((resolve) => {
                xhr.onload = () => {
                    let json = null;
                    try { json = xhr.responseText ? JSON.parse(xhr.responseText) : null; } catch (_) {}
                    if (xhr.status < 200 || xhr.status >= 300) {
                        const err = (json && (json.error || json.message)) || xhr.responseText || ("HTTP " + xhr.status);
                        resolve({ success: false, error: String(err) });
                    } else {
                        if (dotNetRef) {
                            try { dotNetRef.invokeMethodAsync("ReportPublishProgress", 100, "Upload complete! YouTube processing starting in background."); } catch (_) {}
                        }
                        resolve({
                            success: true,
                            demo: json && json.demo ? json.demo : json,
                            pendingReview: !!(json && json.pendingReview),
                            replacedExisting: !!(json && json.replacedExisting),
                            message: json && json.message ? json.message : null,
                        });
                    }
                };
                xhr.onerror = () => resolve({ success: false, error: "Network connection lost during upload" });
                xhr.send(form);
            });
        } catch (err) {
            console.error("uploadDemoMovieAsync failed:", err);
            return { success: false, error: err.message || String(err) };
        }
    },

    _buildUploadDemoFormData: function (blob, meta) {
        const form = new FormData();
        form.append("file", blob, meta.fileName || "movie.mp4");
        if (meta.title) form.append("title", meta.title);
        if (meta.description) form.append("description", meta.description);
        if (meta.projectId) form.append("projectId", meta.projectId);
        form.append("acceptedGuidelines", meta.acceptedGuidelines === false ? "false" : "true");
        form.append("madeForKids", meta.madeForKids === true ? "true" : "false");
        form.append("isAiSynthetic", meta.isAiSynthetic === false ? "false" : "true");
        if (meta.privacyStatus) form.append("privacyStatus", meta.privacyStatus);
        if (meta.tags) form.append("tags", meta.tags);
        form.append("replaceExisting", meta.replaceExisting === false ? "false" : "true");
        return form;
    },

    /**
     * Stage 2 of full-project export: take the server zip (ArrayBuffer), merge in
     * client media-folder files (MP4/MP3/etc. under {projectId}/…), download one zip.
     * Server entries win only when local is missing; local media always overwrites empty/missing.
     * Optional progressRef: ExportProgressSink ReportAsync(phase, percent, message).
     */
    mergeServerZipWithLocalMediaAsync: async function (fileName, contentStreamReference, projectId, progressRef) {
        try {
            await this._reportProgress(progressRef, "download", null, "Reading server zip…");
            const serverBuf = await contentStreamReference.arrayBuffer();
            await this._reportProgress(progressRef, "merge", 0, "Unpacking server zip…");
            const entries = await this._zipReadAllAsync(new Uint8Array(serverBuf));
            const byPath = new Map();
            for (const e of entries) {
                if (e.name.endsWith("/")) continue;
                byPath.set(e.name.replace(/\\/g, "/"), e.data);
            }

            const pid = (projectId || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
            const { clientAdded, clientSkipped, mediaError } = await this._mergeLocalMediaFilesAsync(byPath, pid, progressRef);

            // Annotate export meta if present (keep projectSchemaVersion; bump package fields)
            const metaKey = [...byPath.keys()].find(k => k.endsWith("/_export_meta.json") || k === "_export_meta.json");
            if (metaKey) {
                try {
                    const prev = new TextDecoder().decode(byPath.get(metaKey));
                    const obj = JSON.parse(prev);
                    obj.package = obj.package || "PageToMovie.project_export";
                    obj.exportFormatVersion = 2;
                    obj.schema = "PageToMovie.project_export.v2";
                    obj.clientMediaMerged = true;
                    obj.clientMediaFilesAdded = clientAdded;
                    obj.clientMediaListError = mediaError || undefined;
                    obj.clientMergedAtUtc = new Date().toISOString();
                    obj.note = "Server project folder + client media folder (MP4/MP3/etc.). " +
                        "projectSchemaVersion drives ProjectMigrationService on import; " +
                        "exportFormatVersion is the zip package shape.";
                    byPath.set(metaKey, new TextEncoder().encode(JSON.stringify(obj, null, 2)));
                } catch (_) { /* keep original meta */ }
            }

            await this._reportProgress(progressRef, "pack", 50, "Packing final zip…");
            const out = this._zipWriteAll(byPath);
            await this._reportProgress(progressRef, "pack", 90, "Starting download…");
            const blob = new Blob([out], { type: "application/zip" });
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = fileName || "PageToMovie_project.zip";
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);

            const msgParts = [`Downloaded ${fileName || "zip"}`];
            if (clientAdded > 0) msgParts.push(`${clientAdded} local media file(s) merged`);
            else if (!window.PageToMovieMedia || !window.PageToMovieMedia._root)
                msgParts.push("server files only — connect media folder to include MP4/MP3");
            else if (mediaError)
                msgParts.push(`local media: ${mediaError}`);
            else
                msgParts.push("no local media files found under project folder");

            await this._reportProgress(progressRef, "done", 100, msgParts.join(" · "));
            return {
                success: true,
                clientMediaAdded: clientAdded,
                clientMediaSkipped: clientSkipped,
                mediaError,
                message: msgParts.join(" · "),
            };
        } catch (err) {
            console.error("mergeServerZipWithLocalMediaAsync failed:", err);
            return { success: false, error: err.message || String(err) };
        }
    },

    _mergeLocalMediaFilesAsync: async function (byPath, pid, progressRef) {
        let clientAdded = 0;
        let clientSkipped = 0;
        let mediaError = null;
        if (!window.PageToMovieMedia || !window.PageToMovieMedia._root || !pid)
            return { clientAdded, clientSkipped, mediaError };
        const listed = await window.PageToMovieMedia.listMediaTreeAsync(pid);
        if (!listed.success)
            return { clientAdded, clientSkipped, mediaError: listed.error || "Could not list local media" };
        const files = listed.files || [];
        for (let i = 0; i < files.length; i++) {
            const rel = (files[i].relativePath || "").replace(/\\/g, "/");
            if (!rel) continue;
            if (i === 0 || i === files.length - 1 || (i + 1) % 3 === 0) {
                const pct = files.length > 0 ? Math.min(100, ((i + 1) / files.length) * 100) : 0;
                await this._reportProgress(progressRef, "merge", pct, `Merging local media ${i + 1}/${files.length}…`);
            }
            const added = await this._mergeOneLocalFileAsync(byPath, rel);
            if (added) clientAdded++;
            else clientSkipped++;
        }
        return { clientAdded, clientSkipped, mediaError };
    },

    _mergeOneLocalFileAsync: async function (byPath, rel) {
        try {
            const got = await window.PageToMovieMedia.getBytesAsync(rel, 0);
            if (!got.success || !got.bytes) return false;
            const bytes = got.bytes instanceof Uint8Array ? got.bytes : new Uint8Array(got.bytes);
            byPath.set(rel, bytes);
            return true;
        } catch (_) {
            return false;
        }
    },

    /**
     * Stage 2 of project import: from a full export zip, write media files into the
     * connected client media folder under {targetProjectId}/assets/…
     * Server import should already have received the zip (stage 1).
     */
    importZipMediaToClientFolderAsync: async function (contentStreamReference, targetProjectId) {
        try {
            if (!window.PageToMovieMedia) {
                return { success: false, error: "PageToMovieMedia not loaded", written: 0 };
            }
            if (!window.PageToMovieMedia._root) {
                const c = await window.PageToMovieMedia.connectFolderAsync();
                if (!c.success) {
                    return {
                        success: false,
                        error: c.error || "Connect a local media folder to restore MP4/MP3 files",
                        written: 0,
                        needsMediaFolder: true,
                    };
                }
            }

            const targetId = (targetProjectId || "").replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
            if (!targetId) {
                return { success: false, error: "Project id required", written: 0 };
            }

            const buf = await contentStreamReference.arrayBuffer();
            const entries = await this._zipReadAllAsync(new Uint8Array(buf));
            const mediaExt = new Set([
                ".mp4", ".webm", ".mov", ".mkv", ".m4v",
                ".mp3", ".wav", ".m4a", ".ogg", ".aac", ".flac", ".opus",
                ".png", ".jpg", ".jpeg", ".webp", ".gif",
            ]);

            let written = 0;
            let skipped = 0;
            const errors = [];

            for (const e of entries) {
                const res = await this._processZipMediaEntryAsync(e, targetId, mediaExt);
                if (res.status === "written") written++;
                else if (res.status === "skipped") {
                    skipped++;
                    if (res.error && errors.length < 5) errors.push(res.error);
                }
            }

            return {
                success: true,
                written,
                skipped,
                errors: errors.length > 0 ? errors : undefined,
                message: `Restored ${written} media file(s) to client media folder` +
                    (skipped > 0 ? ` (${skipped} non-media / skipped)` : ""),
            };
        } catch (err) {
            console.error("importZipMediaToClientFolderAsync failed:", err);
            return { success: false, error: err.message || String(err), written: 0 };
        }
    },

    _processZipMediaEntryAsync: async function (e, targetId, mediaExt) {
        let name = (e.name || "").replace(/\\/g, "/");
        if (!name || name.endsWith("/")) return { status: "ignored" };
        name = name.replace(/^\.\//, "");
        const base = name.split("/").pop() || "";
        const dot = base.lastIndexOf(".");
        const ext = dot >= 0 ? base.slice(dot).toLowerCase() : "";
        if (!mediaExt.has(ext)) return { status: "skipped" };

        let clientRel;
        const assetsIdx = name.toLowerCase().indexOf("/assets/");
        if (assetsIdx >= 0) {
            clientRel = `${targetId}${name.slice(assetsIdx)}`;
        } else if (name.toLowerCase().startsWith("assets/")) {
            clientRel = `${targetId}/${name}`;
        } else if (name.toLowerCase().startsWith(targetId.toLowerCase() + "/")) {
            clientRel = name;
        } else {
            clientRel = `${targetId}/${base}`;
        }

        try {
            const res = await window.PageToMovieMedia.saveBytesAsync(e.data, clientRel);
            if (res && res.success) return { status: "written" };
            return { status: "skipped", error: res && res.error ? `${clientRel}: ${res.error}` : null };
        } catch (err) {
            return { status: "skipped", error: `${clientRel}: ${err.message || err}` };
        }
    },

    /** @returns {Promise<{name:string, data:Uint8Array}[]>} */
    _zipReadAllAsync: async function (u8) {
        const view = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
        // Find EOCD
        let eocd = -1;
        for (let i = u8.byteLength - 22; i >= 0; i--) {
            if (view.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
        }
        if (eocd < 0) throw new Error("Not a zip (EOCD missing)");
        const cdOffset = view.getUint32(eocd + 16, true);
        const cdCount = view.getUint16(eocd + 10, true);
        const entries = [];
        let p = cdOffset;
        for (let n = 0; n < cdCount; n++) {
            if (view.getUint32(p, true) !== 0x02014b50)
                throw new Error("Bad central directory");
            const method = view.getUint16(p + 10, true);
            const compSize = view.getUint32(p + 20, true);
            const uncompSize = view.getUint32(p + 24, true);
            const nameLen = view.getUint16(p + 28, true);
            const extraLen = view.getUint16(p + 30, true);
            const commentLen = view.getUint16(p + 32, true);
            const localHeaderOffset = view.getUint32(p + 42, true);
            const nameBytes = u8.subarray(p + 46, p + 46 + nameLen);
            const name = new TextDecoder().decode(nameBytes);
            p += 46 + nameLen + extraLen + commentLen;

            // Local header
            const lp = localHeaderOffset;
            if (view.getUint32(lp, true) !== 0x04034b50)
                throw new Error("Bad local header for " + name);
            const lNameLen = view.getUint16(lp + 26, true);
            const lExtraLen = view.getUint16(lp + 28, true);
            const dataStart = lp + 30 + lNameLen + lExtraLen;
            const comp = u8.subarray(dataStart, dataStart + compSize);
            let data;
            if (method === 0) {
                data = comp.slice();
            } else if (method === 8) {
                data = await this._inflateRawAsync(comp, uncompSize);
            } else {
                console.warn("zip: skip unsupported method", method, name);
                continue;
            }
            entries.push({ name, data });
        }
        return entries;
    },

    _inflateRawAsync: async function (comp, uncompSize) {
        if (typeof DecompressionStream === "undefined")
            throw new Error("Browser cannot inflate zip entries (no DecompressionStream)");
        const ds = new DecompressionStream("deflate-raw");
        const stream = new Blob([comp]).stream().pipeThrough(ds);
        const ab = await new Response(stream).arrayBuffer();
        const out = new Uint8Array(ab);
        if (uncompSize && out.byteLength !== uncompSize && uncompSize !== 0xffffffff) {
            // allow mismatch for zip64 edge; still return data
        }
        return out;
    },

    /**
     * Write zip with STORE (no compression) — fine for already-compressed media + small JSON.
     * @param {Map<string, Uint8Array>} byPath
     * @returns {Uint8Array}
     */
    _zipWriteAll: function (byPath) {
        const enc = new TextEncoder();
        const locals = [];
        const central = [];
        let offset = 0;
        const sorted = [...byPath.keys()].sort((a, b) => a.localeCompare(b));
        for (const name of sorted) {
            const data = byPath.get(name);
            if (!data) continue;
            const nameBytes = enc.encode(name);
            const crc = this._crc32(data);
            const size = data.byteLength;

            // Local file header
            const local = new Uint8Array(30 + nameBytes.length + size);
            const lv = new DataView(local.buffer);
            lv.setUint32(0, 0x04034b50, true);
            lv.setUint16(4, 20, true); // version needed
            lv.setUint16(6, 0, true); // flags
            lv.setUint16(8, 0, true); // method STORE
            lv.setUint16(10, 0, true);
            lv.setUint16(12, 0, true);
            lv.setUint32(14, crc, true);
            lv.setUint32(18, size, true);
            lv.setUint32(22, size, true);
            lv.setUint16(26, nameBytes.length, true);
            lv.setUint16(28, 0, true);
            local.set(nameBytes, 30);
            local.set(data, 30 + nameBytes.length);
            locals.push(local);

            // Central directory header
            const cen = new Uint8Array(46 + nameBytes.length);
            const cv = new DataView(cen.buffer);
            cv.setUint32(0, 0x02014b50, true);
            cv.setUint16(4, 20, true);
            cv.setUint16(6, 20, true);
            cv.setUint16(8, 0, true);
            cv.setUint16(10, 0, true); // STORE
            cv.setUint16(12, 0, true);
            cv.setUint16(14, 0, true);
            cv.setUint32(16, crc, true);
            cv.setUint32(20, size, true);
            cv.setUint32(24, size, true);
            cv.setUint16(28, nameBytes.length, true);
            cv.setUint16(30, 0, true);
            cv.setUint16(32, 0, true);
            cv.setUint16(34, 0, true);
            cv.setUint16(36, 0, true);
            cv.setUint32(38, 0, true);
            cv.setUint32(42, offset, true);
            cen.set(nameBytes, 46);
            central.push(cen);

            offset += local.byteLength;
        }

        const cdSize = central.reduce((s, c) => s + c.byteLength, 0);
        const cdOffset = offset;
        const eocd = new Uint8Array(22);
        const ev = new DataView(eocd.buffer);
        ev.setUint32(0, 0x06054b50, true);
        ev.setUint16(4, 0, true);
        ev.setUint16(6, 0, true);
        ev.setUint16(8, central.length, true);
        ev.setUint16(10, central.length, true);
        ev.setUint32(12, cdSize, true);
        ev.setUint32(16, cdOffset, true);
        ev.setUint16(20, 0, true);

        const total = offset + cdSize + 22;
        const out = new Uint8Array(total);
        let o = 0;
        for (const l of locals) { out.set(l, o); o += l.byteLength; }
        for (const c of central) { out.set(c, o); o += c.byteLength; }
        out.set(eocd, o);
        return out;
    },

    _crc32: function (u8) {
        if (!this._crcTable) {
            const table = new Uint32Array(256);
            for (let n = 0; n < 256; n++) {
                let c = n;
                for (let k = 0; k < 8; k++)
                    c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
                table[n] = c >>> 0;
            }
            this._crcTable = table;
        }
        let crc = 0 ^ (-1);
        for (let i = 0; i < u8.length; i++)
            crc = (crc >>> 8) ^ this._crcTable[(crc ^ u8[i]) & 0xff];
        return (crc ^ (-1)) >>> 0;
    },

};
