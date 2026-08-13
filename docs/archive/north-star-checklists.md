# North-star checklists

Two tracks the user asked to keep tracked across sessions. Checklist A (AI-call feedback loop) is
the active, highest-priority track. Checklist B (pre-UI-consolidation) is on hold per the user's own
priority call — items here are not resumed without the user asking.

## Checklist A — AI-Call Feedback Loop (highest priority)

Design: `AiCallAnalyticsService` + admin `/admin/ai-calls` page. Full plan: the "AI-Call Feedback Loop"
design-doc artifact (5 moves A–E: one contract / one record+sink / one outcome taxonomy / enforce it /
analyzer+loop).

**Shipped and on `origin/master`:** batch 1 = commit `7a8b38df` (DB switch, GrokVisionClient retry/telemetry,
override-reason surfacing, Generation Errors page). Batch 2 = commit `385b0e4d` (video/image client retry +
outcome, canonical outcome taxonomy, portrait style gate migration, enforcement test). Both verified with the
full non-UI + UI suites before push — see "Batching savings" note at the bottom of this doc.

| # | Item | Status |
|---|------|--------|
| — | Design doc + admin analytics page | ✅ Done, shipped, deployed |
| — | Fakes emit telemetry (chat) | ✅ Done |
| — | Fakes emit telemetry (image/video/vision) | ✅ Done — chat/image/image_edit/video/video-extend/vision/review, plus transcribe_page/classify_characters added 2026-08-07 |
| — | Style-gate override + reason capture (3-chip: AI wrong / my preference / other) | ✅ Done end-to-end, incl. reason-breakdown surfaced in admin analytics (2026-08-07) |
| — | AI-calls analytics reads `user_api_calls` (SQLite), not JSONL scan | ✅ Done (2026-08-07) — dual-write to JSONL continues, but the admin page and all aggregation now query the DB |
| — | Admin Generation Errors page (`generation_errors` table) | ✅ Done (2026-08-07) — API endpoint existed, had no UI until now |
| — | Transient retry (429/5xx/network) on `GrokVisionClient` (style gate, dialogue-verify, cast-on-image, transcribe, classify) | ✅ Done (2026-08-07) — was the one client of the four (Grok/Anthropic/Gemini chat+vision) with no retry at all |
| — | Transient retry on `GrokImageClient` (generate + edit) | ✅ Done (2026-08-07) — user call: image gen is cheap enough that the small double-generation risk is acceptable, unlike video |
| — | `Attempt` field reflects real retry count (not misc counters) across all chat/vision/image clients | ✅ Done (2026-08-07) — was never set at all on Grok/Anthropic/Gemini chat+vision, and was silently repurposed as *variant index* in `GrokImageClient.EditVariantsAsync`, both of which broke the "retried" analytics stat |
| — | `Retry-After` header honored on 429s, quadratic-backoff cap raised 4s→15s | ✅ Done (2026-08-07) — `ChatHttpStatusException.FromResponse(resp, msg)` factory, used at all provider-client throw sites; coverage-retry cap (beat classifiers) deliberately left untouched, different concern |
| — | Transient retry + typed job outcome on `GrokVideoClient`/`GeminiVideoClient` | ✅ Done (2026-08-07) — user's call, reversing the earlier "known gap" below: submit-response loss is unrecoverable either way (no request_id to find the job), so auto-retry is no riskier than a human's manual retry; poll-call retry is pure upside (idempotent GET, zero billing risk, previously one blip abandoned an already-paying job). New `Kind="video_job"` summary row (`ok`/`ok_after_retry`/`provider_failed`/`expired`/`timed_out`/`poll_failed`) logged once per job at the point poll resolves — the typed-outcome/provenance piece, since submit+poll's async shape doesn't fit `ValidatedModelOperation`'s single-request contract. Retry-attempt logging pulled out of 4 near-duplicate private methods into one shared `GenerationErrorLoggerExtensions.LogRetryAttemptAsync` extension (`GenerationRetryTelemetry.cs`) reused by GrokVisionClient/GrokImageClient/GrokVideoClient/GeminiVideoClient — user: "don't mirror - reuse". Fakes parity: `FakeGrokVideoClient` logs the same `video_job` row. |
| 0 | Canonical outcome taxonomy | ✅ Done (2026-08-07), scoped per user direction — see below |
| 1 | Migrate bespoke vision gates onto `ValidatedModelOperation` | 🟡 Partial — **portrait style gate migrated 2026-08-07** (pilot, see below); dialogue-verify and cast-on-image gates still bespoke |
| 2 | Migrate ~15 beat classifiers | ✅ **Already done** (discovered 2026-08-07, not new work) — every coverage-retry classifier already routes through `ValidatedModelOperation` via `AiRetryPolicy.RunWithCoverageRetryAsync` → `ValidatedCoverageOperation.ExecuteAsync`. Full transport retry, parse/validate, corrective re-ask on missing ids, deterministic fallback, and `ModelOperationTraceScope` provenance — this must have landed as an infra refactor without the design doc being updated. See "Beat classifiers vs. video/image" below. |
| 3 | Enforcement test (no raw client calls outside the wrapper) | ✅ Done (2026-08-08) — allowlist-based, not blocking: see below |
| 4 | `AiCallAnalyzer` CLI + replay regression | ⬜ Not started — beat classifiers are already replay-ready (provenance trace exists); vision/video/image are not (video's new `video_job` outcome row is telemetry, not a `ModelOperationTraceScope` provenance trace — replay still doesn't reach video/image) |
| 5 | Close the loop into learning | ⬜ Not started |

