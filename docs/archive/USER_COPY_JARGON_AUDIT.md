# User-facing copy — jargon review table

Scan of Blazor UI strings (labels, hints, alerts, placeholders, status messages).
Please mark **Suggested action** for each row.

| # | Copy (as shown / near-shown) | Jargon found | Suggested action | Where | Your note |
| --- | --- | --- | --- | --- | --- |
| 1 | @(p.HasPersonalKey ? "Replace personal key" : "Paste API key") | API | review | `Pages/Configuration.razor` |  |
| 2 | API keys | API | review | `Shared/StudioProcessStrip.razor` |  |
| 3 | Account &amp; API Keys | API | review | `Pages/Configuration.razor` |  |
| 4 | Connect your AI API keys | API | review | `Pages/Home.razor` |  |
| 5 | Connect your AI API keys (bring your own key) before importing a book | API | review | `Shared/StudioProcessStrip.razor` |  |
| 6 | Sign in required to view or save API keys. | API | review | `Pages/Configuration.razor` |  |
| 7 | 🔑 Account &amp; API keys (Global Services) | API | review | `Pages/Configuration.razor` |  |
| 8 | Settings — API keys, models, media folder | API, media folder | rewrite — say film/clips/music, not formats | `Layout/NavMenu.razor` |  |
| 9 | Server not reachable. Start PageToMovie.Api, then reload this page. | Api | review | `Pages/Home.razor` |  |
| 10 | Server env key present on host (not used for your jobs in BYOK mode). | BYOK, env key | review | `Pages/Configuration.razor` |  |
| 11 | Evaluates dialogue &amp; lip sync (Google Gemini natively analyzes MP4 video files). | Gemini, MP4 | rewrite — say film/clips/music, not formats | `Pages/Configuration.razor` |  |
| 12 | Generates MP4 video clips from prompts and character plates. | MP4 | rewrite — say film/clips/music, not formats | `Pages/Configuration.razor` |  |
| 13 | Select a folder on your computer to save MP4 video clips directly to disk. | MP4 | rewrite — say film/clips/music, not formats | `Pages/Configuration.razor` |  |
| 14 | Local Computer Media Folder | Media Folder | rewrite — say film/clips/music, not formats | `Pages/Configuration.razor` |  |
| 15 | 📁 Connect Local Media Folder | Media Folder | rewrite — say film/clips/music, not formats | `Pages/Configuration.razor` |  |
| 16 | 📁 Project Storage &amp; Local Media Folder | Media Folder | rewrite — say film/clips/music, not formats | `Pages/Configuration.razor` |  |
| 17 | Book page OCR, cast-on-image classification, and frame inspection. | OCR | rewrite or strip | `Pages/Configuration.razor` |  |
| 18 | Image Vision &amp; OCR | OCR | rewrite or strip | `Pages/Configuration.razor` |  |
| 19 | Image Vision / OCR | OCR | rewrite or strip | `Pages/Configuration.razor` |  |
| 20 | 🌐 Provider CDN | Provider | rewrite — product capability, not vendor | `Pages/ContributionReview.razor` |  |
| 21 | SignalR: {hex.Message} | SignalR | admin-only / strip from product | `Pages/Characters.razor` |  |
| 22 | YouTube channel connected | YouTube channel | keep if user action; strip if plumbing | `Pages/Review.razor` |  |
| 23 | YouTube channel disconnected | YouTube channel | keep if user action; strip if plumbing | `Pages/Review.razor` |  |
| 24 | No providers listed from the models catalog. Check that | catalog | review | `Pages/Configuration.razor` |  |
| 25 | Planning / quality chat models also have per-token catalog rates used in telemetry; video spend d | catalog, token | review | `Pages/Configuration.razor` |  |
| 26 | Planning / quality chat models also have per-token catalog rates used in telemetry; video spend dominates most film budgets. | catalog, token | review | `Pages/Configuration.razor` |  |
| 27 | Creating end credits video — it will be saved to your connected media folder. | media folder | rewrite — say film/clips/music, not formats | `Pages/Review.razor` |  |
| 28 | Local media folder full path updated. | media folder | rewrite — say film/clips/music, not formats | `Pages/Configuration.razor` |  |
| 29 | No AI provider connected yet — finish | provider | rewrite — product capability, not vendor | `Pages/Home.razor` |  |
| 30 | Settings and provider options for your film projects. | provider | rewrite — product capability, not vendor | `Pages/Configuration.razor` |  |
| 31 | Clips · stitch | stitch | rewrite — say film/clips/music, not formats | `Shared/StudioProcessStrip.razor` |  |
| 32 | Play scene (browser stitch from clips) | stitch | rewrite — say film/clips/music, not formats | `Pages/Review.razor; Pages/Scenes.razor` |  |
| 33 | client stitch | stitch | rewrite — say film/clips/music, not formats | `Pages/Review.razor` |  |
| 34 | Missing reset token. Open the link from your email again. | token | review | `Pages/Login.razor` |  |
| 35 | API login returns a JWT (not a browser cookie). Dev: | API | admin-only / strip from product | `Pages/AdminLogin.razor` |  |
| 36 | Cannot reach API: {ex.Message}. Start PageToMovie.Api (port 5088). | API, Api | admin-only / strip from product | `Pages/AdminLogin.razor` |  |
| 37 | Token missing admin role. Check PageToMovie:Auth on the API. | API, Token | admin-only / strip from product | `Pages/AdminLogin.razor` |  |
| 38 | OAuth not configured. Set | OAuth | admin-only / strip from product | `Pages/AdminDemos.razor` |  |
| 39 | YouTube channel | YouTube channel | admin-only / strip from product | `Pages/AdminDemos.razor` |  |
| 40 | YouTube channel connected. | YouTube channel | admin-only / strip from product | `Pages/AdminDemos.razor` |  |
| 41 | YouTube channel disconnected. | YouTube channel | admin-only / strip from product | `Pages/AdminDemos.razor` |  |
| 42 | API status | API | review | `Pages/About.razor` |  |
| 43 | Capacity (from API) | API | review | `Pages/About.razor` |  |
| 44 | Engine API: | API | review | `Pages/About.razor` |  |
| 45 | Unavailable until API is up. | API | review | `Pages/About.razor` |  |
| 46 | API not reachable at {Engine.ApiBaseUrl}. Start PageToMovie.Api. ({ex.Message}) | API, Api | review | `Pages/About.razor` |  |
| 47 | Turn source text into a short film: Blazor WASM UI, REST + SignalR API, Grok generation, and browser-side clip stitch | API, Blazor, Grok, REST, SignalR, WASM, stitch | admin-only / strip from product | `Pages/About.razor` |  |
| 48 | Turn source text into a short film: Blazor WASM UI, REST + SignalR API, Grok generation, and browser-side clip stitch ( | API, Blazor, Grok, REST, SignalR, WASM, stitch | admin-only / strip from product | `Pages/About.razor` |  |
| 49 | Actual Measured MP4 | MP4 | rewrite — say film/clips/music, not formats | `Pages/Scenes.razor` |  |
| 50 | Transcribed Audio Dialogue (Heard on MP4 Track) | MP4 | rewrite — say film/clips/music, not formats | `Pages/Scenes.razor` |  |
| 51 | Prompt Text: | Prompt | review | `Pages/ClipPromptCompareViewer.razor` |  |
| 52 | 🔍 Multi-Version Clip Prompt & Video Comparison | Prompt | review | `Pages/ClipPromptCompareViewer.razor` |  |
| 53 | @* Inline boot styles so the spinner paints before app.css / WASM download. *@ | WASM | admin-only / strip from product | `App.razor` |  |
| 54 | No clips for S{sn:D2} — connect local media folder or generate clips first | media folder | rewrite — say film/clips/music, not formats | `Pages/Scenes.razor` |  |
| 55 | Not available locally — reconnect your media folder to sync it back. | media folder | rewrite — say film/clips/music, not formats | `Pages/Scenes.razor` |  |
| 56 | Compare video results and prompt iterations side-by-side across Git revision history. | prompt | review | `Pages/ClipPromptCompareViewer.razor` |  |
| 57 | Edit clip fields (dialogue, prompt, characters, lighting…) | prompt | review | `Pages/Scenes.razor` |  |
| 58 | Empty is fine. Shot plan bakes grading into the visual prompt; fill these only to pin palette or stock for this clip. | prompt | review | `Pages/Scenes.razor` |  |
| 59 | Negative prompt (clip extras) | prompt | review | `Pages/Scenes.razor` |  |
| 60 | Visual prompt | prompt | review | `Pages/Scenes.razor` |  |
| 61 | Visual prompt is required. | prompt | review | `Pages/Scenes.razor` |  |
| 62 | You certify that any screenplay, book text, dialogue, character portrait, or prompt you use is ei | prompt | review | `Pages/TermsAgreementModal.razor` |  |
| 63 | ⚙️ Advanced options (Negative prompt, Lighting &amp; Color, Film stock) | prompt | review | `Pages/Scenes.razor` |  |
| 64 | Generate and stitch clips on your machine where possible. Check Estimate before a full render if you are watching credits | stitch | rewrite — say film/clips/music, not formats | `Pages/Scenes.razor` |  |
| 65 | Generate and stitch clips on your machine where possible. Check Estimate before a full render if you are watching credits. | stitch | rewrite — say film/clips/music, not formats | `Pages/Scenes.razor` |  |
| 66 | Play / stitch | stitch | rewrite — say film/clips/music, not formats | `Pages/About.razor` |  |
| 67 | This invite link is missing its token. | token | review | `Pages/Join.razor` |  |
| 68 | API in-flight: | API | admin-only / strip from product | `Pages/Admin.razor` |  |
| 69 | — LoadSim does not appear to be hitting this API (only admin poll). Check LoadSim console: “runni | API | admin-only / strip from product | `Pages/Admin.razor` |  |
| 70 | Anthropic | Anthropic | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 71 | @(_busy ? "Saving…" : "💾 Save & Hot-Apply Catalog") | Catalog | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 72 | Add to Catalog | Catalog | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 73 | New model added to table. Click 'Save & Hot-Apply Catalog' to persist changes. | Catalog | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 74 | Registered Model Catalog ( models) | Catalog | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 75 | _showAddForm = !_showAddForm"> + Add Model to Catalog | Catalog | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 76 | Endpoint Path (optional) | Endpoint | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 77 | Google Gemini | Gemini | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 78 | e.g. xAI Grok 5 | Grok | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 79 | HTTP traffic | HTTP | admin-only / strip from product | `Pages/Admin.razor` |  |
| 80 | @(_showRawJson ? "📋 Table View" : "📝 Raw JSON Editor") | JSON | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 81 | JSON parse error: {ex.Message} | JSON | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 82 | Server files + local media (MP4/MP3) merged in browser | MP3, MP4 | admin-only / strip from product | `Pages/Admin.razor` |  |
| 83 | Media folder not connected — exporting server files only. Connect media folder for MP4/MP3. | MP3, MP4, Media folder, media folder | admin-only / strip from product | `Pages/Admin.razor` |  |
| 84 | Stage 1: project files to the server. Stage 2: MP4/MP3/etc. into your connected media folder | MP3, MP4, media folder | admin-only / strip from product | `Pages/Admin.razor` |  |
| 85 | Stage 1: project files to the server. Stage 2: MP4/MP3/etc. into your connected media folder (same layout as export). Connect the me | MP3, MP4, media folder | admin-only / strip from product | `Pages/Admin.razor` |  |
| 86 | Stage 1: server project folder. Stage 2: merge MP4/MP3/etc. from your connected media folder | MP3, MP4, media folder | admin-only / strip from product | `Pages/Admin.razor` |  |
| 87 | Stage 1: server project folder. Stage 2: merge MP4/MP3/etc. from your connected media folder under this project id. Connect the medi | MP3, MP4, media folder | admin-only / strip from product | `Pages/Admin.razor` |  |
| 88 | Syncing MP4 clips to client disk… | MP4 | admin-only / strip from product | `Pages/Admin.razor` |  |
| 89 | Model ID | Model ID | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |
| 90 | Model ID is required. | Model ID | admin-only / strip from product | `Pages/AdminModelsCatalog.razor` |  |

## How to use

1. Skim **Suggested action** (auto-guess — not final).
2. Put your decision in **Your note**: `strip` / `rewrite: …` / `keep` / `admin-only`.
3. After you mark the table, we apply the rewrites in one pass.

### Action legend

| Action | Meaning |
| --- | --- |
| **strip** | Delete — pure implementation detail |
| **rewrite** | Keep the intent in plain language |
| **keep** | Fine for end users |
| **admin-only** | OK on Admin / dev tools only |