# PageToMovie documentation

This is the **map**. Read top-down. Each section is the product story in a page or two; the links go to the living detail. If a file is not linked here, treat it as history until we archive it.

**Start here if you are new:** [root README](../README.md) (what it is, how to run) → this page → the state machine.

---

## 1. Why we build it this way

| Principle | One line | Detail |
|-----------|----------|--------|
| Excellent film, little input | Select a story → strong film. Automate judgment; keep budgets deterministic. | [AGENTS.md · north star](../AGENTS.md#product-north-star-always-decide-with-this-in-mind) |
| **Max master, cut later** | Write the whole book once. 120 minutes or miniseries is a **view**, not a second adapt. | [Max-master plan](../host/docs/max-master-adaptation-plan.md) |
| Book → Estimate → Film | Cast is optional craft, not a required stop before you see $ and minutes. | [Studio decision flow](studio-decision-flow.md) |
| Enrich is automatic | After the Fountain exists, deepen visuals from the book. Dialogue and scene count stay. | Same max-master plan; admin can re-run a one-off |
| One process | `dotnet run --project PageToMovie.Api` serves UI + API. No second Blazor site. | [root README · Run](../README.md#run) · [host README](../host/README.md) |

---

## 2. Two ways in

### Easy Start — story already filmed, you are the voice

[root README · Easy Start](../README.md#easy-start) · route `/simple-voice`

1. Browse public (forkable) titles — no login to look.
2. Sign in, pick one → we **fork** a private copy (inherit max master + index if present).
3. Record one sample (or choose the speaker).
4. Make movie — narrator lines only; pictures stay.

How voice lands on clips: [Voice substitution](../host/docs/voice-substitution-design.md) · karaoke / alignment: [Voice capture](../host/docs/voice-capture-karaoke.md) (merge these two later).

How a title gets on the shelf: mark **Public (Forkable)** — [Public community](../host/docs/public-community-plan.md) (status table; title still says “plan” but fork/visibility/invite are built).

### Full studio — you are making the film

Strip: **Book → Estimate → Film → Review**. Cast & locations sit under Edit, after you have seen the estimate.

Phases and who may open each step: **[State machine](studio-decision-flow.md#4-state-machine)**.

---

## 3. State machine (canonical)

**Detail:** [studio-decision-flow.md §4](studio-decision-flow.md#4-state-machine)

```text
Need project
  ├─ import book  → write Fountain → auto-enrich → Screenplay ready
  └─ import Fountain shortcut ─────────────────────→ Screenplay ready
         ↓
    Estimating  →  Decision (Generate movie | Edit plan)
         ├─ Owner: confirm → generate → watch
         ├─ Editor: scene gen on Film (not full movie)
         └─ Edit → dirty plan → Estimating again
```

Code implements the same gates (`StudioPhase` / `StudioStep`). Do not treat dated UI checklists as the machine.

Also in that doc: estimate tiers, Generate vs Edit, multi-user guards. Read §1–3 before changing Home / Estimate / Film chrome.

---

## 4. Pipeline (what actually runs)

```text
Ingest → Index → Write max Fountain → Auto-enrich
      → Estimate / trim (view)
      → Cast + locations (used only; 3 looks; auto-lock)
      → Shot plan (classifiers)
      → Generate clips
      → Review + stitch in the browser
      → Share / fork
```

| Stage | What to read |
|-------|----------------|
| Ingest (txt / PDF / Fountain, OCR) | [Adaptation session](../host/docs/adaptation-session-pipeline.md) *(fold `file_id` rules into max-master later)* |
| Index + write + enrich + trim | [Max master](../host/docs/max-master-adaptation-plan.md) · [Adaptation module](../host/PageToMovie.Adaptation/README.md) |
| Prompts | [prompts/README](../prompts/README.md) |
| Every model call | [MODEL_CALL_INVENTORY](architecture/MODEL_CALL_INVENTORY.md) |
| Shot plan / action timing | [Action timing](../host/docs/action-timing-plan.md) |
| Estimate / $ / minutes | [Decision flow §2](studio-decision-flow.md#2-progressive-costing-model) |
| Cast / plates / voice | Decision flow + [voice substitution](../host/docs/voice-substitution-design.md) |
| Review / stitch / ffmpeg.wasm | [host README](../host/README.md) (browser media) |
| What is on disk | [Project artifacts](../host/docs/project_artifacts.md) |
| Models / providers / keys | [Supported models](../host/docs/supported-models.md) · catalog JSON is SSoT |

Default planning chat model: **Grok 4.6** (`models_catalog.json`). Do not list models in prose.

---

## 5. Share, fork, many people

| Topic | Detail |
|-------|--------|
| Visibility + fork + invite + contribution | [Public community](../host/docs/public-community-plan.md) |
| Same-project ACL, leases, presence | [Multi-user collaboration](../host/docs/multi-user-collaboration.md) |
| Scale / 100-user soak (plan + LoadSim) | [multi-user-100](../host/docs/multi-user-100-plan.md) · [LoadSim](../host/docs/loadsim-soak.md) |

Easy Start **is** the public-fork path with a voice step. Do not invent a third sharing model.

---

## 6. Learning and quality

| Topic | Detail |
|-------|--------|
| Review notes → prompts (operator loop) | [host/docs/learning-loop.md](../host/docs/learning-loop.md) — fold into [film-provenance](../host/docs/film-provenance-critic-learning-architecture.md); ignore older `docs/learning_loop.md` |
| Provenance / critic (architecture) | [film-provenance…](../host/docs/film-provenance-critic-learning-architecture.md) (keep as design; do not treat as shipped) |
| Copy / jargon | [USER_COPY_JARGON_AUDIT](USER_COPY_JARGON_AUDIT.md) (working table) |

---

## 7. Run, test, operate

| Topic | Detail |
|-------|--------|
| Run one process | [root README](../README.md#run) |
| API, SignalR, YouTube, LoadSim | [host/README](../host/README.md) |
| Tests | [root README · Tests](../README.md#tests) |
| Playwright | [host/playwright/README](../host/playwright/README.md) |
| Screenplay benchmark | [host/evals/screenplay_benchmark/README](../host/evals/screenplay_benchmark/README.md) |
| Open work | [Backlog](../host/docs/backlog.md) |
| Agent rules | [AGENTS.md](../AGENTS.md) |

`CLAUDE.md` still says “two terminals / run Web.” Ignore that run block; use the root README. Fold CLAUDE into AGENTS or fix it in the cleanup pass.

---

## 8. Sanity check — what stays, what goes

Checked against: Book → Estimate → Film, max master, **auto-enrich**, one-process host, Easy Start, Grok 4.6 catalog default.

### Living (linked above)

Product story and law:

- `README.md` (run + Easy Start) · **this file** (map)
- `AGENTS.md` (north star; fold `CLAUDE.md` into it)
- `docs/studio-decision-flow.md` (state machine)
- `host/docs/max-master-adaptation-plan.md` (**fix header** — P0–P5 are on master)
- `docs/architecture/MODEL_CALL_INVENTORY.md`
- `host/PageToMovie.Adaptation/README.md` · `prompts/README.md`

Estimate, voice, share, models, ops:

- `host/docs/action-timing-plan.md`
- `host/docs/voice-substitution-design.md` (absorb karaoke)
- `host/docs/public-community-plan.md` (**retitle** — most of the table is built)
- `host/docs/multi-user-collaboration.md` · `host/docs/multi-user-100-plan.md`
- `host/docs/supported-models.md`
- `host/docs/film-provenance-critic-learning-architecture.md` (design; not all shipped)
- `host/docs/project_artifacts.md` · `host/docs/learning-loop.md` · `host/docs/backlog.md`
- `host/README.md` · `host/docs/loadsim-soak.md` · `host/docs/sonar-duplicate-detection.md`
- Satellites: `books/`, `scripts/`, `host/scripts/`, `host/evals/`, screenplay-benchmark, Playwright (fix two-terminal)

### Merge next

| Into | From |
|------|------|
| `AGENTS.md` | `CLAUDE.md` (one-process run only) |
| Voice | karaoke + substitution |
| `supported-models.md` | catalog UI / self-test / scan / labMode / cost-catalog-only |
| film-provenance | both learning-loop files + leftover north-star ⬜ rows |
| max-master | `file_id` / session rules from `adaptation-session-pipeline.md` |
| `backlog.md` | `north-star-checklists.md` open rows; leftover Mary4 / editor tasks |
| `public-community-plan.md` | `github-projects-backup-checklist.md` |
| Tests index | Playwright notes; `ui-test-checklist` boxes → backlog |

### Archive (`docs/archive/`) — do not delete

Dated snapshots: leftover `host/docs/*2026-08-05*`, `perf-findings-2026-07`, capability-mapping, provider-heuristic-audit, fake-capability, ui-audit / s3-s5 / stage2-seed / items2-5.

Completed plans: reliability phases, lifecycle checklist, adaptation-module plan, screenplay-editor plan, async-io, client-storage, blazor-refactor, Mary4, runtime-and-Mary, automatic-model-selection, jargon audit.

### Delete candidates

- `docs/PROTOTYPE_LOOK_FEEL.md` — Cast-before-Estimate; branch note
- `host/docs/gap-analysis.md` — contradicts the community status table

### Stale claims (fix when you touch the living file)

| File | Stale |
|------|--------|
| `CLAUDE.md`, Playwright README | Two-terminal Blazor |
| `max-master-adaptation-plan.md` | “not built as the default path” — P0–P5 checked |
| `public-community-plan.md` title | “not implemented” vs table of done |
| `host/README.md` REST row | “Stage 1 scene bible” |
| `prompts/README.md` | Enrich as a tool; novels = 40k chunker; no `book_to_index.txt` |
| `backlog.md` | Embellish as a first-class UI stage |
| `project_artifacts.md` | Remux; missing max / index |
| `adaptation-session-pipeline.md` | Grok 4.5-only; sidecars ≠ product |
| `mary4-implementation-plan.md` | Embellish stage; grok-4.5; Cast before Estimate |

---

## 9. Cleanup sequence (when we say go)

1. Create `docs/archive/` and move the archive set. Stub any inbound links.
2. Merge the pairs in §8.
3. Fix the stale headers (max-master, community title, scene bible, prompts, CLAUDE).
4. Root README docs table points **only** at this file.
5. No new `*-YYYY-MM-DD.md` under `host/docs/`. Update a living doc or add a backlog line.

No mega-doc. Decision flow, max-master, and the model inventory stay separate.
