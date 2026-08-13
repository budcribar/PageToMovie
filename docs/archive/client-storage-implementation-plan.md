# Implementation Plan — Client-Side MP4 Storage as Primary Path (Reviewed & Hardened)

> **Second review pass (2026-07-26)** caught issues in the original hardening — most importantly,
> **Step 2's proposed fix is a regression**: it would silently drop every clip except the last one
> in a multi-clip scene/batch generation. Corrected approach is inline below each affected step.
>
> **All 7 steps are now implemented and tested (2026-07-26)** — see the "Approved Ship Sequence"
> table at the bottom for final status per step, and each step's own "Status" line for what changed
> versus the original draft and what test coverage backs it.

## Background

The server is running out of disk and memory under load. **Most of the core infrastructure already exists** — the generation pipeline issues 45-minute proxy tickets (`ClientMediaUrl`) so the browser can download clips directly into user folders via the JS File System Access API. 

This document details the refined step-by-step implementation plan incorporating architectural review feedback.

---

## What Already Works (Do Not Re-Build)

| Component | File | Status |
|---|---|---|
| Clip generation hands proxy URL to browser (not server disk) | `FilmJobService.cs` L2580–2594 | ✅ Done |
| Server media proxy endpoint (CORS-safe 45-min ticket) | `Program.cs` L3863 `/api/media/proxy/{token}` | ✅ Done |
| `MediaProxyTicketStore` issues short-lived download tickets | `Program.cs` L131 | ✅ Done |
| `JobSnapshot.ClientMediaUrl` + `ClientRelativePath` populated | `FilmJobService.cs` L2588–2589 | ✅ Done |
| JS File System Access API (`showDirectoryPicker`, read/write) | `pagetomovie-media.js` | ✅ Done |
| SHA-256 computed in browser, written to server registry | `pagetomovie-media.js` `_sha256Hex` | ✅ Done |
| ffmpeg.wasm silence trim before save | `ClientMediaFolderService.cs` `SilenceTrimAsync` | ✅ Done |
| `ClientMediaFolderService` auto-saves on `JobUpdated` event | `ClientMediaFolderService.cs` L40–51 | ✅ Done |
| `ClientVideoStitchService` prefers local blob URL over server proxy | `ClientVideoStitchService.cs` L70–74 | ✅ Done |
| Clip history archived to `assets/video/history/` before overwrite | `pagetomovie-media.js` `_archiveClipHistoryAsync` | ✅ Done |
| `.client.json` marker file recognised as "clip present" | `FilmJobService.cs` `ClipPresentOnServerOrClient` | ✅ Done |
| `MediaRegistryService` stores sha256 + path in SQLite | `MediaRegistryService.cs` | ✅ Done |
| `ServerMediaPruningService` purges server media after 48h / 80% disk (sync-confirmed via `MediaRegistryService`, off by default) | `ServerMediaPruningService.cs` | ✅ Done |
| `.client.json` marker already written on verified registration | `Program.cs` ~L3990 inside `POST /api/projects/{id}/media/register` | ✅ Done — **missing from the original table**; Step 3 below was proposing to re-add this as new work |

---

## Hardened Implementation Steps

### Step 1: Stream Media Proxy Response Without Premature Disposal

**File:** `host/PageToMovie.Api/Program.cs` (`/api/media/proxy/{token}`)

**Problem (confirmed real):** `ReadAsByteArrayAsync` buffers entire MP4 files (20–100 MB) into server RAM before returning `Results.File`. Under concurrent client downloads, this causes immediate server Memory (OOM) crashes.

**Original fix had two bugs, both corrected here:**
1. If `ReadAsStreamAsync` throws (between `GetAsync` succeeding and `Results.Stream` being returned), the original draft's `catch` block returned `Results.BadRequest` without ever disposing `resp`/`http` — a real leak on exactly the error path most likely to occur under load.
2. `new HttpClient()` per request is the classic anti-pattern that causes socket exhaustion under concurrent load — the same failure mode this whole fix exists to prevent. Use `IHttpClientFactory` (already used elsewhere in this codebase, e.g. `AddHttpClient<GeminiChatClient>`), not a raw `new HttpClient()`.

