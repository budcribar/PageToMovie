# PageToMovie.Cut

Standalone finish editor. Film already owns order, deletes, and current takes. Cut reads that folder and finishes the movie (trim, transitions, music, cards, export). It is **not** wired into Film Studio yet.

**1.0 (finish, not assembly):** [CUT-1.0.md](CUT-1.0.md) — Clipchamp-style timeline, hop-aware in/out, range-delete, Fountain join ticks, text row (scene cards + titles), music, save/reload. Do not stack two agents on the same slice. Do not add reorder / whole-clip delete / take-picking product work.

Open this folder’s solution — do **not** add it to `host/PageToMovie.slnx`.
No Engine, API, Railway, catalog, or auth. Clip bytes stay on the client.
Compose is **browser ffmpeg.wasm only** (no native server ffmpeg).

## Run

From this folder (`host/PageToMovie.Cut`):

```bash
dotnet run
# or
dotnet watch
```

Default URL: [http://127.0.0.1:5299](http://127.0.0.1:5299)

Build / test the standalone solution:

```bash
dotnet build PageToMovie.Cut.slnx
dotnet test PageToMovie.Cut.slnx
```

Chrome or Edge for **Pick folder** (File System Access API). **Choose MP4s** works as a fallback.

## Take SSoT

- Each take is `scene_SS_clip_CC_take_NN.mp4` (optional `.clip.json` sidecar).
  Take number = `ParseTakeNumber(filename)`.
- Which take is current = `scene_SS_clip_CC.current.json` only (`{ "take": N }`).
- Bare `scene_SS_clip_CC.mp4` is legacy. Ignore it. Do not copy, write, or treat it as current.
- Timestamp leftovers are ignored.

## What this branch does

1. Pick a local folder (or a handful of MP4s).
2. Load current takes from `_take_NN.mp4` + `.current.json` in Film scene/clip order. Ignore alias MP4s.
3. Timeline: one scene-stitched filmstrip block per scene (S01 / S02), white bookend trim handles, purple ruler range-delete (gap closes; not whole-clip delete). Hop fields seed in/out so an extended take does not play from t=0.
4. Join ticks between clips (Cut / Dissolve / Dip to black / Fade to white / Cut to black). Fountain sidecar when present; `cut.project.json` can override.
5. One text row between video and audio: scene cards at the incoming scene, plus `+ Add text` titles. Optional ~2s card hold (usually a dip). One background music track (8 MiB cap).
6. **Play** stitches that finish (ffmpeg.wasm) and plays in-page. **Make movie** downloads `movie.mp4`.
7. **Save cut** writes `cut.project.json` and reloads it with the folder.

The ffmpeg loader is **Web’s file**, copied into Cut `wwwroot/js/` at build (`CopyWebFfmpegToCut`). Do not commit a second `pagetomovie-ffmpeg.js` or `ffmpeg/` tree. Ops share that file’s exclusive queue.

## Out of 1.0

Reorder, whole-clip delete, take-picking product, split, multi-track, undo, wipe/iris, Engine / Railway.

Film Final Edit mount is last — see [CUT-1.0.md](CUT-1.0.md).
