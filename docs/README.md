# PageToMovie documentation

This is the **map**. Read top-down. Each section is the product story in a page or two; the links go to the living detail. If a file is not linked here, treat it as history until we archive it.

**Start here if you are new:** [root README](../README.md) (what it is, how to run) → this page → the state machine.

---

## 1. Why we build it this way

| Principle | One line | Detail |
|-----------|----------|--------|
| Excellent film, little input | Select a story → strong film. Automate judgment; keep budgets deterministic. | [AGENTS.md · north star](../AGENTS.md#product-north-star-always-decide-with-this-in-mind) |
| **Max master, cut later** | Write the whole book once. 120 minutes or miniseries is a **view**, not a second adapt. | [Max-master plan](max-master-adaptation-plan.md) |
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

How voice lands on clips: [Voice](voice-substitution-design.md).

How a title gets on the shelf: mark **Public (Forkable)** — [Public library](public-community-plan.md).

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
| Ingest (txt / PDF / Fountain, OCR) | [Max master](max-master-adaptation-plan.md) (session notes: [archive](archive/adaptation-session-pipeline.md)) |
| Index + write + enrich + trim | [Max master](max-master-adaptation-plan.md) · [Adaptation module](../host/PageToMovie.Adaptation/README.md) |
| Prompts | [prompts/README](../prompts/README.md) |
| Every model call | [MODEL_CALL_INVENTORY](architecture/MODEL_CALL_INVENTORY.md) |
| Shot plan / action timing | [Action timing](action-timing-plan.md) |
| Estimate / $ / minutes | [Decision flow §2](studio-decision-flow.md#2-progressive-costing-model) |
| Cast / plates / voice | Decision flow + [voice substitution](voice-substitution-design.md) |
| Review / stitch / ffmpeg.wasm | [host README](../host/README.md) (browser media) |
| What is on disk | [Project artifacts](project_artifacts.md) |
| Models / providers / keys | [Supported models](supported-models.md) · catalog JSON is SSoT |

Default planning chat model: **Grok 4.6** (`models_catalog.json`). Do not list models in prose.

---

## 5. Share, fork, many people

| Topic | Detail |
|-------|--------|
| Visibility + fork + invite + contribution | [Public community](public-community-plan.md) |
| Same-project ACL, leases, presence | [Multi-user collaboration](multi-user-collaboration.md) |
| Scale / 100-user soak (plan + LoadSim) | [multi-user-100](multi-user-100-plan.md) · [LoadSim](loadsim-soak.md) |

Easy Start **is** the public-fork path with a voice step. Do not invent a third sharing model.

---

## 6. Learning and quality

| Topic | Detail |
|-------|--------|
| Review notes → prompts (operator loop) | [learning-loop](learning-loop.md) · [film-provenance](film-provenance-critic-learning-architecture.md) |
| Provenance / critic (architecture) | [film-provenance…](film-provenance-critic-learning-architecture.md) (design; not all shipped) |
| Copy / jargon | [AGENTS.md](../AGENTS.md) (archived scan: [USER_COPY_JARGON_AUDIT](archive/USER_COPY_JARGON_AUDIT.md)) |

---

## 7. Run, test, operate

| Topic | Detail |
|-------|--------|
| Run one process | [root README](../README.md#run) |
| API, SignalR, YouTube, LoadSim | [host/README](../host/README.md) |
| Tests | [root README · Tests](../README.md#tests) |
| Playwright | [host/playwright/README](../host/playwright/README.md) |
| Screenplay benchmark | [host/evals/screenplay_benchmark/README](../host/evals/screenplay_benchmark/README.md) |
| Open work | [Backlog](backlog.md) |
| Agent rules | [AGENTS.md](../AGENTS.md) |

`CLAUDE.md` run block matches one-process. Durable rules stay in [AGENTS.md](../AGENTS.md).

---

## 8. What lives where (after 2026-08-13 cleanup)

Living docs are the ones linked in §§1–7. Everything else is under [archive/](archive/README.md).

**Rule:** no new dated working notes. Update a living doc here or add a [backlog](backlog.md) line. Engine issue notes stay in [`host/docs/issues/`](../host/docs/issues/).

Decision flow, max-master, and the model inventory stay separate. No mega-doc.
