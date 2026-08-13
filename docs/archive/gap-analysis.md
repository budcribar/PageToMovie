# Plan vs. Reality — Gap Analysis

> **Reconciled 2026-07-26** against the completed 6-phase implementation pass (see
> `host/docs/public-community-plan.md`, the living status table and the primary source of truth
> going forward). This snapshot was accurate when written but predates Phase 5 (real Git engine)
> and Phase 6 (real invite-to-fork) — items 2, 12, and 15 below have since been implemented for
> real and are corrected in place. Everything else here was independently verified and still holds.

## Legend
- ✅ **Done** — Fully implemented as specified
- 🟡 **Partial** — Skeleton/stub exists but key parts missing
- ❌ **Not Done** — Not built at all

---

## Item 1 — Media-Aware Contribution PRs & 2-Tier CDN Fallback

**Plan:** `ProjectContributionService.cs` handles PR payload (JSON diff + SHA-256 + CDN URL).
Window 1 (<24h): direct AI CDN download. Window 2 (>24h): server proxy fallback, auto-purged.

**Reality:** ❌ **Not done.**
- `ProjectContributionService.cs` does **not exist**.
- `ContributionReview.razor` does **not exist**.
- The 2-tier CDN fallback lifecycle is unimplemented.
- `MediaRegistryService.cs` stores SHA-256 hashes and paths correctly ✅, which is a necessary prerequisite — but nothing uses them to drive a PR/contribution flow.

---

## Item 2 — Sync Fork from Origin (`🔄 Sync from Origin`)

**Plan:** LibGit2Sharp rebase/merge engine. Clean = auto-update. Conflict = opens `ContributionReview.razor`.

**Reality:** 🟡 **Merge engine done for real; conflict-review UI still missing.** *(Updated 2026-07-26 — was a stub as described below when this item was first written.)*
- `LibGit2Sharp` 0.31.0 is now a real `PageToMovie.Engine` package reference.
- `SyncForkFromOriginAsync()` adds the parent project as a temporary remote, fetches, and runs a real 3-way merge (`Repository.Merge`) — genuine conflict detection via `MergeStatus.Conflicts`/`repo.Index.Conflicts`, never auto-resolves or auto-commits over a conflict. Reachable via `POST /api/projects/{id}/sync-origin` (owner/admin gated).
- Tests: `ProjectGitRepositoryServiceTests` include a real clean-merge case (combines non-conflicting changes from both sides) and a real conflict case (leaves HEAD untouched).
- `ContributionReview.razor` still does not exist — there is still no *UI* for resolving a reported conflict, only the API telling you `hasConflicts: true`.
- ~~The actual method body is a placeholder that logs a message and returns a `GitMergeResult`.~~ (no longer true)

---

## Item 3 — Creator Profile Badges & Derived Stats

**Plan:** `CreatorProfileHeader.razor` with stats computed from SQLite. Three badges: Debut Director, Featured Filmmaker, Open Source Pioneer.

**Reality:** ❌ **Not done.**
- `CreatorProfileHeader.razor` does **not exist**.
- No badge computation logic anywhere in the engine.
- No `@username` profile page at all.
- `DemoCatalogService.cs` has upvote data that could feed stats, but nothing aggregates it into a creator profile.

---

## Item 4 — Fork Project Button & YouTube Comment Link on `/demo`

**Plan:** Prominent `🍴 Fork Project` and `💬 Comment on YouTube ↗` buttons on gallery cards.

**Reality:** 🟡 **Half done — still accurate.**
- `💬 Comment on YouTube ↗` button: ✅ **Done** — wired into `Demo.razor`.
- `🍴 Fork Project` button: ❌ **Not done** — invite-to-fork (item 15) is now real end-to-end, but it's an *email-invite* flow (owner invites a specific person), not a self-service "click to fork any public project" button on the gallery card. No fork action exists on `/demo` itself.

---

