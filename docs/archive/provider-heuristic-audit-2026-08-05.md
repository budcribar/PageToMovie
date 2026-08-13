# Provider-ID heuristic audit (should be model capabilities)

**Branch:** UITestingBranch · **Updated:** 2026-08-05

Principle: **feature affordances** belong on the **model catalog entry**. **Provider id** is for routing (HTTP client / API key), not feature flags.

---

## P0 — Audio CanSing / vocals — **DONE**

| Item | Status |
|------|--------|
| Catalog `supportsVocals` on Audio models | Done |
| `SupportedModelEntry` / `SupportedModelDto` / ToDto / FromDto | Done |
| `Scenes.SelectedAudioModelCanSing` → `SupportsVocals` | Done |
| `FilmJobService` music gen → `entry.SupportsVocals` | Done |
| `FakeAudioClient.ValidateVocalRequest` → catalog flag | Done |
| Tests `SupportsVocalsCatalogTests` + existing fake audio tests | 25 related tests green |

| Model | supportsVocals |
|-------|----------------|
| suno-v5-5, aimusicapi-suno, elevenlabs-music | true |
| fal-ai/musicgen, udio, minimax/music, stable-audio-2.0 | false |

---

## P1 — Image max refs catalog-only — **DONE**

| Item | Status |
|------|--------|
| All enabled Image models have `maxReferenceImages` in catalog | Done (Flux set to 1) |
| `ImageApiLimits.MaxReferenceImages` uses catalog only (no provider switch) | Done |
| Unknown model id → conservative `DefaultMaxReferenceImages` | Done |

Provider constants (`GrokMaxReferenceImages`, etc.) remain as documentation / unknown-id fallback only.

---

## OK — Provider for routing

MultiProvider clients, voice apply strategies, API keys, Configuration default-model-after-key — appropriate.
