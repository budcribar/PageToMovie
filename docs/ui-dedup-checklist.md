# UI de-duplication checklist (2026-08-19)

Redundant controls: same action reachable from more than one place. One row per action; we go
through these one at a time and record the decision. Rule of thumb proposed earlier: **toolbar =
multi-scene actions · scene header = scene-only actions · clip row = per-clip actions · inspector =
editing only · Review = ratings/approval, not a second play surface.**

Status: ☐ open · ☑ resolved (with decision).

| # | Status | Action | Primary (keep) | Duplicates — state after the Option B rewrite (2026-08-20) |
|---|---|---|---|---|
| 1 | ☑ | Play a scene / selection | Film toolbar "▶ Play selected (N)" | Film Play ladder stays (toolbar + scene header "▶ Play (N)" — INTENTIONAL pair). Review Play tab + clip ▶ stay. Clip-review header "Play scene" removed. Scene-row Play (`review-play-scene-{sn}`) is the one scene play on Review. |
| 2 | ☑ | Play one clip | Clip expansion autoplay (row click) | Per-row "▶ Play C0N" button REMOVED (Option B). Review per-clip ▶ stays (rating context). |
| 3 | ☑ | Verify dialogue | Toolbar (scene scope) + scene header "🔍 Verify (N)" (clip scope) — the scope ladder | Inspector "Verify Dialogue" button REMOVED with the Option B expansion rewrite. |
| 4 | ☑ | Regenerate clips | Scene header "↻ Regen (N) selected clips" only | Inspector/expansion "Regen clip" REMOVED (single-clip regen = check it + Regen (1)). |
| 5 | ☑ | Takes / versions | Expansion action bar "🎬 Takes (N)" | Per-row Takes button REMOVED (Option B). |
| 6 | ☑ | Delete a clip | Scene ⋯ menu "🗑️ Delete (N) selected clips…" (multi-confirm) | Per-row 🗑️ and expansion "Delete clip" REMOVED. |
| 7 | ☑ | Delete a scene | Scene ⋯ menu "🗑️ Delete scene…" | Naked header trash button folded into the menu. |
| 8 | ☑ | Screenplay text from Film | Scene ⋯ menu "📜 Screenplay" drawer | Scene-index footer duplicate gone; left-nav Screenplay = full editor (different tool, fine). |
| 9 | ☑ | Open in ClipChamp / external editor | Scene ⋯ menu | Header button folded into the menu; Review page instance stays (post-review hand-off — intentional). |
| 10 | ☑ | Score music | Scene ⋯ menu "🎵 Score" | Audio takes lives beside it IN THE SAME MENU now — one surface. |
| 11 | ☐ | Connect media folder | Settings → Project Storage (canonical) | Film banner / Review Play tab / NavMenu reconnect / generate-gate — all REACTIVE prompts at point of need; likely keep, decide once. |
| 12 | ☑ | Generate looks for plan | Cast + Locations pages (same `plan_looks` job) | Next-step buttons aligned to Screenplay's top green control; plan-looks button hidden once all used faces/places are locked; Estimate/Shots auto-chain unchanged. |
| 13 | ☐ | Open Film → | Left nav + process strip | Locations/Estimate/Shots inline links — harmless nav sugar? decide once. |
| 14 | ☐ | New project / Import toggles | Home full-studio card | Easy-start landing duplicates the toggles. |
| 15 | ☐ | Make movie (simple path) | SimpleVoice movie phase | Record phase repeats the action (two phases of one flow). |
| 16 | ☑ | Verification report open | Row status chip (one chip: busy/missing/stale/verdict) opens the report | Expansion keeps only a small "report" link next to the heard-vs-expected diff — consistent pair, by design. |
| 17 | ☑ | Watch a demo film on YouTube | Card thumbnail overlay (`demo-watch-link`) | Title is plain text (not a link). "Watch on YouTube ↗" text link REMOVED. One YouTube watch control per card. |
| 18 | ☑ shipped | Open the demo gallery | Heading "Demo films" is the `/demo` link (`home-demo-films-heading`) | Open Gallery button/link REMOVED. Home keeps ONE surface (standalone card; studio-card inline strip dropped). Left-nav Demo stays. |
| 19 | ☐ | Fork a demo ("open story") | Demo card "Fork" | Easy-start story list fork (may be intentional dual surface). NOTE: forkable now requires the new Open visibility (B2) — previously-Public projects need re-marking. |
| 20 | ☑ shipped | Generate vs Regenerate (film level) | ONE "Generate" with scope radio in the confirm (Missing only / All as new takes) | SHIPPED 2026-08-20 — toolbar "🔄 Regenerate (N)" removed; combined confirm is `Scenes.GenerateConfirm.razor` (scope radio + resolution/cost/admin model). |

