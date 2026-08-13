# Prototype look-feel + process / voice branch

**Branch:** `feature/prototype-look-feel` (from `master`)  
**Status:** Look-feel + studio process strip + voice-clone sample capture.

## Product path

```
Book → Cast & voice → Estimate → Film → Review
```

Shared UI: `StudioProcessStrip` on Cast, Estimate, Film, Review.

## Voice cloning (this increment)

| Piece | Behavior |
|-------|----------|
| **Style text** | Existing `voice_profile` / `voice_label` + film voice preview job |
| **Clone sample** | Optional mic record or audio upload |
| **Storage** | `assets/characters/{key}/voice_clone_sample.*` + seed field `voice_clone_sample` |
| **API** | `POST/GET/DELETE …/characters/{key}/voice/clone-sample` |
| **UI** | Cast → Voice panel → “Voice clone sample” |
| **Not yet** | Live ElevenLabs (or other) TTS clone provider — sample is the template on disk |

Mic: `wwwroot/js/pagetomovie-voice-capture.js` (`PageToMovieVoiceCapture`).

## Checkout

```bash
git fetch origin
git checkout feature/prototype-look-feel
cd host
dotnet run --project PageToMovie.Api
```

## Smoke

1. Home 5-step tiles  
2. Cast page: process strip + Voice → Record mic / Upload audio  
3. Sample plays back; Remove sample works  
4. Estimate / Film / Review show process strip  
5. Existing voice preview (style text) still works  
