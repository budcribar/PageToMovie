# Dead code inventory

Cleanup landed on `cursor/dead-code-inventory-f956` (PR 342). High-confidence items below are **done**. Remaining medium/low / leave-alones are listed so they are not re-scanned as new finds.

Scan origin: `master` @ `cac09586`. Method: word-boundary type-name scan + prompt/embed/Razor-tag greps.

---

## Done (deleted this PR)

### Unused enum dumps (full files)

Engine: `CinematicCameraEnums`, `PromptAndCameraEnums`, `VideoFfmpegEnums`, `VideoFfmpegExtendedEnums`, `VoiceAndAudioEnums`, `VoiceAndMusicExtendedEnums`, `EngineLayerEnums` (after CameraDirective/UserMediaStorage removal, no remaining readers).

Web: `WasmClientAndInfraEnums`, `BlazorUiEnums`, `BlazorUiExtendedEnums`, `Components/UiEnums`.

Adaptation / Fountain / Core / Api / LoadSim / Screenplay / evals: `AdaptationDomainEnums`, `AdaptationSyntaxEnums`, `FountainEnums`, `FountainLayerEnums`, `AiModelProvider*`, `BusinessAndMonetizationEnums`, `StorageAndCache*`, `CoreLayerEnums`, `ApiLayerEnums`, `Protocol*`, LoadSim dump files (`CiCdAndDevToolsEnums`, `LoadSimLayerEnums`, `OperationsAdmin*`), `EditorLayerEnums`, ClassifierBenchmarks enum files.

`LoadSimScenario` **kept** as a slim `LoadSimScenario.cs` (the deleted dump file had `global using LoadSimScenario = …` that CLI/`SimOptions` still need).

`CatalogUpdateProbeService` comment kept (rewritten to catalog provider strings; no `AiProviderId` type).

### Partial enum files (unused members removed)

| File | Kept |
|------|------|
| `PipelineEngineEnums.cs` | `LightingCondition`, `CameraAngle`, `CacheInvalidationReason` |
| `MediaEngineEnums.cs` | `AspectRatio`, `CameraLens`, `CameraMovementKind`, `PacingMood`, `NotificationSeverity` + live parse/ToApi helpers |
| `WebLayerEnums.cs` | `UiThemeMode` + `ParseUiThemeMode` / `ToCssTheme` |
| `AdaptationLayerEnums.cs` | `OcrEngineType` + parse / `ToApiString` |
| `CorePipelineEnums.cs` | `UserRole`, `AnalyticsWindow` |
| `EditorEnums.cs` | `OutlineSidebarTab` (used) and `ScreenplayEditorTab` (kept per Bud) |

`VideoResolution` / `StorageTier` dropped from `MediaEngineEnums` with `UserMediaStorage` (only consumer).

### Engine types / methods

- `Stage1PromptPack.cs`
- `UserMediaStorage.cs`
- `MeasuredTimingEntry` (`CompositeTimingEntry` **kept** — used inside `ActionCameraOverheadLedger`)
- `CameraDirective` computed props with no readers
- `EnsureClipDurationSidecarAsync`
- `YouTubeAuthService.ConsumeState`
- `DemoCatalogService.MaxPendingPerUser`
- `ChargePricing.ResolveChargeUsd` (verified: only definition; no callers)

### Web unmounted Razor

`SceneCard`, `ClipEditorPanel`, `AdminDataTable`, `CopyToClipboardButton`, `MediaPlayerChrome`, `ClipPromptCompareViewer` (+ `ClipPromptCompareViewerTests`).

**Named snapshots kept** (Bud, 2026-08-30 — unmounted today, may be wired later). Do not delete:

- `Web/Components/Shared/SceneVersionHistory.razor` + `.razor.cs`
- `ScenesHistory`: `_showInlineSceneHistory`, `HideSceneHistory`, `OnSceneHistoryRestored` (and the P3 comment)
- `Engine/Collaboration/SceneVersionStore.cs`
- GitVersionEndpoints: GET/POST `/api/projects/{projectId}/scenes/{sceneKey}/versions` and POST `.../versions/{versionId}/restore`, plus `SceneVersionStore` DI
- `Tests/SceneVersionStoreTests.cs`

Live git-commit History modal is unchanged: `Scenes.SceneHistory`, `OpenSceneHistoryAsync`, `_showSceneHistory`, `RevertSceneToVersionAsync`.

### Cut

`BrowserSupportsFolderPickerAsync`, `ClearMoviePreview`, `TimelineToPx` / `PxToTimeline`, `IsCacheFileName`, `MergeCovers`.

**Not deleted:** `RequiresComposedMusic`, `ProbeDurationAsync`.

### Prompts

Deleted (not loaded): `adaptation_v16.txt`, `shared_rules.txt`, `stage2_shot_planner.txt`, `verifier_clip.txt`, `compare_json_to_book.txt`, `stage1_scene_bible.schema.json`.

`clip_gen_rules.txt` stub + embed + `TryLoadClipGenRules` / `PromptBodyFromClipGenRules` / `AppendHouseRules` removed. Tests that only asserted the stub was embedded/empty were dropped or narrowed to `clip_auto_review`. Live house rules remain `project_rules.json`.

### JS / tests / docs

- `pagetomovie-ffmpeg.js`: `concatAudioToBytesAsync`, `stripVideoAudioAsync`, `trimTailAsync`, `trimHeadAsync`
- Phantom `<Compile Remove>` for gone test files
- `ProjectLeaseServiceTests.cs` **restored** (string ctor + `ListAsync` / `ReleaseAllForUserAsync` now match)
- Stale `host/docs/issues/issue-24-unused-probe-duration-wrapper.md`

---

## Skipped (medium / low / leave-alones)

| Item | Why skipped |
|------|-------------|
| `DemoPublishResult.PendingReview` | JS still reads `pendingReview` |
| `cut.js` `bindTimeUpdate`, `probeUrlDuration`, `readCurrentTime`, `trimRangeAsync` | Medium JS; no C# callers but may be console/legacy |
| `SpecializedHttpAndMimeEnums` + self-tests | Tests-of-self only |
| Unlinked completed docs (`ui-dedup-checklist`, `ui-test-campaign`, `handoff-continuation`) | Low |
| `prompts/examples/scene_bible_minimal.json`, `clip_plan_minimal.json` | Samples only (medium) |
| S8970 `!` / `AdminBrowserRenderingSection` S4487 / `CutEditor.RequiresComposedMusic` | Known leave-alones; not unused |
| Characters UI | Protected page |
| `ProbeDurationAsync` | Used (`ScenesPlayback`, `ClientMediaFolderService`) |
| `CompositeTimingEntry` | Same-file use in `ActionCameraOverheadLedger` (inventory “outside refs” was a false high) |
| Named snapshot stack (`SceneVersionHistory`, `SceneVersionStore`, version routes, `ScenesHistory` snapshot helpers) | Keep for later named snapshots; git History modal stays live |
| `LoadSimScenario` | Slim enum file kept after deleting the dump that aliased it |

---

## Intentionally not listed

Unused usings, `scripts/*.py` one-offs, `ScreenplayEditorApp`, live routes missing from nav, eval CLIs.
