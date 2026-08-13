# Implementation plan: natural runtime + Mary prompt + tracking-before-prompt-change

**Status:** plan (2026-08-03)  
**Depends on:** [film-provenance-critic-learning-architecture.md](./film-provenance-critic-learning-architecture.md), `AdaptationDensity.cs`, `BookTextAnalyzer.cs`, `prompts/book_to_fountain.txt`  
**North Star:** honest natural length first; optional reduce later; measure every prompt change with artifact tracking.

This plan covers three tracks that must stay ordered:

1. **Runtime / movie length** (product + benchmark parity)  
2. **What must be tracked *before* we change the adaptation prompt** (so Mary deltas are attributable)  
3. **Prompt modifications for Mary** (and short picture books generally)

**Do not ship a Mary prompt edit until Track B minimum is green.** Otherwise we cannot prove whether length, cast rules, or model noise moved the score.

---

## Track A — Movie length (natural-first runtime)

### A.0 Goals

| Goal | Detail |
|------|--------|
| Natural first | Estimate “how long would this be if we filmed the source honestly?” |
| No fake floors | Stop forcing ~8–10 min on 2-minute nursery rhymes |
| Production = benchmark | Same `ResolveStage1RuntimeMinutes` / `AdaptationDensity` everywhere |
| Optional reduce later | Long books only: user (or dual bench) picks a shorter budget and we cut spine-first |
| Visible to user | After import, show natural minutes (± band), not a mystery target |

### A.1 Current state (code)

| Piece | Status |
|-------|--------|
| `AdaptationDensity.EstimateNatural` / `EstimateFromStats` | Present; short literary path speech×staging; novels δ≈2 |
| `BookTextAnalyzer` → `SuggestedTotalMinutes` | Wired to density natural minutes |
| `BookTextAnalyzer.ResolveStage1RuntimeMinutes` | Shared helper, clamp 2–180 |
| `ScreenplayService` injects `{{TOTAL_RUNTIME_MINUTES}}` | Uses `ResolveStage1RuntimeMinutes` |
| Benchmark `ResolveTargetRuntimeMinutes` | Same production helper; `--target-runtime-minutes` override |
| User “reduce from natural” UX | **Not built** |
| Dual bench natural + reduced suite automation | Partial (density has `SuggestReducedBenchmarkMinutes`) |
| Persist natural vs target on project | **Likely incomplete** — confirm `project` / screenplay meta fields |

### A.2 Work items (priority order)

#### A.P0 — Make length decision explicit and stored (1–2 days)

| # | Task | Done when |
|---|------|-----------|
| A.P0.1 | On book prepare / import: compute `AdaptationDensity.EstimateNatural`, store on project meta: `natural_runtime_minutes`, `book_kind`, `word_count`, `syllable_count`, `delta`, `runtime_mode` (`natural` \| `reduced` \| `override`) | Sidecar / project JSON survives reload |
| A.P0.2 | Stage1 always reads **stored** target if set; else natural | No silent recompute drift mid-project |
| A.P0.3 | Inject both into prompt context if useful: `{{TOTAL_RUNTIME_MINUTES}}` = target, optional `{{NATURAL_RUNTIME_MINUTES}}` for model awareness | Prompt template updated only after Track B |
| A.P0.4 | Benchmark run_manifest already has target minutes — add `natural_runtime_minutes`, `runtime_mode`, density fields | History comparable across runs |
| A.P0.5 | Unit tests: Mary (~2–4 min natural), Buster (~3–5), TTH (~15–18), Nick natural large | Fixtures with fixed word/syllable mocks or golden books |

**Mary expectation:** natural ≈ **2–4 minutes**, not 10.  
**TTH expectation:** natural ≈ **15–17 minutes** (matches published cut).  
**Buster expectation:** natural ≈ **3–4 minutes** (matches ~3:26 first cut).

#### A.P1 — Product UX: show natural, optional reduce (2–3 days)

