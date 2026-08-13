# Mary4 UI / Pipeline — Implementation Plan

Source: `PageToMovie_Mary4_UI_Pipeline_Summary_2026-08-03.txt`
Written against `master` @ `69fc2794` (2026-08-04). Supersedes nothing in
`mary4-ui-checklist.md` — this is the *how* for what that file lists as Open.

## 0. State after the last pull — what is already DONE

Commit `69fc2794 fix(ui): Mary4 Estimate continue, micro runtime, cast/screenplay polish`
already closed most of the summary's Critical/High/Medium single-item fixes. Verified in code:

| Summary item | Status | Where |
|---|---|---|
| §4 "Agree & Continue" on Estimate | ✅ done | `Cost.razor:154` → `AgreeAndContinueAsync` navigates to `scenes` |
| §4 Target length carry-over (real ~0.5 → floor 1 min) | ✅ done | `NaturalRuntime.MinMinutes=1`, `FilmLengthCard` min=1, `FilmRuntimeTests` 11/11 |
| §4 Live cost update on length change | ✅ wired | `FilmLengthCard OnChanged="LoadAsync"` on `Cost.razor:90` |
| §2 Screenplay editability regression | ✅ done | `setReadOnly(false)` after init/load, `AdaptationScreenplay.razor:397,445` |
| §2 Re-draft buttons admin-only + helper text removed | ✅ done | `@if (Session.IsAdmin)` block `AdaptationScreenplay.razor:190` |
| §2 Book tooltip "Show source passage" | ✅ done | commit note; book modal present |
| §3 Character images persist on switch | ✅ claimed done | stable `@key` in `Characters.razor` — **needs live smoke** |
| §3 Middle name-strip thumbnail removed | ✅ done | `Characters.razor` |
| §3 Unlock moved into "Look & voice" card | ✅ done | `Characters.razor` |
| §3 Empty voice UI hidden + "Add voice…" hatch | ✅ done | `Characters.razor` |

**Remaining work = 4 real tracks below** (plus two verification smokes).

Also already present as building blocks for the new stages:
- `VisualMediumCard.razor` + API `GET/PUT /api/projects/{id}/visual-medium` +
  `ProjectVisionMeta.SetAdaptationMediumPreference` — the Look & medium *data model and control* exist;
  they are just embedded as a card (`AdaptationImport.razor:154`, `AdaptationScreenplay.razor:12`),
  not a pipeline stage.
- Stage strip is centralized in `StudioProcessStrip.razor` (numbered steps + gating from `ActiveProjectState`).

---

## Track A — Verification smokes (no/low code) — do first

Cheap, unblocks sign-off on the already-landed fixes. Use fakes (`PageToMovie__UseFakes=true`) to avoid spend.

1. **Cast images persist.** Load Mary4 → Characters. Select Lamb → Mary → Teacher; confirm no look image
   drops on switch (the §3 critical bug). If it regresses, the fix is the `@key`/preload path in
   `Characters.razor` — capture which character drops first.
2. **Natural length surfaces end-to-end.** Import → Screenplay → Estimate; confirm Estimate shows
   `~1 min target (natural ~1)` (0.5 floored to 1), not a stale 3 min. Check `FilmLengthCard` and
   `cost-estimate` testid.

Deliverable: tick the two "Open — High" boxes in `mary4-ui-checklist.md`, or file precise repro if either fails.

---

## Track B — Cost page split (§4 "Cost page clutter")  — Medium, self-contained

**Goal:** Two views. (1) *Current Project Estimate* — clean, only the active project. (2) *All Projects /
Account Cost Overview* — the cross-project breakdown, under Account / Billing.

**Today:** `Cost.razor` mixes both — the "Your spending" card (`cost-my-spend`, lines 241–348) already
renders all-projects totals, by-project table, and by-vendor table; the rest of the page is
current-project estimate + spend.