**Phase 3 — enforcement test (2026-08-08).** `PageToMovie.Tests/RawModelClientEnforcementTests.cs`: source-scans
`PageToMovie.Engine` (same pattern as the pre-existing `AdaptationModuleBoundaryTests`) for direct calls to
`CompleteAsync`/`CompleteWithImagesAsync`/`ClassifyCharactersOnImageAsync`/`TranscribePageAsync`. Doesn't block
on Phase 1 finishing first — it's allowlist-based: a call site is fine if it's inside a sanctioned wrapper file
(`ModelBacked/*` operations, or a coverage-retry classifier's own `callChat` lambda) **or** explicitly listed in
`KnownBespokeDebt` with a reason; it only fails on **new, undocumented** drift. `KnownBespokeDebt` is the honest,
complete inventory of remaining bespoke call sites (9, not the 3 the design doc originally named) — this audit
itself is new information:
- `ClipDialogueVerificationService.cs` (vision) — dialogue-verify gate
- `CharacterBookPlateService.cs` (vision, `ClassifyCharactersOnImageAsync`) — cast-on-image gate
- `SceneMusicCompositionService.cs` (vision) — music-supervisor scoring prompts
- `SceneMusicScoringService.cs`, `LearningProposalService.cs`, `ProjectVisionMeta.cs`, `PlateRankClassifier.cs` (chat) — not previously named in the design doc's "3 vision gates" framing
- `BookPrepareService.cs` (vision, `TranscribePageAsync`) — book-page OCR
- `JitBenchmarkService.cs` (vision) — calibration benchmark, not the live per-generation pipeline

A second test (`KnownBespokeDebt_entries_are_still_accurate`) fails if a listed file stops calling a raw
client (migration landed, entry went stale) or stops existing — so the allowlist can't silently rot in either
direction. **Caught a real gap on its first run**: `MultiProviderChatClient`/`MultiProviderVisionClient`
(model-id routing) and `CachingChatClient` (a caching decorator) also call the raw interface methods — these
are `IChatClient`/`IVisionClient` *implementations* (infrastructure, same category as `GrokChatClient.cs`),
not bespoke callers bypassing the wrapper; added to the sanctioned list once identified.

**Phase 0 — canonical outcome taxonomy (2026-08-07):** user's scoping call — no real production data exists
yet, so there's no migration/backfill story to design around, and no value in keeping a second (legacy)
classification path alive "just in case." Rejected the original design doc's literal ask (rename
`ApiCallTelemetry`→`AiCallRecord`, touch all ~15+ write call sites) in favor of the much cheaper equivalent:
`AiCallOutcome` enum (the same 12 values: ok/ok_after_retry/fallback/coverage_gap/validation_reject/
vision_blind/parse_error/schema_invalid/rate_limited/timeout/provider_refusal/cancelled) added as a field on
the *existing* `ApiCallTelemetry`, classified **once, centrally**, inside `ProjectTelemetryService.LogApiCallAsync`
— the single chokepoint every call site already funnels through — instead of touching each site individually.
`AiCallAnalyticsService.ClassifyFailure` (read-time string-guessing on `Error` text, e.g. checking for the
substring "blind") is **deleted outright**, not kept as a fallback — no dual system. New `outcome` column on
`user_api_calls` (`EnsureColumn`, same idiom used all session). **Known scope boundary, not a gap:** the
central classifier only sees transport-level signals (HTTP status, exception type, attempt count), so it
can only reliably produce `ok`/`ok_after_retry`/`rate_limited`/`timeout`/`provider_refusal`/`parse_error`/
`cancelled`. The semantic-only values (`fallback`/`coverage_gap`/`validation_reject`/`vision_blind`/
`schema_invalid`) need a caller with business-logic context to set `ApiCallTelemetry.Outcome` explicitly
before logging — nothing does this yet. Deliberately did NOT wire this into `CharacterDesignService`'s plain
(non-override) style-gate rejection this pass — that call site has no `ProjectTelemetryService` dependency
today, and adding one is a real DI change with its own blast radius, not something to fold in opportunistically
while already mid-batch on the taxonomy itself.

