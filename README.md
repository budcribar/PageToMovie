# PageToMovie (Film Studio)

Turn a book or screenplay into a film: **Book → Estimate → Film**.

Pick a story, get a full screenplay (the **max master**), then cut it to 120 minutes or keep every chapter. Forkable titles share that master — the next person trims or films; they do not adapt Homer again.

**One process.** The API hosts the UI. You do not run a separate Blazor site.

## What you do

| Path | Who | Flow |
|------|-----|------|
| **Easy Start** | First-time / voice | Pick a public story → record one sample → hear the narrator as you |
| **Full studio** | Making a film | Import book or Fountain → Estimate (length + $) → Cast & locations → Film → Review |

Estimate is two numbers: **what it will cost** and **what you’ve spent**. Fit length cuts the outline; it does not re-adapt.

## Run

Needs:

- .NET SDK (solution is `net10.0`)
- `XAI_API_KEY` for real Grok chat / image / video (optional fakes for UI soaks)
- Chrome or Edge for stitch, silence trim, and review frames (**ffmpeg.wasm** in the browser — the server never installs ffmpeg)

```powershell
cd host
$env:XAI_API_KEY = "your-key"          # skip if using fakes
$env:PageToMovie__UseFakes = "false"   # "true" = no xAI spend
dotnet run --project PageToMovie.Api
```

Open **http://127.0.0.1:5088**. Health: `GET /health`.

Visual Studio: open `host/PageToMovie.slnx` and start **PageToMovie.Api** only.

`PageToMovie.Web` is the WASM client **published inside the API**. Run it alone only if you are splitting ports on purpose (`EngineApi:BaseUrl` → the API).

Workspace root (`PageToMovie:WorkspaceRoot`) empty → the API uses the repo root. Projects live under `projects/<owner>/<slug>/`.

## Pipeline (short)

```mermaid
flowchart TD
    A["Book / PDF / Fountain"] --> B["Index the book<br/>acts → sequences → scene cards"]
    B --> C["Write screenplay.max<br/>from the index · book by file_id"]
    C --> D["Estimate<br/>pick a length · trim is a view"]
    D --> E["Cast + locations<br/>3 looks · auto-lock used faces"]
    E --> F["Shot plan<br/>classifiers + blueprint"]
    F --> G["Generate clips<br/>Grok Imagine / Veo / Fal"]
    G --> H["Review + stitch in the browser"]
    H --> I["Play / share / fork the master"]
```

1. **Ingest** — text, PDF (vision OCR for picture books), or an existing Fountain.
2. **Max master** — plan a beat sheet, then write the full Fountain. Short books: one pass. Novels: index, then write sequences. Default planning model: **Grok 4.6**.
3. **Estimate** — set target minutes or keep every sequence. Snapshot first; Undo prune restores.
4. **Cast & places** — extract used faces/locations, generate three looks, auto-lock the best. Voice clone or stock TTS.
5. **Shot plan** — book-wide + per-scene classifiers → clip blueprint. Authoritative call list: [`docs/architecture/MODEL_CALL_INVENTORY.md`](docs/architecture/MODEL_CALL_INVENTORY.md).
6. **Film** — catalog-routed video (Grok Imagine, Veo, Fal) with locked plates.
7. **Review / export** — advisory vision review; stitch and play in the browser.

Look / Enrich are optional polish on the master. Fit length only writes the working `screenplay.fountain`.

## Layout

| Path | Role |
|------|------|
| `host/PageToMovie.Api` | **Start this** — REST, SignalR, hosted UI |
| `host/PageToMovie.Web` | Blazor WASM (same origin as the API) |
| `host/PageToMovie.Engine` | Jobs, store, video, files |
| `host/PageToMovie.Adaptation` | Stage‑1: index, write, trim, enrich |
| `projects/` | Per-film fountain, max, index, cast, clips, WIP |
| `prompts/` | Product prompts |
| `docs/` | Architecture and decision flow |
| `host/docs/max-master-adaptation-plan.md` | Full screenplay, cut later |

## Tests

```powershell
cd host
dotnet test PageToMovie.Tests          # default — no paid API calls

# Paid LiveApi tests are opt-in only
$env:PAGETOMOVIE_LIVE_API_TESTS = "1"
$env:XAI_API_KEY = "xai-..."
dotnet test PageToMovie.Tests --filter "Category=LiveApi"
```

Playwright pilot (against a running API): `host/playwright/README.md`.

## Docs

| Doc | Topic |
|-----|--------|
| [`host/README.md`](host/README.md) | API routes, SignalR, YouTube, LoadSim |
| [`docs/studio-decision-flow.md`](docs/studio-decision-flow.md) | Book → Estimate → Film |
| [`host/docs/max-master-adaptation-plan.md`](host/docs/max-master-adaptation-plan.md) | Index, write, trim, share |
| [`docs/architecture/MODEL_CALL_INVENTORY.md`](docs/architecture/MODEL_CALL_INVENTORY.md) | Every model call |
| [`prompts/README.md`](prompts/README.md) | Prompts and schemas |
| [`AGENTS.md`](AGENTS.md) | North star for contributors |

## Config

- Workspace: `PageToMovie:WorkspaceRoot` (empty = repo root).
- Fakes: `PageToMovie:UseFakes` / `PageToMovie_USE_FAKES=true`.
- Models: `host/PageToMovie.Core/config/models_catalog.json` is the only list of providers and models.
- Auth: `PageToMovie:Auth` in Api `appsettings`.