**Corrected fix:** stream via `Results.Stream`, wrap the whole thing in try/catch-with-cleanup so every exit path disposes `resp`, and get the client from `IHttpClientFactory`.

```csharp
app.MapGet("/api/media/proxy/{token}", async (
    string token,
    MediaProxyTicketStore tickets,
    IHttpClientFactory httpFactory,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var url = tickets.TryTakeUrl(token);
    if (string.IsNullOrWhiteSpace(url))
        return Results.NotFound(new { ok = false, error = "Media ticket expired or invalid" });

    var http = httpFactory.CreateClient("media-proxy");
    HttpResponseMessage? resp = null;
    try
    {
        resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            resp.Dispose();
            return Results.Json(new { ok = false, error = $"Upstream HTTP {code}" }, statusCode: code);
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var ctype = resp.Content.Headers.ContentType?.ToString() ?? "video/mp4";
        // Results.Stream in this ASP.NET Core version has no completion callback param (the original
        // draft's `onCompleted:` doesn't compile — CS1739). RegisterForDispose is the real equivalent:
        // it disposes resp once the response body finishes writing, on every exit path.
        httpContext.Response.RegisterForDispose(resp);
        return Results.Stream(stream, contentType: ctype, fileDownloadName: "clip.mp4");
    }
    catch (Exception ex)
    {
        resp?.Dispose(); // covers the ReadAsStreamAsync-throws case the original draft missed
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});
```

`IHttpClientFactory` client registration (near the other `AddHttpClient<T>` calls): `builder.Services.AddHttpClient("media-proxy", c => c.Timeout = TimeSpan.FromMinutes(10));`

**Status: ✅ Done** — implemented in `Program.cs` exactly as above (adjusted for the `Results.Stream` overload actually available: no `onCompleted`, so disposal is done via `HttpContext.Response.RegisterForDisposeAsync` instead).

---

### Step 2: Idempotent Early Hub Hooking (Global + Page Level) & Double-Fire Prevention

**Files:** `host/PageToMovie.Web/Components/Layout/MainLayout.razor`, `host/PageToMovie.Web/Services/ClientMediaFolderService.cs`

**1. Early Registration (Lifetime Safety) — unchanged, this part is correct:**
Instead of hooking `EnsureHubHookAsync()` only inside `Scenes.razor` (which disposes if the user navigates away mid-generation), invoke `EnsureHubHookAsync()` idempotently inside `MainLayout.razor` on session start:
```csharp
// In MainLayout.razor OnAfterRenderAsync:
if (firstRender)
{
    await MediaFolder.EnsureHubHookAsync();
}
```
`EnsureHubHookAsync()` is idempotent (`if (_hubHooked) return;`).

**2. Double-Fire Prevention — original approach was a regression, corrected here.**

The original draft proposed restricting `OnJobUpdated` to save only when `snap.Status == "done"`. **This breaks multi-clip generation.** Traced precisely: `ClientMediaUrl`/`ClientRelativePath` are set inside `FilmJobService.GenerateOneClipAsync`, which `RunBatchGenAsync` calls in a loop for every clip in a scene — the job's overall `Status` stays `"running"` for the entire loop and only flips to `"done"` once, after all clips finish. Since `ClientMediaUrl` gets overwritten on each clip, a `Status=="done"`-only guard would mean only the *last* clip of a multi-clip scene ever gets saved to the client's folder — every earlier clip's "running" tick (the only time its URL is live) would be silently ignored.

The double-fetch this step is actually trying to prevent is real, but for a different reason: the existing `_savingKeys` lock only blocks *concurrent* duplicate saves for the same path — it doesn't stop a second, *sequential* notification for a path that already finished saving (e.g. a single-clip job's "running" tick saves the clip, then its "done" tick — carrying the same URL — triggers a second, wasted download+hash+write). The fix is to remember which paths have *already completed*, not to gate on job status:

