# Fake multi-capability plan (UITestingBranch)

Goal: several model profiles (catalog flags) under fakes; UI and jobs react correctly.

- [x] **1. Catalog-aware fake video client** — `FakeGrokVideoClient.ValidateAgainstCatalog` (continue / max refs / duration)
- [x] **2. Unit tests for video feature flags** — `FakeVideoCatalogCapabilityTests` **10/10**
- [x] **3. Fake audio CanSing** — `FakeAudioClient.ValidateVocalRequest` mirrors Scenes UI (suno / aimusicapi / elevenlabs only); `FakeAudioVocalCapabilityTests` **6/6**
- [x] **4. API combo smoke** — config + stage2 for `grok-imagine-video` / `wan-2.1` / `veo-3.1` under UseFakes — **0 fails**
- [x] **5. UI data / capability lists** — `GET /api/models?capability=` returns filtered lists (video 3, image 5, chat 13, audio 7, vision 3). Full Playwright Scenes extend/refs UI deferred (optional follow-up)
- [x] **6. Docs** — this file + test class names

## Profiles (catalog ids)

| Id | Continue | Max refs | Duration |
|----|----------|----------|----------|
| `grok-imagine-video` | yes | 7 | 1–15s |
| `fal-ai/wan-2.1` | no | 1 | 5–6s |
| `veo-3.1` | no | 0 | 4/6/8 only |

## Audio sing

| Models | CanSing (UI + fake) |
|--------|---------------------|
| suno-v5-5, aimusicapi-suno, elevenlabs-music | yes |
| fal-ai/stable-audio-2.0, musicgen, … | no |

## How to run under fakes

```bash
export PageToMovie__UseFakes=true
# XAI_API_KEY not required when UseFakes (provider key gate skipped)
dotnet test --filter "FullyQualifiedName~FakeVideoCatalogCapabilityTests|FullyQualifiedName~FakeAudioVocalCapabilityTests"
```

## Optional follow-up

- Playwright: switch video model in Configuration → assert Scenes extend/ref affordances  
- Catalog `supportsVocals` field instead of provider-id heuristic  
