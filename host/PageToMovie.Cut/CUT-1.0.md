# Cut 1.0 — finish, not assembly

This file is the **scope SSoT**. README is run/how-to.

Film already did: current/favorite takes, scene + clip order, scene/clip deletes. Cut does **not** rebuild reorder, delete, or take-picking as a product.

Cut **reads** the folder Film left: `_take_NN.mp4` + `.current.json` in existing scene/clip order. Ignore bare `scene_SS_clip_CC.mp4` aliases. Never copy, write, or treat an alias as current.

Do not implement reorder / delete / take-picker product work. Do not start the Film mount until Cut finish UI is stable.

## Cut 1.0 does

- **Trim** (in/out) on those current takes
- **Transitions** (scene-change fades first; clip-to-clip optional later in 1.0 if cheap)
- **One background music** track (music-under-movie)
- **Chapter/scene cards** (text like “Chapter 1” / “Scene 1” at boundaries — not a full title designer)
- **Stitched preview + export** `movie.mp4` (browser ffmpeg.wasm only; no server ffmpeg; bytes stay client)
- **Save/reload** that finish (trims, transitions, music, cards)
- **Last:** mount on Film Final Edit (RCL or iframe)

## Out of 1.0

Reorder, delete, take-picking product, ripple, split, multi-track, undo, Engine / Railway / catalog.

## Folder SSoT (read-only for Cut)

| Rule | Detail |
|------|--------|
| Take file | `scene_SS_clip_CC_take_NN.mp4` (+ optional `.clip.json`) |
| Take # | `ParseTakeNumber(filename)` |
| Current | `scene_SS_clip_CC.current.json` only |
| Order | Scene then clip number, as Film left the folder |
| Alias MP4 | Legacy. Ignore. Never write. |
| Timestamp leftover | Ignore |
| Missing current | No pointer, or pointer take file gone → Missing. No fallback to another take or the alias. |

## Constraints (every slice)

- Stay under `host/PageToMovie.Cut` (+ `PageToMovie.Cut.Tests`). Own slnx only — **not** `PageToMovie.slnx` / Web slnx.
- No ProjectReference to Engine / Api / Web / Core. No Railway, catalog, auth, LoadSim.
- Compose is browser ffmpeg.wasm only. Loader SSoT is `PageToMovie.Web/wwwroot/js/pagetomovie-ffmpeg.js` (copied into Cut at build). One exclusive queue. Do not invent a second loader or commit a second copy.
- Generalize: no book/cast/page hardcodes in product code.
- Same PR 193 for Cut finish slices. Do not merge until Bud asks.
- **Do not stack two agents on the same slice.**

## Independent slices

| # | Slice | Status | Where |
|---|--------|--------|--------|
| 1 | Folder scan of current takes in Film order | Done | PR 193 |
| 2 | Trim + per-clip play | Done | PR 193 |
| 3 | Preview/export compose | Done | PR 193 (`Play` = stitch, no download; `Make movie` = same path + download) |
| 4 | Background music | Done | PR 193 drop; keep as music-under-movie (8 MiB cap) |
| 5 | Save/reload finish | Open | This app |
| 6 | Transitions | Open | This app |
| 7 | Chapter/scene cards | Open | This app |
| 8 | Film: drop alias MP4, resolve current via `.current.json` | Done | [PR 194](https://github.com/budcribar/PageToMovie/pull/194) merged |
| 9 | Film Final Edit mount | Last | After finish UI is stable |

The take list on this branch is leftover from the earlier assembly sketch. Do **not** expand it into a take-picking product. Film + `.current.json` remain SSoT for which take is current.

### Slice 5 — Save/reload finish

Persist and restore **trims, transitions, music, cards** only.

- Suggested file in the picked folder: `cut.project.json`.
- Do not persist a competing clip order or take-picking UI. Order and current take stay Film/folder SSoT (`.current.json` + `_take_NN.mp4`).
- Music: store the file name / relative path if it lives in the folder; do not upload bytes to a server.
- Missing current take after reload → Missing. No alias fallback.

### Slice 6 — Transitions

Scene-change fades first (consecutive strip items whose **scene number differs**). Same scene → hard cut. Clip-to-clip fades only if cheap, still in 1.0. Preview and export must match. Implement in the existing compose path (`cut.js` / `CutComposeService`), not a second ffmpeg stack.

### Slice 7 — Chapter/scene cards

Short text cards at boundaries (e.g. “Chapter 1” / “Scene 1”). Not a title designer: no fonts/themes/motion library. Generate in the compose path; persist card text via slice 5.

### Slice 8 — Film (merged)

[PR 194](https://github.com/budcribar/PageToMovie/pull/194) merged. Film no longer writes a leftover alias MP4; current take is `.current.json` only. Do not reopen that work here.

### Slice 9 — Mount (last)

RCL or iframe on Film Final Edit. Do not start until slices 5–7 are in and finish UI is stable. Protected Film pages stay frozen until that slice is explicitly requested.

## Run / test

```bash
cd host/PageToMovie.Cut
dotnet run          # http://127.0.0.1:5299
dotnet test PageToMovie.Cut.slnx
```
