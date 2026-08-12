/**
 * Client-side video stitching, audio silence trim, and frame sampling via ffmpeg.wasm.
 * All static ffmpeg assets are served same-origin from /js/ffmpeg/ for maximum speed & zero CORS issues.
 */
function reportProgress(onProgress, pct, msg) {
    if (typeof onProgress === "function") {
        try { onProgress(pct, msg); } catch (_) { }
    }
}

window.PageToMovieFfmpeg = {
    _ffmpeg: null,
    _loaded: false,
    _loading: null,
    _blobUrl: null,
    _silenceSessions: {},
    _silenceSessionSeq: 0,
    _trimTailSeq: 0,
    _lock: Promise.resolve(),

    _assets: {
        ffmpegJs: {
            url: "/js/ffmpeg/ffmpeg.js",
        },
        utilJs: {
            url: "/js/ffmpeg/util.js",
        },
        // ffmpeg-worker-bundle.js has ffmpeg-core.js inlined — no importScripts() or
        // dynamic import() needed inside the worker, sidestepping all module/classic
        // worker loader conflicts.
        workerBundleJs: "/js/ffmpeg/ffmpeg-worker-bundle.js",
        wasmJs: "/js/ffmpeg/ffmpeg-core.wasm",
    },

    _runExclusiveAsync: function (fn) {
        const next = this._lock.then(fn, fn);
        this._lock = next.then(() => {}, () => {});
        return next;
    },

    _log: function (msg) {
        if (typeof msg === "string" && msg.trim().length > 0) {
            console.debug("[PageToMovieFfmpeg]", msg);
        }
    },

    _safeFetchFile: async function (url) {
        if (typeof url === "string" && !url.startsWith("blob:") && !url.startsWith("data:")) {
            const res = await fetch(url);
            if (!res.ok) {
                throw new Error("Clip video missing (" + res.status + " " + res.statusText + "). Please generate clip first.");
            }
            const buf = await res.arrayBuffer();
            return new Uint8Array(buf);
        }
        const util = window.FFmpegUtil || {};
        if (typeof util.fetchFile === "function") {
            return await util.fetchFile(url);
        }
        throw new Error("ffmpeg util fetchFile missing");
    },

    /** Load local ffmpeg assets from same-origin /js/ffmpeg/. */
    ensureLoadedAsync: async function (onProgress) {
        if (this._loaded && this._ffmpeg) return { success: true };
        if (this._loading) return this._loading;
        this._loading = (async () => {
            try {
                reportProgress(onProgress, 0, "Loading video tools…");
                await this._ensureScript(this._assets.ffmpegJs.url);
                await this._ensureScript(this._assets.utilJs.url);

                const FFmpegClass = (window.FFmpegWASM && window.FFmpegWASM.FFmpeg)
                    || (window.FFmpeg && window.FFmpeg.FFmpeg)
                    || window.FFmpeg;
                if (!FFmpegClass) {
                    throw new Error("ffmpeg.wasm UMD not available");
                }

                const ffmpeg = new FFmpegClass();
                ffmpeg.on("log", ({ message }) => this._log(message));
                ffmpeg.on("progress", ({ progress }) => {
                    const pct = Math.max(0, Math.min(99, Math.round((progress || 0) * 100)));
                    reportProgress(onProgress, pct, "Combining…");
                });

                reportProgress(onProgress, 5, "Loading ffmpeg engine…");
                // ffmpeg-worker-bundle.js has ffmpeg-core.js inlined, so no coreURL import
                // is needed inside the worker. wasmURL must be absolute so the inlined core
                // can locate the .wasm binary. classWorkerURL must be absolute because
                // relative paths resolve against blob: origin inside ffmpeg.load().
                const origin = window.location.origin;
                await ffmpeg.load({
                    coreURL: origin + "/js/ffmpeg/ffmpeg-core.js", // used only to derive default wasmURL path
                    wasmURL: origin + this._assets.wasmJs,
                    classWorkerURL: origin + this._assets.workerBundleJs,
                });

                this._ffmpeg = ffmpeg;
                this._loaded = true;
                reportProgress(onProgress, 10, "Ready");
                return { success: true };
            } catch (err) {
                this._loading = null;
                console.error("ffmpeg.wasm load failed:", err);
                return { success: false, error: err.message || String(err) };
            } finally {
                this._loading = null;
            }
        })();

        return this._loading;
    },

    _ensureScript: function (src) {
        return new Promise((resolve, reject) => {
            const key = src;
            const existing = document.querySelector('script[data-ptm-ffmpeg="' + key + '"]');
            if (existing) {
                if (existing.dataset.loaded === "1") resolve();
                else existing.addEventListener("load", () => resolve());
                existing.addEventListener("error", () => reject(new Error("Failed to load " + src)));
                return;
            }
            const s = document.createElement("script");
            s.src = src;
            s.async = true;
            s.dataset.ptmFfmpeg = key;
            s.onload = () => { s.dataset.loaded = "1"; resolve(); };
            s.onerror = () => reject(new Error("Failed to load script: " + src));
            document.head.appendChild(s);
        });
    },

    revokePreviewUrl: function () {
        if (this._blobUrl) {
            try { URL.revokeObjectURL(this._blobUrl); } catch (_) { /* */ }
            this._blobUrl = null;
        }
    },

    /** Fetch ordered URLs into MEMFS as sequentially-named files ("in000.<ext>", …). Shared by
     * concatVideosAsync/concatAudioSegmentsAsync so the download/write loop lives once. */
    _writeSequentialInputsAsync: async function (ffmpeg, urls, ext, onProgress, startPct, endPct) {
        const written = [];
        for (let i = 0; i < urls.length; i++) {
            const name = "in" + String(i).padStart(3, "0") + "." + ext;
            reportProgress(onProgress,
                startPct + Math.round((i / urls.length) * (endPct - startPct)),
                "Downloading " + (i + 1) + "/" + urls.length + "…");
            const data = await this._safeFetchFile(urls[i]);
            await ffmpeg.writeFile(name, data);
            written.push(name);
        }
        return written;
    },

    /** Read an output file as a blob URL and clean up its MEMFS inputs + itself. */
    _readAndCleanupAsync: async function (ffmpeg, outName, mimeType, cleanupNames) {
        const out = await ffmpeg.readFile(outName);
        const blob = new Blob([out.buffer], { type: mimeType });
        const url = URL.createObjectURL(blob);
        for (const n of cleanupNames) {
            try { await ffmpeg.deleteFile(n); } catch (_) { /* */ }
        }
        try { await ffmpeg.deleteFile(outName); } catch (_) { /* */ }
        return url;
    },

    /**
     * Fetch ordered video URLs and concatenate into one MP4 blob URL for <video src>.
     * @param {string[]} urls absolute or root-relative clip/scene URLs
     * @param {(pct:number,msg:string)=>void} [onProgress]
     * @returns {{ success:boolean, url?:string, error?:string, count?:number }}
     */

    /**
     * SHA-256 hex of the media at url (blob: or http). Used for film_build.studio.sha256.
     * @param {string} url
     * @returns {Promise<{ success:boolean, sha256?:string, byteLength?:number, error?:string }>}
     */
    hashUrlAsync: async function (url) {
        if (!url) return { success: false, error: "No URL" };
        try {
            const resp = await fetch(url);
            if (!resp.ok) return { success: false, error: "fetch " + resp.status };
            const buf = await resp.arrayBuffer();
            const digest = await crypto.subtle.digest("SHA-256", buf);
            const bytes = new Uint8Array(digest);
            let hex = "";
            for (let i = 0; i < bytes.length; i++)
                hex += bytes[i].toString(16).padStart(2, "0");
            return { success: true, sha256: hex, byteLength: buf.byteLength };
        } catch (err) {
            return { success: false, error: (err && err.message) ? err.message : String(err) };
        }
    },

    concatVideosAsync: async function (urls, onProgress) {
        let list = [];
        if (Array.isArray(urls)) {
            list = urls;
        } else if (typeof urls === "string") {
            list = Array.from(arguments).filter(a => typeof a === "string" && a.length > 0 && typeof a !== "function");
        } else if (arguments.length > 0) {
            list = Array.from(arguments).filter(a => typeof a === "string" && a.length > 0 && typeof a !== "function");
        }

        if (!list || list.length === 0) {
            return { success: false, error: "No video URLs to combine" };
        }

        // Single file — no stitch needed
        if (list.length === 1) {
            reportProgress(onProgress, 100, "Ready");
            return { success: true, url: list[0], count: 1, single: true };
        }
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return load;

            const ffmpeg = this._ffmpeg;
            const util = window.FFmpegUtil || {};
            const fetchFile = util.fetchFile;
            if (typeof fetchFile !== "function") {
                return { success: false, error: "ffmpeg util fetchFile missing" };
            }

            let written = [];
            try {
                reportProgress(onProgress, 12, "Downloading clips…");
                written = await this._writeSequentialInputsAsync(ffmpeg, list, "mp4", onProgress, 12, 52);

                // concat demuxer list
                const listBody = written.map(n => "file '" + n + "'").join("\n");
                await ffmpeg.writeFile("list.txt", listBody);

                reportProgress(onProgress, 55, "Stitching…");
                let ok = false;
                try {
                    await ffmpeg.exec([
                        "-f", "concat", "-safe", "0", "-i", "list.txt",
                        "-c", "copy",
                        "-movflags", "+faststart",
                        "out.mp4",
                    ]);
                    ok = true;
                } catch (copyErr) {
                    this._log("copy concat failed, re-encoding: " + (copyErr && copyErr.message));
                    try { await ffmpeg.deleteFile("out.mp4"); } catch (_) { /* */ }
                    await ffmpeg.exec([
                        "-f", "concat", "-safe", "0", "-i", "list.txt",
                        "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28",
                        "-c:a", "aac", "-b:a", "128k",
                        "-movflags", "+faststart",
                        "out.mp4",
                    ]);
                    ok = true;
                }

                if (!ok) return { success: false, error: "Stitch failed" };
                console.log("[concat] stitched " + written.length + " clips");

                reportProgress(onProgress, 92, "Preparing player…");
                const out = await ffmpeg.readFile("out.mp4");
                const blob = new Blob([out.buffer], { type: "video/mp4" });
                // Do NOT auto-revoke the previous _blobUrl here — CollectAndMixSceneSegmentsAsync
                // (C#) calls this function once per scene to build several *simultaneous*
                // intermediate segments before combining them in one final call. Auto-revoking on
                // every call was yanking the URL out from under an earlier scene's still-in-use
                // blob the moment a later scene's concat ran, so the final combine's fetch() on
                // that now-revoked blob: URL failed with a bare "Failed to fetch". Explicit
                // top-level preview replacement already calls revokePreviewUrl() itself (see
                // RevokePreviewUrlAsync callers) before requesting a new preview, so nothing here
                // relied on this implicit revoke for correctness — only for eager cleanup.
                // Hash the stitched bytes for film_build.studio.sha256 before handing out the URL.
                let sha256 = null;
                let byteLength = null;
                try {
                    const ab = await blob.arrayBuffer();
                    byteLength = ab.byteLength;
                    const dig = await crypto.subtle.digest("SHA-256", ab);
                    const bytes = new Uint8Array(dig);
                    sha256 = "";
                    for (let i = 0; i < bytes.length; i++)
                        sha256 += bytes[i].toString(16).padStart(2, "0");
                    // Re-wrap so the blob URL still works after arrayBuffer()
                    blob = new Blob([ab], { type: blob.type || "video/mp4" });
                } catch (hashErr) {
                    this._log("stitch sha256 skipped: " + (hashErr && hashErr.message));
                }

                this._blobUrl = URL.createObjectURL(blob);

                // Cleanup MEMFS
                for (const n of written) {
                    try { await ffmpeg.deleteFile(n); } catch (_) { /* */ }
                }
                try { await ffmpeg.deleteFile("list.txt"); } catch (_) { /* */ }
                try { await ffmpeg.deleteFile("out.mp4"); } catch (_) { /* */ }

                reportProgress(onProgress, 100, "Ready");
                return { success: true, url: this._blobUrl, count: list.length, sha256: sha256, byteLength: byteLength };
            } catch (err) {
                console.error("concatVideosAsync failed:", err);
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    /** Load an image URL into an HTMLImageElement (for compositing the logo onto the credits card). */
    _loadImageAsync: function (url) {
        return new Promise(function (res, rej) {
            const im = new Image();
            im.onload = function () { res(im); };
            im.onerror = function () { rej(new Error("image load failed: " + url)); };
            im.src = url;
        });
    },

    /**
     * Draw the deterministic end-credits card on a canvas. We render the EXACT strings ourselves
     * (never a generative model), so text + our branding are always crisp and correct. Returns a
     * <canvas>. Only our own text/shapes are drawn, so the canvas is NOT tainted and exports cleanly.
     */
    _drawCreditsCard: function (opts) {
        const w = Math.max(16, Math.round(opts.width || 1280));
        const h = Math.max(16, Math.round(opts.height || 720));
        const cv = document.createElement("canvas");
        cv.width = w; cv.height = h;
        const g = cv.getContext("2d");

        // Matte black base.
        g.fillStyle = "#000";
        g.fillRect(0, 0, w, h);

        // Fine film grain: a small noise tile stamped at low opacity (cheap, textured, deterministic-enough).
        try {
            const tile = document.createElement("canvas");
            tile.width = tile.height = 128;
            const tg = tile.getContext("2d");
            const img = tg.createImageData(128, 128);
            for (let i = 0; i < img.data.length; i += 4) {
                const v = Math.trunc(Math.random() * 255);
                img.data[i] = img.data[i + 1] = img.data[i + 2] = v; img.data[i + 3] = 255;
            }
            tg.putImageData(img, 0, 0);
            g.save();
            g.globalAlpha = 0.045;
            const pat = g.createPattern(tile, "repeat");
            g.fillStyle = pat; g.fillRect(0, 0, w, h);
            g.restore();
        } catch (_) { /* grain is optional */ }

        // Soft vignette.
        const vg = g.createRadialGradient(w / 2, h / 2, h * 0.2, w / 2, h / 2, h * 0.75);
        vg.addColorStop(0, "rgba(0,0,0,0)");
        vg.addColorStop(1, "rgba(0,0,0,0.75)");
        g.fillStyle = vg; g.fillRect(0, 0, w, h);

        const cx = w / 2;
        // Fit a line to a max width by shrinking the font.
        function fit(text, font, px, maxW) {
            let size = px;
            do { g.font = font.replace("%d", size); size -= 2; }
            while (g.measureText(text).width > maxW && size > 10);
            return g.font;
        }
        g.textAlign = "center";
        g.textBaseline = "middle";
        const maxW = w * 0.84;

        // Vertical rhythm around the middle.
        let y = h * 0.40;
        const title = (opts.title || "The End").trim();
        try { g.letterSpacing = Math.round(w * 0.004) + "px"; } catch (_) { /* older browsers */ }
        g.fillStyle = "#f4f1ea";
        fit(title, 'italic %dpx Georgia, "Times New Roman", serif', Math.round(h * 0.12), maxW);
        g.fillText(title, cx, y);
        try { g.letterSpacing = "0px"; } catch (_) { /* */ }

        if (opts.author && String(opts.author).trim().length > 0) {
            y += h * 0.12;
            g.fillStyle = "rgba(230,226,216,0.72)";
            fit("Based on the story by " + String(opts.author).trim(),
                '%dpx Georgia, "Times New Roman", serif', Math.round(h * 0.045), maxW);
            g.fillText("Based on the story by " + String(opts.author).trim(), cx, y);
        }

        // Thin divider.
        y += h * 0.10;
        g.strokeStyle = "rgba(230,226,216,0.35)";
        g.lineWidth = Math.max(1, Math.round(h * 0.0025));
        g.beginPath(); g.moveTo(cx - w * 0.09, y); g.lineTo(cx + w * 0.09, y); g.stroke();

        // Logo mark — the same favicon the home page shows — rounded + centered above the footer.
        if (opts.logoImg) {
            const size = Math.round(h * 0.12);
            y += h * 0.05 + size / 2;
            const lx = Math.round(cx - size / 2), ly = Math.round(y - size / 2);
            const r = Math.round(size * 0.22);
            g.save();
            g.beginPath();
            g.moveTo(lx + r, ly);
            g.arcTo(lx + size, ly, lx + size, ly + size, r);
            g.arcTo(lx + size, ly + size, lx, ly + size, r);
            g.arcTo(lx, ly + size, lx, ly, r);
            g.arcTo(lx, ly, lx + size, ly, r);
            g.closePath(); g.clip();
            g.drawImage(opts.logoImg, lx, ly, size, size);
            g.restore();
            y += size / 2;
        }

        // Software + site footer.
        y += h * 0.075;
        const soft = (opts.softwareName || "PageToMovie").trim();
        const site = (opts.siteUrl || "pagetomovie.com").trim();
        g.fillStyle = "rgba(230,226,216,0.82)";
        try { g.letterSpacing = Math.round(w * 0.002) + "px"; } catch (_) { /* */ }
        fit("Made with " + soft + " · " + site,
            '%dpx Helvetica, Arial, sans-serif', Math.round(h * 0.038), maxW);
        g.fillText("Made with " + soft + " · " + site, cx, y);
        try { g.letterSpacing = "0px"; } catch (_) { /* */ }

        return cv;
    },

    /**
     * Render the deterministic credits card and roll it into a format-matched H.264 mp4 (a still held
     * for durationSec) so it drops into the normal clip slot and the browser stitch concatenates it
     * like any other clip. Returns { success, mp4Base64, byteLength } or { success:false, error }.
     */
    renderCreditsClipAsync: async function (opts) {
        opts = opts || {};
        const w = Math.max(16, Math.round(opts.width || 1280));
        const h = Math.max(16, Math.round(opts.height || 720));
        const fps = Math.max(1, Math.round(opts.fps || 24));
        const dur = Math.max(1, Number(opts.durationSec || 5));
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(opts.onProgress);
            if (!load.success) return load;
            const ffmpeg = this._ffmpeg;
            try {
                // Same-origin favicon → drawing it does not taint the canvas, so export still works.
                let logoImg = null;
                try { logoImg = await this._loadImageAsync(opts.logoUrl || "/favicon.png"); }
                catch (_) { /* logo is optional — the card still renders without it */ }
                const cv = this._drawCreditsCard({ ...opts, width: w, height: h, logoImg: logoImg });
                const blob = await new Promise((res) => cv.toBlob(res, "image/png"));
                if (!blob) return { success: false, error: "canvas toBlob failed" };
                const png = new Uint8Array(await blob.arrayBuffer());
                await ffmpeg.writeFile("card.png", png);
                try { await ffmpeg.deleteFile("credits.mp4"); } catch (_) { /* */ }
                await ffmpeg.exec([
                    "-loop", "1", "-i", "card.png",
                    "-t", String(dur), "-r", String(fps),
                    "-vf", "scale=" + w + ":" + h + ",format=yuv420p",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                    "-movflags", "+faststart",
                    "credits.mp4",
                ]);
                const out = await ffmpeg.readFile("credits.mp4");
                const bytes = out.buffer ? new Uint8Array(out.buffer) : out;
                // Base64 in chunks (avoid apply() stack limits on large arrays).
                let bin = "";
                const CH = 0x8000;
                for (let i = 0; i < bytes.length; i += CH)
                    bin += String.fromCharCode.apply(null, bytes.subarray(i, i + CH));
                const b64 = btoa(bin);
                try { await ffmpeg.deleteFile("card.png"); } catch (_) { /* */ }
                try { await ffmpeg.deleteFile("credits.mp4"); } catch (_) { /* */ }
                return { success: true, mp4Base64: b64, byteLength: bytes.length };
            } catch (err) {
                console.error("renderCreditsClipAsync failed:", err);
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    probeDurationAsync: async function (url) {
        if (!url) return { success: false, error: "No URL" };
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync();
            if (!load.success) return { success: false, error: load.error };
            const inName = "probe_tmp.mp4";
            try {
                const data = await this._safeFetchFile(url);
                await this._ffmpeg.writeFile(inName, data);
                const probe = await this._probeDurationMemfsAsync(inName);
                try { await this._ffmpeg.deleteFile(inName); } catch (_) {}
                return probe;
            } catch (err) {
                try { await this._ffmpeg.deleteFile(inName); } catch (_) {}
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    _probeDurationMemfsAsync: async function (inName) {
        let durationSec = 0;
        const ffmpeg = this._ffmpeg;
        const logHandler = ({ message }) => {
            if (!message) return;
            const m = message.match(/Duration:\s*(\d+):(\d+):(\d+\.\d+)/);
            if (m) {
                const hrs = parseFloat(m[1]);
                const mins = parseFloat(m[2]);
                const secs = parseFloat(m[3]);
                durationSec = hrs * 3600 + mins * 60 + secs;
            }
        };
        ffmpeg.on("log", logHandler);
        try {
            await ffmpeg.exec(["-hide_banner", "-i", inName]);
        } catch (_) {}
        ffmpeg.off("log", logHandler);
        return { success: durationSec > 0, seconds: durationSec };
    },

    analyzeSilenceAsync: async function (url, opts, onProgress) {
        opts = opts || {};
        if (!url) return { success: false, token: null, totalSec: 0, log: "", error: "No URL" };
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) {
                return {
                    success: true, token: null, totalSec: 0, log: "",
                    error: "skip: ffmpeg load failed — " + (load.error || ""),
                };
            }

            const ffmpeg = this._ffmpeg;
            const token = "sil" + (++this._silenceSessionSeq);
            const inName = token + "_in.mp4";
            try {
                reportProgress(onProgress, 8, "Loading clip…");
                const data = await this._safeFetchFile(url);
                await ffmpeg.writeFile(inName, data);

                reportProgress(onProgress, 18, "Probing duration…");
                const probe = await this._probeDurationMemfsAsync(inName);
                if (!probe.success || !(probe.seconds > 1.5)) {
                    try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                    return {
                        success: true, token: null, totalSec: probe.seconds || 0, log: "",
                        error: "skip: duration unknown or too short",
                    };
                }

                reportProgress(onProgress, 30, "Detecting silence…");
                const det = await this._silenceDetectMemfsAsync(inName, opts.noiseDb, opts.minSilenceSec);
                if (!det.success) {
                    try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                    return {
                        success: true, token: null, totalSec: probe.seconds, log: "",
                        error: "skip: silence detect failed — " + (det.error || ""),
                    };
                }

                this._silenceSessions[token] = inName;
                return { success: true, token: token, totalSec: probe.seconds, log: det.log };
            } catch (err) {
                try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                return { success: true, token: null, totalSec: 0, log: "", error: "skip: " + (err.message || String(err)) };
            }
        });
    },

    /**
     * Run ffmpeg `silencedetect` over an in-MEMFS clip and return its raw log lines. The caller
     * parses `silence_start` / `silence_end` from the log (see parseSilenceLog / ClipSilenceTrimmer).
     * noiseDb (e.g. -30) and minSilenceSec (e.g. 0.3) are the silencedetect thresholds.
     * @returns {{ success:boolean, log?:string, error?:string }}
     */
    _silenceDetectMemfsAsync: async function (inName, noiseDb, minSilenceSec) {
        const ffmpeg = this._ffmpeg;
        const db = (typeof noiseDb === "number" && noiseDb < 0) ? noiseDb : -30;
        const minSil = (typeof minSilenceSec === "number" && minSilenceSec > 0) ? minSilenceSec : 0.3;
        let log = "";
        const logHandler = ({ message }) => {
            if (typeof message === "string" && message.indexOf("silence_") >= 0) {
                log += message + "\n";
            }
        };
        ffmpeg.on("log", logHandler);
        try {
            // -af silencedetect writes silence_start/silence_end to the log; -f null discards output.
            await ffmpeg.exec([
                "-hide_banner",
                "-i", inName,
                "-af", "silencedetect=noise=" + db + "dB:d=" + minSil,
                "-f", "null", "-",
            ]);
        } catch (err) {
            ffmpeg.off("log", logHandler);
            return { success: false, error: (err && err.message) ? err.message : String(err) };
        }
        ffmpeg.off("log", logHandler);
        return { success: true, log: log };
    },

    /**
     * Detect the NON-SILENT (speech) windows of a clip via silencedetect, returned as
     * [{ startSec, endSec }] in clip time. This is the free, local, PRIMARY timestamp source for
     * voice substitution — the known dialogue lines from the shot plan are matched onto these
     * windows server-side (VoiceAlignmentStore.MatchSegmentsToLines).
     * @param {string} url clip URL (blob: or http)
     * @param {{noiseDb?:number,minSilenceSec?:number}} [opts]
     * @returns {{ success:boolean, totalSec?:number, segments?:{startSec:number,endSec:number}[], error?:string }}
     */
    detectSpeechSegmentsAsync: async function (url, opts, onProgress) {
        opts = opts || {};
        if (!url) return { success: false, error: "No URL" };
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error || "ffmpeg load failed" };

            const ffmpeg = this._ffmpeg;
            const inName = "speechdet_in.mp4";
            try {
                reportProgress(onProgress, 10, "Loading clip…");
                await ffmpeg.writeFile(inName, await this._safeFetchFile(url));

                reportProgress(onProgress, 30, "Probing duration…");
                const probe = await this._probeDurationMemfsAsync(inName);
                const totalSec = probe.success && probe.seconds > 0 ? probe.seconds : 0;

                reportProgress(onProgress, 55, "Detecting speech…");
                const det = await this._silenceDetectMemfsAsync(inName, opts.noiseDb, opts.minSilenceSec);
                if (!det.success) {
                    return { success: false, error: det.error || "silence detect failed" };
                }

                const segments = this._invertSilenceToSpeech(det.log || "", totalSec, opts.minSilenceSec);
                reportProgress(onProgress, 100, "Speech detected");
                return { success: true, totalSec: totalSec, segments: segments };
            } catch (err) {
                return { success: false, error: (err && err.message) ? err.message : String(err) };
            } finally {
                try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
            }
        });
    },

    /**
     * Extract a video's audio for the window [startSec, endSec] as a mono 16 kHz WAV, returned as
     * raw bytes (Uint8Array → C# byte[]). Used to send a detected dialogue segment to Scribe (STT)
     * for verification. Throws on failure.
     */
    extractAudioSegmentAsync: async function (videoUrl, startSec, endSec, onProgress) {
        if (!videoUrl) throw new Error("No video URL");
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) throw new Error(load.error || "ffmpeg load failed");
            const ffmpeg = this._ffmpeg;
            const inName = "seg_in.mp4";
            const outName = "seg_out.wav";
            try {
                await ffmpeg.writeFile(inName, await this._safeFetchFile(videoUrl));
                const start = Math.max(0, +startSec || 0);
                const dur = Math.max(0.1, (+endSec || 0) - start);
                const args = ["-hide_banner", "-y"];
                if (start > 0.001) args.push("-ss", String(start));
                args.push("-i", inName, "-t", String(dur),
                    "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", outName);
                await ffmpeg.exec(args);
                const out = await ffmpeg.readFile(outName);
                return out; // Uint8Array → marshals to C# byte[]
            } finally {
                try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                try { await ffmpeg.deleteFile(outName); } catch (_) { /* */ }
            }
        });
    },

    /**
     * Same as extractAudioSegmentAsync but returns a playable blob URL (WAV) for the Listen step.
     */
    extractAudioSegmentToUrlAsync: async function (videoUrl, startSec, endSec, onProgress) {
        if (!videoUrl) return { success: false, error: "No video URL" };
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error || "ffmpeg load failed" };
            const ffmpeg = this._ffmpeg;
            const inName = "segurl_in.mp4";
            const outName = "segurl_out.wav";
            try {
                await ffmpeg.writeFile(inName, await this._safeFetchFile(videoUrl));
                const start = Math.max(0, +startSec || 0);
                const dur = Math.max(0.1, (+endSec || 0) - start);
                const args = ["-hide_banner", "-y"];
                if (start > 0.001) args.push("-ss", String(start));
                args.push("-i", inName, "-t", String(dur), "-vn", "-ar", "44100", "-ac", "1", outName);
                await ffmpeg.exec(args);
                const out = await ffmpeg.readFile(outName);
                const url = URL.createObjectURL(new Blob([out.buffer], { type: "audio/wav" }));
                return { success: true, url: url };
            } catch (err) {
                return { success: false, error: err.message || String(err) };
            } finally {
                try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                try { await ffmpeg.deleteFile(outName); } catch (_) { /* */ }
            }
        });
    },

    /**
     * Play an audio URL and resolve when it finishes (or errors). Used by the voice-capture
     * "Listen" step so the teleprompter can scroll in sync with the original playback.
     */
    playAudioAsync: function (url) {
        return new Promise(function (resolve) {
            if (!url) { resolve(false); return; }
            try {
                const a = new Audio(url);
                a.onended = function () { resolve(true); };
                a.onerror = function () { resolve(false); };
                const p = a.play();
                if (p && typeof p.catch === "function") p.catch(function () { resolve(false); });
            } catch (_) { resolve(false); }
        });
    },

    /**
     * Play two audio URLs at once (narrator + your take) — unison when the rhythm matches, an echo
     * when it drifts. Resolves when both finish.
     */
    playOverlayAsync: async function (urlA, urlB) {
        if (!urlA || !urlB) return false;
        const AC = window.AudioContext || window.webkitAudioContext;
        const ctx = new AC();
        try {
            const decode = async function (url) { return ctx.decodeAudioData(await (await fetch(url)).arrayBuffer()); };
            const bufA = await decode(urlA);
            const bufB = await decode(urlB);
            // Start each at its FIRST SOUND at the same context time, so a matched take plays in unison
            // (an echo means the rhythm drifted). Narrator panned left, you right, so they're distinct.
            const leadA = this._leadSilenceOfBuffer(bufA);
            const leadB = this._leadSilenceOfBuffer(bufB);
            try { await ctx.resume(); } catch (_) { /* */ }
            return await new Promise(function (resolve) {
                let done = 0;
                const fin = function () { done++; if (done >= 2) { try { ctx.close(); } catch (_) { /* */ } resolve(true); } };
                const mk = function (buf, pan) {
                    const src = ctx.createBufferSource(); src.buffer = buf;
                    if (ctx.createStereoPanner) { const p = ctx.createStereoPanner(); p.pan.value = pan; src.connect(p).connect(ctx.destination); }
                    else { src.connect(ctx.destination); }
                    src.onended = fin;
                    return src;
                };
                const t0 = ctx.currentTime + 0.06;
                mk(bufA, -0.6).start(t0, Math.max(0, leadA));
                mk(bufB, 0.6).start(t0, Math.max(0, leadB));
            });
        } catch (_) {
            try { await ctx.close(); } catch (e) { /* */ }
            return false;
        }
    },

    /** Leading-silence duration (seconds) of a decoded buffer — first bin at/above 8% of peak RMS. */
    _leadSilenceOfBuffer: function (buf) {
        const ch = buf.getChannelData(0);
        const n = ch.length;
        const fine = 400;
        const per = Math.max(1, Math.floor(n / fine));
        const raw = new Float32Array(fine);
        let mx = 0;
        for (let i = 0; i < fine; i++) {
            let sum = 0, c = 0; const s = i * per, e = Math.min(n, s + per);
            for (let j = s; j < e; j++) { sum += ch[j] * ch[j]; c++; }
            raw[i] = c ? Math.sqrt(sum / c) : 0; if (raw[i] > mx) mx = raw[i];
        }
        const thr = mx * 0.08;
        let lo = 0; while (lo < fine && raw[lo] < thr) lo++;
        if (lo >= fine) lo = 0;
        return (lo * per) / buf.sampleRate;
    },

    /**
     * Draw an RMS-loudness waveform strip for an audio URL into a canvas. When `regions` (per-part
     * rhythm match, 0..1) is given, bars are coloured green/amber/red by how well that part matched —
     * so the "you" strip doubles as the feedback. Narrator strip passes null ⇒ a neutral blue.
     */
    renderWaveformAsync: async function (canvasId, url, regions) {
        let actx = null;
        try {
            const cv = document.getElementById(canvasId);
            if (!cv || !url) return false;
            const w = Math.max(80, Math.floor(cv.clientWidth || cv.width || 320));
            const h = cv.height || 34;
            cv.width = w; // match backing store to the displayed width
            const ctx = cv.getContext("2d");
            ctx.clearRect(0, 0, w, h);

            const resp = await fetch(url);
            const arr = await resp.arrayBuffer();
            const AC = window.AudioContext || window.webkitAudioContext;
            actx = new AC();
            const audio = await actx.decodeAudioData(arr);
            const data = audio.getChannelData(0);
            const n = data.length;

            // Fine RMS envelope, then trim leading/trailing silence (< 8% of peak) — the SAME trim the
            // scorer uses, so both strips start at the first sound (no leading-silence offset) and the
            // "you" colour regions line up with the speech instead of the dead air at the front.
            const fine = 400;
            const per = Math.max(1, Math.floor(n / fine));
            const raw = new Float32Array(fine);
            let mx = 0;
            for (let i = 0; i < fine; i++) {
                let sum = 0, c = 0; const s = i * per, e = Math.min(n, s + per);
                for (let j = s; j < e; j++) { sum += data[j] * data[j]; c++; }
                raw[i] = c ? Math.sqrt(sum / c) : 0; if (raw[i] > mx) mx = raw[i];
            }
            const thr = mx * 0.08;
            let lo = 0, hi = fine - 1;
            while (lo < fine && raw[lo] < thr) lo++;
            while (hi > lo && raw[hi] < thr) hi--;
            if (lo >= hi) { lo = 0; hi = fine - 1; } // all quiet → keep everything
            const spanLen = hi - lo + 1;

            // Resample the SPEECH span (lo..hi) across the strip.
            const bins = Math.min(w, 220);
            const env = new Array(bins).fill(0);
            let mx2 = 1e-6;
            for (let i = 0; i < bins; i++) {
                const idx = lo + Math.floor(i * spanLen / bins);
                env[i] = raw[Math.min(fine - 1, idx)];
                if (env[i] > mx2) mx2 = env[i];
            }

            const barW = w / bins;
            const hasReg = Array.isArray(regions) && regions.length > 0;
            for (let i = 0; i < bins; i++) {
                const v = env[i] / mx2;
                const bh = Math.max(1, v * (h - 2));
                let color = "rgba(147,197,253,.9)"; // neutral blue (narrator)
                if (hasReg) {
                    const m = regions[Math.min(regions.length - 1, Math.floor(i / bins * regions.length))];
                    color = m >= 0.7 ? "rgba(52,199,89,.95)" : m >= 0.5 ? "rgba(255,204,0,.95)" : "rgba(255,59,48,.95)";
                }
                ctx.fillStyle = color;
                ctx.fillRect(i * barW, (h - bh) / 2, Math.max(1, barW - 0.5), bh);
            }
            return true;
        } catch (_) {
            return false;
        } finally {
            if (actx) { try { await actx.close(); } catch (_) { /* */ } }
        }
    },

    /**
     * Teleprompter (#tele-text): PARK each word's left edge at the fixed marker for exactly its
     * spoken span [starts[i], ends[i]] (seconds from the start), then slide to the next word. A
     * stretched word therefore dwells at the marker longest — an exact copy of the narrator's
     * rhythm — and the word currently at the marker is highlighted, so which word is "now" is
     * never ambiguous. No-op if the element isn't there.
     */
    startWordTeleprompter: function (starts, ends, durationSec) {
        try {
            const el = document.getElementById("tele-text");
            if (!el || !durationSec) return false;
            const spans = el.querySelectorAll(".tele-w");
            const n = Math.min(spans.length, (starts || []).length);
            if (n === 0) return false;
            const D = durationSec;
            const centers = [];
            for (let i = 0; i < n; i++) centers.push(spans[i].offsetLeft + spans[i].offsetWidth / 2);

            // Two keyframes per word (park it CENTRED on the cursor from start→end), then slide to the next.
            const frames = [{ transform: "translateX(" + (-centers[0]) + "px)", offset: 0 }];
            for (let i = 0; i < n; i++) {
                let s = starts[i] / D, e = (ends && ends[i] != null ? ends[i] : starts[i]) / D;
                if (!(s >= 0)) s = 0; if (s > 1) s = 1;
                if (!(e >= s)) e = s; if (e > 1) e = 1;
                const tx = "translateX(" + (-centers[i]) + "px)";
                frames.push({ transform: tx, offset: s });
                frames.push({ transform: tx, offset: e });
            }
            frames.push({ transform: "translateX(" + (-centers[n - 1]) + "px)", offset: 1 });
            // Offsets must be non-decreasing for the Web Animations API; nudge any that regress.
            for (let i = 1; i < frames.length; i++)
                if (frames[i].offset <= frames[i - 1].offset)
                    frames[i].offset = Math.min(1, frames[i - 1].offset + 0.0001);
            el.getAnimations().forEach(function (a) { a.cancel(); });
            el.animate(frames, { duration: D * 1000, easing: "linear", fill: "forwards" });

            // Highlight the word at the marker ONLY while it's actually spoken [start,end], then unlight
            // it — so a pause (e.g. after a comma) is an unlit breath, not a highlight stuck on the last
            // word. Light-on timers are scheduled before light-off so back-to-back words don't cancel.
            if (el._teleTimers) el._teleTimers.forEach(function (t) { clearTimeout(t); });
            el._teleTimers = [];
            const clearAll = function () { for (let k = 0; k < n; k++) spans[k].classList.remove("tele-active"); };
            for (let i = 0; i < n; i++) {
                el._teleTimers.push(setTimeout((function (idx) {
                    return function () { clearAll(); spans[idx].classList.add("tele-active"); };
                })(i), Math.max(0, starts[i] * 1000)));
            }
            for (let i = 0; i < n; i++) {
                const endMs = Math.max(0, (ends && ends[i] != null ? ends[i] : starts[i]) * 1000);
                el._teleTimers.push(setTimeout((function (idx) {
                    return function () { spans[idx].classList.remove("tele-active"); };
                })(i), endMs));
            }
            el._teleTimers.push(setTimeout(clearAll, Math.max(0, D * 1000)));
            return true;
        } catch (_) { return false; }
    },

    /**
     * "How'd I do" rhythm score: compare the loudness ENVELOPE shape (where emphasis/syllables land)
     * of a take against the original, plus duration closeness. Timbre-independent by construction
     * (normalized RMS envelope) — a different voice with the same rhythm scores high. Returns 0..100.
     */
    analyzeRhythmMatchAsync: async function (originalUrl, takeUrl, wordBoundaries) {
        try {
            const frames = 96;
            const a = await this._loudnessEnvelope(originalUrl, frames);
            const b = await this._loudnessEnvelope(takeUrl, frames);

            // Per-region match (0..1) so we can color each word red/yellow/green: both envelopes are
            // normalized + resampled to the same frames, so they're time-aligned; a region's match is
            // 1 − (mean absolute envelope difference), scaled for sensitivity. Timbre-independent.
            //
            // Region boundaries come from the phrase's real per-word STT timing (the same data the
            // teleprompter uses) when the caller has it — words are not equal-width in time (a word
            // right before a comma pause is short, one that absorbs a pause is long), and dividing
            // `frames` into equal slices scored each word against the wrong slice of audio once
            // pacing was uneven. `wordBoundaries` is a flat [start0,end0,start1,end1,...] array of
            // fractions (0..1) of the phrase's window duration; falls back to equal division (the old
            // behavior) when a plain word count is passed instead.
            let boundaryPairs;
            if (Array.isArray(wordBoundaries) && wordBoundaries.length >= 2) {
                boundaryPairs = [];
                for (let i = 0; i + 1 < wordBoundaries.length; i += 2)
                    boundaryPairs.push([wordBoundaries[i], wordBoundaries[i + 1]]);
            } else {
                const rc = Math.max(1, Math.min(frames, Math.trunc(wordBoundaries) || 8));
                boundaryPairs = [];
                for (let r = 0; r < rc; r++) boundaryPairs.push([r / rc, (r + 1) / rc]);
            }

            const regions = [];
            for (const pair of boundaryPairs) {
                const s = Math.max(0, Math.min(frames - 1, Math.floor(pair[0] * frames)));
                const e = Math.max(s + 1, Math.min(frames, Math.ceil(pair[1] * frames)));
                let sum = 0, cnt = 0;
                for (let i = s; i < e; i++) { sum += Math.abs(a.env[i] - b.env[i]); cnt++; }
                const md = cnt ? sum / cnt : 1;
                regions.push(Math.max(0, Math.min(1, 1 - 1.6 * md)));
            }
            const meanMatch = regions.reduce((x, y) => x + y, 0) / (regions.length || 1);
            const dr = a.durationSec > 0 && b.durationSec > 0
                ? Math.min(a.durationSec, b.durationSec) / Math.max(a.durationSec, b.durationSec) : 0;
            const score = Math.round(100 * (0.8 * meanMatch + 0.2 * dr));
            return {
                success: true, score: Math.max(0, Math.min(100, score)), regions: regions,
                originalSec: +a.durationSec.toFixed(2), takeSec: +b.durationSec.toFixed(2),
            };
        } catch (err) {
            return { success: false, error: err.message || String(err) };
        }
    },

    /**
     * Decode audio → normalized RMS loudness envelope resampled to `frames` points, with leading and
     * trailing silence TRIMMED so two clips align at the start of speech (and the returned duration is
     * speech-only, making the comparison meaningful). No time-stretching — a length mismatch stays
     * visible in the score.
     */
    _loudnessEnvelope: async function (url, frames) {
        const resp = await fetch(url);
        const buf = await resp.arrayBuffer();
        const AC = window.AudioContext || window.webkitAudioContext;
        const ctx = new AC();
        try {
            const decoded = await ctx.decodeAudioData(buf);
            const ch = decoded.getChannelData(0);
            const n = ch.length;

            // Fine envelope first, so we can find the speech span.
            const fine = 400;
            const raw = new Float32Array(fine);
            const per = Math.max(1, Math.floor(n / fine));
            for (let i = 0; i < fine; i++) {
                let sum = 0, cnt = 0;
                const s = i * per, e = Math.min(n, s + per);
                for (let j = s; j < e; j++) { sum += ch[j] * ch[j]; cnt++; }
                raw[i] = cnt ? Math.sqrt(sum / cnt) : 0;
            }
            let mx = 0; for (let i = 0; i < fine; i++) if (raw[i] > mx) mx = raw[i];

            // Trim leading/trailing silence (bins below 8% of peak).
            const thr = mx * 0.08;
            let lo = 0, hi = fine - 1;
            while (lo < fine && raw[lo] < thr) lo++;
            while (hi > lo && raw[hi] < thr) hi--;
            if (lo >= hi) { lo = 0; hi = fine - 1; } // all quiet → keep everything
            const span = hi - lo + 1;

            // Resample the speech span to `frames`, normalized.
            const env = new Float32Array(frames);
            for (let i = 0; i < frames; i++) {
                const idx = lo + Math.floor(i * span / frames);
                env[i] = raw[Math.min(fine - 1, idx)];
            }
            let mx2 = 0; for (let i = 0; i < frames; i++) if (env[i] > mx2) mx2 = env[i];
            if (mx2 > 0) for (let i = 0; i < frames; i++) env[i] /= mx2;

            return { env: env, durationSec: decoded.duration * (span / fine) };
        } finally {
            try { await ctx.close(); } catch (_) { /* */ }
        }
    },

    /** Pearson correlation of two equal-length series, -1..1. */
    _pearson: function (x, y) {
        const n = Math.min(x.length, y.length);
        if (n === 0) return 0;
        let sx = 0, sy = 0;
        for (let i = 0; i < n; i++) { sx += x[i]; sy += y[i]; }
        const mx = sx / n, my = sy / n;
        let num = 0, dx = 0, dy = 0;
        for (let i = 0; i < n; i++) { const a = x[i] - mx, b = y[i] - my; num += a * b; dx += a * a; dy += b * b; }
        const den = Math.sqrt(dx * dy);
        return den > 1e-9 ? num / den : 0;
    },

    /**
     * Build the voice-clone sample from the kept takes: trim each take's ragged leading/trailing
     * silence (the countdown-to-speak delay), then stitch them back with a CONSISTENT natural pause
     * (`gapSec`) between them — so the clone keeps real between-sentence dead air instead of either a
     * random offset or run-on speech. Returns mono 16-bit PCM WAV bytes (Uint8Array → C# byte[]).
     */
    buildCloneSampleAsync: async function (urls, gapSec) {
        const list = Array.isArray(urls) ? urls.filter(u => u) : [];
        if (list.length === 0) throw new Error("no audio urls");
        const AC = window.AudioContext || window.webkitAudioContext;
        const ctx = new AC();
        try {
            const sr = ctx.sampleRate;
            const gap = Math.max(0, gapSec == null ? 0.4 : gapSec);
            const gapSamples = Math.round(gap * sr);
            const pad = Math.round(0.03 * sr); // keep 30 ms either side so we don't clip soft edges

            const slices = [];
            let total = 0;
            for (const url of list) {
                const resp = await fetch(url);
                const arr = await resp.arrayBuffer();
                let decoded;
                try { decoded = await ctx.decodeAudioData(arr); } catch (_) { continue; }
                const ch = decoded.getChannelData(0);
                const n = ch.length;

                // Fine RMS envelope → speech span (< 8% of peak = silence), same trim as the scorer.
                const fine = 400;
                const per = Math.max(1, Math.floor(n / fine));
                const raw = new Float32Array(fine);
                let mx = 0;
                for (let i = 0; i < fine; i++) {
                    let sum = 0, c = 0; const s = i * per, e = Math.min(n, s + per);
                    for (let j = s; j < e; j++) { sum += ch[j] * ch[j]; c++; }
                    raw[i] = c ? Math.sqrt(sum / c) : 0; if (raw[i] > mx) mx = raw[i];
                }
                const thr = mx * 0.08;
                let lo = 0, hi = fine - 1;
                while (lo < fine && raw[lo] < thr) lo++;
                while (hi > lo && raw[hi] < thr) hi--;
                if (lo >= hi) { lo = 0; hi = fine - 1; }

                const sStart = Math.max(0, lo * per - pad);
                const sEnd = Math.min(n, (hi + 1) * per + pad);
                if (sEnd > sStart) { slices.push(ch.subarray(sStart, sEnd)); total += (sEnd - sStart); }
            }
            if (slices.length === 0) throw new Error("no decodable takes");

            const totalLen = total + gapSamples * (slices.length - 1);
            const out = new Float32Array(totalLen); // gaps stay as zeros = the natural pause
            let pos = 0;
            for (let k = 0; k < slices.length; k++) {
                out.set(slices[k], pos);
                pos += slices[k].length;
                if (k < slices.length - 1) pos += gapSamples;
            }
            return this._encodeWavPcm16(out, sr);
        } finally {
            try { await ctx.close(); } catch (_) { /* */ }
        }
    },

    /** Encode a mono Float32 buffer to a 16-bit PCM WAV (Uint8Array). */
    _encodeWavPcm16: function (samples, sampleRate) {
        const n = samples.length;
        const buffer = new ArrayBuffer(44 + n * 2);
        const view = new DataView(buffer);
        const wr = function (off, str) { for (let i = 0; i < str.length; i++) view.setUint8(off + i, str.charCodeAt(i)); };
        wr(0, "RIFF"); view.setUint32(4, 36 + n * 2, true); wr(8, "WAVE");
        wr(12, "fmt "); view.setUint32(16, 16, true); view.setUint16(20, 1, true);
        view.setUint16(22, 1, true); view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
        wr(36, "data"); view.setUint32(40, n * 2, true);
        let off = 44;
        for (let i = 0; i < n; i++) {
            let s = Math.max(-1, Math.min(1, samples[i]));
            view.setInt16(off, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
            off += 2;
        }
        return new Uint8Array(buffer);
    },

    /**
     * Concatenate several kept take audio clips (blob/data URLs) into one mono 44.1 kHz WAV, returned
     * as raw bytes (Uint8Array → C# byte[]) for the voice-clone sample. Uses the concat FILTER (decodes
     * each) so mismatched containers/codecs still join cleanly. Throws on failure.
     */
    concatAudioToBytesAsync: async function (urls, onProgress) {
        const list = Array.isArray(urls) ? urls.filter(u => u) : [];
        if (list.length === 0) throw new Error("no audio urls");
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) throw new Error(load.error || "ffmpeg load failed");
            const ffmpeg = this._ffmpeg;
            const names = [];
            const outName = "cat_out.wav";
            try {
                const inputs = [];
                for (let i = 0; i < list.length; i++) {
                    const nm = "cat_in_" + i;
                    await ffmpeg.writeFile(nm, await this._safeFetchFile(list[i]));
                    names.push(nm);
                    inputs.push("-i", nm);
                }
                const labels = names.map((_, i) => "[" + i + ":a]").join("");
                const filter = labels + "concat=n=" + names.length + ":v=0:a=1[a]";
                await ffmpeg.exec(["-hide_banner", "-y", ...inputs,
                    "-filter_complex", filter, "-map", "[a]", "-ar", "44100", "-ac", "1", outName]);
                return await ffmpeg.readFile(outName); // Uint8Array → C# byte[]
            } finally {
                for (const n of names) { try { await ffmpeg.deleteFile(n); } catch (_) { /* */ } }
                try { await ffmpeg.deleteFile(outName); } catch (_) { /* */ }
            }
        });
    },

    /**
     * Turn a silencedetect log into non-silent [start,end] windows over [0,totalSec].
     * Silence runs are the complement of speech; a clip with no detected silence is one speech run.
     */
    _invertSilenceToSpeech: function (log, totalSec, minSilenceSec) {
        const total = totalSec > 0 ? totalSec : 0;
        const minGap = (typeof minSilenceSec === "number" && minSilenceSec > 0) ? minSilenceSec : 0.3;
        // Collect (start,end) silence intervals from the log.
        const silences = [];
        let curStart = null;
        const lines = String(log).split("\n");
        for (const line of lines) {
            let m = line.match(/silence_start:\s*(-?\d+(?:\.\d+)?)/);
            if (m) { curStart = Math.max(0, parseFloat(m[1])); continue; }
            m = line.match(/silence_end:\s*(-?\d+(?:\.\d+)?)/);
            if (m) {
                const end = parseFloat(m[1]);
                if (curStart !== null) { silences.push([curStart, end]); curStart = null; }
            }
        }
        if (curStart !== null && total > 0) silences.push([curStart, total]);

        // Speech = complement of silence within [0,total].
        if (total <= 0) {
            // Unknown duration: fall back to a single open window if any speech implied.
            return silences.length === 0 ? [] : [];
        }
        const speech = [];
        let cursor = 0;
        for (const [s, e] of silences) {
            const gs = Math.max(0, s);
            if (gs - cursor > 0.05) speech.push({ startSec: cursor, endSec: gs });
            cursor = Math.max(cursor, Math.min(total, e));
        }
        if (total - cursor > 0.05) speech.push({ startSec: cursor, endSec: total });

        // Merge windows separated by less than minGap (avoids chopping one line into fragments).
        const merged = [];
        for (const w of speech) {
            if (merged.length > 0 && w.startSec - merged[merged.length - 1].endSec < minGap) {
                merged[merged.length - 1].endSec = w.endSec;
            } else {
                merged.push({ startSec: w.startSec, endSec: w.endSec });
            }
        }
        return merged;
    },

    /**
     * Overlay cloned-voice speech clips onto a video's ORIGINAL audio at given time windows, ducking
     * (lowering) the original only during those windows so ambience/music/SFX stay intact everywhere
     * else. This is the client-side compose step for voice substitution — the API host never spawns
     * native ffmpeg.
     *
     * Each segment: { audioUrl, startSec, endSec }. The cloned audio is delayed to startSec; the
     * original track is ducked via a volume envelope that dips inside each [startSec,endSec] window.
     * If a cloned line is longer than its window it simply plays past it (over ducked original); a
     * future enhancement can atempo-fit it to the window (see design doc TODO).
     *
     * @param {string} videoUrl
     * @param {{audioUrl:string,startSec:number,endSec:number}[]} segments
     * @param {{duckVolume?:number}} [opts] duckVolume 0-1 for original during speech (default 0.15)
     * @returns {{ success:boolean, url?:string, error?:string }}
     */
    overlayVoiceSegmentsAsync: async function (videoUrl, segments, opts, onProgress) {
        if (!videoUrl) return { success: false, error: "No video URL" };
        const list = Array.isArray(segments) ? segments.filter(s => s && s.audioUrl) : [];
        if (list.length === 0) return { success: true, url: videoUrl }; // nothing to overlay

        opts = opts || {};
        const duck = Math.max(0, Math.min(1, opts.duckVolume != null ? opts.duckVolume : 0.15));
        // muteBase: drop the original clip audio entirely and use the cloned voice as the whole
        // soundtrack (narrator-only scenes) — no bed to duck, so no double voice.
        const muteBase = !!opts.muteBase;
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return load;

            const ffmpeg = this._ffmpeg;
            const inVideo = "ov_in_video.mp4";
            const outName = "ov_out.mp4";
            const audioNames = [];
            try {
                reportProgress(onProgress, 8, "Loading picture…");
                await ffmpeg.writeFile(inVideo, await this._safeFetchFile(videoUrl));

                console.log("[dub] overlay: " + list.length + " voice segment(s)");

                // Write + PRE-DECODE each cloned-voice clip to a clean 48 kHz stereo WAV. Feeding the
                // raw TTS mp3 straight into amix was unreliable: if ffmpeg.wasm couldn't decode that
                // particular mp3 the mix silently fell back to just the (ducked) base — "background,
                // no voice". Transcoding first makes decode failures loud and hands amix known-good PCM.
                for (let i = 0; i < list.length; i++) {
                    reportProgress(onProgress, 8 + Math.round((i / list.length) * 22),
                        "Loading voice " + (i + 1) + "/" + list.length + "…");
                    const seg = list[i];
                    // Flat log (survives console export) so bad timing is visible: start/end vs clip.
                    console.log("[dub] seg " + i + ": start=" + seg.startSec + "s end=" + seg.endSec + "s");
                    let ext = ".mp3";
                    if (/\.wav(\?|$)/i.test(seg.audioUrl) || (seg.audioUrl.indexOf("audio/wav") >= 0)) ext = ".wav";
                    else if (/\.m4a(\?|$)/i.test(seg.audioUrl) || (seg.audioUrl.indexOf("audio/mp4") >= 0)) ext = ".m4a";
                    const rawName = "ov_voice_raw_" + i + ext;
                    const wavName = "ov_voice_" + i + ".wav";
                    const bytes = await this._safeFetchFile(seg.audioUrl);
                    console.log("[dub] voice " + i + ": " + (bytes ? bytes.length : 0) + " bytes");
                    if (!bytes || bytes.length < 512) {
                        console.warn("[dub] voice " + i + " suspiciously small — TTS likely returned silence/empty.");
                    }
                    await ffmpeg.writeFile(rawName, bytes);
                    try {
                        await ffmpeg.exec(["-hide_banner", "-y", "-i", rawName,
                            "-ar", "48000", "-ac", "2", wavName]);
                    } catch (decErr) {
                        console.error("[dub] voice " + i + " decode→wav FAILED:", decErr && decErr.message);
                        try { await ffmpeg.deleteFile(rawName); } catch (_) { /* */ }
                        throw new Error("Cloned voice audio could not be decoded (segment " + i + ")");
                    }
                    try { await ffmpeg.deleteFile(rawName); } catch (_) { /* */ }
                    audioNames.push(wavName);
                }

                // Build filter_complex — deliberately simple. The earlier version used a per-frame
                // volume='if(between(t,…))' envelope on the bed plus an un-padded, short second input;
                // in ffmpeg.wasm that combination produced a mix where the (verified ~-28 dB) voice was
                // absent while the bed survived. This version drops both fragile pieces:
                //  - bed: constant gentle duck (no per-frame expression).
                //  - voice: format-normalized, delayed on ALL channels, boosted, and apad-ed to full
                //    length so amix never drops it partway.
                const inputs = ["-i", inVideo];
                for (const n of audioNames) inputs.push("-i", n);

                // aformat first — amix does not resample, so a rate/layout mismatch silences an input.
                const fmt = "aformat=sample_rates=48000:channel_layouts=stereo";

                const parts = [];
                let filter;
                if (muteBase) {
                    // Narrator-only scene: original clip audio is dropped; the cloned narration is the
                    // whole soundtrack. Rather than warp each line to its own window (varying pace), we
                    // calibrate ONE stretch factor from up to 3 sample points (median of natural ÷ window
                    // ratios) so the voice keeps a single consistent pace matched to the original speaker,
                    // then place each line at its window start via a leading silence and mix.
                    const segInfo = [];
                    for (let i = 0; i < list.length; i++) {
                        const seg = list[i];
                        const startSec = Math.max(0, +seg.startSec || 0);
                        const targetDur = Math.max(0.2, (+seg.endSec || 0) - startSec);
                        const probe = await this._probeDurationMemfsAsync(audioNames[i]);
                        const natSec = probe && probe.success && probe.seconds > 0 ? probe.seconds : targetDur;
                        segInfo.push({ i: i, startSec: startSec, natSec: natSec, ratio: natSec / targetDur });
                    }

                    // Sample up to 3 points (first / middle / last for a representative spread), take the
                    // median ratio → one calibrated stretch factor (atempo). Clamp so we never warble.
                    const sample = segInfo.length <= 3
                        ? segInfo.slice()
                        : [segInfo[0], segInfo[Math.floor(segInfo.length / 2)], segInfo[segInfo.length - 1]];
                    const ratios = sample.map(s => s.ratio).sort((a, b) => a - b);
                    let tempo = ratios.length ? ratios[Math.floor(ratios.length / 2)] : 1.0;
                    // Cap the stretch to a natural range: beyond ~±25% atempo starts to warble, so we
                    // prefer a natural-sounding voice (with gaps if the window is longer) over filling
                    // the window at any cost. Record close to the original pace and this barely engages.
                    tempo = Math.max(0.8, Math.min(1.25, tempo));
                    console.log("[dub] calibrated stretch factor: " + tempo.toFixed(3) +
                        " (from " + sample.length + " of " + segInfo.length + " line(s))");

                    const vl = [];
                    for (const s of segInfo) {
                        const voice = "[" + (s.i + 1) + ":a]" + fmt + ",atempo=" + tempo.toFixed(4) +
                            ",volume=1.3,asetpts=PTS-STARTPTS";
                        if (s.startSec >= 0.05) {
                            parts.push("anullsrc=channel_layout=stereo:sample_rate=48000,atrim=duration=" +
                                s.startSec.toFixed(3) + ",asetpts=PTS-STARTPTS[sil" + s.i + "]");
                            parts.push(voice + "[sv" + s.i + "]");
                            parts.push("[sil" + s.i + "][sv" + s.i + "]concat=n=2:v=0:a=1[v" + s.i + "]");
                        } else {
                            parts.push(voice + "[v" + s.i + "]");
                        }
                        vl.push("[v" + s.i + "]");
                    }
                    if (vl.length === 1)
                        parts.push(vl[0] + "apad[a]");
                    else
                        parts.push(vl.join("") + "amix=inputs=" + vl.length +
                            ":duration=longest:normalize=0,apad[a]");
                    filter = parts.join(";");
                } else {
                    // Duck the bed to ~0.30 so the narrator sits on top (ambience/music still audible).
                    parts.push("[0:a]" + fmt + ",volume=0.30[base]");
                    const mixLabels = ["[base]"];
                    for (let i = 0; i < list.length; i++) {
                        // No adelay (it zeroes the voice in this ffmpeg.wasm build); the narrator plays
                        // from the clip start, amix silence-pads the tail. Boost ~2.2× to sit over the bed.
                        parts.push("[" + (i + 1) + ":a]" + fmt + ",volume=2.2[v" + i + "]");
                        mixLabels.push("[v" + i + "]");
                    }
                    parts.push(mixLabels.join("") + "amix=inputs=" + mixLabels.length +
                        ":duration=first:normalize=0[a]");
                    filter = parts.join(";");
                }
                console.log("[dub] filter" + (muteBase ? " (muteBase)" : "") + ": " + filter);

                reportProgress(onProgress, 45, "Overlaying voice…");
                await ffmpeg.exec([
                    "-hide_banner", "-y",
                    ...inputs,
                    "-filter_complex", filter,
                    "-map", "0:v", "-map", "[a]",
                    "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                    "-shortest",
                    outName,
                ]);

                reportProgress(onProgress, 90, "Saving clip…");
                const url = await this._readAndCleanupAsync(
                    ffmpeg, outName, "video/mp4", [inVideo].concat(audioNames));
                reportProgress(onProgress, 100, "Ready");
                return { success: true, url: url };
            } catch (err) {
                console.error("overlayVoiceSegmentsAsync failed:", err);
                for (const n of [inVideo, outName].concat(audioNames)) {
                    try { await ffmpeg.deleteFile(n); } catch (_) { /* */ }
                }
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    encodeSliceAsync: async function (token, startSec, durationSec, onProgress) {
        return this._runExclusiveAsync(async () => {
            const inName = this._silenceSessions[token];
            if (!inName) return { success: false, error: "Unknown or expired silence-trim session" };
            delete this._silenceSessions[token];

            const ffmpeg = this._ffmpeg;
            const outName = token + "_out.mp4";
            try {
                reportProgress(onProgress, 55, "Re-encoding trimmed clip…");
                const args = ["-hide_banner", "-y"];
                if (startSec > 0.001) args.push("-ss", String(startSec));
                args.push("-i", inName);
                args.push("-t", String(Math.max(0.5, durationSec)));
                args.push(
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
                    "-c:a", "aac", "-b:a", "128k",
                    "-movflags", "+faststart",
                    outName);
                await ffmpeg.exec(args);

                reportProgress(onProgress, 90, "Preparing…");
                const out = await ffmpeg.readFile(outName);
                const blob = new Blob([out.buffer], { type: "video/mp4" });
                const outUrl = URL.createObjectURL(blob);
                reportProgress(onProgress, 100, "Silence trim done");
                return { success: true, url: outUrl };
            } catch (err) {
                return { success: false, error: err.message || String(err) };
            } finally {
                try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                try { await ffmpeg.deleteFile(outName); } catch (_) { /* */ }
            }
        });
    },

    // Trims a video down to its last `keepSeconds` — used to prepare a video-extend continuation
    // source (see FilmJobService.GenerateOneClipAsync): the model rejects input video longer than
    // its own max clip length, so the client keeps only the tail before uploading it. Standalone
    // (not tied to the silence-trim session bookkeeping that encodeSliceAsync uses) since the
    // caller only ever wants one trim, not an analyze-then-slice round trip.
    trimTailAsync: async function (url, keepSeconds, onProgress) {
        if (!url) return { success: false, error: "No URL" };
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error };

            const ffmpeg = this._ffmpeg;
            const seq = ++this._trimTailSeq;
            const inName = "trimtail_in_" + seq + ".mp4";
            const outName = "trimtail_out_" + seq + ".mp4";
            try {
                reportProgress(onProgress, 10, "Loading clip…");
                const data = await this._safeFetchFile(url);
                await ffmpeg.writeFile(inName, data);

                reportProgress(onProgress, 30, "Probing duration…");
                const probe = await this._probeDurationMemfsAsync(inName);
                if (!probe.success || !(probe.seconds > 0)) {
                    return { success: false, error: "Could not read source duration" };
                }

                const totalSec = probe.seconds;
                const keepSec = Math.max(0.5, Math.min(keepSeconds, totalSec));
                const startSec = Math.max(0, totalSec - keepSec);

                reportProgress(onProgress, 55, "Trimming tail…");
                const args = ["-hide_banner", "-y"];
                if (startSec > 0.001) args.push("-ss", String(startSec));
                args.push("-i", inName);
                args.push("-t", String(keepSec));
                args.push(
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
                    "-c:a", "aac", "-b:a", "128k",
                    "-movflags", "+faststart",
                    outName);
                await ffmpeg.exec(args);

                reportProgress(onProgress, 90, "Preparing…");
                const out = await ffmpeg.readFile(outName);
                const blob = new Blob([out.buffer], { type: "video/mp4" });
                const outUrl = URL.createObjectURL(blob);
                reportProgress(onProgress, 100, "Trim done");
                return { success: true, url: outUrl, sourceDurationSec: totalSec, keptSec: keepSec };
            } catch (err) {
                return { success: false, error: err.message || String(err) };
            } finally {
                try { await ffmpeg.deleteFile(inName); } catch (_) { /* */ }
                try { await ffmpeg.deleteFile(outName); } catch (_) { /* */ }
            }
        });
    },

    discardSessionAsync: async function (token) {
        return this._runExclusiveAsync(async () => {
            const inName = this._silenceSessions[token];
            if (!inName) return { success: true };
            delete this._silenceSessions[token];
            try { await this._ffmpeg.deleteFile(inName); } catch (_) { /* */ }
            return { success: true };
        });
    },

    extractFramesAsync: async function (url, opts, onProgress) {
        opts = opts || {};
        if (!url) return { success: false, error: "No URL" };
        const mode = (opts.mode || "span").toLowerCase();
        const count = Math.max(1, Math.min(6, opts.count != null ? opts.count : (mode === "tail" ? 3 : 3)));
        const maxWidth = opts.maxWidth != null ? opts.maxWidth : 640;
        const quality = opts.quality != null ? opts.quality : 5;
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return { success: false, error: load.error || "ffmpeg load failed" };

            const ffmpeg = this._ffmpeg;
            const inName = "frame_in.mp4";
            const written = [];
            try {
                reportProgress(onProgress, 10, "Loading video for frames…");
                const data = await this._safeFetchFile(url);
                await ffmpeg.writeFile(inName, data);
                written.push(inName);

                const scale = "scale='min(" + maxWidth + ",iw)':-2";
                const pattern = "frame_%02d.jpg";
                reportProgress(onProgress, 40, mode === "tail" ? "Sampling clip end…" : "Sampling clip…");

                try {
                    if (mode === "tail") {
                        await ffmpeg.exec([
                            "-hide_banner", "-y",
                            "-sseof", "-1.5",
                            "-i", inName,
                            "-vf", scale + ",fps=2",
                            "-frames:v", String(count),
                            "-q:v", String(quality),
                            pattern,
                        ]);
                    } else {
                        await ffmpeg.exec([
                            "-hide_banner", "-y",
                            "-i", inName,
                            "-vf", scale + ",fps=1/2",
                            "-frames:v", String(count),
                            "-q:v", String(quality),
                            pattern,
                        ]);
                    }
                } catch (execErr) {
                    this._log("frame extract primary failed: " + (execErr && execErr.message));
                    try {
                        await ffmpeg.exec([
                            "-hide_banner", "-y",
                            "-ss", "0.5",
                            "-i", inName,
                            "-vf", scale,
                            "-frames:v", "1",
                            "-q:v", String(quality),
                            "frame_01.jpg",
                        ]);
                    } catch (fbErr) {
                        return {
                            success: false,
                            error: "Frame extract failed: " + (fbErr.message || String(fbErr)),
                        };
                    }
                }

                reportProgress(onProgress, 80, "Encoding frames…");
                const frames = [];
                for (let i = 1; i <= count + 2; i++) {
                    const name = "frame_" + String(i).padStart(2, "0") + ".jpg";
                    try {
                        const out = await ffmpeg.readFile(name);
                        written.push(name);
                        if (!out || !out.length) continue;
                        const bytes = out instanceof Uint8Array ? out : new Uint8Array(out.buffer || out);
                        if (bytes.length < 64) continue;
                        frames.push({
                            base64: this._bytesToBase64(bytes),
                            mime: "image/jpeg",
                        });
                    } catch (_) {
                        if (i > 1) break;
                    }
                }

                for (const n of written) {
                    try { await ffmpeg.deleteFile(n); } catch (_) { /* */ }
                }

                if (frames.length === 0)
                    return { success: false, error: "No frames produced" };

                reportProgress(onProgress, 100, "Frames ready");
                return { success: true, frames: frames };
            } catch (err) {
                for (const n of written) {
                    try { await ffmpeg.deleteFile(n); } catch (_) { /* */ }
                }
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    _bytesToBase64: function (bytes) {
        let binary = "";
        const chunk = 0x8000;
        for (let i = 0; i < bytes.length; i += chunk) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
        }
        return btoa(binary);
    },

    /**
     * Concatenate ordered background-music segment URLs (WAV) into one continuous AAC track.
     * Segments come from IAudioClient.MaxSegmentDurationSeconds-sized provider calls (see
     * FilmJobService's music job) — most scenes produce just one segment, handled as a no-op.
     * @param {string[]} urls
     * @returns {{ success:boolean, url?:string, error?:string }}
     */
    concatAudioSegmentsAsync: async function (urls, onProgress) {
        const list = Array.isArray(urls) ? urls.filter(u => typeof u === "string" && u.length > 0) : [];
        if (list.length === 0) return { success: false, error: "No audio URLs to combine" };
        if (list.length === 1) {
            reportProgress(onProgress, 100, "Ready");
            return { success: true, url: list[0], single: true };
        }
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return load;

            const ffmpeg = this._ffmpeg;
            let written = [];
            try {
                reportProgress(onProgress, 12, "Downloading music segments…");
                written = await this._writeSequentialInputsAsync(ffmpeg, list, "wav", onProgress, 12, 55);

                const listBody = written.map(n => "file '" + n + "'").join("\n");
                await ffmpeg.writeFile("music_list.txt", listBody);

                reportProgress(onProgress, 60, "Combining music…");
                await ffmpeg.exec([
                    "-f", "concat", "-safe", "0", "-i", "music_list.txt",
                    "-c:a", "aac", "-b:a", "192k",
                    "out_music.m4a",
                ]);

                reportProgress(onProgress, 90, "Preparing…");
                const url = await this._readAndCleanupAsync(
                    ffmpeg, "out_music.m4a", "audio/mp4", written.concat(["music_list.txt"]));
                reportProgress(onProgress, 100, "Ready");
                return { success: true, url: url };
            } catch (err) {
                console.error("concatAudioSegmentsAsync failed:", err);
                for (const n of written) { try { await ffmpeg.deleteFile(n); } catch (_) { /* */ } }
                try { await ffmpeg.deleteFile("music_list.txt"); } catch (_) { /* */ }
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    /**
     * Layer a background-music track under a scene video with volume ducking + a 1.5s fade-out,
     * replacing the server-side ffmpeg filter_complex this feature used to run — the API host
     * never spawns native ffmpeg, so this composite step happens entirely in the browser now,
     * the same as clip/scene stitching.
     * @param {string} videoUrl
     * @param {string} musicUrl single (already-concatenated) music track URL
     * @param {number} volumePercent 0-100
     * @returns {{ success:boolean, url?:string, error?:string }}
     */
    mixSceneAudioAsync: async function (videoUrl, musicUrl, volumePercent, onProgress) {
        if (!videoUrl) return { success: false, error: "No video URL" };
        if (!musicUrl) return { success: true, url: videoUrl }; // nothing to mix — pass through

        const volRatio = Math.max(0.05, Math.min(1.0, (volumePercent != null ? volumePercent : 20) / 100));
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return load;

            const ffmpeg = this._ffmpeg;
            const inVideo = "mix_in_video.mp4";
            const inMusic = "mix_in_music.m4a";
            const outName = "mix_out.mp4";
            try {
                reportProgress(onProgress, 10, "Loading video…");
                await ffmpeg.writeFile(inVideo, await this._safeFetchFile(videoUrl));
                reportProgress(onProgress, 30, "Loading music…");
                await ffmpeg.writeFile(inMusic, await this._safeFetchFile(musicUrl));

                const probe = await this._probeDurationMemfsAsync(inVideo);
                const durationSec = probe.success && probe.seconds > 0 ? probe.seconds : 0;
                const fadeStart = Math.max(0, durationSec - 1.5);
                const musicFilter = "[1:a]volume=" + volRatio.toFixed(2) +
                    (durationSec > 0 ? ",afade=t=out:st=" + fadeStart.toFixed(1) + ":d=1.5" : "") +
                    "[bg]";

                reportProgress(onProgress, 50, "Mixing audio…");
                await ffmpeg.exec([
                    "-hide_banner", "-y",
                    "-i", inVideo, "-i", inMusic,
                    "-filter_complex", musicFilter + ";[0:a][bg]amix=inputs=2:duration=first[a]",
                    "-map", "0:v", "-map", "[a]",
                    "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                    "-shortest",
                    outName,
                ]);

                reportProgress(onProgress, 90, "Preparing player…");
                const url = await this._readAndCleanupAsync(ffmpeg, outName, "video/mp4", [inVideo, inMusic]);
                reportProgress(onProgress, 100, "Ready");
                return { success: true, url: url };
            } catch (err) {
                console.error("mixSceneAudioAsync failed:", err);
                for (const n of [inVideo, inMusic, outName]) { try { await ffmpeg.deleteFile(n); } catch (_) { /* */ } }
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    /**
     * Strip all original audio from a clip and replace with a single TTS (or other) track.
     * Video stream is copied; new audio is AAC. If TTS is shorter than video, pad with silence;
     * if longer, cut to video length (-shortest against padded audio matching video duration).
     * @param {string} videoUrl blob: or http(s) URL
     * @param {string} audioUrl blob: / data: / http(s) URL for the replacement speech
     * @returns {{ success:boolean, url?:string, error?:string }}
     */
    replaceVideoAudioAsync: async function (videoUrl, audioUrl, onProgress) {
        if (!videoUrl) return { success: false, error: "No video URL" };
        if (!audioUrl) return { success: false, error: "No audio URL" };
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return load;

            const ffmpeg = this._ffmpeg;
            const inVideo = "rv_in_video.mp4";
            const inAudio = "rv_in_audio";
            const outName = "rv_out.mp4";
            try {
                reportProgress(onProgress, 8, "Loading picture…");
                await ffmpeg.writeFile(inVideo, await this._safeFetchFile(videoUrl));
                reportProgress(onProgress, 28, "Loading voice…");
                // Keep extension so ffmpeg can sniff container (mp3/wav/m4a)
                let audioName = inAudio + ".bin";
                if (typeof audioUrl === "string") {
                    if (audioUrl.indexOf("audio/wav") >= 0 || /\.wav(\?|$)/i.test(audioUrl)) audioName = inAudio + ".wav";
                    else if (audioUrl.indexOf("audio/mp4") >= 0 || /\.m4a(\?|$)/i.test(audioUrl)) audioName = inAudio + ".m4a";
                    else if (audioUrl.indexOf("audio/mpeg") >= 0 || /\.mp3(\?|$)/i.test(audioUrl)) audioName = inAudio + ".mp3";
                    else audioName = inAudio + ".mp3";
                }
                await ffmpeg.writeFile(audioName, await this._safeFetchFile(audioUrl));

                const probe = await this._probeDurationMemfsAsync(inVideo);
                const durationSec = probe.success && probe.seconds > 0 ? probe.seconds : 0;

                reportProgress(onProgress, 50, "Replacing audio…");
                // Drop original audio entirely; use TTS only. Pad TTS with silence to video length
                // when known so picture does not cut short; -shortest still guards runaway audio.
                if (durationSec > 0.05) {
                    const filter =
                        "[1:a]aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=mono," +
                        "apad=whole_dur=" + durationSec.toFixed(3) + "[a]";
                    await ffmpeg.exec([
                        "-hide_banner", "-y",
                        "-i", inVideo, "-i", audioName,
                        "-filter_complex", filter,
                        "-map", "0:v", "-map", "[a]",
                        "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                        "-t", durationSec.toFixed(3),
                        outName,
                    ]);
                } else {
                    await ffmpeg.exec([
                        "-hide_banner", "-y",
                        "-i", inVideo, "-i", audioName,
                        "-map", "0:v", "-map", "1:a",
                        "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                        "-shortest",
                        outName,
                    ]);
                }

                reportProgress(onProgress, 90, "Saving clip…");
                const url = await this._readAndCleanupAsync(
                    ffmpeg, outName, "video/mp4", [inVideo, audioName]);
                reportProgress(onProgress, 100, "Ready");
                return { success: true, url: url };
            } catch (err) {
                console.error("replaceVideoAudioAsync failed:", err);
                for (const n of [inVideo, outName]) { try { await ffmpeg.deleteFile(n); } catch (_) { /* */ } }
                return { success: false, error: err.message || String(err) };
            }
        });
    },

    /**
     * Strip all audio from a video (silent picture). Used when a clip has no dialogue.
     * @param {string} videoUrl
     * @returns {{ success:boolean, url?:string, error?:string }}
     */
    stripVideoAudioAsync: async function (videoUrl, onProgress) {
        if (!videoUrl) return { success: false, error: "No video URL" };
        return this._runExclusiveAsync(async () => {
            const load = await this.ensureLoadedAsync(onProgress);
            if (!load.success) return load;
            const ffmpeg = this._ffmpeg;
            const inVideo = "sa_in.mp4";
            const outName = "sa_out.mp4";
            try {
                reportProgress(onProgress, 20, "Loading picture…");
                await ffmpeg.writeFile(inVideo, await this._safeFetchFile(videoUrl));
                reportProgress(onProgress, 55, "Removing audio…");
                await ffmpeg.exec([
                    "-hide_banner", "-y",
                    "-i", inVideo,
                    "-map", "0:v", "-an",
                    "-c:v", "copy",
                    outName,
                ]);
                reportProgress(onProgress, 90, "Saving…");
                const url = await this._readAndCleanupAsync(ffmpeg, outName, "video/mp4", [inVideo]);
                reportProgress(onProgress, 100, "Ready");
                return { success: true, url: url };
            } catch (err) {
                console.error("stripVideoAudioAsync failed:", err);
                for (const n of [inVideo, outName]) { try { await ffmpeg.deleteFile(n); } catch (_) { /* */ } }
                return { success: false, error: err.message || String(err) };
            }
        });
    },
};