**Portrait style gate migration (2026-08-07, pilot for Phase 1):** `CharacterDesignService.RunPortraitStyleGateAsync`
now runs through `ValidatedModelOperation<PortraitStyleGateInput, string, PortraitStyleGateResult>` instead of a raw
`CompleteWithImagesAsync` + manual parse. New `PageToMovie.Engine/ModelBacked/PortraitStyleGateOperation.cs`:
`PortraitStyleGateOperation` (vision-flavored sibling of `Stage2DirectiveOperation` — same corrective-retry prompt
injection, calls `IVisionClient.CompleteWithImagesAsync` instead of `IChatClient.CompleteAsync`),
`PortraitStyleGateResponseParser` (adapts the existing, still-unit-tested `ParsePortraitStyleGateResponse`),
`PortraitStyleGateValidator` (new — rejects an unrecognized `medium`, e.g. a hallucinated value; nothing checked
this before). `PortraitStyleGateResult` changed from a `readonly record struct` to a `sealed record` (`TResult`
must be a class). Generalized `DirectiveTerminalFallback<T>` → `DirectiveTerminalFallback<TInput, TResult>` (was
hardcoded to `Stage2DirectiveInput`) so the vision gate could reuse it verbatim instead of forking a duplicate —
updated its 3 existing callers (`NegativePromptClassifier`/`ColorPaletteGradingClassifier`/
`CinematicLightingClassifier`) + 2 test call sites to the two-type-param form. `TransportMaxAttempts` set to 1
explicitly — `CompleteWithImagesAsync` already retries transiently inside itself; leaving the outer default (3)
would have multiplied attempts the same way the earlier beat-classifier near-miss would have. Gains: corrective
re-ask on malformed JSON or an invalid medium (previously an immediate hard failure), schema validation, and
provenance/reproducibility tracing via `ModelOperationTraceScope`. Dialogue-verify and cast-on-image gates not
yet migrated — dialogue-verify's response shape is materially more complex (many field-name aliases, computed
accuracy fallback, speaker-name post-processing) and deserves its own pass rather than copy-pasting this one.

**Beat classifiers vs. video/image clients — architecture comparison (2026-08-07, superseded by the row above
for the retry/billing point specifically — kept for the provenance/fallback comparison, which still holds):**
the beat classifiers are still more architecturally mature than `GrokImageClient`/`GrokVideoClient`/
`GeminiVideoClient` in two ways even after today's video work:
1. **Centralized vs. duplicated** — classifiers share one pipeline; video/image still hand-roll telemetry at
   each call site (now DRY'd up for retry-logging via the shared extension, but not unified into one
   pipeline the way `ValidatedCoverageOperation` unifies the classifiers).
2. **No provenance/replay** on video/image — no reproducibility hash, no `ModelOperationTraceScope` entry;
   the new `video_job` row is real typed-outcome telemetry but not the same thing as a replayable trace.
The retry/billing asymmetry that WAS point 3 here no longer applies to video specifically — see the row above.
**No deterministic fallback** still holds for video/image (a failed generation just throws) and reasonably
so — there's no way to return "half a video."

**Batching savings, this session (2026-08-07/08):** two full-suite checkpoints (non-UI + UI) covered 16
features total — batch 1 measured 1m10s + 6m12s = 7m22s; batch 2 measured 1m6s + 5m56s = 7m2s. Actual total
test time: ~14m24s. Had each feature been tested individually instead, that's 16 × ~7m12s (average measured
checkpoint) ≈ 115 minutes — roughly **100 minutes saved** by batching. Using the 15-min-per-run estimate from
the conversation that motivated batching in the first place (each run is a bit faster than that in practice —
7-7.5 min measured, not 15): 16 individual runs × 15 min = 240 min vs. 2 batched runs × 15 min = 30 min ≈
**210 minutes (3.5 hours) saved** on that estimate. Real number is the ~100-minute one; the 15-min figure was
always an upper-bound estimate, not measured — worth knowing the actual cadence runs faster than assumed.