## Item 5 — Dedicated GitHub Organization (`github.com/PageToMovie`)

**Plan:** Create `PageToMovie` GitHub Org. Three repos. Dedicated access tokens for Railway isolated to Org.

**Reality:** ❌ **Not done** (infrastructure, not code — but still outstanding).
- This is a manual GitHub setup task, not purely code.
- The Railway deployment uses the personal `budcribar` repo, not a `PageToMovie` org.
- Dedicated org-scoped tokens for Railway backups: not configured.

---

## Item 6 — Modular Blazor Git UI NuGet Package (`PageToMovie.GitUi`)

**Plan:** Decoupled Razor Class Library with `<GitCommitTimeline />`, `<GitDiffViewer />`, `<GitThreeWayMergeResolver />`, `<GitBranchManager />` bound to generic interfaces.

**Reality:** ❌ **Not done.**
- No `PageToMovie.GitUi` project exists anywhere in the solution.
- No generic `IGitCommitProvider`, `IGitDiffModel`, `IGitMergeConflict` interfaces.
- The Git UI components in `ClipPromptCompareViewer.razor` are hardcoded in the main web project.

---

## Item 7 — Git LFS Strategy

**Plan:** Default = `.gitignore` ignores `assets/video/*.mp4`. Optional opt-in with `.gitattributes`.

**Reality:** 🟡 **Partially done.**
- The `.gitignore` likely ignores MP4s (standard project behavior), but this was not explicitly verified in code.
- No `.gitattributes` file for LFS opt-in was added to the project template scaffolding.
- No in-app setting exists for users to opt into LFS.

---

## Item 8 — YouTube Upload Metadata Form (`PublishDemoModal.razor`)

**Plan:** Modal collecting title, description, COPPA declaration, AI Synthetic Content disclosure, category, tags, privacy.

**Reality:** 🟡 **Moved, not removed** *(corrected 2026-07-26 — the "UI gate is gone" claim below was wrong).*
- `PublishDemoModal.razor` was built in Phase 3 commit `e848337`, then deleted (`d0b04ac`) — but not because the metadata form was dropped. It duplicated a save-dialog that already existed and worked in `Review.razor`, and was never wired into any page in the first place.
- The COPPA "made for kids" radio and AI-synthetic-content-disclosure checkbox were added directly to `Review.razor`'s existing publish dialog instead — the operator still explicitly declares both before submitting, same as the plan requires. Nothing happens "silently."
- Title/description/tags/privacy still flow `Review.razor` → `POST /api/demos` → `DemoEntry` → `DemoYouTubePublisherService` at actual upload time.
- Category is hardcoded to Film & Animation (`"1"`) rather than user-selectable — a real, smaller gap than "the gate is gone."

---

## Item 9 — Terms of Service & IP Licensing Agreement

**Plan:** `TermsAgreementModal.razor` gates users on login. Recorded in SQLite `terms_accepted_at`.

**Reality:** ✅ **Done.**
- `TermsAgreementModal.razor` exists and is wired into `MainLayout.razor`.
- `UserDatabaseService.AcceptTermsAsync()` and `HasAcceptedTermsAsync()` implemented.
- `terms_accepted_at` and `terms_version` columns added to SQLite.
- API endpoint `POST /api/users/terms/accept` wired in `Program.cs`.

---

## Item 10 — Cryptographic Provenance & Instant Auto-Approval

**Plan:** SHA-256 hashes logged for every AI-generated clip. On demo submission: if 100% of clip hashes match the audit ledger → instant auto-approve + YouTube upload, bypassing manual admin queue.