## Feature decisions from the mockup review (2026-08-19)
- Insert scene / insert clip at ANY position (not just append). DESIGN (revised 2026-08-19, user call:
  inserts are rare): RENUMBER ON INSERT — keep the "number = order = filename" invariant; the insert is
  a bounded renumber pass. Server side renames only small JSON (no media lives there): sidecars (xAI
  pointers inside), QA verifications, prompt meta, history/trash, registry rows, _extend_src markers.
  Client folder catches up via a rename manifest applied on next Film load (same self-heal pattern as
  sidecar restore). Renumber highest-first + idempotent for crash safety. Clip after the insertion point
  goes stale (new predecessor). ID-named files + map rejected for now (migration of every naming
  convention; revisit only if inserts become frequent). NOT BUILT YET.
  - GitHub-repo interaction (reviewed 2026-08-19): the per-project repo tracks only small JSON/text
    (media binaries are git-ignored), so a renumber pass renames tiny text blobs — cheap, and git's
    rename detection keeps per-file history across it. Rules: (1) the whole renumber lands as ONE
    auto-git commit ("Insert scene 03: renumbered S03–S05 → S04–S06") so any clone/fork sees the
    rename atomically, never a half-renumbered tree; (2) the client rename manifest is COMMITTED
    project state (append-only log, e.g. source/renames.jsonl) so forks and other machines' local
    media folders can replay renames they missed — each entry carries a monotonically increasing id,
    clients remember the last id applied; (3) manifest entries are never rewritten, only appended.
  - Prerequisite fixed 2026-08-19: the project .gitignore ignored ALL of assets/video/, which hid the
    clip sidecars (the only provider-video pointers) from the repo — a GitHub restore came back with
    zero clips. Now only media binaries (*.mp4 etc.) are ignored; existing repos self-heal their
    .gitignore on the next auto-commit (ProjectGitRepositoryService).
- Film-level Generate/Regenerate merged (see #20). Row = checkbox + expander + line + duration + chip;
  delete via scene menu ("Delete (N) selected clips…"); autoplay on expand; "Edit Clip Script" / "AI Edit Video".
- Clip-card "Regen clip" removed from the expansion card (decided 2026-08-19): single-clip regen =
  check just that clip → header "Regen (1)". One regen verb, one place; card keeps AI Edit Video /
  Takes / Edit Clip Script.

## Option B — BUILT 2026-08-19 (user call: "implement and test thoroughly option B")
- Clip row = grip + checkbox + numbered expander chip + line + duration + ONE status chip
  (generating > missing > stale [QA folds the verify score in] > verdict > ready). Row/chip click
  expands the clip IN PLACE (ScenesClipInspector renders inside the row): autoplay player,
  verification one-liner + diff, action bar AI Edit Video / Takes / Edit Clip Script / Fix cast
  look, admin-only Details fold. No separate inspector panel; no per-row Play/Takes/🗑 buttons.
- "+ Add clip…" ghost row at the table foot (opens the add-clip editor — trigger was orphaned).
- Scene header = ▶ Play (N) / ↻ Regen (N) / 🔍 Verify (N) + ⋯ menu (Score, Screenplay drawer,
  Audio takes, Open in editor, Delete (N) selected clips…, Delete scene…). Multi-clip delete got
  its own confirm.
- DRAG-AND-DROP REORDER (renumber-on-drop — the renumber engine from the insert design, built):
  - Clips within a scene (grip ⠿; disabled while duration-sorted or a job runs). Scenes in the
    index (disabled while filtered). `ProjectStore.ReorderClips/ReorderScenes`
    (ProjectStore.Reorder.cs): blueprint order + contiguous numbers, two-phase file renames
    (crash-healing .renumtmp marker) across video/history/.trash/qa/music/revoice, sidecar +
    verification JSON content renumbered, extend-src markers deleted (stale predecessor),
    composite + sources.json invalidated, registry rows renamed in one transaction, ONE auto-git
    commit.
  - Scene reorder also permutes the SCREENPLAY's fountain scene chunks (refused on scene-count
    mismatch — no silent plan/script divergence); a blueprint-only trailing credits scene is
    tolerated and must stay last.
  - Client catch-up: committed manifest media_renames.jsonl (append-only, increasing ids) served
    via GET /media-renames; ClientMediaFolderService.ApplyServerRenamesAsync replays it on Film
    load (localStorage bookmark; renames are exact-name + skip-if-target-exists = idempotent).
  - Endpoints: POST scenes/reorder, POST scenes/{s}/clips/reorder (owner/admin).
  - Tests: 26 unit (ProjectReorderTests) + 3 Playwright (OptionBFilmPageTests: expansion/menu/add,
    clip drag, scene drag) + updated InteractionTests.
- Insert-anywhere UI is the remaining piece of the insert design (the renumber engine + manifest
  now exist; "+ Add clip" appends — insert at position = append + drag).

## Resolved this session (for reference)
- ☑ "Edit in Screenplay" on Film scene detail — removed (nav covers it).
- ☑ "Show Fountain Script" — renamed "📜 Screenplay", moved to scene header.
- ☑ Per-row "↻ Regen" buttons — replaced by "↻ Regen (N) selected clips" + confirm.
- ☑ "Refresh list" on the finished-job card — removed (list auto-reloads).
- ☑ Duplicate "Cancelled" messages — one surface per audience.
- ☑ "Generating…" spinner inside the Generate button — removed (modal owns progress).
- ☑ "Regenerate Selected Scenes" → "Regen shot plan for selected scenes" (+ confirm, no silent full rebuild).