**Plan:**
1. New page `Components/Pages/AccountCosts.razor`, route `/account/costs` (add to nav under Account/Billing).
   Move the entire "Your spending" block (all-projects totals + `cost-my-by-project` + `cost-my-by-vendor`)
   there. It already binds to `Engine.GetMySpendAsync()` (account-wide, project-independent) — lift as-is.
2. Trim `Cost.razor` to current-project only: hero cards (Estimate/Spent/Remaining), `FilmLengthCard`,
   Agree & Continue, estimate pie, spent pie, category cards. Keep the project `<select>` (a power user can
   still switch), but drop the cross-project "By project"/"Projects with spend" tiles.
3. Keep provider names only on these two cost surfaces (allowed per CLAUDE.md §5). Preserve all `data-testid`s
   when moving nodes so Playwright specs keep resolving; move the specs' navigation target to `/account/costs`.
4. Add a small "See all projects & billing →" link from Cost to `/account/costs`.

**Risk:** low — mostly relocating existing markup. No API change. Watch the shared `_mySpend`/`LoadAsync`
split so each page only loads what it shows.

---

## Track C — Promote "Look & medium" to its own stage (§1) — Medium

**Goal:** `Book → Look & medium → Screenplay → …` so changing visual style does not require a full
re-import, and support a lightweight fountain→fountain regeneration.

**Today:** medium is a *preference* only. `PUT /visual-medium` (`Program.cs:5163`) writes the preference and
is "applied when the screenplay is written" — it does **not** regenerate an existing Fountain. So changing
medium after a draft exists does nothing until a full re-import.

**Plan:**
1. **New stage page** `Components/Pages/AdaptationLook.razor`, route `/adaptation/look`. Host the existing
   `VisualMediumCard` as the primary control plus copy explaining what changing it does. Remove the card from
   `AdaptationImport.razor` and `AdaptationScreenplay.razor` (single home; CLAUDE.md §5 "one fact in one place").
2. **Strip step.** Add a step to `StudioProcessStrip.razor` between Book (1) and Cast (renumber Book=1,
   Look=2, then Cast/Estimate/Film/Review shift +1). Add `Active="look"` handling. Gate it behind
   "book imported" (reuse whatever `CanEstimate`/book-text signal exists; add `CanLook` to
   `ActiveProjectState` if a distinct gate is wanted).
3. **Lightweight fountain→fountain regeneration.** New endpoint
   `POST /api/projects/{id}/adaptation/reskin` that takes the *current approved Fountain* + new medium and
   runs a medium-swap pass (visual/setting/wardrobe description only; dialogue, beats, scene order unchanged).
   Implement as a new method on `AdaptationService` reusing `MultiProviderChatClient`; add a prompt
   `prompts/fountain_reskin.txt`. When medium changes on the Look stage *and* a draft already exists, offer
   "Re-apply look to screenplay" (runs reskin as a job) vs. "Only affect next import".
   - Reuse the existing job plumbing (`FilmJobService`/`JobStore`, SignalR `/hubs/jobs`) so the UI shows
     progress like other adaptation jobs.
   - Guardrail: reskin must not change scene count or `@character` cues — validate output the same way
     `BookToFountainConverter` fixup does; reject/retry on structural drift.
4. Copy stays provider-neutral and outcome-only (CLAUDE.md §5): "look", "medium", "screenplay" — no model names.

**Risk:** medium. The regeneration is the real work; the page/strip move is mechanical. Ship the page + strip
move first (pure preference, no regen) as C1, then add reskin as C2 — they're independently valuable.

---

## Track D — Max fountain → Embellish → Trim → Edit → Cast (§1, revised) — largest, do last

