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
8. **Text row:** one Clipchamp-style titles/text track. Empty state is `+ Add text`. **Add text stays clickable after the first title/card** — it is chrome on the text row (above tiles), not under the first clip and not under the video overlay. Free titles and scene cards also render as a **live overlay on the preview/Play video** (native hop Play). A composed movie already has them burned in — do not stack a second overlay on that surface. Not a title designer, captions, or a Text library. Scene-change joins and the text row stay at scene boundaries. While the label field is focused, Backspace/Delete edit one character; they remove the text clip only when the clip is selected and no field is focused. Selecting a title or scene card opens a small inspector: place (center / lower / top), size (S / M / L), color (white / yellow / black), bar (none / dark bar), fade (none / short in-out), duration. Defaults stay centered white, no bar, no fade so existing cards look the same until changed. Compose (card still + title overlay) honors the mapped look. Right-click a title tile or the selected live overlay for Duplicate / Copy / Paste / Delete / Split (playhead inside) / Edit duration. Same menu both places; no text-style library. Shortcuts work when a title is selected and no field is focused. Delete stays on the inspector, not the stretch handle. Drag a title box to slide it (pointer capture, pixel-to-time from the drag origin, persist on pointer up — same as music). Titles and cards on the one text row never overlap: drag/trim push against neighbors and 0 / movie end; add / paste / duplicate / split land in the next free gap, never stacked.
9. **Save/reload** the finish to `cut.project.json` (trims, range-deletes, split windows, join types, cards, text clips, text style, music filename + optional display name + start/in/out).
10. **Play / audio:** Clipchamp zoom cluster (out / in / Fit timeline) is pinned in the timeline chrome — always visible; only the filmstrip scroller moves. **Play and Make movie enable once the folder has any current-take clip** — do not wait for `movie.mp4` or a finished merge, and do not stay disabled because another slot is missing. Compose keeps each clip’s native VO (hop/trim window); optional **one music track** mixes under. Drag the music block to place it anywhere (including over credits); drag handles to trim head/tail. Persist start/in/out. Right-click the music block for Copy / Paste placement, Delete, disabled Split (one track — keep trim), Edit duration (focus out-handle), Rename (display name; real file stays). No second song. Titles stretch along the timeline past 30s (up to movie length). Text Delete lives on the inspector, not on the out-handle. Dropped audio shows the file name, not the property identifier. Hard-cut concat keeps audio. `xfadeAsync` maps `[v]` + `[a]` (acrossfade, else audio concat) — never `-an` on a dissolve/dip (Mary19 scene-change default). Cards (`stillVideoAsync`) may stay silent. Free titles overlay the clip they sit on. **Play is one merged file.** Edit stays clip-based (trim, scissors, scene joins, titles). **Segment cache (Nick and Me length):** persist each scene picture and outgoing join under `cut.cache/` plus fingerprints in `cut.project.json`. A title or join on scene 40 rebuilds that scene and the affected join only — scenes 1–39 stay on disk. Music mix is a last pass over cached picture; moving the score does not re-xfade clean scenes. When a Play/JIT compose finishes the current cut, write `movie.mp4` + segment files in the project folder if writable — no browser download. Folder reopen / next Play / Make movie reuse every clean segment; unchanged film plays and exports from the cached merge with no ffmpeg stitch. Dirty film hops/JIT only the dirty gap, then concats cached + rebuilt segments. **Make movie** downloads the reused file or the new concat; it can promote the cached merge to `movie.mp4` without re-encoding when the fingerprint matches. A fresh `MoviePreviewUrl` or last `movie.mp4` (whole-cut fingerprint matches) plays immediately. Otherwise Play is JIT: the hop-sliced take at the playhead starts immediately; ffmpeg.wasm rebuilds dirty segments in the background. When that merge covers playback, switch once to that file and stay there — clip/scene edges are times on the merge, not `video.src` swaps between take MP4s. Prefix growth does not replace the playing file mid-stream. Seek past the ready prefix shows the overlay until that gap is ready. A scene-change dissolve/dip is that gap — prefix EOF at S01→S02 waits for the stitch, it is not Stop. While preview/JIT is playing, the Play control is **Stop** — it ends playback and leaves the playhead. Stopped, it is Play again. Not a full NLE pause/resume. **Play clock is JS-painted** — timeupdate, the first-start→merge handoff, and prefix growth must not `StateHasChanged` the page (no whole-page stutter at clip edges). The one allowed src change (first-start take → merge) holds the outgoing last frame until the merge has a decoded frame. Preview markup freezes while playing so a wait-overlay render cannot reset `src`. Cut loads **only Film’s current take** (`scene_SS_clip_CC.current.json` → `_take_NN.mp4`). No take-picker UI and no Cut-side take switching.
11. **White playhead:** the current-time needle is white (`#ffffff` / `--cut-playhead`) so it reads on the dark filmstrip.
12. **Scissors split:** Clipchamp-style scissors on the transport splits the take/window at the playhead (range-delete/trim SSoT — two adjacent in-memory windows of the same `_take_NN.mp4`, no new take file, no `scene_SS.mp4`). Same-scene result abuts with no gap; the S01/S02 stitched block stays one tile. Scene-bookend handles stay on the new first/last of that scene. Persist extra windows as extra `cut.project.json` clip rows for the same scene/clip.
13. Tests: hop-seeded in/out, scene-bookend trim handles, range-delete, scene-stitched video blocks / labels, scene bands, visible-join ticks, JIT ready/wait, zoom/fit, naming, `.current.json` current-take-only (no other takes loaded, no take switch), text-row cards/titles, Backspace-in-edit does not wipe the text clip, Add text stacks above the first title tile, live Play overlay cues, play-clock / prefix-swap render path (no timeupdate re-render), white playhead class/color, scissors split at playhead, Play/Stop toggle, text-style defaults and option mapping (compose + `cut.project.json`), Play uses one merge file (no take hops; first-start only until merge; fresh `movie.mp4` fingerprint), Play enables with any current take, titles hold past 30s, music place/trim persists, title duplicate / copy / paste / delete / split-at-playhead, title move-by-dt / no-overlap clamp / paste-duplicate gap, music menu delete / rename / edit-duration, persist + reuse the merge on reload when unchanged, dirty one scene rebuilds only that segment, unchanged film does no ffmpeg stitch on Play or Make movie.

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
| Finish file | `cut.project.json` (trims / range-deletes / split windows / joins / cards / text clips / text style / music name + start/in/out / movie + per-scene/join fingerprints) |
| Merge cache | `cut.cache/sSS.mp4` (scene picture), `cut.cache/jSS.mp4` (outgoing join), `cut.cache/picture.mp4` (picture concat), `movie.mp4` (mixed film) |

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
| 7e | Scene-stitched video blocks | Done (PR 205) |
| 7f | White playhead, scissors split, Play/Stop, text inspector | Done (PR 206) |
| 7g | Play smoothness, live text overlay, current take only | Done (PR 212) |
| 7h | Play one merged file (no take hops / no black flash) | Done (PR 213) |
| 7i | Play enable, text stretch, music place/trim, Make movie | **This PR** |
| 8 | Film alias drop | [PR 194](https://github.com/budcribar/PageToMovie/pull/194) merged |
| 9 | Final Edit mount | Last — not tonight |

```bash
cd host/PageToMovie.Cut
dotnet run          # http://127.0.0.1:5299
dotnet test PageToMovie.Cut.slnx
```