| # | Task | Done when |
|---|------|-----------|
| A.P1.1 | After import: “Natural film length ~N min” on estimate / home project strip | User sees N before gen |
| A.P1.2 | For short sources (natural < ~12–15 min): **no reduce slider by default** — ship natural | Mary/Buster path simple |
| A.P1.3 | For long sources: optional “Shorter cut” → set `runtime_mode=reduced`, target = half natural (or user pick), clamp | Stored on project |
| A.P1.4 | Estimate cost uses **target** clip count / minutes, not a hidden 10 | Cost matches gen |
| A.P1.5 | Re-import / new book recalculates natural; changing mode does not re-run Stage1 until user confirms | Clear dirty flag if target changes after fountain exists |

#### A.P2 — Benchmark suite modes (1–2 days)

| # | Task | Done when |
|---|------|-----------|
| A.P2.1 | Default suite: each short book **natural only** | CLI docs match |
| A.P2.2 | Long books (Nick, Carol, …): run **natural + reduced** (use `SuggestReducedBenchmarkMinutes`) | Two history rows / package kinds |
| A.P2.3 | Report header: natural, target, mode, δ, τ | Human-readable |
| A.P2.4 | Leaderboards do not mix natural vs reduced without filter | Dashboard filter or separate columns |

#### A.P3 — Prompt contract for runtime (after Track B + with Track C)

| # | Task | Done when |
|---|------|-----------|
| A.P3.1 | Goals section: if natural short, “do not pad to a longer runtime; prefer complete book at natural length” | See Track C |
| A.P3.2 | When reduced: “spine-first cuts; preserve iconic lines; no invented padding” | Already partially present; tighten |
| A.P3.3 | Judge/benchmark: score padding / invented beats when under natural short books | Deterministic or judge rubric flag |

#### A.P4 — Later

- Syllable + action/camera overhead refinement from real stitch durations (`film_build` total vs estimate)  
- Calibrate δ from published PageToMovie films once film_build exists  
- User-facing “why N minutes?” (words, speech estimate, staging factor)

### A.3 Out of scope for this phase

- Changing video model clip length caps (catalog)  
- Auto Clipchamp  
- Post-YouTube length analysis (lab only; see film-provenance doc)

---

## Track B — Tracking before any prompt change (blocker)

**Purpose:** When we change `book_to_fountain.txt` for Mary, we must answer:

- Which prompt sha?  
- Which app sha / catalog?  
- Which model + temp + runtime target?  
- Which artifacts (fountain, vision_meta, cast)?  
- What score / failure tags before vs after?

Without this, Mary “gets better” is anecdote.

### B.0 Minimum viable tracking (must ship first)

| # | Task | Done when |
|---|------|-----------|
| **B.P0.1** | **Prompt dirty gate stays on** for benchmark (already). Before production Mary experiment, commit prompt only via intentional `prompt:` commits | No dirty-prompt runs |
| **B.P0.2** | **Run manifest completeness** on every live ScreenplayBenchmark / adaptation pilot run: `run_id`, book slug, word count, natural + target minutes, runtime_mode, generator model ids, judge model ids, temp, judge temp, reasoning effort, **prompt file path + git sha**, **app HEAD sha**, models_catalog sha or mtime hash, composite + dimension scores, paths to fountain/vision_meta/cast | One JSON per run under `evals/runs/{run_id}/run_manifest.json` |
| **B.P0.3** | **Artifact digests** written next to outputs: sha256 of fountain, vision_meta, cast_seeds (if present) recorded in manifest | Diff-able |
| **B.P0.4** | **Append `evals/benchmark_history.json`** with same knobs (extend `HistoricalBenchmarkRun` if fields missing: natural minutes, runtime_mode, app_sha) | Dashboard can filter |
| **B.P0.5** | **Baseline Mary package (before prompt edit)** — run live Mary natural once on current prompt; save learning-style folder: `evals/learning_packages/lp_mary_baseline_*/` with manifest + fountain copy or hash + judge notes (ELI/CLARA etc.) | Immutable baseline for comparison |
| **B.P0.6** | **Failure tags on baseline** — even manual: `invented_named_extras`, `invented_dialogue`, `padding`, `runtime_over` | Tags exist for analyzer later |
| **B.P0.7** | **Operator checklist doc** (section below) committed | Anyone can reproduce baseline |

