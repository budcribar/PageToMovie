# Master Integration Plan: Screenplay Editor ➔ PageToMovie Web, State Machine & Live Engine

This document provides complete, self-contained instructions for integrating the standalone **Structured Screenplay Editor** (`host/tools/ScreenplayEditorApp`) into the primary `PageToMovie.Web` application, connecting it to the `ProjectStore` SQLite database, `StudioStateMachine`, `PageToMovie.Adaptation`, and `PageToMovie.Engine`.

---

## 1. Executive Summary & North Star Alignment

**Goal:** Provide a single, state-of-the-art **Screenplay Editor Workbench** on `PageToMovie.Web` where users can edit screenplay text, manage locations with visual descriptions, lock character voice clones and visual image reference styles, sign off on the screenplay ("Looks Good — Continue"), and trigger Stage-2 Shot Planning (`blueprint.json`) and clip generation.

### Durable Rules to Enforce During Implementation
1. **Generalize for Any Book / Any Cast**: Mechanisms must work for arbitrary stories without title-specific branching in product code.
2. **Catalog SSoT**: All models, providers, and voice/video capabilities resolve through `models_catalog.json` / `SupportedModelCatalog`. No hardcoded provider/model strings in C#.
3. **Strict Code-Behind & Line Count Caps**: Razor markup files must stay under 250 lines with companion `.razor.cs` code-behind files.
4. **Single Source of Truth**: Reuse existing helpers (`FountainFormatter`, `EnumExtensions`, `AdaptationService`). Never clone logic across call sites.

---

## 2. Component Inventory & Source Files

The standalone editor is built and 100% verified in `host/tools/ScreenplayEditorApp/`:

| Component | Path | Responsibility |
| :--- | :--- | :--- |
| **Main Shell** | `Components/ScreenplayEditor.razor` & `.cs` | Two-column Master-Detail shell, header menu, view modes (`metadata`, `scene`, `credits`). |
| **Outline Navigator** | `Components/ScreenplayEditor.OutlineSidebar.razor` & `.cs` | Shrinkable navigation sidebar, drag-drop scene reordering, select-all checkboxes, play preview modal, delete confirm modal. |
| **Scene Card** | `Components/ScreenplayEditor.SceneCard.razor` & `.cs` | 1-line scene heading (`INT.`, location dropdown, `DAY`/`NIGHT`), beat list container, bottom-left `[➕ Add Beat... ▾]` selector. |
| **Beat Editor** | `Components/ScreenplayEditor.BeatEditor.razor` & `.cs` | 1-line Action, Dialogue, and Transition beat rows; character speaker dropdown; `V.O.`/`O.S.`/`CONT'D` jargon tooltips; drag-drop beat handles (`⋮⋮`). |
| **Title Metadata** | `Components/ScreenplayEditor.MetadataHeader.razor` & `.cs` | Title, Author, Credit, Source, Draft Date fields. |
| **Final Credits** | `Components/ScreenplayEditor.CreditsHeader.razor` & `.cs` | Director, Executive Producer, Cast & Voice credits, Music attribution, Copyright notice. |
| **Location Manager** | `Components/ScreenplayEditor.LocationModal.razor` & `.cs` | Auto-discovers locations, visual/environmental description textareas, delete location. |
| **Character Manager** | `Components/ScreenplayEditor.CharacterModal.razor` & `.cs` | Auto-discovers characters (`NARRATOR`, `BUSTER`), Voice Clone Lock (elevenlabs/suno), Visual Image Lock prompt & wardrobe controls. |
| **Fountain Exchange** | `Components/ScreenplayEditor.FountainModal.razor` & `.cs` | 1-Click instant file import, Fountain text export preview & download. |
| **Data Models** | `Models/ScreenplayModel.cs` & `FountainFormatter.cs` | Strongly-typed enums (`BeatType`, `SceneEnvironment`, `TimeOfDay`, `SpeakerExtension`, `TransitionPreset`), loss-less Fountain parser & serializer. |

---

