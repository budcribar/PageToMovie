# Project artifacts (manual final review)

Zip export is deferred. All reviewable run data lives under:

```text
projects/{projectId}/
```

## Refresh the map

After gen / clip review (or anytime):

```http
POST /api/projects/{projectId}/artifacts/index
```

Or **Review** page → **Refresh artifact map**.

Writes:

| Path | Purpose |
|------|---------|
| `ARTIFACTS.md` | Human map + how to review with Claude |
| `artifact_index.json` | Presence + stats |
| `telemetry/cost_ledger.json` | Cost snapshot from pipeline_state |
| `telemetry/models.json` | Models / options snapshot |
| `assets/review/FINAL_REVIEW_TEMPLATE.json` | Rubric template (created if missing) |

Live streams (appended during jobs):

| Path | Purpose |
|------|---------|
| `telemetry/api_calls.jsonl` | Full prompts per live API call |
| `telemetry/ffmpeg.jsonl` | Browser ffmpeg.wasm frame-sample / stitch notes |

## Manual Claude workflow

1. Finish pipeline so WIP + prompts exist.
2. Refresh artifact map.
3. Open `projects/{id}` in Claude Code (or similar).
4. Start from `ARTIFACTS.md`.
5. Compare book + fountain + WIP; use prompts, review, sources, telemetry.
6. Copy `FINAL_REVIEW_TEMPLATE.json` → `final_review.json` and fill scores.

## Core paths (also listed in ARTIFACTS.md)

- `source/book_full.txt`, `source/screenplay.max.fountain`, `source/screenplay.index.json`, `source/screenplay.fountain`
- `source/cast_seeds.json`
- `assets/movie_wip.mp4`
- `assets/characters/`, `assets/locations/`
- `assets/video/` + `assets/video/prompts/*.meta.json`
- `assets/review/`
- `project_rules.json`, `pipeline_state.json`, `blueprint.clips.grok.json`

## Auto-refresh

`FilmJobService` rebuilds the artifact map when these jobs finish successfully:

- `gen-scene`, `gen-batch`
- `clip-auto-review`, `clip-auto-review-batch`
- `stage1`, `stage2`, `character-variants`
