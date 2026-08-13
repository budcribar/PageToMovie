# Prompts

Shared operator prompts, schemas, and tiny examples for book → film adaptation.
Paths are relative to the **workspace root** (repo root with `host/`, `projects/`, `prompts/`).

## Naming

`snake_case` + role + optional version:

| File | Role |
|------|------|
| `book_to_fountain.txt` | **Product path:** book → editable Fountain screenplay |
| `fountain_reskin.txt` | **Product path:** re-render an existing Fountain draft's descriptive layer for a different visual medium (Screenplay editor "Look" tool) |
| `embellish_scene.txt` | **Product path:** enrich an existing Fountain draft's descriptive prose, grounded in the book text (Screenplay editor "Enrich" tool) |
| `trim_scene.txt` | **Product path:** condense an existing Fountain draft toward a target runtime (Screenplay editor "Fit length" tool) |
| `fountain_to_cast.txt` | **Product path:** Fountain (+ book) → `source/cast_seeds.json` closed cast |
| `cast_visual_literalize.txt` | Cast post-pass: figurative/idiomatic looks → literal filmable prose |
| `clip_gen_rules.txt` | **Product path:** house rules composed into clip video prompts |
| `clip_auto_review.txt` | **Product path:** QC checklist + JSON schema for clip auto-review |
| `adaptation_v16.txt` | Full-film adaptation rules (optional learning append) |
| `shared_rules.txt` | Rules Stage 2 + verifier must all respect |
| `stage1_scene_bible.schema.json` | Optional schema for internal materialised scene lists (not an operator prompt) |
| `stage2_shot_planner.txt` | Shot plan from approved screenplay build (+ multi-cast tokens, audio_payload) |
| `verifier_clip.txt` | Clip QA verifier (routing hints for learning layers) |
| `compare_json_to_book.txt` | Fidelity audit against book text |
| `examples/scene_bible_minimal.json` | Minimal scene-list sample |
| `examples/clip_plan_minimal.json` | Minimal Stage 2 sample |

Embedded resources at build time via `AdaptationPromptPack` (Stage 1 + screenplay-tool prompts: `book_to_fountain`,
`fountain_reskin`, `embellish_scene`, `trim_scene` — in `PageToMovie.Adaptation`) and equivalent Engine-side
loaders (`fountain_to_cast`, `cast_visual_literalize`, `clip_gen_rules`, `clip_auto_review`). Edit in git →
redeploy (rebuild `PageToMovie.Adaptation` for the Stage 1 group). Optional local override:
`PAGETOMOVIE_PROMPTS_DIR`.

**Operator flow:** book PDF → prepare text → **Fountain draft** (`book_to_fountain.txt`) → edit → approve → **shot plan (reads Fountain)** → clips.
Long novels use **multi-chunk adapt → stitch → merge** in `BookToFountainConverter` (ordered book chunks, continuity brief, final merge pass).
There is no `stage1_scene_bible.txt` prompt and no intermediate scenes.json for planning.
Optional `source/cast_seeds.json` holds plate/voice overlays only.


## Learning loop (Phase A)

Feedback is **routed** by layer — not sprayed into every prompt:

| Layer | Effect |
|-------|--------|
| `clip` | This take / visual_prompt |
| `stage2` | Stage 2 prompt + scene **dirty** for replan |
| `stage1` | Stage 1 prompt + dirty **stage1→stage2** |
| `verifier` | `verifier_clip.txt` (+ optional shared rules) |
| `engine` | `review_feedback/SCRIPT_NOTES.md` only |

Dirty flags live in project `pipeline_state.json` → `scene_dirty`.  
Phase A does **not** auto-run Stage 1/2 LLMs; UI shows a cascade checklist.

## Usage

- **Scenes** → choose feedback layer on Fail / Regen / Log.
- **Edit Log** → apply to layer prompts, shared rules, LEARNINGS, or script notes.
- **Scripts:** `scripts/two_stage_adaptation/` — historical/ad-hoc Python helpers, not required to run Film Studio (see `scripts/README.md`). The product path is native: Stage 1 in `PageToMovie.Adaptation`, Stage 2 in `PageToMovie.Engine`'s `Stage2PlannerService`.