```csharp
// New field alongside _savingKeys:
private readonly HashSet<string> _savedKeys = new(StringComparer.OrdinalIgnoreCase);

private void OnJobUpdated(JobSnapshot snap)
{
    if (snap is null) return;
    if (!string.Equals(snap.Status, "done", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(snap.Status, "running", StringComparison.OrdinalIgnoreCase))
        return;
    if (string.IsNullOrWhiteSpace(snap.ClientMediaUrl) ||
        string.IsNullOrWhiteSpace(snap.ClientRelativePath) ||
        string.IsNullOrWhiteSpace(snap.ProjectId))
        return;

    var key = $"{snap.ProjectId}|{snap.ClientRelativePath}";
    lock (_savingKeys)
    {
        if (_savedKeys.Contains(key)) return; // already completed — the later "done" tick for this same path is a no-op
    }
    _ = SaveJobMediaAsync(snap);
}
```
`SaveJobMediaAsync`'s success path adds `key` to `_savedKeys` (alongside the existing `_savingKeys.Remove(key)` in its `finally`). This fixes the single-clip double-fire (second notification for an already-saved path is skipped) without dropping any clip in a multi-clip batch (each clip has its own distinct path, saved exactly once on its own first sighting).

**Status: ✅ Done** — implemented in `ClientMediaFolderService.cs` as above. Covered by `ClientMediaFolderServiceTests.cs` (`PageToMovie.Tests`, new `ProjectReference` to `PageToMovie.Web` added): one test asserts every clip in a 2-clip "running"-only batch gets saved, the other asserts a later "done" tick for an already-saved path is a no-op.

---

### Step 3: `.client.json` Marker — Already Done, No New Code Needed

