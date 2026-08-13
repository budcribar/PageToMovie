# Max master — full screenplay, cut later

**Status:** planned (north-star law; not built as the default path yet)  
**Date:** 2026-08-13  
**North star:** [`AGENTS.md`](../../AGENTS.md) § *Build the full screenplay — cut later*  
**Related:** Mary4 D0 (`screenplay.max` as shareable artifact), `docs/studio-decision-flow.md` principle 11

## Law

We do not know the cut. A user may want the entire Odyssey as a miniseries, or a 120‑minute feature, or one voyage. **Adding** a missing Nekyia later means re-adapting. **Dropping** Sparta is a checkbox.

So:

1. Adapt the **whole book once**.
2. Persist a **max master** (index + Fountain).
3. Every shorter film is a **trim / view**, not a new Stage‑1.
4. **Err long.** Extra cards are expected. Missing beats are a defect.
5. Cuts are **logical** (act → sequence → scene), not “delete 40k of characters from the middle of a chapter.”

`TOTAL_RUNTIME_MINUTES` on first write is a **hint for pacing language**, not a scene cap. Soft “≤45 scenes for Novel” is a **warning** after the fact, never a generation stop.

## Why not today’s chunker

| Today | Max master |
|---|---|
| Split on ~40k chars / chapter regex | Split on **story cards** |
| 8 chunks used to dump half the poem in the last call (fast, thin) | Complete coverage, even if 80–175 cards |
| 20 equal slices + failed merge (slow, seamy) | Index once, write **sequences** in batches |
| Scene count is an accident | Scene count is the index |
| 17‑min sidecar after stitch | Runtime from **body words** or index sums — never last-chunk `est_runtime_min` |
| Re-adapt to try 120 vs miniseries | One master; many trims |

File_id one-shot stays the **happy path for books that finish in one pass**. Novels that time out or exceed the single-pass ceiling use **index + batched writes**, not blind text slices.

## Artifacts (SSoT)

| File | Role | `file_id` |
|---|---|---|
| Book text | Source of truth | Existing `BookTextRegistryService` / `IBookFileSession` |
| `screenplay.index.json` | Hierarchical beat sheet (this plan) | `ProjectXaiArtifactFiles` kind `screenplay.index` |
| `screenplay.max.fountain` | Full Fountain from the index | Existing kind `screenplay.max` |
| `screenplay.fountain` | Current working cut (trim / user edit) | Existing |
| Snapshots | Revert Fit Length | Existing package history / checkpoints |

Do **not** invent a second file-handle store. Extend `ProjectXaiArtifactFiles`.

### Index schema (v1, draft)

Hierarchical so trim is cheap:

```text
acts[]
  sequences[]          // e.g. "Telemachus in Sparta", "Cyclops", "Return"
    id, title
    scenes[]
      id, order
      heading            // INT./EXT. + place (no DAY/NIGHT yet if unknown)
      location_key
      speaking_cast[]
      beat               // one or two sentences
      book_anchor_start  // first ~360 chars of the source span
      book_anchor_end
      optional: approx_minutes
```

Plus rollups the Estimate page already wants: unique `location_key`s, speaking-cast union, card count. **Used vs unused** later = “appears on a card we kept in the current cut.”

No video clip grid. No shot plan. This is Stage‑1 only.

Reuse ideas from `prompts/stage1_scene_bible.schema.json` (scenes, seeds) — **do not** fork a second bible. Either extend that schema with `acts/sequences` or map 1:1 so we do not grow two outlines.

## Pipeline

```text
Book (file_id)
    │
    ├─ fits + finishes in one pass ──► screenplay.max
    │
    └─ novel / timeout / output ceiling
            │
            ▼
     1. INDEX          one Responses call, book file_id
                       small JSON out (minutes, not 25)
            │
            ▼  (optional checkpoint: show card count / locs / cast)
     2. WRITE          N sequence batches (not N scenes)
                       book file_id + index file_id
                       “Write sequences 3–4 (cards 18–31)…”
            │
            ▼
     3. STITCH         deterministic concat (title page once)
            │
            ▼
     4. REPAIRS        loc / name / narration — Fountain file_id
                       (do not paste 60k words)
            │
            ▼
     screenplay.max + index   ← share / fork / download
            │
            ▼
     TRIM / Fit Length / user edit   ← 120 min, miniseries, custom
            │
            ▼
     screenplay.fountain (working cut)
```

### Call budget (Odyssey-scale, target)

| Step | Calls | Output size |
|---|---|---|
| Index | 1 | Small (cards, not pages) |
| Write | **8–12 sequence batches**, not 175 | Fountain for that sequence only |
| Stitch | 0 (code) | — |
| Repairs | 0–3, only if candidates exist | Full max, via `file_id` |
| Trim | 1 per user cut, later | Shorter Fountain |

Continuity on a write batch: last ~20 lines of prior sequence + next sequence title. Not a 1200‑char sticky note from a 40k text chunk.

### Progress (operator)

Every step has a **label + heartbeat** (same 20s clock as single-pass). Extra passes must not sit on “Checking 58 names…” with no clock.

Suggested lines:

- `Indexing the book… (2m 10s)`
- `Writing sequence 4/9 — Cyclops (1m 05s)`
- `Stitching master…`
- `Unifying names… (0m 40s)`

