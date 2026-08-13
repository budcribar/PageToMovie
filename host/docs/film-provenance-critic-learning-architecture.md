# Film provenance, critics, and the learning loop

**Status:** architecture + implementation plan (2026-08-03)  
**North Star:** drop a book → get a movie, with less human post-processing over time.  
**Related:** [learning-loop.md](./learning-loop.md), [project_artifacts.md](./project_artifacts.md), [archive/github-projects-backup-checklist.md](../../docs/archive/github-projects-backup-checklist.md)

This document crystallizes decisions from product discussion: **where truth lives**, **how stitch/YouTube provenance works**, **pre- vs post-publish critics**, and a **prioritized build order**.

---

## 1. North Star

| Principle | Meaning |
|-----------|---------|
| First cut quality | Automation produces a watchable film without Clipchamp |
| Less post-processing | Human edits and external polish decline over time |
| Honest provenance | We never pretend a re-exported file still matches our EDL |
| Learn from finished work | YouTube / published cuts feed **pipeline** improvement, not silent user regen |

---

## 2. Three stores (what lives where)

Do **not** merge these histories.

| Store | Location | Owns | Does not own |
|-------|----------|------|----------------|
| **A. App / adaptation brain** | `github.com/budcribar/PageToMovie` | Prompts, engine, `models_catalog.json`, scorers, benchmark tool, `evals/`, learning packages, analyzer proposals | Per-user project edits as product-of-record |
| **B. Studio packages** | Per-project git → optional `PageToMovie:Git:ProjectsRepoUrl` (e.g. `PageToMovie/Projects`), branch `proj/{user}/{slug}` | Fountain, vision_meta, cast, blueprint, config, **film_build.json**, stage/human commit trajectory (text) | App source, prompts, MP4/MP3, API keys |
| **C. Media** | Client media folder (and optional vault) | MP4, MP3, voice samples | Anything required to *reason* about adaptation quality in git |

**Rules**

- Improves **everyone’s** next adaptation → **A**
- One user’s film recipe → **B**
- Heavy media bytes → **C**
- **Benchmarks always write to A** (`evals/…`)
- **User auto-commit always writes to B** (project `.git`)
- Nested project repos must not sit inside the app repo (existing guard)

---

## 3. Core definitions

| Term | Definition |
|------|------------|
| **Adaptation package** | Book + fountain + vision_meta + cast_seeds (+ stage2/blueprint). *What we meant to film.* |
| **Film build** (`film_build` / `*.film.json`) | Timeline EDL + hashes + model/prompt/code pins for **one stitched cut**. *How we assembled this MP4.* |
| **Studio cut** | Bytes produced by our FFmpeg/client stitch, with `studio.sha256` + duration |
| **Publish cut** | Bytes uploaded (YouTube or export); may equal studio or diverge after Clipchamp |
| **Learning package** | Self-contained unit (knobs + scores + tags + trajectory + artifact refs) for the analyzer |
| **Proposal** | Typed suggestion (prompt patch, scorer rule, pipeline step) with evidence package ids |
| **Precise critic** | Segment-mapped (time → scene/clip); requires trusted timeline |
| **Holistic critic** | Whole-film only; no safe SxCy mapping |

---

## 4. Artifact model

### 4.1 Already in the product

| Artifact | Role today |
|----------|------------|
| `*.clip.json` | Per-clip model, prompt, script text, duration, sha256 |
| `*.mp4.sources.json` / `movie_wip.mp4.sources.json` | Concat include/exclude / assembly note |
| Project git auto-commit | Stage-end text recipe commits |
| Clip auto-review | Model review per clip after generation |
| Benchmark history | `evals/benchmark_history.json` + prompt version gating |

### 4.2 Film build (required addition)

Written **at end of stitch** (client and any server path), next to the movie and committed to **project git** (not the MP4).

```text
assets/movie_wip.mp4                 → media (C), not git
assets/movie_wip.film.json           → project package (B)  ← film build
assets/video/scene_XX_clip_YY.clip.json
```

**Minimum `film_build.v1` fields**