## Checklist B — Pre-UI-Consolidation (resumed 2026-08-07: A-2, A-4, A-5)

| Item | Status |
|------|--------|
| A-1 E2E through Scenes (+ varied fixtures) | ✅ Done |
| A-1b Clip generation → Scenes/Review unlock | ✅ Done |
| A-3 Characters operator flow (looks/lock/voice) | ✅ Done |
| A-2 Review page depth | ✅ Done |
| A-4 Configuration depth | ✅ Done |
| A-5 Home depth | ✅ Done |

**A-5 Home page depth (2026-08-07):** `PageToMovie.UiTests/HomeFlowTests.cs` — 2 tests covering the Home
page's "Manage" panel actions that had zero prior dedicated coverage: rename (display name, possibly
re-slugging the project id) and checkpoints (named git-backed snapshots — save, list, revert), each
verified to round-trip through the server via in-app nav-away/nav-back.

Found and fixed two real bugs:

1. **Rename could throw "Access to the path … is denied" and silently fail.** `ProjectStore.DeleteProjectAsync`
   (called by the re-slug rename path — export → import → delete old → activate new) does a plain
   `Directory.Delete(recursive: true)`. Git writes loose-object files read-only by design (immutable
   objects), and `Directory.Delete` cannot remove read-only files on Windows — this is deterministic, not
   transient, so it failed on *every* rename of a project with any git history (i.e. every project, since
   creation already commits "Initial project state"). A blind retry loop (tried first, up to ~8s) never
   helped, because the block isn't time-based. Fixed by clearing the read-only attribute recursively
   before deleting (`ClearReadOnlyRecursive`), with a short retry still in place for the separate,
   genuinely transient case of a file open from a running job.
