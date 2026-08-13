# PageToMovie (.NET solution)

Visual Studio / `dotnet` solution: **one host** — REST + SignalR + the Blazor WASM UI.  
**No Python.** **Do not run `PageToMovie.Web` as a second site** unless you are deliberately splitting ports.

```text
host/
  PageToMovie.slnx          # open this in Visual Studio
  PageToMovie.Api/          # start this — :5088, serves UI + /api + /hubs/jobs
  PageToMovie.Web/          # WASM client (published into Api)
  PageToMovie.Engine/       # project store + jobs
  PageToMovie.Adaptation/   # Stage‑1 index / write / trim / enrich
  PageToMovie.Core/         # models, options, catalog
  PageToMovie.Fakes/        # fake clients + fixtures
  PageToMovie.LoadSim/      # concurrent virtual-user load client
  PageToMovie.Tests/        # unit tests
  docs/                     # engine issue notes only — product docs are ../docs/
```

Product flow and north star: repo-root [README.md](../README.md) and [AGENTS.md](../AGENTS.md).

## Run (one process)

```powershell
cd host
$env:XAI_API_KEY = "your-key"        # required for real Stage 1 / images / video
# $env:PageToMovie__UseFakes = "true"  # optional — no xAI spend
dotnet run --project PageToMovie.Api
# http://127.0.0.1:5088
# GET /health
# SignalR: /hubs/jobs
```

Visual Studio: open `host/PageToMovie.slnx`, start **PageToMovie.Api** only (launch profile `http`).

All video stitch / silence-trim / auto-review frame sampling is in the browser (`ffmpeg.wasm`). The API never installs or spawns native `ffmpeg`.

Optional split-port (UI on :5079, API on :5088):

```powershell
$env:EngineApi__BaseUrl = "http://127.0.0.1:5088"
dotnet run --project PageToMovie.Web
```

Same-origin (the usual case) needs no `EngineApi:BaseUrl`.

## REST (Api)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Liveness + workspace |
| GET | `/api/projects` | List / active |
| POST | `/api/projects/{id}/activate` | Switch project |
| GET | `/api/jobs?mine=1` | List my jobs (Phase F: bare `/api/jobs` → **400**) |
| GET | `/api/jobs?projectId=` | List jobs for project |
| GET | `/api/jobs/{jobId}` | Job detail |
| POST | `/api/jobs/{jobId}/cancel` | Cancel one job |
| POST | `/api/jobs/book-prepare` | PDF extract / vision OCR |
| POST | `/api/jobs/stage1` | Stage 1: index + max Fountain + auto-enrich |
| POST | `/api/jobs/stage2` | Stage 2 clip plan |
| POST | `/api/jobs/gen-scene` | Generate scene clips (client may save MP4 to media folder) |
| POST | `/api/jobs/clip-auto-review` | Auto-review one clip (body includes browser-sampled frames) |
| POST | `/api/jobs/voice-preview` | Short voice sample clip (MP4; no server audio extract) |
| POST | `/api/jobs/youtube-upload` | Upload export/WIP to YouTube when configured |
| POST | `/api/jobs/cancel` | Cancel all / active |
| GET | `/api/stage2-status` | Blueprint present? |

## SignalR

Hub: `/hubs/jobs`  
Events: `JobUpdated` (JobSnapshot), `JobLog` (string)

## Config

`PageToMovie.Api/appsettings.json` → `PageToMovie:WorkspaceRoot` (empty = auto-detect repo root).

### YouTube upload (Review screen)

The Review screen's **Upload to YouTube** button uploads `assets/movie_wip.mp4` via the
YouTube Data API v3 (resumable upload, `youtube.upload` scope). It's off by default — no
button appears until an admin connects a channel. To enable it:

1. In [Google Cloud Console](https://console.cloud.google.com/) → APIs & Services →
   Credentials, create an **OAuth client ID** (Application type: **Web application**), and
   enable the **YouTube Data API v3**.
2. Add an authorized redirect URI matching your Api host, e.g.
   `http://127.0.0.1:5088/api/youtube/oauth2callback`.
3. Set in `PageToMovie.Api/appsettings.json` (or env vars
   `PageToMovie__YouTube__ClientId` / `__ClientSecret` / `__RedirectUri`):
   ```json
   "PageToMovie": {
     "YouTube": {
       "ClientId": "...apps.googleusercontent.com",
       "ClientSecret": "...",
       "RedirectUri": "http://127.0.0.1:5088/api/youtube/oauth2callback"
     }
   }
   ```
4. Sign in as admin, open **Review**, click **Connect YouTube**, and approve access. The
   refresh token is stored under `{workspace}/.PageToMovie/youtube_token/` — one shared
   channel per PageToMovie instance, not per-user.

## LoadSim (Phase E)

```powershell
# Terminal 1 — API with fakes
$env:PageToMovie_USE_FAKES = "true"
dotnet run --project PageToMovie.Api

# Terminal 2 — virtual users
dotnet run --project PageToMovie.LoadSim -- --users 25 --duration 90 --scenario mixed --out loadsim-results.json
```

Uses checked-in **`projects/LoadSimBuster`** (isolated from real Buster). See [`docs/loadsim-soak.md`](../docs/loadsim-soak.md).

## Capability matrix (native C#)

| Feature | Status |
|---------|--------|
| PDF extract + vision OCR + page render | Yes |
| Stage 1 (index + Fountain max master) | Yes |
| Stage 2 clip planner | Yes |
| Multi-ref video + audio prompt build | Yes |
| Character portrait gen / lock | Yes |
| Browser stitch / silence-trim / auto-review frames (ffmpeg.wasm) | Yes |
| Review / edit log / approve | Yes |
| SignalR live UI | Yes |

See the repo-root `README.md` for the supported run path and workspace layout.
