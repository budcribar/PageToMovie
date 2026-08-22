# PageToMovie.Cut

Standalone finish editor. Film already owns order, deletes, and current takes. Cut reads that folder and finishes the movie (trim, transitions, music, cards, export). It is **not** wired into Film Studio yet.

**1.0 (finish, not assembly):** [CUT-1.0.md](CUT-1.0.md) — scope SSoT. Do not stack two agents on the same slice. Do not add reorder / delete / take-picking product work.

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

## What this branch already does (slices 1–4)

1. Pick a local folder (or a handful of MP4s).
2. Load current takes from `_take_NN.mp4` + `.current.json` in Film scene/clip order. Ignore alias MP4s.
3. Show a strip; per-clip preview + mark in / out on the current take.
4. Optional one background music track mixed under the whole movie (8 MiB cap).
5. **Play** stitches trimmed current takes + music (same ffmpeg.wasm queue as export) and plays the blob in-page. It does not download.
6. **Make movie** uses that same compose path and downloads `movie.mp4`.

Remaining 1.0 work (save/reload finish, transitions, chapter/scene cards, Film mount) is listed in [CUT-1.0.md](CUT-1.0.md).

The ffmpeg loader is **Web’s file**, copied into Cut `wwwroot/js/` at build (`CopyWebFfmpegToCut`). Do not commit a second `pagetomovie-ffmpeg.js` or `ffmpeg/` tree. Ops share that file’s exclusive queue.

## Out of 1.0

Reorder, delete, take-picking product, ripple, split, multi-track, undo, Engine / Railway.

Film Final Edit mount is last — see [CUT-1.0.md](CUT-1.0.md).
