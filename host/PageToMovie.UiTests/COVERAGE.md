# UI test coverage matrix

End-to-end browser tests (C# Microsoft.Playwright + xUnit) against the fakes API
(`PageToMovie__UseFakes=true`, auto login-bypass). Run serially:

```bash
cd host && dotnet test PageToMovie.UiTests
```

Fixtures (AppFixture.cs): `ui` (shared repo workspace, port 5088), `ui-pipeline`
(PipelineFixture: fresh-project pipeline runs), `ui-home` (HomeFixture: empty temp workspace,
port 5084 — project create/delete churn isolated), `ui-multiuser-lease`, plus per-class isolated
fixtures. Concurrent suites sharing the repo `projects/` workspace can clobber each other's
active-project pointer — keep runs serial.

Legend: ✅ covered · ◐ partial (smoke / hydrate only) · ❌ gap

## Home / project management

| Feature | Test | Status |
|---|---|---|
| Home renders tagline, project card, process steps | AppShellTests.Home_renders_tagline_projects_and_steps; PageSmokeTests | ✅ |
| Easy-start landing hidden when no timed books; open full studio | PipelineFlow.CreateFreshProjectAsync (implicit); AppShell | ◐ |
| "+ New" modal creates project, selects it, lists it | HomeProjectManagementTests.Create_via_new_modal_selects_the_new_project_and_lists_it | ✅ |
| Picker switch → selection, badge, persists across reload | HomeProjectManagementTests.Picker_switch_updates_selection_and_badge_and_persists | ✅ |
| Delete selected project → dropped from picker, another adopted | HomeFlowTests.Delete_confirm_is_a_modal…; HomeProjectManagementTests.Delete_selected_project_drops_it_from_picker_and_adopts_another | ✅ |
| Delete last project → empty state | HomeProjectManagementTests.Delete_picked_then_last_project_reaches_empty_state | ✅ |
| Delete confirm names the picked project (not stale list-active) | HomeFlowTests.Delete_confirm_is_a_modal_that_names_the_selected_project | ✅ |
| Rename (re-slug) moves selection to new id, persists | HomeFlowTests.Rename_project…; HomeProjectManagementTests.Rename_with_new_slug_moves_selection_to_the_new_id_and_persists | ✅ |
| Rename to existing name → error, selection kept | HomeProjectManagementTests.Rename_to_an_existing_project_name_shows_error_and_keeps_selection | ✅ |
| Visibility toggle → badge, persists | HomeProjectManagementTests.Visibility_change_updates_badge_and_persists | ✅ |
| Manage / Import / Rename panels toggle & close each other | HomeProjectManagementTests.Manage_import_and_rename_panels_toggle_and_close_each_other | ✅ |
| Picking another project clears message banner (slice → page sync) | HomeProjectManagementTests.Picking_another_project_clears_the_home_message_banner | ✅ |
| Checkpoint save/revert round-trips; count in Manage header | HomeFlowTests.Checkpoint_save_and_revert…; HomeProjectManagementTests.Saving_a_checkpoint_bumps_the_count_in_the_manage_header | ✅ |
| Import project zip → in picker, persists | HomeProjectManagementTests.Import_project_zip_adds_it_to_the_picker_and_persists | ✅ |
| Fork / "open story" from easy-start | — | ❌ (needs a timing-complete forkable story in fakes workspace) |
| Server outage banner + recovery re-hydrates Home | ServerHealthBannerTests | ✅ |
| Account menu, left nav between core pages | InteractionTests.Account_menu_opens_and_closes; AppShellTests.Left_nav_navigates_between_core_pages | ✅ |
| Login / signup / admin login / view-as-user | AuthUiTests; UserModeTests.View_as_user_hides_admin_surfaces | ✅ |

## Adaptation (Book → Screenplay → Shot plan)

| Feature | Test | Status |
|---|---|---|
| Import fountain → auto-nav to screenplay | PipelineFlow.ImportFountainAsync (used by every pipeline test) | ✅ |
| Import book text (non-fountain) → Stage 1 with fake chat | — | ❌ |
| Film length control (natural / target, "Use estimate") | — | ❌ |
| Screenplay title/author chips | ScreenplayTitleAuthorChipTests | ✅ |
| Screenplay approve / sign-off | PipelineFlow.SignOffScreenplayAsync | ✅ |
| Screenplay beat editor (edit line, multi-line dialogue shows spaces) | — | ❌ |
| Look / Embellish / Trim / Fit-length pages | — | ❌ |
| Shot plan build/rebuild + heading + counts | PipelineFlow.BuildShotPlanAsync | ✅ |
| Nav gating step by step (blocked reasons) | PipelineNavGatingTests.Nav_gates_open_step_by_step_and_each_step_page_renders | ✅ |
| Style override ("Use this look anyway") | StyleOverrideTests | ✅ |

## Characters / cast

| Feature | Test | Status |
|---|---|---|
| Cast heading / list renders; varied cast display | PageSmokeTests; CastDisplayVariationTests | ✅ |
| Select character → look panel opens (slice → page sync) | CharactersFlowTests.Selecting_a_character_and_uploading_a_photo_sets_its_look | ✅ |
| Upload photo → thumbnail | CharactersFlowTests.Selecting_a_character_and_uploading_a_photo_sets_its_look | ✅ |
| Generate looks from description → pick grid | CharactersFlowTests.Generate_looks_from_description_shows_the_pick_grid | ✅ |
| Voice section for speaking character | CharactersFlowTests.Speaking_character_shows_a_voice_section | ✅ |
| Book pictures route, age variants, lock/unlock look | — | ❌ |
| Locations page | AdminAndToolPagesSmokeTests (hydrate) | ◐ |

## Scenes / clips

| Feature | Test | Status |
|---|---|---|
| Toolbar controls, filters panel, jargon-free user mode | ScenesTests; UserModeJargonTests | ✅ |
| Open scene → clip detail / select bar (slice → page sync) | ScenesPipelineTests.Opening_a_scene_shows_its_clip_select_bar; InteractionTests.Opening_a_scene_reveals_its_clip_detail | ✅ |
| Generate batch (fakes) → clips on disk | ClipGenerationTests; PipelineFlow.GenerateClipsAsync | ✅ |
| Regenerate selected scenes (scoped replan) | ScenesRegenerateSelectedTests | ✅ |
| Select-all → generate button label; delete scene modal | InteractionTests | ✅ |
| Credits scene auto-insert / re-add | ScenesPipelineTests | ✅ |
| AI video edit → new take | ScenesPipelineTests.AI_edit_button_opens_prompt_and_saves_a_new_take_on_completion | ✅ |
| Music scoring menu / compare | — | ❌ |
| Clip field editor save (prompt/dialogue) | — | ❌ |
| Clip versions / compare viewer | — | ❌ |
| Capability gating (video off → setup link) | CapabilityGatingTests | ✅ |
| Job resume after reload | JobResumeUiTests | ✅ |
| Multi-user lease | MultiUserLeaseUiTests | ✅ |

## Review / Estimate / Configuration / tools

| Feature | Test | Status |
|---|---|---|
| Review tab strip; approve clip & scene checklist; play/share tabs | ReviewFlowTests; PageSmokeTests | ✅ |
| Estimate (/cost) renders; scale for target | PageSmokeTests; PageDepthTests | ◐ |
| Cost breakdown (/cost/breakdown), account costs | AdminAndToolPagesSmokeTests | ◐ |
| Configuration coverage rows; ?focus deep link opens key panel; section test ids | ConfigurationFlowTests | ✅ |
| Dialogue timing, simple voice / revoice, about, locations | AdminAndToolPagesSmokeTests.Tool_page_hydrates_and_shows_its_key_control | ◐ |
| Admin pages (admin, ai-calls, generation-errors, users, config, demos, book-cache, learning, models-catalog) | AdminAndToolPagesSmokeTests | ◐ |
| Demo gallery | PageSmokeTests | ◐ |
| Full pipeline E2E (fresh project → generated clips) | PipelineE2ETests; PipelineFlow.RunToGeneratedClipsAsync | ✅ |

## Known issues

- Rename_project (HomeFlowTests) was failing on master until the re-slug active-pointer fix
  (ProjectEndpoints rename + HomeProjects.ConfirmRenameAsync).
- HomeFlowTests.Checkpoint_save_and_revert is timing-flaky when run right after the delete test
  in the shared workspace.