**This already exists** — `Program.cs` (`POST /api/projects/{id}/media/register`) writes it today:
```csharp
var marker = full + ".client.json";
await File.WriteAllTextAsync(marker, System.Text.Json.JsonSerializer.Serialize(new
{
    storage = "client",
    sha256 = dto.Sha256,
    sizeBytes = dto.SizeBytes,
    registeredAt = dto.CreatedAt,
    userId = user.UserId,
}) + "\n", ct);
```
— guarded by `Directory.CreateDirectory(Path.GetDirectoryName(full)!);` right before it, so it never silently skips writing the marker for a brand-new scene folder (the original draft's proposed snippet used `if (Directory.Exists(dir))` instead of creating it, which would have been a real regression for exactly that case — several places, `CreditsGeneratorService` and `ReviewIndexService` among them, treat a missing marker as "clip not present").

**Action:** none. Do not add a second copy of this logic to `MediaRegistryService` — it would either duplicate the write or, if it replaced the existing one, reintroduce the directory-creation gap above. If a later refactor wants this centralized in the service instead of the endpoint, that's a plain code-move, not new functionality.

---

### Step 4: Add User-Facing "Connect Folder" Banner in `Scenes.razor`

**Files:** `host/PageToMovie.Web/Components/Pages/Scenes.razor`, `NavMenu.razor`

**Unified State & User-Centric Language:**
Reuse `ClientMediaFolderService.IsConnected` and `FolderName` (shared with `NavMenu`). Avoid technical jargon like "Railway" or "server disk".

```html
@if (!MediaFolder.IsConnected)
{
    <div class="alert alert-warning d-flex align-items-center justify-content-between py-2 mb-3">
        <span>📁 <strong>Connect a folder</strong> so clips save on this computer.</span>
        <button class="btn btn-sm btn-warning ms-3 text-nowrap" @onclick="ConnectMediaFolderAsync">Connect Folder</button>
    </div>
}
else
{
    <div class="alert alert-success d-flex align-items-center justify-content-between py-2 mb-3">
        <span>✅ Saving clips to: <strong>@MediaFolder.FolderName</strong></span>
    </div>
}
```

**Adjusted for what already exists.** `NavMenu.razor` (L173–175) already shows a persistent connected/disconnected indicator (`"Media: {FolderName}"` vs `"Connect media folder…"`), so the `else` "✅ Saving clips to…" half of this snippet would just duplicate that on every `Scenes.razor` render — skipped. `Scenes.razor` also already has a *reactive* warning (`MediaFolder.LocalSaveWarning`, feature 8) that appears only after a save attempt without a connected folder. What was actually missing was the *proactive* nudge before that ever happens; added right above the cast-readiness gate, guarded so it doesn't double up with the reactive warning:

```html
@if (!MediaFolder.IsConnected && string.IsNullOrEmpty(MediaFolder.LocalSaveWarning))
{
    <div class="alert alert-warning d-flex flex-wrap align-items-center justify-content-between gap-2 py-2 mb-3"
         data-testid="scenes-connect-folder-banner">
        <span>📁 <strong>Connect a folder</strong> so clips save on this computer.</span>
        <button type="button" class="btn btn-sm btn-warning text-nowrap" disabled="@_busy"
                @onclick="ConnectMediaFolderFromWarningAsync">
            Connect folder
        </button>
    </div>
}
```

**Status: ✅ Done** — implemented in `Scenes.razor`, reusing the existing `ConnectMediaFolderFromWarningAsync` handler (no new connect-flow code needed).

---

### Step 5: Aggressive Server MP4 Prune Pass When `.client.json` Marker Exists

**File:** `host/PageToMovie.Engine/ServerMediaPruningService.cs`

**Original snippet doesn't match the current file — corrected here.** The service has already been rewritten (as part of the public-community-plan work) to route every deletion through `CollectSyncedMediaFilesAsync`, which itself only returns files with a confirmed `MediaRegistryService.TryGetAsync` match — there is no `rootPath` parameter (root is resolved as `Path.Combine(_projects.WorkspaceRoot, "projects")` inside `PerformPruningAsync(TimeSpan maxAge, double maxDiskPercent, CancellationToken ct)`), and `_logger` is a non-nullable constructor-injected field, so the `_logger?.` null-conditionals in the original snippet don't apply.

More importantly, a raw "marker exists ⇒ delete immediately, zero grace period" pass is riskier than it looks: the `.client.json` marker is written server-side as soon as the browser's registration POST arrives claiming a successful local save, but that claim races the actual local write completing (tab closed mid-write, disk full on the client, etc.). The existing age-based pass already deletes marker-confirmed (synced) files — deleting them *instantly* on marker-write removes the only remaining window in which a bad client claim could be noticed (e.g. by a future "did the client actually re-open this file" reconciliation check) before the server's copy is gone for good.

**Corrected approach:** don't add a separate raw-file-scanning "Pass 0" — reuse the existing marker-confirmed candidate list and prune it with a short grace period instead of the full `MaxFileAgeHours` window:

```csharp
// New option: PageToMovieOptions.MediaPruning.AggressivePruneGraceMinutes (default e.g. 5)
var aggressiveCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _opts.AggressivePruneGraceMinutes));

foreach (var c in candidates.Where(c => c.LastWriteTimeUtc < aggressiveCutoff).ToList())
{
    if (TryDelete(c))
    {
        deletedCount++;
        candidates.Remove(c);
    }
}
```

This slots in as a new pass before the existing age-based Pass 1 (which becomes redundant once this runs, since `aggressiveCutoff` is always more permissive than `cutoff`, but is harmless to leave as a fallback), reuses `TryDelete`/`CollectSyncedMediaFilesAsync` as-is, and keeps a small buffer instead of zero.

**Status: ✅ Done** — implemented in `ServerMediaPruningService.cs` and `PageToMovieOptions.cs` (`MediaPruningOptions.AggressivePruneGraceMinutes`, default 5) as above. Covered by two new `ServerMediaPruningServiceTests` cases: one confirms a synced file past the grace period is deleted even though it's nowhere near `MaxFileAgeHours`, the other confirms a synced file still inside the grace period survives.

---

### Step 6: Folder Persistence (localStorage + Quick Re-connect)

**Files:** `host/PageToMovie.Web/wwwroot/js/pagetomovie-media.js`, `ClientMediaFolderService.cs`

**Original approach doesn't actually work — corrected here.** `localStorage.setItem('ptm_media_folder', this._root.name)` only remembers the folder's *name string*. `showDirectoryPicker()` requires a fresh user gesture (a click) on every call, has no way to accept a remembered name or path to pre-select, and does not skip its dialog just because a folder with that name was previously chosen — "Chrome/Edge remember the directory selection, making reconnection seamless" is not how the API behaves. Re-running `showDirectoryPicker()` on reload would still pop the full OS folder-browser dialog and requires the user to navigate to and re-select the folder themselves — a real click-through, not a 1-click reconnect.

**Corrected approach:** `FileSystemDirectoryHandle` objects are structured-cloneable, so they can be persisted directly in IndexedDB (not `localStorage`, which only stores strings). On disconnect/reload:
1. Read the saved `FileSystemDirectoryHandle` from IndexedDB instead of re-prompting with `showDirectoryPicker()`.
2. Call `await handle.queryPermission({ mode: 'readwrite' })`. If `'granted'`, reconnect immediately with no dialog at all.
3. If `'prompt'` (the common case after a reload — permission grants don't survive a full page reload in most browsers), show the **"Reconnect {folderName}"** button and call `await handle.requestPermission({ mode: 'readwrite' })` from its click handler (still needs a user gesture, but re-grants permission on the *same* handle/folder — no folder-browser dialog, no re-navigating).

This is the only path that gets an actual 1-click reconnect to the *same previously-chosen* folder; the name-string-in-localStorage approach can't do that regardless of implementation effort.

**Status: ✅ Done** — implemented in `pagetomovie-media.js` (`_saveHandleToDbAsync`/`_loadHandleFromDbAsync` against an IndexedDB `ptm-media` database, `tryReconnectAsync`/`reconnectAsync`) and `ClientMediaFolderService.cs` (`TryReconnectAsync` called silently from `NavMenu.razor`'s first render, `ReconnectAsync` wired to the existing "Connect folder" buttons in `NavMenu.razor` and `Scenes.razor` — both switch to "Reconnect {name}" wording when `NeedsReconnect` is set). Covered by 4 new `ClientMediaFolderServiceTests` cases (silent success, prompt-needed, no-prior-folder no-op, and gesture-driven `ReconnectAsync` completing a pending reconnect).

---

### Step 7: `ClientStorageMode` Server Direct-Proxy Flag

**Premise doesn't match reality — no flag needed, verified by reading the actual generation path.**

This step assumed there's still a "write raw bytes to server disk" behavior today that a `PageToMovie__ClientStorageMode=true` flag would need to opt into (with Steps 1–5 as the safety net before flipping it). Tracing `FilmJobService.GenerateOneClipAsync` (the single method every clip generation goes through — `_grok` is typed `IVideoClient`, so Grok *and* Gemini/Veo both route through it, despite the field's legacy name) shows the opposite: it **unconditionally** issues a `MediaProxyTicketStore` ticket and sets `ClientMediaUrl`/`ClientRelativePath` (L2580–2594) — there is no code path left anywhere that writes a freshly-generated clip's raw `.mp4` bytes to server disk. That migration already happened, and it isn't behind a flag; it's the only path that exists. Non-connected clients already get the "48h `ServerMediaPruningService`" fallback the doc describes, just unconditionally rather than as an `else` branch of a toggle.

Building a `ClientStorageMode` flag now would mean re-adding a legacy raw-bytes-to-server-disk branch that was already deleted, purely to give the flag something to gate — new code with no real caller, added to satisfy a stale plan rather than an actual need.

**Status: ✅ Done (no code needed)** — client-storage-only clip generation is already unconditional in `FilmJobService.cs`; there is nothing left to gate.

---

## Handling Edge Cases & Platform Limitations

1. **Ticket Expiration on Delayed Connect:**
   If a user connects their folder >45 minutes after generation completes, the proxy ticket will return HTTP 404/401. `ClientMediaFolderService` will detect 401/404 and fall back to fetching the standard scene clip URL `/api/projects/{id}/scenes/{s}/clips/{c}/video`.
   *(Follow-up: add `POST /api/media/proxy/refresh` if ticket timeouts are frequent).*

2. **Safari & Mobile iOS (No File System Access API):**
   Safari does not support `window.showDirectoryPicker`. For Safari/iOS users:
   - UI displays: *"Folder save requires Chrome or Edge."*
   - Generation falls back seamlessly to 48-hour server pruning + direct streaming.

3. **Clip finished, no media folder (feature 8 — shipped 2026-07-26, `6769a93`):**
   When a job reaches **`done`** with `ClientMediaUrl` + `ClientRelativePath` and the folder is not connected:
   - Service attempts `ConnectFolderAsync` once (picker).
   - If the user cancels or the browser is unsupported → sets `LocalSaveWarning` (outcome-only copy; Chrome/Edge wording when the API is missing).
   - **Scenes** shows a dismissible warning with **Connect folder** (or **Reconnect folder** if a prior folder just needs its permission re-granted — see Step 6).
   - Warning clears on successful connect/reconnect or Dismiss.
   - Hub subscription is early/idempotent (`MainLayout` + Scenes) so this still works if the user is not on Scenes mid-gen.
   - Auto-save now accepts **both `running` and `done`** (Step 2 correction below) — the original "`done`-only" note here described a regression that would have dropped every clip but the last in a multi-clip batch; `_savedKeys` is what actually prevents double-saving now.

---

## Approved Ship Sequence

| Step | Item | Status |
|------|------|--------|
| **1** | Stream Proxy (`Program.cs`) | ✅ Done — `IHttpClientFactory` + `Results.Stream` + `HttpResponse.RegisterForDispose` |
| **2** | Early hub hook + accept `running`+`done`, dedupe via `_savedKeys` | ✅ Done (`MainLayout`, Scenes, `ClientMediaFolderService`) — corrected from the original `done`-only design, which would have dropped every clip but the last in a multi-clip batch |
| **3** | Write `.client.json` on verified register | ✅ Done — already existed in `Program.cs`, no new code needed |
| **4** | Proactive “Connect folder” banner (pre-gen) | ✅ Done (`Scenes.razor`) — post-gen warning was feature 8, already shipped |
| **5** | Prune server MP4 shortly after client marker confirms sync | ✅ Done — `ServerMediaPruningService` grace-period pass (`AggressivePruneGraceMinutes`), not instant-on-marker |
| **6** | Folder reconnect via persisted `FileSystemDirectoryHandle` | ✅ Done — IndexedDB handle + `queryPermission`/`requestPermission`, not localStorage name-only |
| **7** | `ClientStorageMode` skip server write | ✅ Done (no code needed) — already unconditional in `FilmJobService.cs`, no legacy path left to gate |
| **8** | Fallback UI when folder not connected | ✅ Done (`6769a93`) |

1. **Step 1: Stream Proxy** (`Program.cs`) — pure server fix, zero UX risk, eliminates OOM.
2. **Step 2: Early Idempotent Hub Hook & Status Guard** (`MainLayout.razor` & `ClientMediaFolderService.cs`) — **shipped**.
3. **Step 3: Write `.client.json` Marker on Verified Register** (`MediaRegistryService.cs`).
4. **Step 4: Connect Folder Banner** (`Scenes.razor`) — pre-gen; distinct from feature-8 post-gen warning.
5. **Step 5: Prune Redundant Server MP4s** (`ServerMediaPruningService.cs`).
6. **Step 6: Folder Persistence** (`pagetomovie-media.js`).
7. **Step 7: `ClientStorageMode` Flag** (`FilmJobService.cs`) — only after 1–5 are verified in production.
8. **Feature 8: No-folder fallback warning** — **shipped** (`6769a93`).
