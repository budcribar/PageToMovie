# PageToMovie.Cut

Standalone finish editor. Film already owns order, deletes, and current takes. Cut reads that folder and finishes the movie (trim, transitions, music, cards, export). It is **not** wired into Film Studio yet.

**1.0 (finish, not assembly):** [CUT-1.0.md](CUT-1.0.md) — Clipchamp-style timeline, hop-aware in/out, range-delete, Fountain join ticks, text row (scene cards + titles), music, save/reload. Do not stack two agents on the same slice. Do not add reorder / whole-clip delete / take-picking product work.

**Media behavior:** [media-timeline-contract.md](../../docs/media-timeline-contract.md) defines valid video/audio and picture at audio-only edges. Cut uses decoder evidence, not a minimum MP4 byte size.

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

FFmpeg scene and transition preparation default to one worker. Mary19Test's fastest measured experiment used `?ffmpegWorkers=3&ffmpegStitchWorkers=4` (each accepts 1–4); either pool retries through one worker after a parallel failure. Add `&ffmpegFresh=1` to bypass render caches for comparable timing. See the [media/timeline contract](../../docs/media-timeline-contract.md#ffmpeg-worker-experiments).

Add `&ffmpegCombined=1` to use one final concat-and-soundtrack encode instead of the normal reusable-picture concat followed by a mix. Mary19Test measured 2:50.6 versus 5:59.7 for the two-pass path. The combined result is duration-checked, browser-decodes both streams, and automatically retries the proven two-pass path on failure. A successful combined render intentionally has no reusable dry-picture cache, so later music-only edits must compose again.

## Take SSoT

- Each take is `scene_SS_clip_CC_take_NN.mp4` (optional `.clip.json` sidecar).
  Take number = `ParseTakeNumber(filename)`.
- Which take is current = `scene_SS_clip_CC.current.json` (`{ "take": N }`). If Film omitted the pointer, Cut recovers only the highest numbered take from that exact scene/clip slot, then validates it with the browser decoder.
- Bare `scene_SS_clip_CC.mp4` is legacy. Ignore it. Do not copy, write, or treat it as current.
- Timestamp leftovers are ignored.

## What this branch does

1. Pick a local folder (or a handful of MP4s).
2. Load current takes from `_take_NN.mp4` + `.current.json` in Film scene/clip order. If the pointer is absent, recover the highest take from the same slot only and decoder-validate it. Ignore alias MP4s.
3. Timeline: one scene-stitched filmstrip block per scene (S01 / S02), white bookend trim handles, purple ruler range-delete (gap closes; not whole-clip delete). Hop fields seed in/out so an extended take does not play from t=0.
4. Join ticks between clips (Cut / Dissolve / Dip to black / Fade to white / Cut to black). Fountain sidecar when present; `cut.project.json` can override.
5. One text row between video and audio: scene cards at the incoming scene, plus `+ Add text` titles. Optional ~2s card hold (usually a dip). One background music track (8 MiB cap), with an optional music-over-black intro and a frozen final frame when music outlasts picture.
6. **Play** stitches that finish (ffmpeg.wasm) and plays in-page. **Make movie** downloads `movie.mp4`.
7. **Save cut** writes `cut.project.json` and reloads it with the folder.

The ffmpeg loader is **Web’s file**, copied into Cut `wwwroot/js/` at build (`CopyWebFfmpegToCut`). Do not commit a second `pagetomovie-ffmpeg.js` or `ffmpeg/` tree. Ops share that file’s exclusive queue.

## Out of 1.0

Reorder, whole-clip delete, take-picking product, split, multi-track, undo, wipe/iris, Engine / Railway.

Film Final Edit mount is last — see [CUT-1.0.md](CUT-1.0.md).
