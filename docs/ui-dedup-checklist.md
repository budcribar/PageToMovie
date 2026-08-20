# UI de-duplication checklist (2026-08-19)

Redundant controls: same action reachable from more than one place. One row per action; we go
through these one at a time and record the decision. Rule of thumb proposed earlier: **toolbar =
multi-scene actions · scene header = scene-only actions · clip row = per-clip actions · inspector =
editing only · Review = ratings/approval, not a second play surface.**

Status: ☐ open · ☑ resolved (with decision).

| # | Status | Action | Primary (keep) | Duplicates (candidates to remove/merge) |
|---|---|---|---|---|
| 1 | ☐ | Play a scene / selection | Film toolbar "▶ Play selected (N)" (`ScenesSceneIndex.razor:207`) | Scene header "Play selected clips" (`ScenesSceneDetail.razor:82`) · scene detail "Play full scene" (`ScenesSceneDetail.razor:236`) · Review tab per-row "▶ Play" (`ReviewReviewTab.razor:158`) + detail "▶ Play scene" (`ReviewReviewTab.razor:206`) |
| 2 | ☐ | Play one clip | Clip row "▶ Play C0N" (`ScenesClipTable.razor`) | Review tab per-clip "▶" (`ReviewReviewTab.razor:242`) |
| 3 | ☐ | Verify dialogue | Film toolbar "🔍 Verify Scene Dialogue (N)" (`ScenesSceneIndex.razor:226`) — scene scope; scene header "Verify (N) Selected Clips" (`ScenesSceneDetail.razor:91`) — clip scope | Clip inspector "Verify Dialogue" button (`ScenesClipInspector.razor:299`) — redundant with the row's verification badge |
| 4 | ☐ | Regenerate clips | Scene header "↻ Regen (N) selected clips" (`ScenesSceneDetail.razor`, new) · Film toolbar "🔄 Regenerate (N)" (`ScenesSceneIndex.razor:214`) — scene scope | Clip inspector "Regen clip" (`ScenesClipInspector.razor:31,136` — appears twice in the inspector itself) |
| 5 | ☐ | Takes / versions | Clip row "🎬 Takes" (`ScenesClipTable.razor:103`) | Clip inspector "🎬 Takes (N)" (`ScenesClipInspector.razor:40`) |
| 6 | ☐ | Delete a clip | Clip row 🗑️ (`ScenesClipTable.razor:120`) | Clip inspector "Delete clip" (`ScenesClipInspector.razor:56`) |
| 7 | ☐ | Delete a scene | Scene header 🗑️ (`ScenesSceneDetail.razor:143`) | — (single site; listed for the pattern discussion) |
| 8 | ☐ | Screenplay text from Film | Scene header "📜 Screenplay" drawer (`ScenesSceneDetail.razor:124`) | Scene-index footer "Screenplay: View" (`ScenesSceneIndex.razor:280`) · left-nav "Screenplay" (full editor — arguably fine) |
| 9 | ☐ | Open in ClipChamp / external editor | Scene header (`ScenesSceneDetail.razor:119`) | Review page (`Review.razor:83`) |
| 10 | ☐ | Score music | Scene header "🎵 Score" (`ScenesSceneDetail.razor`) | "Audio takes" button beside it (compare vs create — could be one split button) |
| 11 | ☐ | Connect media folder | Settings → Project Storage card (`Configuration.razor`) — canonical | Film banner (`Scenes.razor:119`) — reactive, keep? · Review Play tab "Connect folder" (`ReviewPlayTab.razor:74`) · NavMenu reconnect (`NavMenu.razor.cs:144`) · generate-gate prompt (by design) |
| 12 | ☐ | Generate looks for plan | Cast page (`Characters.razor:92`) | Locations page (`Locations.razor:40`) · Estimate page "Generate" (`Cost.razor`) · Shots page (`AdaptationShots.razor.cs`) |
| 13 | ☐ | Open Film → | Left nav "Film" + process strip step 6 (every page) | Locations header "Open Film →" (`Locations.razor:43`) · Estimate page link (`Cost.razor.cs`) · Shots page "Open Scenes →" (`AdaptationShots`) |
| 14 | ☐ | New project / Import project toggles | Home full-studio card (`HomeStudioCard.razor`) | Same toggles duplicated in easy-start landing + `HomeImportPanel.razor` |
| 15 | ☐ | Make movie (simple path) | SimpleVoice movie phase (`SimpleVoiceMoviePhase.razor`) | SimpleVoice record phase (`SimpleVoiceRecordPhase.razor`) — same action on two phases of one flow |
| 16 | ☐ | Verification report open | Clip row status badge (opens report) | Clip inspector status badge (opens the same report) — consistent pair, maybe fine |
| 17 | ☐ | Watch a demo film on YouTube | Card thumbnail overlay (`Demo.FilmCard.razor:12`, ▶ + "Watch on YouTube") | Film title link (`Demo.FilmCard.razor:47`) · "Watch on YouTube ↗" link (`Demo.FilmCard.razor:59`) — three links per card to the same URL |
| 18 | ☐ | Open the demo gallery | Left-nav "Demo" | Home demo-films card "Open gallery" — rendered in TWO variants (inline `HomeDemoFilmsCard.razor:18` + card `:56`), both with the same `home-open-demo-gallery` testid |
| 20 | ☑ | Generate vs Regenerate (film level) | ONE "Generate (N scenes)…" button; scope radio in the confirm: "Missing clips only" (default) vs "All clips (new takes)" with per-scope cost | Toolbar "🔄 Regenerate (N)" (`ScenesSceneIndex.razor:214`) folds into it. Scene-header "Regen (N) selected clips" is NOT part of this — checked clips always exist. Decided 2026-08-19 (mockup). |
| 19 | ☐ | Fork a demo ("open story") | Demo card "Fork" (`Demo.FilmCard.razor:113`) | Home easy-start story list fork (same action, different surface — may be intentional) |

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
