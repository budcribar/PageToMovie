# Migration Plan: Centralize Pipeline Gating on `StudioStateMachine`

**Working branch:** `feature/studio-state-machine-migration`  
**PR1 status:** Core machine hardened + unit tests.  
**PR2 status:** `ActiveProjectState` façade → `StudioStateMachine`.
**PR3 status:** `AdaptationStepUi` Outline/Shots + SuggestedStepPath phase fallback (this branch).

**Goal:** Make [`StudioStateMachine`](../PageToMovie.Core/Models/StudioStateMachine.cs) the **single source of truth** for studio pipeline phase and step navigation, so readiness rules are not duplicated in Web nav, adaptation UI helpers, and server `NextStep` logic.

**Non-goals (this migration):**
- Rewriting the structured Screenplay Editor
- Introducing a *stored* phase column that can drift from project artifacts
- Changing operator-facing step names in the strip without product sign-off

**Design rule:** Phase is **derived** from `AdaptationStatus` (and related readiness flags). Mutations stay event-driven (`SignOffScreenplay`, build shot plan, generate clips). The machine only **evaluates**.

---

## Current state (baseline)

| Source of truth today | Role | Problem |
| :--- | :--- | :--- |
| `StudioStateMachine` | Phase + `CanNavigateTo` | Defined in Core; **no product call sites** |
| `ActiveProjectState.ApplyFromStatusPayload` | `CanCharacters` / `CanScenes` / … + blocked reasons | Near-duplicate of `CanNavigateTo` |
| `StudioProcessStrip` | Top nav enable/disable | Consumes `ActiveProjectState` only |
| `AdaptationStepUi` | Outline/shots unlock, banners, redirects | Parallel rules on flags + `NextStep` strings |
| `ProjectStore` status builder | Server `NextStep` | Third encoding of the same journey |

---

## Success criteria

- [ ] Every top-nav gate (`Cast`, `Estimate`, `Film`, `Review`) is computed via `StudioStateMachine.CanNavigateTo` (directly or through a thin façade).
- [ ] `DeterminePhase` has unit tests covering Setup → Import → Draft → Approved → Shot plan → Review.
- [ ] No second hand-rolled copy of cast/shot/review unlock rules in `ActiveProjectState`.
- [ ] Screenplay sign-off still unlocks Cast solely because status flags change and phase re-evaluates (no special-case nav hacks).
- [ ] Offline tests green for changed projects; no intentional UX change except fixing proven inconsistencies.

---

## Phase 0 — Inventory & freeze the contract

**Owner intent:** Agree what each phase *means* before moving call sites.

- [ ] **0.1** Document operator meaning of each `StudioPhase` value (one sentence each) in this file or `StudioStateMachine` XML docs.
- [ ] **0.2** Map each `StudioStep` → product route(s):
  - [ ] `Setup` → settings / keys
  - [ ] `Book` → `/adaptation/import` + `/adaptation/screenplay`
  - [ ] `Cast` → `/characters`
  - [ ] `Estimate` → `/cost`
  - [ ] `Film` → `/scenes`
  - [ ] `Review` → `/review`
- [ ] **0.3** List every current gate consumer (expected set):
  - [ ] `ActiveProjectState`
  - [ ] `StudioProcessStrip.razor`
  - [ ] `Characters.razor` gate
  - [ ] `Cost.razor.cs` post-agree navigation
  - [ ] `AdaptationStepUi.OutlineEnabled` / `ShotsEnabled`
  - [ ] `AdaptationShell` reapprove + next banner helpers
  - [ ] `ProjectStore` `NextStep` builder (~line 5165+)
- [ ] **0.4** Capture 3–5 real `AdaptationStatus` fixture snapshots (JSON or test builders): empty project, draft unsigned, signed no stage2, stage2 ready, clips complete.
- [ ] **0.5** Freeze: **no new** ad-hoc `ReadyForShots` / `Stage2Ready` unlock checks in Web without routing through the machine (team agreement / PR note).

**Exit:** Written contract + fixtures ready for tests.

---

## Phase 1 — Harden `StudioStateMachine` (Core only)

**Risk:** Low (no UI change if still unused).

### 1.A Fix evaluation bugs

- [x] **1.1** Revisit **approval** predicate so it matches product:
  - Preferred: `Screenplay.Signed` and/or `ReadyForShots` (define whether Stage1 alone is enough — likely **not** for Cast unlock).
  - [ ] Align with structured editor sign-off (`Engine.SignOffScreenplayAsync` outcomes).
- [x] **1.2** Implement **`ReviewReady`** (reserved until clip rollup on status; documented):
  - Define inputs (e.g. stage2 ready + all clips generated / review unlock flag already on status).
  - Ensure `DeterminePhase` can return `ReviewReady` (today it never does).
- [x] **1.3** Simplify shot-plan branch (ready vs stale vs missing) so Film vs Review gates are obvious.
- [x] **1.4** Confirm PDF `TextExtractionPending` vs text-only import paths against Import UI.

### 1.B Optional helpers (keep API small)

