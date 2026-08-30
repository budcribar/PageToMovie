# Dead code inventory (list only — no deletions)

Scan of `master` at `cac09586` (2026-08-30). **No files were deleted or refactored.**

Method: word-boundary type-name scan across 1,458 `host/**/*.cs|*.razor|*.js` files (excluding `bin`/`obj`), plus targeted greps for loaders, `@page` routes, Razor tags, and prompt embeds. Unused-import noise is omitted.

Confidence: **high** = no product/test references except the definition (or only a comment). **medium** = tests-of-self, reflection/JS, or compile-excluded. **low** = looks unused but might be JSON/API/Razor-bound.

---

## clip_gen_rules.txt (requested item 6)

| Path | Symbol | Why | Conf. |
|------|--------|-----|-------|
| `prompts/clip_gen_rules.txt` | file (5-line stub) | Retired (PR 311). Body is comments only: “not composed into clip generate prompts.” | high |
| `host/PageToMovie.Engine/PageToMovie.Engine.csproj` | `EmbeddedResource` | Still embedded as `PageToMovie.Prompts.clip_gen_rules.txt`. | high |
| `ClipVideoPromptBuilder.TryLoadClipGenRules` / `PromptBodyFromClipGenRules` / `AppendHouseRules` | loaders | Still **read** on every clip prompt. `PromptBodyFromClipGenRules` strips `#` lines, so the stub yields empty and is not appended. Product generate path is a no-op; tests still assert the stub is embedded and has no house bullets. | high |

Nothing else loads this file. Stage 1 / Adaptation embeds do not include it. `FilmJobService.ApplyProjectRulesToPromptAsync` comments that the global file is retired; project `project_rules.json` is the live house-rule path.

---

## Engine

### Sonar “pseudo-enum → C# enum” dumps (never wired)

