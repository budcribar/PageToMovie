# UI test campaign — full-feature coverage (started 2026-08-20)

Goal (user): every feature, button, input field, checkbox, insert, delete etc. covered by a UI
test, on fakes that produce Mary-shaped artifacts, from import to final movie — plus export/import,
fork → change → merge, two users on one project, settings. Find bugs.

Fixture: `host/playwright/fixtures/mary_had_a_lamb.fountain` — narrator (V.O.) + speaking Mary /
Teacher / Children + a SILENT lamb, 5 scenes; the same shape as the real Mary19 project.

## New tests added

| # | Test (PageToMovie.UiTests) | Covers | Status |
|---|---|---|---|
| 1 | OptionBFilmPageTests.Clip_row_expands_in_place_with_actions_add_clip_row_and_scene_menu | Row expansion, action bar, + Add clip row, scene ⋯ menu incl. both deletes | ✅ |
| 2 | OptionBFilmPageTests.Dragging_a_clip_row_reorders_and_renumbers_the_scene | Clip drag-drop reorder end-to-end (renumber engine) | ✅ |
| 3 | OptionBFilmPageTests.Dragging_a_scene_row_reorders_and_renumbers_the_film | Scene drag-drop reorder incl. screenplay chunk permutation | ✅ |
| 4 | MaryEndToEndTests.Mary_arc_import_generate_verify_score_review | Import → cast → shots → generate all clips → verify dialogue → score music (⋯ menu → modal → job → 🎵) → review approve → Play tab → Share card | ✅ |
| 5 | MaryEndToEndTests.Mary_silent_lamb_clips_exist_and_narrator_is_voice_only | Plan shape: silent clips (visual-only verify path), narrator verses across clips | ✅ |
| 6 | ProjectLifecycleTests.Generated_project_zip_roundtrip_rehydrates_clips | Export zip of a fully GENERATED project → import under new name → clips re-hydrate from sidecars (scene/clip/on-disk counts survive) | ✅ |
| 7 | ProjectLifecycleTests.Fork_edit_both_sides_then_sync_origin_merges_cleanly | Visibility select → fork → disjoint clip edits in origin & fork (real Edit Clip Script modal) → "Sync origin" 3-way merge keeps both edits + fork identity | ✅ |
| 8 | ProjectForkTests.Fork_shares_history_and_sync_origin_merges_disjoint_edits_keeping_identity (unit) | Fork shares git history with parent; disjoint merges clean; project.json pinned to fork | ✅ |
| 9 | ProjectReorderTests (26 unit tests) | Renumber engine: files/blueprint/sidecars/QA/manifest/registry, scene chunk splitting | ✅ |
| 10 | InteractionTests (2 updated) | Scene ⋯ menu holds Screenplay drawer + Delete scene | ✅ |
 11 | TwoUserCollaborationUiTests.Second_editors_scene_lease_shows_in_owners_ui_and_blocks_delete | Two users, one project: editor grant (ACL), bob's scene lease shows in the owner's UI (row 🔒 + detail chip), ⋯-menu Delete scene disabled, server 423, release clears | ✅ |
| 12 | PageSweepTests.Locations_and_dialogue_timing_pages_work_on_a_generated_project | Locations index/plate state/unused toggle + Dialogue-Timing scene pick → analyze | ✅ |
| 13 | PageSweepTests.Screenplay_outline_drag_reorders_scenes_and_persists | Screenplay outline drag-reorder + autosave persistence across reload | ✅ |
 14 | PageSmokeTests (+7 routes) | /locations, /dialogue-timing, /simple-revoice, /simple-voice, /cost/breakdown, /account/costs, /about hydrate without console errors | ✅ |
| 15 | VoiceLockConsistencyTests.Film_page_cast_lock_tracks_narrator_voice_profile_edits | Regression guard for the 2026-08-19 live bug: Film-page cast lock follows narrator voice-profile edits in both directions (sexless profile unlocks, pinned sex+age re-locks) | ✅ |
 16 | ScreenplayBeatDepthTests.Add_beat_pick_speaker_type_line_delete_beat_and_persist | Structured editor: add Dialogue beat, cast dropdown speaker pick, line entry, beat delete, autosave persistence across full reload | ✅ |
 17 | ScreenplayBeatReorderAndLocationTests.Beat_drag_reorder_swaps_rows_and_persists | Beat ⋮⋮ drag-reorder inside a scene card | ✅ |
| 18 | ScreenplayBeatReorderAndLocationTests.Scene_card_locations_gear_deep_links_to_locations_page_focused | ⚙ Locations gear → /locations deep-link focus; heading location select change persists to the draft | ✅ |
 19 | LocationsLookFlowTests.Generate_looks_then_lock_and_switch_preferred_variant | Locations: generate 3 looks (fake image job), lock variant #2, switch preferred to #1 | ✅ |