### B.1 Nice-before-prompt (strongly recommended, same week)

| # | Task | Done when |
|---|------|-----------|
| B.P1.1 | `learn: package=` writer from benchmark (auto folder under `evals/learning_packages/`) | No manual copy |
| B.P1.2 | Git commit on app repo after live suite: `bench: run=… book=mary …` including history + package (no secrets) | Pullable history |
| B.P1.3 | Production Stage1 write `source/adaptation_run.json` on project: models, prompt sha, app sha, natural/target minutes | Studio projects attributable too |
| B.P1.4 | Vision_meta + cast_seeds generation recorded in same run id when package validation runs | Full package tracking |

### B.2 Not required before first Mary prompt A/B

- Full `film_build` / stitch timeline (needed for film critic, not Stage1 Mary text)  
- YouTube hash gate  
- Auto analyzer proposals  
- Whole-film auto-regen  

Those are parallel (film-provenance P0+) and do **not** block a **screenplay-level** Mary experiment—but **B.P0.*** does.

### B.3 Baseline → treatment protocol

```text
1. git status clean on prompts/book_to_fountain.txt
2. Note app HEAD, catalog hash
3. Run: Mary natural, fixed model+judges+temps (record all)
4. Save baseline package (B.P0.5)
5. Only then: prompt PR (Track C) → commit prompt:
6. Re-run Mary identical knobs (only PromptVersion changes)
7. Save treatment package; diff failure_tags + composite + cast inventions
8. Decide keep/revert from evidence, not single demo feel
```

**Identical knobs checklist:** model, judges, temp, judge temp, reasoning effort, target=natural (no override), same book file bytes.

---

## Track C — Prompt modifications for Mary (and short picture books)

### C.0 Problems Mary exposed (from real adaptation)

| Issue | Example | Desired behavior |
|-------|---------|------------------|
| Invented named extras | ELI, CLARA as speaking pupils | Unnamed groups stay group/action or one collective treatment; **no new proper names** |
| Invented dialogue | Lines not in source | Book wording / zero invented banter default (prompt already partial) |
| Runtime padding | Stretching a 2-min verse toward old 10-min habits | Natural short target + “do not pad” |
| Consecutive duplicate headings | EXT. SCHOOLHOUSE twice | Prefer merge same location+time |
| Cast/judge gap | Score without cast_seeds | Separate issue (package validation); prompt should still not invent speakers |

### C.1 Prompt changes (concrete)

File: `prompts/book_to_fountain.txt` (and any shared include if used).

#### C.1.1 Runtime goals (align with Track A)

**Replace / tighten GOALS item 1** roughly as:

- Cover the book in **about `{{TOTAL_RUNTIME_MINUTES}}` minutes**.  
- If that target is the **natural** length for a short source, **do not pad** with new incidents, reprises, or invented business.  
- Prefer complete fidelity at natural length over artificial runtime.  
- When a **reduced** target is set, cut whole minor beats; keep iconic lines; do not invent filler to “feel cinematic.”

Optional second placeholder: `{{RUNTIME_MODE}}` = `natural` | `reduced` | `override` so the model knows which regime.

#### C.1.2 Unnamed groups / no invented named extras (Mary-critical)

Add a **HARD** block, e.g. **UNNAMED GROUPS & EXTRAS**:

- If the book says “the children,” “the eager children,” “they,” etc. **without proper names**, do **not** invent given names (no ELI, CLARA, …).  
- Crowd/class may appear in **Action** as a group, or as a single collective cue only if the book truly speaks as one voice (rare).  
- **Do not** invent dialogue for unnamed group members. Prefer Action: “The children laugh and point.”  
- Named speaking roles only when the **source names them** or a single functional role is required (TEACHER, NARRATOR) already justified by the book.  
- Any ALL-CAPS speaker must be accountable to source membership (aligns with later cast_seeds validation).