```json
{
  "schema_version": "film_build.v1",
  "film_id": "film_{user}_{slug}_{utc}_{short}",
  "created_at_utc": "…",
  "project_id": "user/slug",
  "studio": {
    "sha256": "…",
    "duration_seconds": 205.59,
    "path": "assets/movie_wip.mp4"
  },
  "timeline": {
    "total_seconds": 205.59,
    "segments": [
      {
        "i": 0,
        "scene": 1,
        "clip": 1,
        "take": 1,
        "t_start": 0.0,
        "t_end": 4.2,
        "src": "assets/video/…mp4",
        "src_sha256": "…",
        "sidecar": "…clip.json"
      }
    ]
  },
  "assembly": {
    "tool": "ffmpeg",
    "where": "client"
  },
  "provenance": {
    "app_repo": "budcribar/PageToMovie",
    "app_sha": "…",
    "prompt_files": { "book_to_fountain": { "path": "…", "sha": "…" } },
    "models_catalog_sha": "…",
    "project_git_sha": "…",
    "models_used": {
      "script_planning": "…",
      "video": "…",
      "tts": "…"
    },
    "knobs": { "resolution": "…", "runtime_mode": "natural" }
  },
  "artifacts": {
    "fountain_sha256": "…",
    "vision_meta_sha256": "…",
    "cast_seeds_sha256": "…",
    "blueprint_sha256": "…"
  },
  "publish": {
    "sha256": null,
    "duration_seconds": null,
    "path": null,
    "youtube_video_id": null
  }
}
```

**Reproduce** means: same app_sha + prompt shas + project_git_sha + models + film_build — not eternal bit-identical vendor video.

### 4.3 Review bundle (what a film critic receives)

```text
review_bundle/
  adaptation/     ← fountain, vision_meta, cast, blueprint, book
  film_build.json ← stitch timeline + studio hash (sibling, not stuffed into fountain)
  movie            ← path or URL (studio cut only for precise mode)
```

Screenplay/cast judges need **adaptation only**.  
Post-stitch film critic needs **adaptation + film_build + movie**.

---

## 5. Hash gate (Clipchamp and other external edits)

Record hash **after FFmpeg**. Recheck **at upload**.

```text
studio_sha256, studio_duration   ← stitch time → film_build.studio
upload_sha256, upload_duration   ← upload time → film_build.publish
```

| Condition | `publish.path` | Timeline trust | Critic mode |
|-----------|----------------|----------------|-------------|
| `upload_sha256 == studio_sha256` | `studio_intact` | **Full** | Precise (segment-mapped) + optional auto-regen |
| Hash differs, \|Δduration\| ≤ ε (e.g. 0.5s) | `external_same_length` | **Weak** | Holistic preferred; **no** auto-regen by SxCy |
| Hash differs, duration changes | `external_restructured` | **None** | Holistic only / skip mapped analysis |

**Notes**

- Hash the **exact file bytes** we produced and the exact file we upload (not YouTube’s re-encode).
- Same length after Clipchamp can still mean replaced middle shot or new audio — never treat as intact.
- ε for duration: ~0.25–0.5s, not float equality.

---

## 6. Critics: pre-YouTube vs post-YouTube

Same family of checks; **different job and audience**.

### 6.1 Per-clip review (existing)

After each clip (or batch): dialogue match, quality, style lock. Fast gate before stitch.

### 6.2 Whole-film critic — **pre-publish (product)**

**When:** after stitch; **before** YouTube. Requires `studio_intact` for precise + auto paths.

```text
clips → per-clip review → stitch → film_build + studio hash
     → WHOLE-FILM CRITIC
     → suggest regen SxCy / optional bounded auto-regen
     → re-stitch → user accepts → upload
```

**Output (actionable)**

```json
{
  "verdict": "pass | pass_with_notes | needs_work",
  "blocking": [
    {
      "type": "dialogue_mismatch | style_break | continuity | pacing | black_tail | …",
      "t_start": 124.0,
      "t_end": 128.5,
      "scene": 3,
      "clip": 2,
      "action": "regen_clip | regen_scene | re_tts | none",
      "reason": "…"
    }
  ],
  "nits": []
}
```

**Modes**

| Mode | Behavior |
|------|----------|
| Suggest only (default) | List regens + cost; user confirms |
| Auto (admin / opt-in) | Regen up to K blocking items, re-stitch, re-critic; cost multiplier (e.g. start 1.3×); hard caps |

**Guardrails**

- Auto-regen only if hash still matches studio cut after last stitch.
- Cap clips, dollars, and rounds.
- Whole-film catches **join** failures per-clip review misses (style pop, ending black, pacing).

### 6.3 Pipeline learning — **post-YouTube (lab)**

**When:** after publish (or explicit “contribute”).  
**Goal:** improve **adaptation pipeline** for future users — **not** open regen UI for the published film.