- [ ] **1.5** Add `DeterminePhase(AdaptationStatus?)` coverage only — avoid bloating until needed.
- [ ] **1.6** Add `NextStudioStep(StudioPhase phase, AdaptationStatus status)` **or** `DescribeBlocked(...)` only if it replaces stringly `NextStep` later (can defer to Phase 4).
- [ ] **1.7** Map `StudioStep` ↔ strip keys in one helper used by Web (optional).

### 1.C Unit tests (required before rewiring UI)

New test class e.g. `host/PageToMovie.Tests/StudioStateMachineTests.cs`:

- [x] **1.8** `DeterminePhase_null_or_no_keys` → `SetupRequired` / `ImportRequired` as designed.
- [x] **1.9** Draft exists, not signed → `ScreenplayDraft`.
- [x] **1.10** Signed / ready for shots, no stage2 → `ScreenplayApproved`.
- [x] **1.11** Stage2 ready + not stale → at least `ShotPlanReady`.
- [x] **1.12** Clips/review complete → `ReviewReady` (once defined).
- [x] **1.13** `CanNavigateTo(Cast)` false before approved; true after.
- [x] **1.14** `CanNavigateTo(Film)` respects stale stage2 + castReady args.
- [x] **1.15** `CanNavigateTo(Estimate)` tracks Cast unlock phase (approved).
- [x] **1.16** PDF extraction pending blocks Cast with the extraction message.

**Exit:** Core tests green; phase semantics match product intent; machine still unused by UI is OK.

```powershell
dotnet test host/PageToMovie.Tests/PageToMovie.Tests.csproj --filter "FullyQualifiedName~StudioStateMachine"
```

---

## Phase 2 — Façade: `ActiveProjectState` delegates to the machine

**Risk:** Medium (nav strip + page gates). Highest leverage step.

### 2.A Implementation

- [x] **2.1** In `ApplyFromStatusPayload`, after resolving `AdaptationStatus`:
  - [ ] `var phase = StudioStateMachine.DeterminePhase(status);`
  - [ ] Read `castReady` / `stage2Stale` once from status (keep existing property reads).
  - [ ] For each of Cast, Estimate, Film, Review: call `CanNavigateTo` and assign `Can*` + `*BlockedReason`.
- [x] **2.2** Delete duplicated boolean soup that reimplements the same rules (keep JSON prop helpers only if still needed for cast/stage2 inputs).
- [x] **2.3** Optionally expose `StudioPhase CurrentPhase { get; private set; }` on `ActiveProjectState` for debugging / future UI badges.
- [x] **2.4** Ensure `ClearReadiness` messages still match machine default blocked strings (or call machine with a null/empty status).

### 2.B Verification

- [ ] **2.5** Manual: unsigned project → Cast/Estimate/Film/Review disabled with expected tooltips.
- [ ] **2.6** Manual: after screenplay approve → Cast + Estimate enabled; Film still blocked until shot plan.
- [ ] **2.7** Manual: stage2 stale → Film blocked with “Update the shot plan first”.
- [ ] **2.8** Manual: cast incomplete → Film blocked with voice/image message when plan exists.
- [ ] **2.9** Smoke `Characters.razor` gate and `StudioProcessStrip` (no code change expected if façade is faithful).

**Exit:** Strip behavior equivalent (or intentionally fixed); single implementation path for nav gates.

---

## Phase 3 — Adaptation book strip helpers

**Risk:** Medium (adaptation sub-steps, banners).

- [x] **3.1** Rewrite `AdaptationStepUi.OutlineEnabled` in terms of phase or shared predicates (draft/import available), not a one-off OR-chain if avoidable.
- [x] **3.2** Rewrite `ShotsEnabled` to match machine approval (same as Cast unlock / `ScreenplayApproved+`), not a lone `ReadyForShots` if that diverges from phase.
- [x] **3.3** Audit `ShowNextStepBanner` — either leave as presentation-only on server `NextStep`, or gate “should show” using phase to avoid contradictory banners.
- [x] **3.4** Audit `AdaptationShell` `NeedsReapprove` (signed hash + not ready) — leave as draft-dirty signal; ensure it doesn’t fight phase.
- [ ] **3.5** UiTests / manual pass on Import → Screenplay → Shots step strip.

**Exit:** Book/adaptation strip unlock rules consistent with top nav.

---

## Phase 4 — Server `NextStep` alignment (optional but recommended)

**Risk:** Medium-high (API consumers, banners, redirects).

- [ ] **4.1** Inventory all readers of `AdaptationStatus.NextStep` (Web, tests, any clients).
- [ ] **4.2** Choose strategy:
  - **A (preferred):** ProjectStore computes phase via `DeterminePhase`, then maps phase → `NextStep` string in **one** Core helper used by the store; or  
  - **B:** Keep store algorithm but add parity tests: for each fixture, store next-step **implies** the same unlocks as `CanNavigateTo`.
- [ ] **4.3** Implement chosen strategy; remove divergent branches.
- [ ] **4.4** Update `SuggestedStepPath` if redirects should follow phase map.
- [ ] **4.5** Regression: import → draft → sign-off → characters → shots → scenes happy path (UiTest or manual script).

**Exit:** One pipeline story on server and client.

