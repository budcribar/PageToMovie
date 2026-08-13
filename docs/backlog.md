# PageToMovie backlog

Single prioritized backlog. Older checklist docs were merged here and removed (2026-08-06).

**How to use:** Work top-down within P0 → P1 → P2 → P3. Each item has **context**, **where to look**, **done when**, and **notes** so you can resume without re-discovering the thread.

---

## Done recently (do not re-open)

- ActiveProjectState load lifecycle (`IsLoading` / `IsReady` / `EnsureLoadedAsync` + single-flight `_ensureLoadTask`)
- EnsureLoaded on Cost, Characters, Scenes; MainLayout loading cursor + `Changed` unsubscribe on dispose
- Navigate-away **P0** (`fix/p0-navigate-away-async`): page `IDisposable`, `_pageCts`, `_disposed`, `SafeSoftReloadAsync`, Cost `OnParametersSetAsync` awaits ensure-load
- Scenes SoftReload body restored (job list + `OpenSceneAsync`)
- Catalog fail-fast direction (no invented model capabilities / soft defaults)
- Cost from model JSON only (no hard-coded cost constants in product path)
- Provider heuristic audit → prefer catalog capabilities
- Lab mode direction + admin-only visibility (partial — see P0 #3)
- UI audit sequences S1–S2 exercised; many S3–S5 notes captured historically
- Mary4 UI batch (Agree&Continue, MinMinutes, character `@key`, etc.)
- P1 pipeline stages shipped (2026-08-06):
  - **Stage-1 full-book Fountain + length-later split**
  - **Auto-enrich after write** (admin can still re-run a one-off; not a required user button)
  - **Look & medium** (`VisualMediumCard` + visual-medium API)

---

## P0 — Correctness / safety (do next)

### 1. [Test suite] ClipEdit / ShotPlan test compile — green `dotnet test`

**Context:** Unit tests around clip edit (`UpdateClipFields_*`, `AddClip_inserts_in_clip_number_order`, bounds tests) were written against shapes that do not match current `Adaptation.Models` (e.g. `DialogueDelivery` values, `Characters` type, missing `ClipEditRequest` / `ClipEditService` alignment). CSC reports ambiguous types and missing members (`DialogueDelivery.Soft`, etc.). Until this is fixed, full `dotnet test` is not a reliable gate.

**Where to look:**
- `host/PageToMovie.Tests/` — especially any `ClipEdit*`, `ModelBounds*`, `ClipDelete*` tests
- `host/PageToMovie.Adaptation/Models/` (or equivalent) — real `ShotPlanFile`, `ShotPlanClip`, `ShotPlanAudio`, `DialogueDelivery`, `CharacterList`
- `host/PageToMovie.Engine/ClipEditService.cs` (if present) and API usage in `host/PageToMovie.Api/Program.cs` (`ClipEditService.*`)
- Fake store patterns in tests (`LoadShotPlanAsync` / `SaveShotPlanAsync`)

**Done when:**
- `dotnet test host/PageToMovie.Tests/PageToMovie.Tests.csproj` builds and runs without CS0104/CS0229/CS0117 on ShotPlan/ClipEdit types
- Either tests assert against real `Adaptation.Models` + real service API, **or** incomplete ClipEdit tests are explicitly removed/skipped with a backlog pointer back here

**Notes:** Prefer aligning tests to production types over inventing parallel model graphs. Do not invent `DialogueDelivery.Soft` if the product enum is `None | Neutral | Emotional | Urgent` — change test expectations instead.

---

### 2. [Navigate-away] Thread `CancellationToken` through `EngineApiClient`

**Context:** Pages now have `_pageCts` and pass `ct` into `EnsureLoadedAsync`, but most HTTP helpers on `EngineApiClient` still ignore tokens. After navigate-away, cancel stops *waiting* in some paths but **not** the underlying HTTP calls (status, job list, character image, soft-reload fetches).

**Where to look:**
- `host/PageToMovie.Web/Services/EngineApiClient.cs` — add optional `CancellationToken ct = default` to methods used by studio pages first: adaptation status, `GetCharactersAsync`, `GetJobListAsync`, character image, any soft-reload endpoints
- Call sites: `Characters.razor`, `Scenes.razor`, `Cost.razor`, `ActiveProjectState.RefreshReadinessAsync`
- Prefer `HttpClient` overloads that accept `CancellationToken`

**Done when:**
- Studio-critical API methods accept `CancellationToken`
- Page soft-reload / ensure-load / image paths pass `_pageCts.Token`
- Cancel on dispose actually aborts in-flight HTTP (or at least stops processing responses)

**Notes:** Do not require every client method in one PR — prioritize the three studio pages + readiness. Keep optional `ct = default` so existing call sites compile.

---

### 3. [Cost / Agree UX] Lab vs production pricing + admin-only lab models

**Context:** Lab / incomplete models can surface $0 or missing pricing. Product rule: **regular users must never treat lab as free production**; **only admins** turn on lab mode and see lab-only models.

**Where to look:**
- Cost page / Agree & Continue UX (`host/PageToMovie.Web/Components/Pages/Cost.razor`)
- Model catalog flags (`labMode` / lab / unreliable) in models JSON + catalog services
- Admin vs non-admin gates (`AuthGate.RequireAdmin`, user roles / claims)
- Any estimate API that returns zero cost when pricing is missing

**Done when:**
- Non-admin users cannot select or estimate with lab-only models
- UI clearly labels lab / missing pricing (not silent $0)
- Agree & Continue blocked or strongly warned when pricing is incomplete (product choice documented in this item)

**Notes:** Partial work may already exist (`includeLabModels` style flags). Audit end-to-end from model list → estimate → Agree.

---

### 4. [Catalog] Unknown model ID fail-fast everywhere

**Context:** Policy is: if a model is not in the catalog, it **does not exist** — no invented default capabilities. Soft defaults caused production surprises.

**Where to look:**
- Model catalog load / resolve paths (Engine + Web)
- Any remaining `??` defaults for max duration, extension, cost, capabilities
- Provider-ID heuristic branches (should not invent capabilities)

**Done when:**
- Unknown model ID throws / returns hard error at resolve time
- Grep for capability/cost soft defaults is clean on product paths
- Tests cover “unknown model → fail”

**Notes:** Lab exemption is a separate explicit flag (see P1 catalog self-test), not silent defaults.

---

## P1 — Product / pipeline

### 6. [Adaptation] Post–Mary $ run + report evaluation

**Context:** After a live Mary run, evaluate `ADAPTATION_REPORT` and other prompts. Handoff historically lived in adaptation remaining docs (now this backlog).

**Where to look:**
- Live Stage-1 prompt: `book_to_fountain` (VISION_META + ADAPTATION_REPORT)
- `AdaptationPromptTokens` / `ApplyPromptTokens` — leftover `{{tokens}}` must throw
- Mary project outputs / report sections

**Done when:**
- Mary $ run completed and report reviewed
- Prompt gaps filed as concrete sub-items or fixed
- No unresolved `{{tokens}}` in active prompts

---

### 7. [Mary / live] Image + length smoke

**Context:** Live smoke for Mary image generation and length behavior still open after UI batch work.

**Where to look:**
- Characters page image path; Scenes/film length card; Cost estimates under live keys
- Fake mode vs live mode configuration

**Done when:**
- Documented smoke steps pass on a real Mary project (image + length)
- Failures filed with API error codes / UI state

---

### 10. [Cost split]

**Context:** Remaining product cost-split UX / accounting (not the same as catalog-only pricing sources).

**Where to look:**
- Cost page cards; estimate breakdown; any “split” requirements from Mary4 notes

**Done when:**
- Agreed split behavior is implemented and shown in UI
- Or closed as “won’t do” with note

---

### 11. [Characters] ChatEngine rename audit

**Context:** Inject renamed from `Engine` → `ChatEngine` for `IChatCompletionEngine` to avoid shadowing `EngineApiClient Engine`. Residual wrong-service calls are possible.

**Where to look:**
- `Characters.razor` — all `Engine.` vs `ChatEngine.` usages
- Any chat-completion-only APIs still going through `Engine`

**Done when:**
- Chat operations use `ChatEngine`; API/project operations use `Engine`
- Build + quick manual pass on Characters chat actions

---

## P1 — Models catalog / admin

### 12. [Admin] Models catalog UI

**Context:** Need admin page to add / edit / delete / review models. Review/modify/add should verify required parameters and stamp **last reviewed** date.

**Where to look:**
- Admin routes / existing models admin UI stubs
- Models JSON schema and catalog services
- Auth admin gate

**Done when:**
- CRUD + review flow works for admins
- Required-parameter validation runs on save/review
- Last-reviewed date updates on review

---

### 13. [Admin] Scan for updates

**Context:** Admin button to search for changed parameters and new models; color code green (same) / yellow (not found) / red (different); accept applies. Nested fields (e.g. `videoCostPerSecondByResolution.720p`) should accept cleanly without raw JSON.

**Where to look:**
- Prior scan-plan notes; vendor “list models” / pricing endpoints (many vendors lack full pricing APIs)
- Admin catalog UI

**Done when:**
- Scan produces a reviewable diff UI with color codes
- Accept writes catalog JSON safely
- Nested path accept works for known cost maps

**Notes:** Expect partial automation — human confirm still required when vendors only publish HTML docs.

---

### 14. [Catalog] Self-test on deploy / change

**Context:** Every model should self-test required values on catalog load/deploy so generation does not fail late.

**Where to look:**
- Catalog load path; startup hooks; lab-mode exemption for incomplete models

**Done when:**
- Startup/catalog change runs validation for non-lab models
- Lab models can opt out via explicit flag
- Failure is loud and blocks unsafe production use

---

### 15. [Catalog] Parameter completeness

**Context:** Remaining hard parameters need accurate, sourced values in JSON (not code constants).

**Checklist inside this item:**
- [ ] Max reference image dimension (where capability requires it)
- [ ] `supportsVideoContinue` vs `maxExtensionSeconds` (≤0 or omit when no continue; no extend if ≤0)
- [ ] Audio duration limits where applicable
- [ ] Cost rows with **source comment** + **last-reviewed date** only in JSON

**Done when:** All production models either have complete required params or are explicitly lab-flagged.

---

### 16. [Fakes] Capability matrix

**Context:** Need multiple fake models with distinct capabilities so UI can be tested for combinations without live vendors. Capabilities must live on the model, not provider-ID heuristics.

**Where to look:**
- Fake mode / fake catalog
- UI gating for continue, reference images, audio, etc.
- Provider heuristic audit leftovers

**Done when:**
- ≥2–3 fakes with intentionally different capability sets
- UI tests or manual matrix proves correct enable/disable behavior
- No capability invented from provider id alone on product paths

---

## P2 — UI polish / guards

### 17. [Sequences] Remaining S3–S5 / button-state gaps

**Context:** Audit exercised early sequences; later sequences (gen-scene, Agree, length after nav) still need explicit passes for button enablement, range validation, missing inputs.

**Where to look:**
- Historical UI audit notes; Scenes gen-scene button busy-disable; Cost Agree; length card after navigation
- Fake mode for repeatable runs

**Done when:** Documented S3–S5 sequences pass with correct disabled/enabled states and validation messages.

---

### 18. [Optional] Server 409 on duplicate gen-scene

**Context:** UI busy-disable is the primary guard against double submit. Optional server **409** if a scene gen is already in progress for the same project/scene.

**Where to look:**
- Gen-scene API endpoint; job store “already running” checks

**Done when:** Double POST while busy returns 409 (or documented equivalent); UI still primary.

---

### 19. [ActiveProjectState] Page-local `Changed` handlers

**Context:** Layout unsubscribes correctly. Any page that subscribed to `Changed` must also unsubscribe or use disposed guards (P0 covers the three studio pages’ reload paths).

**Done when:** Grep shows no page-level `Changed +=` without matching dispose unsubscribe (or equivalent).

---

### 20. [Watch] ObjectDisposedException logs

**Context:** After rapid navigation Cost ↔ Characters ↔ Scenes, watch browser console / server logs for disposed-component UI updates.

**Done when:** Spot-check after P0 merge shows no recurrent disposed exceptions; residual issues filed here.

---

## P3 — Experiments / non-blocking

### 21. [Grok loop / optimus] Long-dialogue video prototype

**Context:** Experiment branch for multi-segment video + silence/de-dup ideas. Not production path.

**Where to look:** Experimental branches / `optimus_loop` / benchmarks notes in artifacts.

**Done when:** Learnings captured or experiment closed; no requirement to productionize.

---

### 22. [Docs] Prompt token resolution discipline

**Context:** All `{{tokens}}` must resolve via `AdaptationPromptTokens` / `ApplyPromptTokens`; leftovers throw.

**Done when:** New prompts follow the rule; CI or self-check optional later.

---

### 23. [Infra] Sandbox / agent .NET bootstrap

**Context:** Agent environments need `ensure-dotnet.sh` / AGENTS rules for .NET SDK. Keep working for agent image and Windows Grok app bootstrap docs as needed.

**Where to look:** `artifacts/ensure-dotnet.sh`, project AGENTS notes.

**Done when:** Fresh agent session can restore SDK and build without manual archaeology.

---

## Execution order (when picking up)

| Order | Item # | Title |
|------:|-------:|-------|
| 1 | 1 | ClipEdit test compile → green `dotnet test` |
| 2 | 3 | Lab vs production pricing + admin-only lab models |
| 3 | 4 | Unknown model fail-fast audit |
| 4 | 2 | EngineApiClient CancellationToken wiring |
| 5 | 12–14 | Admin catalog UI, scan, self-test |
| 6 | 15–16 | Parameter completeness + capability fakes |
| 7 | 6–7 | Mary report, live smoke |
| 8 | 10–11 | Cost split, ChatEngine audit |
| 9 | 17–20 | Sequence guards, optional 409, Changed audit, log watch |
| 10 | 21–23 | Experiments / tokens / agent bootstrap |

---

## Branch notes

| Branch | Purpose |
|--------|---------|
| `fix/p0-navigate-away-async` | P0 navigate-away guards (2026-08-06) — merge when ready |
| `fix/p1-navigate-away-async` / older UI testing branches | Superseded for navigate-away; prefer master + P0 branch |

---

## Folded from north-star checklists (2026-08-13)

Open leftovers (full history: [archive/north-star-checklists.md](archive/north-star-checklists.md)):

- Migrate remaining bespoke vision/chat sites onto `ValidatedModelOperation` (dialogue-verify, cast-on-image, music, OCR — see `KnownBespokeDebt`)
- `AiCallAnalyzer` CLI + replay for vision/video/image
- Close AI-call telemetry into the learning loop

Checklist B (pre-UI-consolidation) stays on hold.

---

## Sources folded into this backlog

- Former `mary4-ui-checklist.md`, `adaptation-remaining-checklist.md`, `ui-fix-order-checklist.md`, `ui-testing-branch-checklist.md`
- UI audit sequence / capability notes
- Session: ActiveProjectState, SoftReload, Cost EnsureLoaded, navigate-away review (2026-08-06)
- Project memory: Stage-1 full Fountain, Mary remaining, catalog/lab direction
- `north-star-checklists.md` (archived)

`host/docs/github-project-v2-setup-checklist.md` is **kept** (infra setup, not product backlog).
