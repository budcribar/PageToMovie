# Supported models (master catalog)

**Single source of truth:** [`host/PageToMovie.Core/config/models_catalog.json`](../host/PageToMovie.Core/config/models_catalog.json)

Code must **not** hardcode model lists, provider labels, pricing, or “default model” ids. Everything selectable in Settings / used for keys and costing is driven by this file (loaded by `SupportedModelCatalog`, served as `GET /api/models` and `GET /api/models/catalog-json`).

## Shape

### `providers[]` (who holds the API key)

| Field | Purpose |
|--------|---------|
| `id` | Stable key-slot id (`grok`, `gemini`, `suno`, `aimusicapi`, …) |
| `label` | Operator-facing name (`xAI`, `Suno API (sunoapi.org)`, …) |
| `aliases` | Alternate strings from model `provider` field (`Xai`, `Google`, …) |
| `order` | Settings sort order |

**Provider ≠ model product.** Example: model name **Suno** / **Suno v5.5**; providers **Suno API (sunoapi.org)** vs **AI Music API (aimusicapi.ai)**.

### `models[]` (what the user selects)

| Field | Purpose |
|--------|---------|
| `id` | Stable model id sent to APIs / stored in project config |
| `displayName` | Model product label only (no provider suffix) |
| `capability` | `Video` · `Image` · `Chat` · `Vision` · `Audio` · `Voice` · … |
| `provider` | Family/name matching `providers[].aliases` (e.g. `Xai`, `Suno`) |
| `providerId` | Key-slot id (must match a `providers[].id`) |
| `providerLabel` | Display label for that provider |
| `apiBase` / `endpointPath` | HTTP surface |
| `requiredEnvKeys` | e.g. `XAI_API_KEY`, `SUNO_API_KEY` |
| `enabled` | Shown in pickers when true |
| costs / limits / flags | Pricing, clip duration, `supportsVideoReview`, `isVoiceCloneStep`, … |

### `capabilities[]`

Studio jobs + **`defaultModelId`** (must be a real enabled model id in `models[]`).

## Runtime rules

1. **If it is not in the JSON, it is not real** — no C# fallback model cards.
2. Resolve provider only via catalog (`providerId` / `NormalizeProviderId` + aliases), never from model-id heuristics.
3. Project config that points at a missing or unknown model id must **fail** with a clear error naming the capability and the bad id. Do **not** reset to `capabilities[].defaultModelId`. Required slots (video, image, chat, vision) also fail when empty or whitespace. Optional slots (audio, voice, lipsync, video-edit, video-review) may stay unset or `none`; an unknown non-empty id still fails. `capabilities[].defaultModelId` may stay in the JSON as documentation or a Settings placeholder suggestion — runtime and heal must not apply it.
4. Configuration writes provider fields derived from the **selected model’s** catalog row (for keys/cost), not from free-typed service names.

## Adding a model

1. Implement/wire the HTTP client if the API shape is new.
2. **Add or enable a row in `models_catalog.json`** (and `providers[]` if it is a new key-holder).
3. Ship. Settings and coverage pick it up from the catalog — **no** new hard-coded id in Razor/C# options.

## Not supported yet

Do **not** enable half-working models. Prefer a disabled row + `featureRequestUrl` / GitHub issue until the client exists.

## Admin catalog UI

`/admin/models-catalog` edits the same JSON (add/enable/disable, scan, labMode for incomplete rows). Cost rates come only from the catalog — no C# fallbacks. Dated 2026-08-05 notes: [docs/archive](archive/README.md).