---

## Phase 5 — Page-local cleanup & observability

**Risk:** Low–medium (drive-by refactors).

- [ ] **5.1** Replace obvious duplicate gates in `Cost.razor.cs` / Scenes entry with `ActiveProject.*` (already façade) — avoid re-reading raw stage flags for unlock.
- [ ] **5.2** Screenplay sign-off path: after success, refresh status → phase must be `≥ ScreenplayApproved` before `NavigateTo("characters")` (assert in test or soft-check).
- [ ] **5.3** Add debug-only or admin readout: current `StudioPhase` (settings/diagnostics) to validate production projects.
- [ ] **5.4** Grep guardrail in CI or doc: fail PR review if new `ReadyForShots &&` unlocks appear outside Core/machine façade (manual checklist OK).
- [ ] **5.5** Update `host/docs/screenplay-editor-integration-plan.md` Step 3 language: **no** `SetStateAsync(ScreenplayApproved)` — sign-off mutates status; machine derives phase.

**Exit:** Fewer scattered checks; phase visible for support.

---

## Phase 6 — Hardening & close-out

- [ ] **6.1** Full offline suite:  
  `dotnet test host/PageToMovie.Tests/PageToMovie.Tests.csproj --filter "FullyQualifiedName!~LiveApi"`
- [ ] **6.2** Targeted UI tests for nav gates / screenplay sign-off if present under `PageToMovie.UiTests`.
- [ ] **6.3** Mark completed items in this doc; note any intentional behavior fixes (e.g. Stage1-only no longer unlocks Cast).
- [ ] **6.4** Short PR description: before/after diagram + list of deleted duplicate logic.
- [ ] **6.5** Ship behind no flag if parity tests pass; otherwise feature-flag façade for one release (`UseStudioStateMachineGates`).

**Exit:** Migration complete; machine is the live SSoT.

---

## Suggested PR sequence (small slices)

| PR | Scope | Checkbox range |
| :--- | :--- | :--- |
| **PR1** | Core fixes + `StudioStateMachineTests` | Phase 1 |
| **PR2** | `ActiveProjectState` façade only | Phase 2 |
| **PR3** | `AdaptationStepUi` unlock helpers | Phase 3 |
| **PR4** | ProjectStore `NextStep` alignment | Phase 4 |
| **PR5** | Cleanup, docs, optional diagnostics | Phase 5–6 |

Do **not** combine PR1+PR4 in one change set.

---

## Verification cheat sheet

```powershell
# Machine only
dotnet test host/PageToMovie.Tests/PageToMovie.Tests.csproj --filter "FullyQualifiedName~StudioStateMachine"

# Web still builds after façade
dotnet build host/PageToMovie.Web/PageToMovie.Web.csproj -c Release

# Broad offline
dotnet test host/PageToMovie.Tests/PageToMovie.Tests.csproj --filter "FullyQualifiedName!~LiveApi"
```

Manual matrix:

| Project state | Expect Cast | Expect Estimate | Expect Film | Expect Review |
| :--- | :---: | :---: | :---: | :---: |
| No keys / no import | off | off | off | off |
| Draft, unsigned | off | off | off | off |
| Signed, no stage2 | on | on | off | off |
| Stage2 ready, cast incomplete | on | on | off* | off* |
| Stage2 ready, cast ready | on | on | on | per ReviewReady rule |
| All clips / review ready | on | on | on | on |

\*Exact Film/Review split follows `CanNavigateTo` + `castReady` / `ReviewReady` definition from Phase 1.

---

## Risks & mitigations

| Risk | Mitigation |
| :--- | :--- |
| Silent unlock/lock change | Fixture parity tests before deleting old logic; manual matrix |
| `ReadyForShots` vs `Signed` mismatch | Phase 1 product decision, tested |
| UiTests coupled to old disabled tooltips | Update selectors/titles in same PR as façade |
| Server `NextStep` breaks redirects | Phase 4 isolated PR + SuggestedStepPath tests |

---

## Out of scope reminders

- [ ] Do **not** add `ProjectStore.SetStateAsync(StudioPhase)` unless product later requires durable workflow instances.
- [ ] Do **not** move Screenplay Editor models into the state machine.
- [ ] Do **not** block this migration on full LiveApi suite.

---

## Quick reference — files to touch

| File | Phases |
| :--- | :--- |
| `host/PageToMovie.Core/Models/StudioStateMachine.cs` | 1 |
| `host/PageToMovie.Tests/StudioStateMachineTests.cs` (new) | 1, 6 |
| `host/PageToMovie.Web/Services/ActiveProjectState.cs` | 2 |
| `host/PageToMovie.Web/Components/Shared/StudioProcessStrip.razor` | 2 (verify only) |
| `host/PageToMovie.Web/Components/Pages/AdaptationStepUi.cs` | 3 |
| `host/PageToMovie.Web/Components/Pages/AdaptationShell.razor.cs` | 3 |
| `host/PageToMovie.Engine/ProjectStore.cs` (NextStep builder) | 4 |
| `host/docs/screenplay-editor-integration-plan.md` | 5 |
| This file | all (check off as you go) |