| 20 | LocationsLookFlowTests.Configuration_media_folder_connects_and_reports_name | Configuration storage section: connect media folder, connected-name card renders (fixture stub now names the OPFS handle) | ✅ |

| 21 | RemainingCoverageTests.Characters_voice_profile_edit_via_panel_persists | Voice picking through the real panel: open the folded Voice card, type a pinned profile, autosave persists to the characters API | ✅ |
| 22 | RemainingCoverageTests.Configuration_theme_and_external_editor_persist_across_reload | Appearance section: theme select + preferred external editor persist across reload | ✅ |
| 23 | RemainingCoverageTests.Simple_revoice_page_shows_main_card_on_generated_project | simple-revoice main card renders with the truthful no-clone hand-off | ✅ |
| 24 | RemainingCoverageTests.Play_tab_stitches_the_full_movie_in_the_browser | ffmpeg.wasm depth: with no fresh server WIP, the Review Play tab combines the full cut IN THE BROWSER and plays a blob-backed video | ✅ |

## Bugs found (and status)

| # | Bug | Found by | Status |
|---|---|---|---|
| B1 | After scoring music, the scene's 🎵 marker and "Audio takes" stayed hidden until re-navigation — `CompleteSceneMusicDownloadAsync` saved+registered the audio AFTER the job's terminal list reload and never refreshed the list. Visible half of the 2026-07 "music sync" complaint. | Test #4 | ✅ fixed (reload after save) |
| B2 | Visibility select advertises "Public (Read-Only)" vs "Public (Forkable)" (value `Open`), but `ProjectVisibility` has no Open member — server coerces open/forkable→Public and ANY Public project is forkable; the select visibly snaps to "Public (Read-Only)" after choosing Forkable. | Test #7 | ⚠ documented — needs a product call: add a real Open mode or collapse to one "Public" option |
| B3 | Fork created a FRESH git repo (unrelated history) — "Sync origin" had no merge base, so every file either side ever changed conflicted; the advertised 3-way merge could essentially never succeed. Also origin's project.json could overwrite the fork's identity. | Test #7 | ✅ fixed (fork adopts parent history via fetch; sync pins project.json to ours; conflict paths now reported) |
| B4 | `sync-origin` endpoint never invalidated the store's read caches (and would have used the %2F-encoded id as the cache key anyway) — after a merge, Film/Screenplay pages kept serving the pre-merge plan. | Test #7 | ✅ fixed (invalidate with normalized id on success) |
| B5 | `media-renames` (new manifest endpoint) was missing from `ProjectIdRouting.ResourceSegments` — 404 for every real `owner/Name` project id, so client folders would silently never replay renames in production. | Code review during #7 | ✅ fixed |
| B6 | (Not a bug — noted) `SanitizeSpokenDialogue` expands unknown hyphen compounds ("SNOW-DRIFT" → "SNOW — DRIFT") on clip-script save; deliberate speech-safety behavior, kept. | Test #7 | ℹ by design |
| B7 | Locations page froze on "Loading locations…" forever: `OnProjectChanged` ran `LoadAsync` fire-and-forget with no re-render on completion, so whenever `ActiveProject.Changed` fired around navigation (readiness refresh does, repeatedly) the last render stuck at the loading state despite loaded data. | Test #12 | ✅ fixed (StateHasChanged after reload) |
| B9 | Scene-heading dropdown edits (INT/EXT, location, time-of-day) were SILENTLY DISCARDED on save: `FormatSceneHeading` serializes the raw imported `SceneTitle` verbatim whenever present, so structured edits showed in the UI, never reached the Fountain draft, and reverted on reload. Fixed: changing a structured heading field invalidates `SceneTitle` (parser/Clone assign it last, so imported headings still survive untouched until a real user edit). | Test #18 | ✅ fixed |
| B10 | Session hydration was SILENT (RestoreFromStoredJsonAsync never raised Changed) — pages that checked IsLoggedIn before hydration completed never learned the user signed in; /simple-revoice stuck on its "Sign in" alert while the nav showed @admin. Fixed in AdminSessionService + SimpleRevoice now subscribes and also hydrates the active-project pointer (RefreshFromServerAsync) instead of concluding "no project". | Test #23 | ✅ fixed |
| B8 | (Observation) Screenplay-page scene drag-reorder rewrites the fountain draft only — it does NOT renumber clip files/blueprint like the Film page's reorder does. Harmless pre-shot-plan; after a plan exists it desyncs plan and files until a replan. Consider routing it through ProjectStore.ReorderScenes once a plan exists. | Test #13 | ⚠ documented |

## Still to cover — NONE. Campaign complete 2026-08-20.

Remaining product calls for the user: B2 (visibility "Open" mode vs one Public option) and
B8 (route Screenplay-page scene reorder through ProjectStore.ReorderScenes once a plan exists).
