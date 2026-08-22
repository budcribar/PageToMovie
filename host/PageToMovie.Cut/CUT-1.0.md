# Cut 1.0 — finish, not assembly

This file is the **scope SSoT**. README is run/how-to.

Mary19 is practice. The real movie is Nick and Me. Cut must be enough to **honor that film**: finish, not another assembly editor.

Film already did: current takes, scene/clip order, deletes. Cut does **not** rebuild reorder, delete (whole clip), or take-picking as a product.

Cut **reads** `_take_NN.mp4` + `.current.json` only, in Film scene/clip order. Ignore bare `scene_SS_clip_CC.mp4` aliases. Never copy, write, or treat an alias as current.

## In this PR (timeline)

1. Folder scan, **Clipchamp-style timeline** (one video track in Film order), hop-aware in/out, one background music track, stitched preview, export `movie.mp4`.
2. **Scene-stitched video track:** one contiguous block per scene, labeled **S01 / S02** only. No clip tiles, seams, gaps, or per-clip titles inside a scene. Takes stay separate files (`_take_NN.mp4` + `.current.json`) — do not write a `scene_SS.mp4` alias. Play / Make movie / JIT still hop-slice each take and hard-cut concat same-scene clips (no black/silence gap). Native VO stays.
3. **Trim handles:** white pills only on **scene bookends** — left on the first clip of S01/S02/…, right on the last of that stitched block. Mid-scene seams have no handles (hard cuts). Left handle trims the first clip’s MarkIn; right handle trims the last clip’s MarkOut (hop-aware). Mid-scene MarkIn/Out stay hop defaults unless already saved.
4. **Range-delete:** drag a purple span on the time ruler, delete it (works across the stitched scene), concat closes the gap. Not whole-clip delete.
5. **Joins / scene marks:** timeline marks **scenes** (S01, S02, …), not clip seams. Same-scene hard cuts are silent concat — no tick and no visual seam. Scene-change join tick only for a visible look (Dissolve / Dip to black / Fade to white / Cut to black). Hard-cut scene change = scene label only. Fountain sidecar is SSoT; `cut.project.json` can override.
6. **Hop / extend:** seed MarkIn/MarkOut from sidecar `provider_clip_start_seconds` / `provider_clip_stop_seconds`, or `provider_lead_in_seconds` + duration. Timeline width, filmstrip, and preview start at the hop — not t=0 of a combined take file.
7. **Chapter/scene cards:** optional text card at scene boundaries, hold ~2s, usually with a dip. Cards appear as blocks on the **text row** (between video and audio) at the incoming scene time. Edit the label, drag duration, or delete on that row.
8. **Text row:** one Clipchamp-style titles/text track. Empty state is `+ Add text`. Free titles are centered white text on a simple card/overlay. Not a title designer, captions, or a Text library. Scene-change joins and the text row stay at scene boundaries. While the label field is focused, Backspace/Delete edit one character; they remove the text clip only when the clip is selected and no field is focused.
9. **Save/reload** the finish to `cut.project.json` (trims, range-deletes, join types, cards, text clips, music filename).
10. **Play / audio:** Clipchamp zoom cluster (out / in / Fit timeline) is pinned in the timeline chrome — always visible; only the filmstrip scroller moves. Compose keeps each clip’s native VO (hop/trim window); optional music mixes under. Hard-cut concat keeps audio. `xfadeAsync` maps `[v]` + `[a]` (acrossfade, else audio concat) — never `-an` on a dissolve/dip (Mary19 scene-change default). Cards (`stillVideoAsync`) may stay silent. Free titles overlay the clip they sit on. **Play is JIT:** first ready window (hop-sliced clip, native VO) starts immediately; ffmpeg.wasm keeps combining later clips on the exclusive queue. Playback continues as the prefix grows (no restart from 0). Seek past the ready prefix shows the overlay until that gap is ready. A valid full `MoviePreviewUrl` still skips compose. **Make movie** / export stays a full compose.
11. Tests: hop-seeded in/out, scene-bookend trim handles, range-delete, scene-stitched video blocks / labels, scene bands, visible-join ticks, JIT ready/wait, zoom/fit, naming, `.current.json`, text-row cards/titles, Backspace-in-edit does not wipe the text clip.

## Fountain → join

Film writes the join as a Fountain transition immediately before the next scene heading
(empty / omitted = hard cut). That line is the SSoT — not `transition_type` on the shot plan.

Optional chapter/scene card lives on the same join as a Fountain note:
`[[CARD: Chapter 1]]` (after the transition, or alone before the heading on a hard cut).
`cut.project.json` remains Cut's finish override, not Film's store.

| Fountain (sidecar / screenplay line) | Join |
|--------------------------------------|------|
| CUT TO / SMASH / MATCH / JUMP | hard cut |
| DISSOLVE TO | crossfade |
| FADE IN | from black |
| FADE OUT / FADE TO BLACK / BLACKOUT | dip / fade to black |
| FADE TO WHITE | through white |
| CUT TO BLACK | instant black hold |
| WIPE TO | skip (hard cut tonight) |
| `[[CARD: …]]` | optional text card at that incoming scene |

Same-scene default = hard cut. Scene-number change default = dissolve if no Fountain line.

## Out of this slice

Multi-track NLE, Clipchamp sidebars (captions / filters / speed / brand kit), undo stack, wipes, Film Final Edit mount, voice lock, longer-cut generation, reorder, whole-clip delete, take-picking product, Engine / Railway / catalog / auth.

## Folder SSoT

| Rule | Detail |
|------|--------|
| Take file | `scene_SS_clip_CC_take_NN.mp4` (+ optional `.clip.json`) |
| Take # | `ParseTakeNumber(filename)` |
| Current | `scene_SS_clip_CC.current.json` only |
| Order | Scene then clip, as Film left the folder |
| Alias MP4 | Legacy. Ignore. Never write. |
| Missing current | Missing. No fallback to another take or the alias. |
| Hop slice | Sidecar `provider_lead_in_seconds`, `provider_clip_start_seconds`, `provider_clip_stop_seconds` |
| Finish file | `cut.project.json` (trims / range-deletes / joins / cards / text clips / music name) |

## Constraints

- `host/PageToMovie.Cut` + tests only. Own slnx — **not** `PageToMovie.slnx`.
- No Engine / Api / Web / Core ProjectReference. No Railway, catalog, auth.
- Browser ffmpeg.wasm only. Loader SSoT is Web’s `pagetomovie-ffmpeg.js`, copied at build. One exclusive queue. Do not commit a second copy.
- Bytes stay on the client. No hardcoded model attributes.
- **Do not merge** until Bud asks.
- **Do not stack two agents on the same slice.**

## Slices

| # | Slice | Status |
|---|--------|--------|
| 1–4 | Folder, trim, preview/export, music | Done |
| 5 | Save/reload finish | Done |
| 6 | Range-delete + Fountain joins + cards | Done |
| 7 | Clipchamp timeline + hop-seeded in/out | Done (PR 199) |
| 7b | Scene marks, Clipchamp zoom, native VO, Play cache | Done (PR 200) |
| 7c | Scene-bookend handles + JIT Play | Done (PR 201) |
| 7d | Clipchamp text row | Done (PR 202) |
| 7e | Scene-stitched video blocks | **This PR** |
| 8 | Film alias drop | [PR 194](https://github.com/budcribar/PageToMovie/pull/194) merged |
| 9 | Final Edit mount | Last — not tonight |

```bash
cd host/PageToMovie.Cut
dotnet run          # http://127.0.0.1:5299
dotnet test PageToMovie.Cut.slnx
```