```text
upload + hash gate → youtube_id + publish.path
  → deeper/slower analysis (ASR, narrative, multi-book compare)
  → learning package + failure_tags
  → analyzer → proposals (prompt / scorer / pipeline)
  → app repo only
```

| Publish path | Lab value |
|--------------|-----------|
| `studio_intact` | Best (timeline + film) |
| `external_same_length` | Holistic + weak segment claims |
| `external_restructured` | Holistic; signal “needed human polish” |

Cost category: **automated_review** (pre-publish user-visible; post-publish admin/batch).

---

## 7. Repositories, commits, packages

### 7.1 App repo commit grammar

```text
prompt: <area> — <intent>
bench: run=<id> book=<slug> mode=<natural|reduced> top=<model> score=<n>
learn: package=<package_id> kind=<benchmark|studio|mixed>
analyzer: proposal=<id> status=<open|applied|rejected>
```

- Dirty prompt → refuse benchmark (existing).
- After live bench → commit `evals/` (`bench:` / `learn:`).
- Never mix `prompt:` and `bench:` in one commit.

### 7.2 Project repo commit grammar

```text
ptm:stage=<name> project=<user/slug> [app_sha=…] [prompt=…] [film_id=…]
  name ∈ book_prepared | screenplay_created | cast_built | stage2_written
         | film_job | music_job | film_stitched | critic_pass

ptm:human=<action> project=<user/slug>
  action ∈ edit_fountain | edit_cast | lock_character | voice_clone
         | external_edit | trim_runtime | other
```

### 7.3 Learning package (analyzer input)

Location: **A** — `evals/learning_packages/{package_id}/`

| File | Content |
|------|---------|
| `package.json` | schema, knobs, scores, failure_tags, outcome, film_id, app/project shas |
| `trajectory.jsonl` | ordered ops (bench / stage / human / critic) |
| `diffs/` | optional short text patches only |

Analyzer **eats packages**, not raw multi-repo git merges.

### 7.4 Analyzer → proposals

Cluster failure_tags + human diffs → typed proposals:

| Type | Example | Auto-apply |
|------|---------|------------|
| `prompt_patch` | No invented named extras | No (review) |
| `scorer_rule` | Speakers must exist in cast_seeds | Yes if tests pass |
| `pipeline_step` | Cast judge before stage2 | No |
| `knob_default` | Natural runtime for short verse | Gated |

Loop: packages → proposals → gate → `prompt:` commit → re-bench → new packages.

---

## 8. End-to-end flow (one picture)

```text
Book drop
  → Stage1 (screenplay + vision_meta)     [adaptation package grows]
  → Cast / voice
  → Stage2 / clips + per-clip review
  → Stitch FFmpeg
  → film_build.json + studio.sha256       [B + C]
  → WHOLE-FILM CRITIC (precise if intact)
  → optional bounded auto-regen
  → Upload
       ├─ hash match     → studio_intact  → YouTube + full provenance
       ├─ same length    → external_same_length → holistic
       └─ restructured   → external_restructured → holistic
  → (lab) learning package → analyzer → better prompts/pipeline
```

**YouTube** = playback / demo truth for gallery.  
**film_id + package + film_build** = reproducibility and learning.  
Description or DB: `youtube_id ↔ film_id ↔ project_id` (full JSON not stored only on YouTube).

---

## 9. Implementation plan (highest priority first)

Priorities optimize for: (1) honest clip mapping, (2) user-facing whole-film fix loop, (3) durable learning signal.

### P0 — Film build at stitch + hash at upload  ← **start here**