**Revised pipeline (this session's decision):**

```
Book → Look & medium → Screenplay (MAX fountain) → Scene Embellishment
     → Trim to cost/length → User edit → Cast & voice → Estimate → Film → Review
                    └──────── canonical shareable base ────────┘
                              (fork point: forkers start at Trim)
```

**Core idea — generate maximal once, subset many times.** Produce the *full natural-length* screenplay and
enrich it once (the expensive steps). Everything downstream is a cheap, non-destructive *derivation* of that
base. The **max fountain + embellishment is a canonical artifact** other users can fork and trim to their own
time/cost needs — a head start that skips book→fountain→embellish generation entirely.

How we *get* that max (index of logical scenes → batched writes, not 40k text chunks): [`max-master-adaptation-plan.md`](max-master-adaptation-plan.md).

**Why this order (user rationale):** trimming is where the user shapes the film to their budget — they may add
or remove scenes and change dialogue. Cast must therefore extract from the *final edited* screenplay, not any
earlier draft (see D5).

### D0. Screenplay = MAX fountain (change existing generation)

**Today:** generation targets a length — `BookToFountainConverter` bakes `{{TOTAL_RUNTIME_MINUTES}}` into the
prompt ("Target runtime about X minutes"), and `FilmRuntime.Mode` is `natural | reduced | custom`.

**Change:** at the Screenplay stage, always generate at **natural/max** (pass natural minutes, never a reduced
target). Length stops constraining generation and becomes a Trim input. Persist the result as the immutable
base `screenplay.max.fountain`. Keep `reduced`/`custom` modes only as *trim* targets, not generation targets.
Move the `FilmLengthCard`/target control **off** Screenplay onto the Trim stage.

**Feasibility — measured, not assumed (2026-08-04).** Model limits come from the catalog SSoT
(`models_catalog.json`); sizes measured on real book/fountain pairs in this repo. This de-risks D0 substantially:

- **Model limits (chat Grok):** `grok-4.20-reasoning` 1,000,000 in / 128,000 out · `grok-4.5` 500,000 in /
  128,000 out · `grok-4` 256,000 in / 128,000 out. **Input is no longer the constraint; the 128K *output* cap
  is the ceiling for single-pass generation.**
- **Output is a fraction of input.** A fountain is a condensation (headings + action + dialogue, not prose).
  Measured out/in token ratios: A Christmas Carol 0.37× (44,478→16,454), Call of the Wild 0.11×
  (48,788→5,388), Nick and Me 0.09× (68,141→5,897). Today's fountains run **~10–40% of input tokens**.
- **Single-pass max is realistic for most of the library.** Even the largest books — Little Women (~258K
  tok), Dracula (~216K tok), Huck Finn (~148K tok) — now fit in one input context on grok-4.5/4.20, so the old
  input-driven multi-chunk *adapt → stitch → merge* in `BookToFountainConverter` is **no longer needed for most
  books** (and dropping it removes the continuity-merge step that has been a bug source — track as a follow-up
  simplification, not a rewrite: keep multi-chunk as a fallback path).

**D0 build guidance:**
1. Generate max in **one pass** when `bookTokens ≲ model maxInputTokens` (nearly all books on grok-4.5/4.20);
   fall back to the existing multi-chunk path only above that.
2. A *max + embellished* fountain is ~2–4× today's trimmed size — for the longest 3–4 novels that can approach
   the **128K output cap**. Add a **continuation strategy on output** (continue-from-cursor / resume when the
   response hits `maxOutputTokens`), distinct from the old *input* chunking. Guard on
   `estimatedOutputTokens > ~100K` → generate in ordered continued segments and concatenate.
3. **Model choice:** default max generation to a high-output **non-reasoning** model (grok-4.5, 128K out) —
   reasoning traces eat the output budget. Reserve reasoning models for the shorter enrich (D1) / trim (D2)
   passes where their budget is ample.

### D1. Scene Embellishment (enrich the max) — nothing exists today

1. **Prompt** `prompts/embellish_scene.txt`: input = max Fountain + per-scene book passage + resolved visual
   medium token; output = same Fountain with enriched **action lines only**. Hard rules in-prompt: never touch
   dialogue, `@character` cues, scene headings, scene count, or order. Register in the prompt pack
   (`AdaptationPromptPack` / `AdaptationPromptTokens`) so tokens resolve before the model sees them (commit
   `61f6a8b0`).
2. **Engine service** `SceneEmbellishmentService` (`PageToMovie.Adaptation`): per-scene enrich with continuity
   carry-over (same chunk/merge discipline as `BookToFountainConverter`), book passages via
   `BookTextRegistryService`, medium via `ProjectVisionMeta`. Validate structural invariants per scene; on
   drift, retry once then keep the original scene.
3. **API** `POST /api/projects/{id}/adaptation/embellish` → `FilmJobService` job (kind `embellish`), writes
   `screenplay.embellished.fountain` from the max base. Non-destructive: base preserved.
4. **UI stage** `Components/Pages/AdaptationEmbellish.razor`, route `/adaptation/embellish`; strip step between
   Screenplay and Trim. Before/after per scene, Regenerate, and **Approve → Trim**. Provider-neutral copy.

### D2. Trim to cost/length (new derivation stage)

1. **Prompt** `prompts/trim_scene.txt` (or a length-aware condense pass): input = embellished Fountain + target
   minutes/budget; output = a shorter Fountain that cuts/condenses scenes and beats to hit target while keeping
   the descriptive richness proportionate. Structure change here is *expected* (unlike embellish).
2. **Engine** reuse `FilmRuntime.SetTargetAsync` (already persists target + mode, `Program.cs:5234`) to hold
   the target; add a trim job (kind `trim`) that derives `screenplay.fountain` (the working screenplay) from
   `screenplay.embellished.fountain`. Re-running with a different target re-derives cheaply from the base —
   never re-generates from book.
3. **UI stage** `Components/Pages/AdaptationTrim.razor`, route `/adaptation/trim`; strip step between Embellish
   and Cast. Host the `FilmLengthCard` + a **live cost readout** (satisfies summary §4 "live cost updates as
   target length changes" — the same `Engine.GetCostAsync` used on Cost). Show natural vs. trimmed length and
   the resulting number. **This is a rough preview, not the commitment** — label it so ("estimated from the
   screenplay; firms up after cast & voice"). The real quote still lives on the Estimate stage after the
   cost levers below are set (see D7). Actions: Retrim, and **Continue → edit / Cast**.

### D3. User edit + D4. Cast

- The trimmed `screenplay.fountain` loads into the existing Screenplay editor for hand-edits (reuse
  `AdaptationScreenplay` editor; it already autosaves + validates Fountain). User may add/remove scenes here too.
- **Cast extraction runs on approval of the *edited, trimmed* screenplay**, not earlier.

### D5. Cast trigger moves off screenplay sign-off (ripple)

- **Today** `SignOffScreenplayAsync` (`Program.cs:5044`) does save + approve + **cast build**, and the page
  navigates to `characters` (`AdaptationScreenplay.razor:709`). That must move.
- **New:** the max-screenplay sign-off approves the base and advances to **Embellish**; it no longer builds
  cast. Cast build moves to the **final "Continue → Cast"** action after Trim + edit, running on
  `screenplay.fountain`.
- `ActiveProjectState.CanCharacters` re-keyed on *trimmed screenplay approved* (not max signed). Add
  `pipeline_state` flags (`embellished`, `trimmed`, and dirty variants alongside `scene_dirty`); editing or
  re-trimming marks cast dirty → re-extract on next approval (reuse learning-loop cascade).
- Because the user restructures on Trim/edit, structural validation there is **advisory** (warn on drift, don't
  reject). The hard "no structure change" rule applies only to the automated **embellish** pass (D1).

### D6. Fork reuse — leverage existing fork infra (no net-new plumbing)

- The base (`screenplay.max.fountain` + `screenplay.embellished.fountain`) is the shareable artifact. Fork
  support already exists: `ForkProjectAsync`, 1-click public-forkable (`Program.cs:4257`),
  `SyncForkFromOriginAsync`, `ParentProjectId`/`VisibilityMode`, invite-to-fork.
- **Plan:** allow publishing a project as forkable once **Embellish is approved** (the base is complete). A
  forker inherits max + embellished fountain and **starts at the Trim stage** with their own target/budget —
  they never pay for book→fountain→embellish. `StudioProcessStrip` should detect a fork whose base is present
  and land the user on Trim (skip Book/Look/Screenplay/Embellish, shown as inherited/done).
- Keep max + embellished as clean, separate, effectively immutable artifacts (distinct from the per-fork
  trimmed working screenplay) so forks stay cheap and the base stays reusable.

### D7. Two cost touchpoints — Trim (preview) vs. Estimate (commit). Keep both.

Estimate is **not** folded into Trim. Cost-changing levers land *between* Trim and the commit, during
Cast & voice — so the Trim number can only be a preview:

- **Voice cloning / user-as-character.** The user may clone their own voice and become a cast member — adds
  voice-clone + per-character cost and can change the speaking-cast count. (Voice is currently shown as
  "not in estimate" until chosen — `Cost.razor:201`.)
- **Reuse vs. regenerate video.** They may leverage previously generated video and only clone a narrator
  voice over it (little/no new video spend), or generate all-new video (full video spend). Huge cost delta,
  decided after Trim.
- **Resolution / retries / model choices** already live on Cost (`_draftRes`, retries) and move the number too.

**Therefore:**
- **Trim stage** = screenplay-level *preview* (length → rough $), helps the user pick a target early.
- **Estimate stage** (existing, `Cost.razor`) = the **commit gate** after cast/voice/reuse are set, with the
  precise per-model/per-vendor breakdown and the "Agree & Continue to Film" button (already shipped, §4).
- Order stays: … → Trim → Cast & voice → **Estimate (commit)** → Film. The Estimate strip step already gates
  on `CanEstimate`; no change needed there beyond it consuming the trimmed screenplay + chosen voice/reuse
  levers.

**Risk:** high — new prompts (embellish, trim) + two Engine services + two jobs + two pages + generation-mode
change + cast-trigger move + fork entry-point. Sequence inside D: D0 (max gen) → D1 (embellish) → D2 (trim) →
D5 (cast move) → D6 (fork). Gate behind admin, dogfood on Mary4, then expose. Most likely to need a
prompt-quality loop (checklist "Open — Later").

---

## Recommended sequence

1. **Track A** (smokes) — confirm the landed fixes; ~30 min.
2. **Track B** (Cost split) — self-contained, no API/prompt work, clears the "locked/cluttered" complaint fully.
3. **Track C1** (Look page + strip move, preference only) — mechanical, low risk.
4. **Track C2** (fountain→fountain reskin) — first real generation feature; validates the fountain→fountain
   job + structural-invariant pattern that D1 (embellish) and D2 (trim) both reuse.
5. **Track D** (Max fountain → Embellish → Trim → Edit → Cast, + fork reuse) — largest; build in the internal
   order D0 → D1 → D2 → D5 → D6. Benefits from C2's validation/job patterns.

## Cross-cutting constraints (from CLAUDE.md / AGENTS.md)

- **Generalize, never hardcode** — no "Mary"/"Lamb"/book-specific strings in Engine/Web/API. Sample-project
  data edits are fine; the code path must work for the next book.
- **Workflow copy** is outcome-only, provider-neutral, jargon-free on Book/Look/Screenplay/Embellish/Trim/Cast/
  Estimate/Film/Review. Model/provider names only on Configuration and Cost/Account-costs. Avoid pipeline jargon
  in labels — "fountain" → "screenplay", "trim/subset" → user-facing "fit to your length & budget".
- **One fact in one place** — moving `VisualMediumCard` to its own stage means removing it from the two
  current hosts.
- **Spend discipline** — every new generation path (reskin, embellish) runs through `FilmJobService`, respects
  `UseFakes`, and never calls paid APIs from non-`LiveApi` unit tests. Add fake-backed tests for each new
  service + a Playwright pass for each new page.
