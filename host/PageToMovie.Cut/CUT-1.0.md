# Cut 1.0 (locked goal)

Usable editor: finish a movie without Clipchamp.

This file is the **scope SSoT**. README is run/how-to. Do not expand V1 UI except when implementing a remaining slice below.

## Product

- Open a project media folder (e.g. Mary19 — example only; code stays story-agnostic).
- Takes SSoT: `_take_NN.mp4` + `.current.json` only. Ignore and **never write** `scene_SS_clip_CC.mp4` alias.
- Strip, take chooser (writes `.current.json`), in/out, per-clip play.
- One dropped audio track.
- Scene transitions: fade/dissolve **only when the scene number changes**. Same-scene stays a hard cut.
- Stitched preview + export `movie.mp4` (browser ffmpeg.wasm, no server ffmpeg, bytes stay client).
- Save/reload the cut (order, in/out, audio, current take numbers).
- Reorder clips.
- Later: mount on Film Final Edit (RCL or iframe). **Do not start mount until Cut UI is stable.**

## Take SSoT (do not reopen)

| Rule | Detail |
|------|--------|
| Take file | `scene_SS_clip_CC_take_NN.mp4` (+ optional `.clip.json`) |
| Take # | `ParseTakeNumber(filename)` |
| Current | `scene_SS_clip_CC.current.json` only (`{ "take": N }`) |
| Alias MP4 | Legacy. Ignore. Never copy, write, or treat as current. |
| Timestamp leftover | `_take_NN_yyyyMMdd_HHmmss` — ignore |
| Missing current | No pointer, or pointer take file gone → clip is Missing. Do **not** fall back to another take or the alias. |

Chooser switches current in memory and writes `.current.json`. Never writes an alias MP4.

## Out of 1.0

Ripple delete, split, multi-track mix, titles, undo stacks, Engine / Railway / catalog, credits special-case.

## Constraints (every slice)

- Stay under `host/PageToMovie.Cut` (+ `PageToMovie.Cut.Tests`). Own slnx only — **not** `PageToMovie.slnx` / Web slnx.
- No ProjectReference to Engine / Api / Web / Core. No Railway, catalog, auth, LoadSim.
- Compose is browser ffmpeg.wasm only. Reuse `wwwroot/js/pagetomovie-ffmpeg.js` + `wwwroot/js/ffmpeg/**` (vendor copy; Sonar/CPD excluded). One exclusive queue. Do not invent a second loader.
- MP4 bytes stay on the client. No native server ffmpeg.
- Generalize: no book/cast/page hardcodes in product code.
- Do not merge this PR until Bud asks.

## Independent slices

**Do not stack two agents on the same slice.** One slice per agent/PR increment. Slices 1–4 are done on this branch (PR 193). Slice 8 is a **separate Film PR** — do not implement it here.

| # | Slice | Status | Where |
|---|--------|--------|--------|
| 1 | Folder + take SSoT + strip | Done | PR 193 |
| 2 | In/out + per-clip play | Done | PR 193 |
| 3 | Preview/export compose | Done | PR 193 (`Play` = stitch, no download; `Make movie` = same path + download) |
| 4 | One audio track | Done | PR 193 (optional; duck then replace; 8 MiB cap) |
| 5 | Save/reload cut project | Open | This app |
| 6 | Reorder clips | Open | This app |
| 7 | Scene transitions (scene-boundary fade only) | Open | This app |
| 8 | Film: drop alias MP4, resolve current via `.current.json` | Separate PR | [PR 194](https://github.com/budcribar/PageToMovie/pull/194) |
| 9 | Film Final Edit mount (RCL or iframe) | Last | After Cut UI is stable |

### Slice 5 — Save/reload

Persist and restore: clip **order**, per-clip **in/out**, **audio** handle, **current take numbers**.

- Suggested file in the picked folder: `cut.project.json` (do not invent a second take SSoT).
- Current take on disk remains `.current.json`. Reload may rewrite those pointers to match the saved take numbers; still never write an alias MP4.
- Audio: store the file name / relative path in the folder if present; do not upload bytes to a server.
- If the pointer take is missing after reload → Missing (same as slice 1). No fallback.

### Slice 6 — Reorder

Operator can change strip order. Same-scene vs cross-scene order both allowed. Persist via the slice 5 file when that file exists. Do not change take identity or write alias MP4s.

### Slice 7 — Scene-boundary fade

Fade/dissolve **only** between consecutive strip items whose **scene number differs**. Same scene → hard cut (concat as today). Preview and export must match. Implement in the compose path (`cut.js` / `CutComposeService`), not as a second ffmpeg stack.

### Slice 8 — Film (not this PR)

Already launched: drop leftover current-clip alias; current take is `.current.json` only.

### Slice 9 — Mount (last)

RCL or iframe on Film Final Edit. Do not start until slices 5–7 are in and Cut UI is stable. Protected Film pages stay frozen until that slice is explicitly requested.

## Run / test

```bash
cd host/PageToMovie.Cut
dotnet run          # http://127.0.0.1:5299
dotnet test PageToMovie.Cut.slnx
```