Landed in `b71a81da` (“replace 300 domain pseudo-enums with strongly-typed C# enums”) and later Sonar packs. Each file is enums + `ToApiString`/`Parse*` helpers. **No call sites outside the defining file.**

| Path | Lines | Types | Conf. |
|------|------:|------:|-------|
| `host/PageToMovie.Engine/CinematicCameraEnums.cs` | 353 | 11 | high |
| `host/PageToMovie.Engine/PromptAndCameraEnums.cs` | 570 | 21 | high |
| `host/PageToMovie.Engine/VideoFfmpegEnums.cs` | 662 | 21 | high |
| `host/PageToMovie.Engine/VideoFfmpegExtendedEnums.cs` | 648 | 21 | high |
| `host/PageToMovie.Engine/VoiceAndAudioEnums.cs` | 574 | 21 | high |
| `host/PageToMovie.Engine/VoiceAndMusicExtendedEnums.cs` | 653 | 21 | high |

### Partially unused enum files (keep the live members)

| Path | Unused symbols | Why / keep | Conf. |
|------|----------------|------------|-------|
| `EngineLayerEnums.cs` | 29 of 37, e.g. `JobPriorityLevel`, `WorkerPoolName`, `BeatActionCategory` | Live only as return types of unread `CameraDirective` computed properties (`ShotAngleType`, `CameraLensSpec`, `CameraMovementPattern`, `LightingStyleType`) and as fields on unused `UserMediaStorage` (`StorageTierKind`, `VideoResolutionPreset`, `ExportQualityLevel`). | high |
| `PipelineEngineEnums.cs` | `SubtitlePosition`, `FfmpegFilterKind`, `AudioMixingMode`, `VoiceCloneEngine`, `ImageGenEngine`, `VideoGenEngine`, `AudioCodec`, `VideoCodec`, `Stage2JobType` | Live: `LightingCondition`, `CameraAngle` (`CameraDirective`), `CacheInvalidationReason` (`SceneListCache`). | high |
| `MediaEngineEnums.cs` | `MusicTempo`, `MusicMood`, `ExportQualityPreset`, `SubtitleFormat`, `ScriptDocumentFormat` | Live: `AspectRatio`, `CameraLens`, `CameraMovementKind`, `PacingMood`, `NotificationSeverity`; `VideoResolution`/`StorageTier` only via unused `UserMediaStorage`. | high |

### Leftover types / methods (not enums)

| Path | Symbol | Why | Conf. |
|------|--------|-----|-------|
| `host/PageToMovie.Engine/Stage1PromptPack.cs` | `Stage1PromptPack` | Entire file unused. Stage 1 loads prompts via `AdaptationPromptPack`. | high |
| `host/PageToMovie.Engine/UserMediaStorage.cs` | `UserMediaStorage`, `UserMediaStorageEnumConversions` | Nobody constructs it. Exists to “use” unused storage/resolution enums. | high |
| `host/PageToMovie.Engine/ActionCameraOverheadLedger.cs` | `MeasuredTimingEntry` | Record declared; no references. Ledger class itself is live. | high |
| same | `CompositeTimingEntry` | Record declared; no references. | high |
| `CameraDirectorClassifier.cs` (`CameraDirective`) | `.ShotAngleType`, `.LensSpecType`, `.MovementPattern`, `.LightingStyle` | Computed properties; no readers anywhere. | high |
| `FilmJobService.cs` | `EnsureClipDurationSidecarAsync` | Private; zero callers. `host/docs/issues/issue-23` already notes this leftover from the old cost-duration flow. | high |
| `YouTubeAuthService.cs` | `ConsumeState` | Legacy wrapper; only `TryConsumeState` is called (`YouTubeEndpoints`). | high |
| `DemoCatalogService.cs` | `MaxPendingPerUser` | Comment: “Legacy cap (no longer used for admin review queues).” Constant never read. | high |

---

## Web

### Unused enum dumps

| Path | Lines | Unused | Why | Conf. |
|------|------:|--------|-----|-------|
| `host/PageToMovie.Web/WasmClientAndInfraEnums.cs` | 1133 | all 31 | DNS/serverless/DR/PWA/WebGL enums; no readers. | high |
| `host/PageToMovie.Web/BlazorUiEnums.cs` | 743 | all 21 | Toast/tooltip/pagination/player enums; no readers. | high |
| `host/PageToMovie.Web/BlazorUiExtendedEnums.cs` | 414 | all 11 | `*Kind` twins; no readers. | high |
| `host/PageToMovie.Web/WebLayerEnums.cs` | 213 | 8 of 10 | Unused: `NavMenuItem`, `AdminSectionTab`, `ModalDialogSizePreset`, `ToastNotificationSeverity`, `BadgeColorStyle`, `ButtonVariantStyle`, `ProjectSortField`, `ProjectSortDirection`. **Keep** `UiThemeMode` + `WebLayerEnumExtensions` (`ThemeState`). | high |
| `host/PageToMovie.Components/UiEnums.cs` | 20 | `ModalSize`, `BadgeColor` | Shared RCL enums; no readers. | high |

### Unmounted Razor (no `<Tag>` / no `@page`)

| Path | Symbol | Why | Conf. |
|------|--------|-----|-------|
| `PageToMovie.Components/Shared/SceneCard` | `SceneCard` | Never mounted. (Name hits on `CutTextKind.SceneCard` are a different type.) | high |
| `PageToMovie.Components/Shared/ClipEditorPanel` | `ClipEditorPanel` | Never mounted. Scenes uses `Scenes.ClipFieldEditor`. | high |
| `PageToMovie.Components/Shared/AdminDataTable` | `AdminDataTable` | Never mounted. | high |
| `PageToMovie.Components/Shared/CopyToClipboardButton` | `CopyToClipboardButton` | Never mounted. | high |
| `PageToMovie.Components/Shared/MediaPlayerChrome` | `MediaPlayerChrome` | Never mounted. | high |
| `Web/Components/Shared/SceneVersionHistory` | `SceneVersionHistory` | Never mounted. Comment in `ScenesHistory.cs` calls it P3. | high |
| `Web/Components/Pages/ScenesHistory.cs` | `_showInlineSceneHistory`, `HideSceneHistory`, `OnSceneHistoryRestored` | Written only; no Razor bind. Leftover for the unmounted panel. | high |
| `Web/Components/Pages/ClipPromptCompareViewer` | `ClipPromptCompareViewer` | No `@page`, never mounted. Engine still archives prompts “for” it (comments only). | high |

Routed pages **not** listed as dead: `/adaptation/embellish`, `/look`, `/trim` (redirects); `/simple-voice`, `/simple-revoice`, `/studio/share`, `/dialogue-timing`, `/cut`, `/join` (live, just not all on the main nav).

### Flags / JS that can never do work

| Path | Symbol | Why | Conf. |
|------|--------|-----|-------|
| `EngineApiClient.DemoPublishResult.PendingReview` | property | Comment: “Legacy; always false — admin content queue is retired.” API hard-codes `pendingReview = false` (`DemoEndpoints`). WASM still reads `json.pendingReview` in `pagetomovie-export.js`. | medium |
| `wwwroot/js/pagetomovie-ffmpeg.js` | `concatAudioToBytesAsync`, `stripVideoAudioAsync` | No C# or JS callers. | high |
| same | `trimTailAsync`, `trimHeadAsync` | Aliases of keep-last/first; no remaining callers. | high |

`ClientVideoStitchService.ProbeDurationAsync` **is used** (`ScenesPlayback`, `ClientMediaFolderService`). `host/docs/issues/issue-24-unused-probe-duration-wrapper.md` is stale — do not treat as dead.

---

## Cut

| Path | Symbol | Why | Conf. |
|------|--------|-----|-------|
| `CutFolderService.cs` | `BrowserSupportsFolderPickerAsync` | Only C# wrapper of `PageToMovieCut.supportsDirectoryPicker`; no callers. | high |
| `CutComposeService.cs` | `ClearMoviePreview` | No callers. Editor uses `InvalidateMovie()`. | high |
| `CutTimelineLayout.cs` | `TimelineToPx`, `PxToTimeline` | No callers. | high |
| `CutMergeCache.cs` | `IsCacheFileName` | No callers (`IsPictureFileName` / `TryParse*` are live). | high |
| `CutPlayMerge.cs` | `MergeCovers` | No callers (`MergeCoversTimeline` is live). | high |
| `cut.js` | `bindTimeUpdate`, `probeUrlDuration`, `readCurrentTime`, `trimRangeAsync` | No C# or internal JS callers (`trimRangeWithApiAsync` is used). | medium |

`RequiresComposedMusic` on `CutEditor` **is called** (compose / preview gates). Listed under known leftovers as S2325, not as unused.

---

## Adaptation / Fountain / Core / Api / LoadSim / Screenplay / evals

Same Sonar enum-dump pattern as Engine.

| Path | Unused | Why | Conf. |
|------|--------|-----|-------|
| `Adaptation/Analysis/AdaptationDomainEnums.cs` | all 21 | No outside refs. | high |
| `Adaptation/Analysis/AdaptationSyntaxEnums.cs` | all 31 | No outside refs. | high |
| `Adaptation/Analysis/AdaptationLayerEnums.cs` | `AdaptationPromptKind`, `AdaptationReportType`, `BookImportSourceType`, `CharacterWardrobeSeason` | **Keep** `OcrEngineType` + parse helpers (`OcrEngineIdentity` / `BookPrepareService`). | high |
| `Fountain/FountainEnums.cs` | `FountainPageFormat`, `FountainFont` | Entire file unused. | high |
| `Fountain/FountainLayerEnums.cs` | all 8 | Entire file unused. | high |
| `Core/Models/AiModelProviderEnums.cs` | 20 of 21 | `AiProviderId` appears only in a **comment** in `CatalogUpdateProbeService`. | high |
| `Core/Models/AiModelProviderExtendedEnums.cs` | all 21 | No outside refs. | high |
| `Core/Models/BusinessAndMonetizationEnums.cs` | all 21 | No outside refs. | high |
| `Core/Models/StorageAndCacheEnums.cs` | all 21 | No outside refs. | high |
| `Core/Models/StorageAndCacheExtendedEnums.cs` | all 31 | No outside refs. | high |
| `Core/Models/CoreLayerEnums.cs` | all 4 | `ModelEnablementState`, `ProjectStateName`, `UserAccountStatus`, extensions. | high |
| `Core/Models/CorePipelineEnums.cs` | `HttpHeader`, `ContainerType`, `Stage1JobType` | **Keep** `UserRole`, `AnalyticsWindow`. | high |
| `Api/ApiLayerEnums.cs` | all 11 | No outside refs. | high |
| `Api/ProtocolEnums.cs` | all 21 | No outside refs. | high |
| `Api/ProtocolSecurityEnums.cs` | all 21 | No outside refs. | high |
| `Api/SpecializedHttpAndMimeEnums.cs` | types + extensions | Product never uses them; only `SpecializedHttpAndMimeEnumsTests` (tests of themselves). | medium |
| `LoadSim/CiCdAndDevToolsEnums.cs` | all 21 | No outside refs. | high |
| `LoadSim/LoadSimLayerEnums.cs` | all 11 | No outside refs. | high |
| `LoadSim/OperationsAdminEnums.cs` | all 21 | No outside refs. | high |
| `LoadSim/OperationsAdminExtendedEnums.cs` | all 21 | No outside refs. | high |
| `Screenplay/EditorLayerEnums.cs` | all 4 | No outside refs. | high |
| `Screenplay/EditorEnums.cs` | `ScreenplayEditorTab` | **Keep** `OutlineSidebarTab` (editor sidebar). | high |
| `tools/ClassifierBenchmarks/EvalsBenchmarkEnums.cs` | all 21 | No outside refs. | high |
| `tools/ClassifierBenchmarks/EvalsBenchmarkExtendedEnums.cs` | all 21 | No outside refs. | high |
| `tools/ClassifierBenchmarks/MachineLearningPipelineEnums.cs` | all 31 | No outside refs. | high |
| `Core/Billing/ChargePricing.cs` | `ResolveChargeUsd` | Obsolete alias of `DisplayCharge`; no callers. | high |

`AdaptationEnums.cs` (`BookKind` / `TextDensity` / `TextQuality`) and `CoreDomainEnums.cs` are **live** — do not delete.

---

## Prompts (not loaded)

Not in Engine or Adaptation `.csproj` embeds; no `PromptFiles` / `AdaptationPromptPack` read. README still describes some as optional learning appends.

| Path | Why | Conf. |
|------|-----|-------|
| `prompts/adaptation_v16.txt` (476 lines) | Documented as optional learning append; nothing reads it. | high |
| `prompts/shared_rules.txt` | Same. | high |
| `prompts/stage2_shot_planner.txt` | Docs call it operator reference, “not on the product path.” | high |
| `prompts/verifier_clip.txt` | Learning-loop docs mention it; no loader. | high |
| `prompts/compare_json_to_book.txt` | 4-line story-specific audit prompt (names characters from one title); no loader. | high |
| `prompts/stage1_scene_bible.schema.json` | Product test asserts the system prompt does **not** contain this name. No scene-bible prompt exists. | high |
| `prompts/examples/scene_bible_minimal.json`, `clip_plan_minimal.json` | Samples only. | medium |

Live embeds: `book_to_fountain`, `book_to_index`, `fountain_reskin`, `embellish_scene`, `trim_scene`, `fountain_to_cast`, `cast_visual_literalize`, `clip_auto_review`, plus the retired `clip_gen_rules` stub (see above).

---

## Tests

| Path | Symbol | Why | Conf. |
|------|--------|-----|-------|
| `PageToMovie.Tests.csproj` | `<Compile Remove>` `ModelBoundsTests.cs`, `ClipEditRequestTests.cs`, `ClipEditServiceTests.cs` | Files are gone; leftover exclude lines. Backlog P0 #1 still talks about ClipEdit compile breaks. | high |
| same | `<Compile Remove>` `SmartScriptBreakdownTests.cs`, `YouTubeUploadCallbackTests.cs` (listed twice) | Phantom excludes; no source on disk. | high |
| `ProjectLeaseServiceTests.cs` | file | **On disk** but compile-removed. Comment says the test API shape does not match shipped `ProjectLeaseService`. Re-enable or delete in a later PR — not dead product code. | medium |
| `ClipPromptCompareViewerTests.cs` | class | Does not instantiate the component; two string asserts. Only “test” of an unmounted viewer. | medium |
| `SpecializedHttpAndMimeEnumsTests.cs` | class | Tests unused Api enums (keep-if-enums-stay). | medium |

`PageToMovie.Cut.Tests` is in `PageToMovie.Cut.slnx` only — not unused, just not in the main slnx gate.

---

## Docs

| Path | Why | Conf. |
|------|-----|-------|
| `host/docs/issues/issue-24-unused-probe-duration-wrapper.md` | Claims `ProbeDurationAsync` has no callers. **Stale** — used from `ScenesPlayback` / `ClientMediaFolderService`. | high (stale note, not dead code) |
| `docs/ui-dedup-checklist.md`, `docs/ui-test-campaign.md`, `docs/handoff-continuation-clip-planning.md` | Completed working notes; not on `docs/README.md` map (map says treat unlinked as history). | low |
| `docs/archive/` | Already parked; not product code. | n/a |

---

## Known leftovers (already flagged; not new)

| Item | Note |
|------|------|
| S4487 unread fields in `AdminBrowserRenderingSection` | User-requested leave-alone. Current `.razor` **does** read `_loading` / `_error` / `_status` / worker fields; treat remaining S4487 as the known leftover, not a new unused-field find. |
| S2325 `CutEditor.RequiresComposedMusic` | User-requested leave-alone. Property **is** used (compose / preview). S2325 is “could be static,” not unused. |
| S8970 `!` / null-forgiving (`Scenes.razor.cs` Health inject, `ClientVideoStitchService`, `AdminBrowserRenderingSection`, `CutEditor`) | **False positives.** Do not list as dead. Nullable warnings are disabled. |

---

## Intentionally not listed

- Unused `using`s.
- `scripts/*.py` one-offs (`scripts/README.md`: not the product path).
- `host/tools/ScreenplayEditorApp` (standalone prototype with its own tests).
- Live routed pages missing from the main nav.
- Eval CLIs and `host/evals/` gold/history.
- Characters UI (not searched for cleanup and not touched).

---

## Headline

The real pile is the unused Sonar enum dumps (~20k lines across Engine / Web / Core / Api / Adaptation / Fountain / LoadSim / evals). After that: `Stage1PromptPack`, `UserMediaStorage`, a handful of leftover methods/constants, six unmounted shared Razor components, `ClipPromptCompareViewer`, retired prompt files that nothing loads, and the `clip_gen_rules.txt` stub that is still embedded and still read (but stripped to empty).

Known Sonar leftovers (S4487 / S2325 / S8970) are **not** the interesting dead code.
