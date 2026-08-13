# Catalog update scan (2026-08-05)

Admin → Models catalog → **Scan for updates**

## Color legend
| Color | Status | Meaning |
|-------|--------|---------|
| **Green** | `unchanged` | Live probe found the value; matches catalog |
| **Yellow** | `not_found` / `error` | No probe, parse miss, missing API key, or fetch error |
| **Red** | `changed` | Live value differs from catalog |

## Actions
- **Accept live** — patches the draft table field with the live value (then **Save**)
- **Accept as LAB** — adds a discovered model with `labMode: true`

## Probes (P0 / P1)

### P0 — fal list prices
- `GET https://api.fal.ai/v1/models/pricing?endpoint_id=…`
- Auth: `Authorization: Key $FAL_KEY` (or `FAL_API_KEY`)
- Maps `unit_price` → `imageCostPerImage` or video base / per-sec fields
- Requires key; without it → yellow

### P1 (pricing) — xAI docs

- Fetches model docs HTML on `docs.x.ai` (Imagine video/image, grok-4.x)
- Parses Input/Output `$/1M`, `$/image`, resolution `$/sec` tiers
- Still runs duration/ref probes for video capability pages

### Also
- OpenAI / xAI `GET /v1/models` when API keys present (id existence + new models)
- Other providers: yellow “no probe”

## Notes
- List prices only — not usage/invoice APIs
- Nested JSON fields (e.g. `videoCostPerSecondByResolution.720p`) may need Raw JSON edit after Accept if the simple patch is insufficient

### P1 — model lists & discovery
| Probe | Endpoint | Needs | What |
|-------|----------|-------|------|
| **A Anthropic** | `GET /v1/models` | `ANTHROPIC_API_KEY` | Existence; `max_input_tokens` / `max_tokens` when present; new Claude ids |
| **B Gemini** | `GET /v1beta/models` (+ get) | `GEMINI_API_KEY` or `GOOGLE_API_KEY` | Existence; `inputTokenLimit` / `outputTokenLimit`; new gemini/imagen ids |
| **C fal** | `GET /v1/models` | `FAL_KEY` | New `endpoint_id`s (category → suggested capability); pricing still P0 |

## Accept nested fields
**Accept live** writes dotted paths into nested JSON objects:

- `videoCostPerSecondByResolution.720p` → `videoCostPerSecondByResolution["720p"]`
- `videoBaseCostByResolution.*` → all existing child keys (or seeds 480p/720p/1080p if empty)
- Top-level fields (`maxExtensionSeconds`, `imageCostPerImage`, …) unchanged

No Raw JSON required for resolution-tier price accepts.