## 3. Step-by-Step Integration Tasks

### Step 1: Web Project Integration (`PageToMovie.Web`)
1. Reference `ScreenplayEditorApp.csproj` in `PageToMovie.Web.csproj` (or copy/share components under `PageToMovie.Components` / `PageToMovie.Web/Components/Pages/`).
2. Add page route `/adaptation/{ProjectId}/editor` or integrate into `AdaptationScreenplay.razor` / `AdaptationScreenplay.Editor.cs`.

### Step 2: Data Hydration & SQLite `ProjectStore` Bridge
1. In `AdaptationScreenplay.Editor.cs`, load `ScreenplayModel` when opening a project:
   ```csharp
   var project = await ProjectStore.GetProjectAsync(ProjectId);
   var model = FountainFormatter.Parse(project.FountainText);
   ```
2. Hydrate Location Descriptions and Character Cast Packages (`characters.json`) into `model.LocationProfiles` and `model.CharacterProfiles`.
3. Auto-save edits back to SQLite on change (`project.FountainText = FountainFormatter.ToFountain(model)`).

### Step 3: Operator Sign-off & State Machine Transition
1. In `ScreenplayEditor.razor` header, include the primary **"Looks Good — Continue"** sign-off button.
2. Clicking sign-off invokes `ProjectStore.UpdateStateAsync(ProjectId, StudioState.ScreenplayApproved)`.
3. Transition `StudioStateMachine` navigation steps from Step 2 (Screenplay) ➔ Step 3 (Shot Plan).

### Step 4: Shot Plan Rebuild (`blueprint.json`)
1. On screenplay sign-off (or manual rebuild), call:
   ```csharp
   await AdaptationService.BuildShotPlanAsync(ProjectId);
   ```
2. `AdaptationService` calculates natural runtime, clip durations, and model clip bounds, generating `blueprint.json`.
3. `SceneDependencyGraph` updates SHA-256 scene hashes so modifying Scene X flags ONLY Scene X as `Stale`, preserving cached clips for unchanged scenes.

### Step 5: Cast Package & Location Sidecar Persistence
1. Sync `model.CharacterProfiles` to `characters.json` (`CharacterSummary` sidecar records).
2. Ensure voice clone selections feed into TTS voice generation and visual lock prompts feed into Stage-2 portrait & clip prompts.

---

## 4. Verification & Regression Workflow

Before pushing any changes to `master`, execute the following verification steps:

```powershell
# 1. Build Standalone Editor & Test Suite
dotnet build host/tools/ScreenplayEditorApp/ScreenplayEditorApp.csproj
dotnet test host/tools/ScreenplayEditorApp/ScreenplayEditorApp.Tests/ScreenplayEditorApp.Tests.csproj

# 2. Build Primary Web Solution
dotnet build host/PageToMovie.Web/PageToMovie.Web.csproj -c Release

# 3. Run Non-UI Offline Test Suite
dotnet test host/PageToMovie.Tests/PageToMovie.Tests.csproj --filter "FullyQualifiedName!~LiveApi"

# 4. Commit and Push to Master
git add .
git commit -m "feat(web): integrate structured screenplay editor into PageToMovie state machine and engine"
git pull --rebase origin master
git push origin master
```

---

## 5. Checklist Matrix

- [ ] **Step 1**: Reference Screenplay Editor components in `PageToMovie.Web`.
- [ ] **Step 2**: Hydrate `ScreenplayModel` from SQLite `ProjectStore`.
- [ ] **Step 3**: Wire **"Looks Good — Continue"** button to transition `StudioStateMachine` to `ScreenplayApproved`.
- [ ] **Step 4**: Trigger `AdaptationService.BuildShotPlanAsync(ProjectId)` to generate `blueprint.json`.
- [ ] **Step 5**: Persist character voice/image locks to `characters.json`.
- [ ] **Step 6**: Confirm 100% test pass on `ScreenplayEditorApp.Tests` and `PageToMovie.Tests`.
- [ ] **Step 7**: Commit and push to `origin/master`.