**Reality:** 🟡 **The auto-approval check does exist and predates this analysis — corrected 2026-07-26.**
- `MediaRegistryService.cs` ✅ stores SHA-256, size, scene, clip, kind per media object.
- `ClientMediaFolderService.cs` ✅ computes SHA-256 client-side and registers via API.
- ~~The auto-approval check ... is not implemented~~ — it is: `POST /api/demos` hashes the uploaded file, calls `MediaRegistryService.IsTrustedShaAsync`, and if it matches, calls `DemoCatalogService.SetStatus(..., Public, "Auto-public: upload SHA-256 matches trusted gen/export registry")` immediately, skipping the admin queue. This predates the 6-phase pass; it was already real when first audited.
- What's genuinely a gap: this trust check only covers the **whole exported movie file's** hash, not "100% of the constituent clip hashes" per the plan's exact wording — a movie stitched from trusted clips but exported/re-encoded client-side won't match unless that exact export was itself separately registered (which `RegisterBlobAsExportAsync` does opportunistically, but it's not guaranteed).
- Since Phase 3, an auto-approved demo also triggers the real YouTube upload automatically (`DemoYouTubePublisherService`, fire-and-forget) — so the full "auto-approve → auto-upload" chain the plan describes does work end-to-end, just gated on the whole-file hash rather than a stricter per-clip check.
- Manual admin review queue (`/admin/demos`) remains the path for anything not auto-trusted, and now also triggers the same YouTube upload on manual approval.

---

## Item 11 — YouTube API Auto-Upload & Video Replacement

**Plan:** Verified submission → auto-approve → `YouTubeUploadService.cs` streams to YouTube. On re-publish: upload V2, update gallery pointer, delete old video ID.

**Reality:** ✅ **Done (upload + V2 replace).** *(V2 replace shipped 2026-07-26)*
- `YouTubeUploadService.cs` was built (hand-rolled HTTP, its own separately-configured/unset-up OAuth client duplicating the app's existing working one), then **deleted** and replaced by `DemoYouTubePublisherService.cs`, which reuses the already-working `YouTubeAuthService` + official `Google.Apis.YouTube.v3` SDK.
- Upload to YouTube: ✅ via `DemoYouTubePublisherService.PublishAsync()`, fired on auto-approval and admin approval.
- After upload, deletes local MP4 and updates demo record: ✅.
- Gallery streams via YouTube embed: ✅ in `Demo.razor`, with server-stream fallback if upload hasn't happened yet or failed.
- Hash-gated auto-approval trigger: ✅ (Item 10).
- **Video replacement (V2):** ✅ When a public demo for the project already has a `YoutubeId` and the user re-publishes (`replaceExisting` default true), the WIP/upload is attached to that demo, a new YouTube video is uploaded, the gallery pointer is updated, then the old video ID is deleted best-effort (`videos.delete`). Requires `youtube.force-ssl` scope (re-connect YouTube if tokens predate this). On V2 upload failure the old `YoutubeId` is kept so the gallery still works.
- Upload still runs fire-and-forget (`Task.Run`), not through the app's job/SignalR system — see `host/docs/issues/issue-25-demo-youtube-upload-not-a-job.md`.

---

## Item 12 — Git-Backed Server Engine (LibGit2Sharp)

**Plan:** Every project backed by a Git repo. Auto-commit on every save. 3-way merge on collaboration. Remote GitHub push for off-site backup.

**Reality:** 🟡 **Real engine now exists; auto-commit-on-save and remote push are deliberately not wired yet.** *(Updated 2026-07-26 — was a stub as described below.)*
- `LibGit2Sharp` 0.31.0 is a real package reference. `CommitProjectStateAsync()` does a genuine `Repository.Init` + stage + commit against the project's own directory (ignoring video/audio binaries), no-oping (returns existing HEAD) when nothing changed instead of an empty commit.
- `SyncForkFromOriginAsync()` does a real 3-way merge (see item 2). Both reachable via `POST /api/projects/{id}/commit` and `/sync-origin` (owner/admin gated).
- **No auto-commit hook on save** — deliberate, not an oversight: in this repo's current *development* layout, sample projects (`projects/Buster/`, etc.) are themselves committed inside the main app repo, so `Repository.Init`-ing on every save would nest a second `.git` inside an already-tracked directory. See `host/docs/issues/issue-26-git-auto-commit-not-wired-automatically.md` for the guard needed before wiring this in. Manual, explicitly-triggered commits via the endpoints above are safe today.
- **No remote GitHub push for off-site backup** — genuinely not implemented, not attempted.
- Tests: `ProjectGitRepositoryServiceTests` (6 tests) exercise real commits, real file tracking, a real clean merge, and a real conflict that's correctly left unresolved.

---

## Item 13 — Admin Cross-User Export & Local Storage Handoff

**Plan:** Admin re-assigns project `ownerUserId`. Lightweight ZIP (<5 MB). Target user opens project → `ClientMediaFolderService.cs` binds their local hard drive folder.

**Reality:** ✅ **Confirmed implemented** *(corrected 2026-07-26 — the "not verified" note below was resolved by reading the actual code path).*
- `ClientMediaFolderService.cs` ✅ fully implemented — folder picker, JS interop, auto-save on job completion, SHA-256 registration.
- `ProjectArchiveService.cs` ✅ exists and is real: `ExportAsync` zips the full project directory (not filtered to <5 MB — it includes video, unlike the new lightweight `ForkProjectAsync` in item 15); `ImportAsync(..., targetUserId: ...)` writes `ownerUserId`/`owner_user_id` into the imported project's `project.json`, confirmed in `ProjectArchiveService.cs` (`EnsureProjectJsonIdAsync`).
- `POST /api/admin/projects/import` (admin-gated) exposes this with a `targetUserId` form field — **admin re-assignment on import is real, not a stub.**
- Not independently re-verified in this pass: an actual browser click-through of the full admin UI flow (code-level confirmation only).

---

## Item 14 — Client MP4 Storage & Railway Disk Guard

**Plan:** MP4s live in browser (IndexedDB/OPFS/local folder). Server only caches transiently. `ServerMediaPruningService.cs` prunes after 48h or at 80% disk.

**Reality:** 🟡 **Partial — both paths exist but server is still primary; pruner is now correct but off by default.** *(pruner + feature 8 updated 2026-07-26)*
- `ClientMediaFolderService.cs` ✅ fully implemented with ffmpeg.wasm silence trimming and SHA-256.
- `pagetomovie-media.js` ✅ exists (File System Access API JS interop).
- `ServerMediaPruningService.cs` — was previously buggy in a way this analysis hadn't caught: it resolved its root via `Directory.GetCurrentDirectory()` (wrong under the documented run command) and deleted files by age alone with no check that the client had actually synced a copy first (real data-loss risk). Now fixed: resolves via `ProjectStore.WorkspaceRoot`, only deletes a file `MediaRegistryService` confirms was synced, and **defaults off** (`PageToMovie:MediaPruning:Enabled`) until explicitly opted into per deployment.
- **Still true:** server-side `assets/video/` remains the primary write path; the client folder is opt-in. Until a user connects a media folder, clips still accumulate on Railway disk (and now, correctly, are never auto-deleted until they do).
- **Shipped 2026-07-26 (feature 8 / fallback UX):** When a job finishes with `ClientMediaUrl` but the local folder is not connected (picker cancelled or browser unsupported), Scenes shows a one-shot warning with **Connect folder** / Dismiss. Hub auto-save only runs on status **`done`**. Early `EnsureHubHookAsync` from `MainLayout` + Scenes so the warning can fire even if the user navigates. Commit `6769a93`. Remaining steps (stream proxy, `.client.json` markers, connect banner, aggressive prune, folder persistence, `ClientStorageMode`) still open — see `client-storage-implementation-plan.md`.

---

## Item 15 — Invite-to-Fork Collaboration

**Plan:** Owner invites by `@handle` or email. User B accepts → lightweight fork created. Independent local work, zero file lock conflicts.

**Reality:** ✅ **Done for real, end-to-end.** *(Updated 2026-07-26 — was UI-shell-only as described below.)*
- `ProjectInviteService` (new) persists real single-use, 48h-expiring invite tokens in their own SQLite table, SHA-256 hashed (the raw token only ever exists in the emailed link, matching `UserDatabaseService`'s own auth-token pattern).
- `POST /api/projects/{id}/invites` (owner/admin gated): resolves a `@handle` to its email server-side, creates the persisted invite, and sends a real email via the existing `IEmailSender` (Resend/SMTP/log-only dev fallback — not `ResendEmailSender` specifically hardcoded, the same provider-agnostic sender everything else uses) with a `/join` link built via `AdminAuthService.BuildAppLink`.
- New `Join.razor` page (`/join?token=...`): prompts sign-in if needed, otherwise calls the new `POST /api/invites/accept`.
- `ProjectStore.ForkProjectAsync` (new): real fork creation — copies screenplay/cast/blueprint/rules/character-reference images into a new project under the accepting user, excluding video/audio binaries and any `.git` history; does not touch the process-global active-project pointer (accepting an invite on one user's behalf must never steal another user's active project).
- `ProjectCollaboratorsModal.razor` had two real bugs, now fixed: `Close()` didn't propagate to the parent, and `SendInvite()` reported "Invitation sent!" even on HTTP errors and thrown exceptions. Wired into `Home.razor` as a "Collaborate" button for project owners/admins.
- Tests: `ProjectInviteServiceTests`, `ProjectForkTests`, and `InviteToForkApiTests` — a real `WebApplicationFactory` round trip (create project → invite by email → accept as a different user → verify the fork exists under the new owner with `parentProjectId` set → verify the token can't be reused), plus an ownership-gating check.

---

## Summary Table

| # | Feature | Status | Key Gap |
|---|---|---|---|
| 1 | Media-Aware Contribution PRs | ❌ Not Done | `ProjectContributionService.cs` doesn't exist |
| 2 | Sync Fork from Origin | ✅ Done (2026-07-26) | Real 3-way merge + conflict detection; only the conflict-review *UI* is missing |
| 3 | Creator Profile Badges | ❌ Not Done | `CreatorProfileHeader.razor` doesn't exist |
| 4 | Fork & YouTube Comment Buttons | ✅ Done | Comment ✅; gallery 🍴 Fork project via `POST /api/demos/{id}/fork` (Feature 11) |
| 5 | GitHub Org Strategy | ❌ Not Done | Manual infra task, not set up |
| 6 | `PageToMovie.GitUi` NuGet Package | ❌ Not Done | Project doesn't exist |
| 7 | Git LFS Strategy | 🟡 Partial | No `.gitattributes`, no in-app opt-in |
| 8 | YouTube Metadata Modal | 🟡 Moved, not removed | COPPA/AI-disclosure fields live in `Review.razor`'s dialog now, not silently dropped |
| 9 | Terms of Service Gate | ✅ Done | Fully wired (and, since 2026-07-26, actually server-enforced on gated endpoints, not just client-side) |
| 10 | Cryptographic Auto-Approval | 🟡 Partial, corrected | Auto-approve-by-hash already existed pre-analysis; gap is whole-file vs. per-clip hash granularity |
| 11 | YouTube Auto-Upload & Replace | ✅ Done | Upload + auto-approve + V2 pointer replace (delete old ID best-effort) |
| 12 | Git-Backed Server Engine | ✅ Done (2026-07-26) | Real commit/merge; auto-commit-on-save deliberately not wired (issue-26), no remote push |
| 13 | Admin Export & Handoff | ✅ Done, confirmed | `targetUserId` re-assignment on import verified in code |
| 14 | Client MP4 Storage (Primary) | 🟡 Partial | Infra ✅ + feature-8 fallback warning; server still primary; stream/marker/mode steps open |
| 15 | Invite-to-Fork Collaboration | ✅ Done (2026-07-26) | Real persisted invites, real email delivery, real fork creation, end-to-end tested |