#### C.1.3 Dialogue invention (tighten existing §)

Existing “summarized exchanges → at most one line” is good. Strengthen for verse/nursery:

- Nursery rhyme / short picture book: **prefer near-verbatim book lines** as NARRATOR or character speech; do not expand into multi-speaker banter.  
- Teacher / classmates: only lines clearly implied or quoted; default **silent reaction in Action**.

#### C.1.4 Scene economy for short verse

- Do not add post-resolution reprises that restate the whole rhyme without new story information.  
- Merge consecutive scenes with same location + time of day when purpose is continuous.  
- VISION_META / medium: keep illustrated picture-book lock (already in medium rules).

#### C.1.5 What not to put in the prompt

- Hard-coded “Mary” examples only (generalize to all unnamed groups).  
- Regex-based “photoreal” hacks (medium from content / vision_meta — already product direction).  
- New JSON schemas in this prompt (Fountain remains SOT for this step).

### C.2 Supporting non-prompt code (with or right after prompt)

| # | Task | Why |
|---|------|-----|
| C.2.1 | Deterministic check: speakers in fountain ⊆ cast_seeds / source-derived allowlist | Catch ELI/CLARA without waiting for LLM judge |
| C.2.2 | Benchmark cast package judge (planned earlier) | Full package score, not fountain-only |
| C.2.3 | Syntax/score flag: invented proper names not in book text (heuristic) | Cheap regression alarm |

### C.3 Validation plan for Mary (after Track B baseline)

| Check | Pass criteria |
|-------|----------------|
| Natural runtime injected | 2–4 (or density output), not 10 |
| No invented pupil names | Grep cast / cues vs book |
| Composite / fidelity | ≥ baseline or clear tag reduction |
| No padding scenes after moral/end | Manual or judge |
| Medium | picture-book / illustrated consistent |
| Teacher + Mary + lamb only as named focus | Stable keys |

Run **same model** as baseline first; optional second model later.

### C.4 Rollout

1. Track B.P0 complete + baseline package frozen  
2. PR: prompt only (`prompt: book_to_fountain — unnamed groups + no pad short natural`)  
3. Mary natural re-run → treatment package  
4. If green: leave prompt; run Buster smoke (no regression on rhyme book)  
5. If red: revert prompt commit; keep packages for autopsy  

---

## Combined order of operations (single checklist)

```text
□ A.P0.1–A.P0.5   Store natural/target; tests for Mary/Buster/TTH/Nick bands
□ B.P0.1–B.P0.7   Run manifests, digests, history fields, Mary BASELINE package
□ A.P1 (optional same sprint)  UX “~N min natural” after import
□ C.1 prompt PR   Only after baseline exists
□ B protocol steps 5–8  Treatment package + compare
□ C.2.1 deterministic speaker check  ASAP after prompt
□ A.P2 dual long-book bench
□ film_build P0 (other doc)  Parallel; not required for Mary text A/B
```

### Suggested sprint slice (highest value first)

| Day | Focus |
|-----|--------|
| 1 | A.P0 store natural/target + unit tests; extend benchmark manifest fields |
| 1–2 | B.P0 run_manifest digests + Mary baseline live run + freeze package |
| 2 | C.1 prompt edit PR (Mary/unnamed/no pad) |
| 2–3 | Mary treatment run; compare tags; ship or revert |
| 3+ | A.P1 UX; C.2.1 speaker check; A.P2 long dual mode |

---

## Success criteria

| Track | Success |
|-------|---------|
| **A** | Mary natural 2–4 min target; TTH ~16; production and benchmark always agree; project stores mode |
| **B** | Every Mary run has prompt sha + app sha + models + artifact hashes; baseline vs treatment packages exist |
| **C** | Mary treatment has zero invented named pupils; no worse fidelity; Buster smoke clean |

---

## One-sentence summary

**Lock runtime natural-first and full run provenance first; freeze a Mary baseline package; only then change `book_to_fountain` so unnamed groups and short-book non-padding are hard rules—and prove the win with identical knobs and artifact hashes.**
