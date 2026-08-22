# PageToMovie.Cut

Standalone V1 cut / editor. A thin, in-browser movie assembler (Clipchamp-like, but tiny).
It is **not** wired into Film Studio yet.

**1.0 scope and remaining slices:** [CUT-1.0.md](CUT-1.0.md) (SSoT — do not stack two agents on the same slice).

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

## What V1 does

1. Pick a local folder (or a handful of MP4s).
2. Load `_take_NN` files; seed the Active take from `.current.json`.
3. Show a horizontal strip in scene / clip order.
4. Per clip: take chooser (list takes, mark **Active**). Changing the take updates
   preview, in/out, and export, and writes `.current.json`. It never writes an alias MP4.
5. Per clip: preview + mark in / out (on the current take).
6. Optional one audio track (music or wav) mixed under the whole movie (duck, then replace if duck fails).
7. **Play** stitches the current in/out-trimmed takes plus optional audio (same ffmpeg.wasm queue as export) and plays the blob in-page. It does not download.
8. **Make movie** uses that same compose path and downloads `movie.mp4`.

ffmpeg assets and `pagetomovie-ffmpeg.js` are copied from `PageToMovie.Web/wwwroot/js/` so the loader, concat, duration probe, and encode args stay the same. Ops share that file’s exclusive queue.

## Out of scope (not in V1)

Ripple delete, multi-track mix, titles, transitions, undo, Film / Review integration,
RCL / iframe mount, Engine API, alias MP4 read/write, credits special-case.

Integration with the main studio app comes later.