Non-admin: short outcome only (existing UI copy rules). Admin: the lines above.

## What we will not do

- Cap the index at 45 because a novel “should” be 45 boards.
- One API call per scene card on a 175-card Odyssey.
- Re-introduce 8-chunk “last slice is half the book.”
- Trust `adaptation_report.est_runtime_min` from a chunk/merge trailer over counting body words (155 wpm) or summing index `approx_minutes`.
- Re-run Stage‑1 because the user changed 180 → 120.
- Put merge/repair Fountain bodies back in `chat/completions` when a `file_id` exists.

## Implementation phases

### P0 — Law + estimate honesty (docs + small code)

- [x] North star in `AGENTS.md` + this plan.
- [ ] `EstimateDraftRuntimeMinutes`: if trailer `est_runtime_min` disagrees with body-word estimate by >2×, **use the word count** and log the sidecar as suspect.
- [x] Heartbeat on merge + loc/name/narration (15s ticks via `Stage1ProgressHeartbeat`); live elapsed on the job card; operator phase lines.

### P1 — Fountain `file_id` on extra passes

After stitch (and after each repair that changes SHA):

- Upload / reuse via `ProjectXaiArtifactFiles` (`screenplay.max` or a `screenplay.stitch` kind).
- Merge + repairs: instruction + `input_file`, same shape as enrich (`ScreenplayEnrichFiles`).
- Fallback inline + log if Files is down.

This is the cheapest win on today’s 20-chunk path.

### P2 — Index artifact

- New prompt `prompts/book_to_index.txt` (Adaptation-owned). Book `file_id` only. JSON out, schema-validated (`ValidatedModelOperation`).
- Persist `screenplay.index.json` next to the book/fountain; SHA + `file_id`.
- Gate: every card has heading + beat + anchors; unique `id`s; order covers first and last book anchors (source-complete check).
- **No scene-count max.** Optional warning if card count ≪ chapters (possible collapse) or == 1.

Show the index on Estimate (counts only for non-admin): N scenes, N locations, N speaking cast. Not the 17‑minute lie.

### P3 — Write from index

- `BookToFountainConverter` path `AdaptPath.Indexed` (or sibling in Adaptation — **no** second converter in Engine).
- Batch planner: pack cards into sequences already in the index; if a sequence is huge, split by card count (~8–15 cards/call), never by raw book chars.
- Each write: book `file_id` + index `file_id` + “cards 18–31 only.”
- Deterministic stitch. Title page from batch 1.
- Quality: stitched heading count ≈ index card count (soft 80–120%); missing card ids → rewrite that batch only.

Short books: keep `AdaptPath.Single` (file_id). Indexed path when single-pass times out, book > single-shot ceiling, or operator/admin “index first.”

### P4 — Trim is a view

- Fit Length / target minutes / “miniseries keep all sequences” read the **index**, snapshot max, write `screenplay.fountain`.
- Do not destroy `screenplay.max` or the index.
- Estimate: one **total** (max or current cut) + **spent**. No second “finish for $X” number that confuses sidecar runtime.

### P5 — Share the master

- Export / backup already includes fountain + assets. Add `screenplay.index.json`.
- Forkable projects: new user inherits max + index; their first action is trim or film, not adapt.

## Files to touch (when building)

| Area | Files |
|---|---|
| Law | `AGENTS.md`, this doc |
| Runtime honesty | `BookToFountainConverter.EstimateDraftRuntimeMinutes` |
| Fountain file_id | `ScreenplayEnrichFiles`, `ProjectXaiArtifactFiles`, merge/repair in `BookToFountainConverter` / `Stage1ChatExecutor` |
| Index | new prompt + Adaptation types; `AdaptationService` façade |
| Orchestration | `ScreenplayService` (save index, file_id, jobs only) |
| UI | Estimate card counts; job heartbeats (admin detail) |
| Tests | schema validate; stitch heading count; trim does not delete max; file_id reuse on repair |

Architecture tests already forbid Stage‑1 logic in Engine. Keep it that way.

## Odyssey acceptance (when we build this)

Using the same `books/TheOdyssey.txt`:

- Index includes Cyclops, Circe, underworld/Tiresias, Sirens, Scylla, Thrinacia cattle, bow, marriage bed, Laertes — as **cards**, not V.O. mentions.
- Write+stitch heading count within ~20% of card count.
- Max Fountain is downloadable and forkable without a second adapt.
- A 120‑minute trim does **not** call book→index again.
- Job UI never sits >30s without a new elapsed tick.

Compare to handoff drafts in `evals/hand_off/odyssey_grok-4.5-vs-4.6/` (chunked 4.5 = 91 scenes / thin Nekyia; chunked 4.6 = 175 scenes / full beats). Indexed max should look like **4.6 coverage** with **logical seams**, not 4.5 omissions.

## Open choices (decide at P2, not now)

- Extend `stage1_scene_bible.schema.json` vs a slimmer `screenplay.index.json`.
- Whether the user can edit the index before write (checkpoint). Default: auto-write after a valid index; admin can stop.
- Sequence batch size (cards vs estimated minutes).
- Whether Grok always uses book `file_id` on the index call (yes) even for short books (optional; shorts can skip the index).