2. **Named checkpoints could silently discard the user's chosen name.** `ProjectGitRepositoryService.
   CommitProjectStateAsync` skips creating a commit and returns the existing HEAD tip whenever the tree is
   clean (no file changes) — a deliberate optimization for the auto-commit-after-save caller (Scenes.razor,
   "Manual scene/clip updates"), so an unchanged save doesn't spam the git history. But the *named
   checkpoint* caller (Home.razor's "Save checkpoint") shares the exact same method and endpoint
   (`/api/projects/{id}/git/commit`), so saving a checkpoint immediately after project creation — before
   touching any files — silently no-opped: the UI showed "Checkpoint '…' saved" but the checkpoint list
   still only had the original "Initial project state" entry, with the user's name discarded. Fixed by
   threading a `forceCommit`/`ForceCommit` flag through `CommitProjectStateAsync` →
   `CommitProjectChangesAsync` → the `/git/commit` endpoint → `EngineApiClient.CreateCheckpointAsync`
   (sets it `true`); the auto-commit caller (`EngineApiClient.CommitProjectChangesAsync`, used by
   Scenes.razor) is untouched and keeps the skip-if-clean behavior.

Also fixed a bug in the test's own polling helper: `Page.EvalOnSelectorAsync` throws immediately if the
selector isn't in the DOM yet (no Playwright auto-wait, unlike `Locator` methods) — a manual polling loop
around it needs to catch that per-iteration or it fails on the very first check during a page navigation.

**Test-infra lesson (2026-08-07):** while verifying this, the local Playwright/Chromium environment
became unable to launch a working browser after an unusually long session of repeated `dotnet test` runs —
every subsequent run failed waiting for the app shell to render, with no code, build-cache, or Chromium-
reinstall fix resolving it (verified via `git stash` isolation that the code was not the cause, and via a
direct manual browser session that the app itself rendered correctly). Root-caused to Windows session
resource exhaustion (orphaned `chrome.exe`/`dotnet.exe` processes accumulating across dozens of test runs,
some killed forcefully) rather than anything in the app or test code — a fresh terminal/session resolves it.
`HomeFlowTests` (2/2) and `ConfigurationFlowTests`/`ReviewFlowTests` all passed cleanly earlier in this same
session, before the environment degraded.

**A-4 Configuration page depth (2026-08-07):** `PageToMovie.UiTests/ConfigurationFlowTests.cs` — 2 tests
covering the page's two save paths: the debounced autosave (Format & Resolution / Pipeline Behavior fields,
450ms debounce, no bottom Save button) and the immediate save fired by a studio-coverage provider/model change,
plus the music optional-capability on/off toggle (provider pick → model pick → Ready badge → Turn off → Off
badge), each verified to round-trip through the server via in-app nav-away/nav-back (not just client state).

Found and fixed a real bug: `SupportedModelCatalog.BuildProviderKeyRows()` filters out any provider whose
models all have empty `requiredEnvKeys` — which silently dropped the **entire "fake" provider row** in fakes
mode, since every `fake-*` model has `requiredEnvKeys: []` by design (key-free). That meant
`GetUserSettingsDtoAsync`'s "fake is always configured" special-case (`UserDatabaseService.cs:2131-2141`) never
had a row to apply to — the fake provider was simply absent from `/api/user/settings`, so **every** Studio
coverage row showed "Need key" in fakes mode despite everything actually being wired and working. Fixed by
letting the "fake" provider id survive both filters in `BuildProviderKeyRows()` (`SupportedModelCatalog.cs`)
regardless of `requiredEnvKeys` being empty — real providers with no required keys are still dropped as before.

Also hit — and correctly diagnosed as NOT a regression — a strict-mode Playwright violation where both the
music and voice rows had a "Turn off" button simultaneously in a full-suite run. Reproduced in isolation and
found voice correctly stayed "Off" throughout; the cross-row state only appeared when run inside the full
60-test suite, alongside 5 failing `MultiUserLeaseUiTests` (the already-flagged, pre-existing task #8
collaboration-feature breakage — a different subsystem entirely). Read as shared-fixture state pollution from
that unrelated broken feature, not a Configuration bug. Fixed by scoping the "Turn off" locator to the specific
row (`musicRow.GetByRole(...)`) rather than an unscoped page-wide lookup — the correct fix either way, since two
independently-off-able rows can legitimately both show the button at once.

**A-2 Review page depth (2026-08-07):** `PageToMovie.UiTests/ReviewFlowTests.cs` — 2 tests covering the
Review-page approve/checklist workflow (pass a clip, approve a scene, checklist count updates without a page
reload) and Play/Share tab reachability once clips exist, on top of the same `RunToGeneratedClipsAsync` pipeline
`ClipGenerationTests` (A-1b) uses.

Found and fixed a real bug while writing these: `Review.razor`'s Play-tab `else if (_activeTab == "play") { ... }`
block (opened ~line 301) was missing its closing brace. The file still balanced overall (a later brace absorbed
the shift), so this was **not a compile error, console error, or Blazor error banner** — the entire job-status
block and the whole "Scenes & clips" review table were silently nested inside the Play-tab-only branch, making
the core Review-and-approve table invisible on the default "Review & Approve" tab. Only visible when the Play tab
happened to be selected. Diagnosed via an unconditional marker div toggled across all three tabs (confirmed
`markerOnReview=0 markerOnShare=0 markerOnPlay=1` before the fix, `1/1/1` after) — see the closing-brace fix right
after the clip-player block in `Review.razor`. Fixed with one inserted `}`.

Also corrected a wrong assumption made while first writing the test: approval is **one-way**
(`EditLogService.MarkSceneApprovedAsync` always writes `status="approved"`; there's no unapprove endpoint), so
clicking the "✓ Approved" button again re-approves and the checklist count stays the same — it does not toggle
back off despite the button rendering in what looks like a pressed state. The test's assertion was updated to
match actual behavior rather than adding an unapprove feature that wasn't asked for.

7/7 tests pass (`ReviewFlowTests` ×2 + `PageDepthTests` + `CapabilityGatingTests`, no regressions from moving
where the scenes table renders).
| B: bug-fix-first (jargon audit) | 🔵 Superseded — folded into the localization backlog |
| C: RCL decision / extraction order / Scenes component boundaries | 🟡 In progress, unmerged — branch `refactor/blazor-components`: RCL `PageToMovie.Components` created, 5 presentational components moved, `ConfirmModal` built + applied to Scenes delete dialogs, verified (build clean, 1570 tests, fakes-browser smoke). Not merged — needs the user's visual review. Open decision queued: full component extraction (slow, regression-prone, "right" structure) vs. code-behind split (`@code` → `.razor.cs`, near-zero-risk, halves file sizes, but a bigger restructure than asked for) |