| # | Work | Done when |
|---|------|-----------|
| P0.1 | Define `film_build.v1` schema (C# model + JSON) | Shared type used by stitch + upload |
| P0.2 | On client (and server if any) stitch complete: write `assets/movie_wip.film.json` with **timeline segments** (t_start/t_end from concat order + durations), **studio.sha256**, duration | File exists next to WIP; segments cover full runtime |
| P0.3 | Fill provenance: project_git_sha, models_used from coverage/catalog ids, artifact content hashes for fountain/cast/blueprint | Manifest not empty placeholders only |
| P0.4 | Auto-commit film_build on project git: `ptm:stage=film_stitched film_id=…` | In project history without MP4 |
| P0.5 | On YouTube (or publish) upload: rehash file, set `publish.*` path enum | Intact / same_length / restructured stored |
| P0.6 | API/DB: optional `youtube_video_id → film_id` for gallery/admin | Lookup works |

**Exit:** Every stitched WIP has a timeline + studio hash; every upload classifies integrity.

### P1 — Whole-film critic (suggest mode, pre-publish)

| # | Work | Done when |
|---|------|-----------|
| P1.1 | Review bundle builder: adaptation + film_build + movie path | One call site for critic |
| P1.2 | Whole-film critic prompt + structured JSON output (blocking with scene/clip via timeline) | Stable parse; types closed-set |
| P1.3 | UI: “Review full cut” → list regen actions + cost estimate | User can act without reading essays |
| P1.4 | Wire “regen these clips” from critic actions (manual confirm) | One-click queue uses existing gen pipeline |
| P1.5 | Cost category `automated_review` on all critic calls | Usage DB attributable |

**Exit:** User can run whole-film review on studio cut and regenerate named clips before upload.

### P2 — Bounded auto-regen mode (admin / opt-in)

| # | Work | Done when |
|---|------|-----------|
| P2.1 | Flag: auto film critic + max clips / max rounds / cost multiplier | Config on admin |
| P2.2 | Loop: critic → regen blocking → re-stitch → rehash → critic | Stops on pass, cap, or budget |
| P2.3 | Estimate includes expected auto-retry (e.g. 1.3×) when flag on | Estimate page reflects mode |

**Exit:** Unattended improve-before-publish for trusted accounts only.

### P3 — Commit grammar + learning package writer

| # | Work | Done when |
|---|------|-----------|
| P3.1 | Normalize stage auto-commit messages to `ptm:stage=…` | Greppable history |
| P3.2 | After live ScreenplayBenchmark run: write `evals/learning_packages/{id}/` + append history | Package on disk |
| P3.3 | Optional git commit on app repo: `bench:` / `learn:` (CI or operator flag) | History not only local |
| P3.4 | Post-publish job: if `studio_intact`, create learning package with film_id + coarse tags | Channel feeds lab |

**Exit:** Packages exist for automated rounds; analyzer has food.

### P4 — Analyzer v0 (proposals, no auto-apply)

| # | Work | Done when |
|---|------|-----------|
| P4.1 | Cluster failure_tags / critic blocking types across packages | Report or admin page |
| P4.2 | Emit `evals/analyzer/proposals/{id}.json` with evidence package ids | Human can PR a prompt |
| P4.3 | Document apply path: edit prompt → `prompt:` commit → re-bench | Operator runbook |

**Exit:** Data-backed prompt changes, not vibes.

### P5 — Gated continual loop

| # | Work | Done when |
|---|------|-----------|
| P5.1 | Anchor suite: Mary (natural), Buster (natural), TTH (natural), one long natural+reduced | Fixed books |
| P5.2 | On prompt change or schedule: run suite, refuse merge if regress beyond ε | CI or script |
| P5.3 | Optional: open PR from analyzer proposals only if suite green | Safe automation |

**Exit:** Loop can improve prompts without silent regressions.

### P6 — Hardening (later)

- ffmetadata chapters in MP4 (human nicety; sidecar remains source of truth)
- Align external same-length cuts (research; not required for P0–P2)
- Third learning-only remote if `evals/` grows large
- Full dual-write migration for legacy flat projects (see github-projects checklist)

---

## 10. Explicit non-goals

- Bit-identical recreation of vendor video forever  
- Storing full FFmpeg logs as the EDL (use structured film_build)  
- Clip-mapped critic or auto-regen on hash-mismatched uploads  
- Auto-editing videos already on YouTube for the user  
- Benchmark history living in the Projects remote  
- Media (MP4/MP3) in git  

---

## 11. Success metrics

| Metric | Direction |
|--------|-----------|
| First-cut whole-film **pass** rate (blocking = 0) on anchor suite | ↑ |
| Mean human / external_edit steps after first stitch | ↓ |
| % uploads `studio_intact` | ↑ over time |
| Repeated failure_tags (invented dialogue, style break, black tail) | ↓ |
| Applied proposals with evidence packages | 100% of prompt changes from analyzer path |

---

## 12. One-sentence model

**Adaptation package = intent; film_build = studio EDL + hash; hash gate = trust; pre-YouTube critic = fix this film; post-YouTube analysis = fix the pipeline; learning packages bridge both into prompt and code improvement—without lying about Clipchamp.**

---

## 13. Doc maintenance

When implementing a phase, check boxes in §9 in the PR that lands it, and link the PR under a short “Changelog” subsection at the bottom of this file.
