# Cut 1.0 — finish, not assembly

This file is the **scope SSoT**. README is run/how-to.

Mary19 is practice. The real movie is Nick and Me. Cut must be enough to **honor that film**: finish, not another assembly editor.

Film already did: current takes, scene/clip order, deletes. Cut does **not** rebuild reorder, delete (whole clip), or take-picking as a product.

Cut **reads** `_take_NN.mp4` + `.current.json` only, in Film scene/clip order. Ignore bare `scene_SS_clip_CC.mp4` aliases. Never copy, write, or treat an alias as current.

## In this PR (tonight)

1. Folder scan, strip in Film order, per-clip play, in/out trim, one background music track, stitched preview, export `movie.mp4`.
2. **Range-delete:** mark a span, delete it, concat closes the gap (useful ripple). Not whole-clip delete.
3. **Joins:** honor Fountain transitions from a sidecar when present; otherwise a simple default. Per-join UI override: cut / dissolve / dip.
4. **Chapter/scene cards:** optional text card at scene boundaries, hold ~2s, usually with a dip. Simple centered text — not a title designer.
5. **Save/reload** the finish to `cut.project.json` in the folder (trims, range-deletes, join types, cards, music filename).
6. Tests: naming, `.current.json`, in/out, range-delete clamp, transition mapping.

## Fountain → join

| Fountain (sidecar) | Join |
|--------------------|------|
| CUT TO / SMASH / MATCH / JUMP | hard cut |
| DISSOLVE TO | crossfade |
| FADE IN | from black |
| FADE OUT / FADE TO BLACK / BLACKOUT | dip / fade to black |
| FADE TO WHITE | through white |
| CUT TO BLACK | instant black hold |
| WIPE TO | skip (hard cut tonight) |

Same-scene default = hard cut. Scene-number change default = dissolve if no Fountain line.

## Out tonight

Film Final Edit mount, voice lock, longer-cut generation, reorder, multi-track, undo, wipe/iris, Engine / Railway / catalog.

## Folder SSoT

| Rule | Detail |
|------|--------|
| Take file | `scene_SS_clip_CC_take_NN.mp4` (+ optional `.clip.json`) |
| Take # | `ParseTakeNumber(filename)` |
| Current | `scene_SS_clip_CC.current.json` only |
| Order | Scene then clip, as Film left the folder |
| Alias MP4 | Legacy. Ignore. Never write. |
| Missing current | Missing. No fallback to another take or the alias. |
| Finish file | `cut.project.json` (trims / range-deletes / joins / cards / music name) |

## Constraints

- `host/PageToMovie.Cut` + tests only. Own slnx — **not** `PageToMovie.slnx`.
- No Engine / Api / Web / Core ProjectReference. No Railway, catalog, auth.
- Browser ffmpeg.wasm only. Loader SSoT is Web’s `pagetomovie-ffmpeg.js`, copied at build. One exclusive queue. Do not commit a second copy.
- Bytes stay on the client. No hardcoded model attributes.
- Same PR 193. **Do not merge** until Bud asks.
- **Do not stack two agents on the same slice.**

## Slices

| # | Slice | Status |
|---|--------|--------|
| 1–4 | Folder, trim, preview/export, music | Done (this PR) |
| 5 | Save/reload finish | This PR |
| 6 | Range-delete + Fountain joins + cards | This PR |
| 8 | Film alias drop | [PR 194](https://github.com/budcribar/PageToMovie/pull/194) merged |
| 9 | Final Edit mount | Last — not tonight |

```bash
cd host/PageToMovie.Cut
dotnet run          # http://127.0.0.1:5299
dotnet test PageToMovie.Cut.slnx
```
