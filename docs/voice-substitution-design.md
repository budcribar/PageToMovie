# Voice — Easy Start and substitute-in-place

**Status:** implemented. Easy Start (`/simple-voice`) forks a public film and re-voices the narrator. The same overlay path can replace any speaking character on an already-generated movie.

Optional STT word-alignment is a future plug-in. Clone-sample “listen and copy” design: [archive/voice-capture-karaoke.md](archive/voice-capture-karaoke.md).

## Easy Start

1. Browse Public (Forkable) titles.
2. Fork a private copy (inherit max master + index when present).
3. Record one sample (or pick the speaker if there is no narrator).
4. Overlay cloned speech on narrator windows; pictures stay.

## Substitute an existing movie

1. determine **when** the target character speaks (start/end of each spoken line);
2. **associate** each speech window with the character speaking it, using the shot plan's already
   known dialogue text + cast — never guesswork;
3. generate cloned-voice speech of that dialogue;
4. overlay the cloned voice onto the original clip audio at those windows, **ducking** the original
   dialogue there while keeping ambience / music / SFX intact everywhere else;
5. **persist** the detected timestamps so a future substitution skips detection and is fast.

## Where each step runs, and why

| Step | Runs | Why |
|------|------|-----|
| Associate lines ↔ speaker (from shot plan) | **Server** (`VoiceAlignmentStore`) | Deterministic parse of the shot plan; no media needed. |
| Cloned-voice TTS per line | **Server** (`FilmJobService`, reuses `IVoiceClient` / `IVoiceCloneClient`) | Provider keys never leave the API host; paid work only behind an explicit job. |
| Persist alignment (association + TTS paths + timestamps) | **Server** (`VoiceAlignmentStore` → project file) | It's a project data file; must travel with export/import. |
| **Speech-window detection** | **Client** (ffmpeg.wasm `silencedetect`) | The API host never spawns native ffmpeg (hard rule). Silence detection is local and free. |
| **Overlay / duck / mix** | **Client** (ffmpeg.wasm `filter_complex`) | Same hard rule — all audio/video compose is browser-side. |

## Timestamp detection: free silence detection is the primary, implemented path

The **default and implemented** timestamp source is **client-side ffmpeg silence detection**
(`silencedetect`), which is local and essentially free. Because each clip's dialogue text and
speaker are already known from the shot plan, we do **not** need to recognise *who* is talking from
audio — we only need *when* speech happens, then match those windows onto the known lines by
order/count.

Flow:

1. Browser runs `silencedetect` over the clip and inverts the silence runs into **non-silent
   (speech) windows** (`PageToMovieFfmpeg.detectSpeechSegmentsAsync` →
   `_invertSilenceToSpeech`). Windows separated by less than the min-silence gap are merged so one
   line is not chopped into fragments; sub-threshold clicks are dropped.
2. The windows are POSTed to the server, which matches them onto the clip's known dialogue lines
   with `VoiceAlignmentStore.MatchSegmentsToLines` (the single home of the matching rule) and
   persists the result. Match rule:
   - `#windows == #lines` → 1:1 in order;
   - counts differ but ≥1 window detected → take the overall speech span `[firstStart, lastEnd]`
     and split it across lines proportional to each line's character length (rough but general
     time-fit); still tagged `silence` because the span was measured;
   - nothing detected → spread lines across the whole clip proportionally, tagged `estimate`.

An **optional, paid enhancement** (not wired by default) is STT/transcript word-level alignment via
the existing dialogue-verification route. Its transcription today yields only text + accuracy, not
word timestamps, so it would need a timestamp-returning STT call. The persisted shape already
carries a `source` field (`silence` | `transcript` | `estimate` | `manual`), so a better source can
be swapped in per-segment without changing the model or the overlay code.

## Persisted alignment model

**Location:** `<project>/assets/alignment/voice_alignment.json` — a project data file (like the
`assets/qa/*` verification sidecars and `assets/audio/revoice/*` audio), so it travels with the
project on export/import. Path is centralised as `VoiceAlignmentStore.RelativePath`.

**Shape** (`PageToMovie.Core.Models.VoiceSubstitutionModels`):

```
ProjectVoiceAlignment
  schemaVersion   "voice_alignment.v1"
  projectId
  charKey         voice this alignment was last built for
  generatedAtUtc
  clips[]  ClipSpeechAlignment
    scene, clip
    clipDurationSeconds
    segments[]  SpeechSegment
      index                  ordinal within the clip (stable across runs)
      characterKey           speaker, from the shot plan
      dialogueText           expected line, from the shot plan
      startSec, endSec       when the line plays in the clip
      source                 silence | transcript | estimate | manual
      voiceAudioRelativePath cloned-voice TTS for this line (assets/audio/revoice/..._seg_NN.mp3)
```

`ClipSpeechAlignment.IsDetected` is true when every segment has a non-estimate (measured) source —
that is the flag the client uses to skip re-detection on a re-run.

## Cloned-voice TTS: reuse, don't reinvent

The movie-wide job reuses the existing per-clip re-voice machinery rather than building TTS anew:

- Voice resolution and single-line synthesis were **extracted** from the existing speak-batch job
  into shared `FilmJobService` helpers `ResolveSpeakContextAsync` (clone id + speak model + provider
  via `SupportedModelCatalog`, ElevenLabs vs Fal) and `SynthesizeLineAsync` (bytes or url→download +
  TTS telemetry). Both `RunSpeakBatchAsync` and the new `RunVoiceSubstitutionAsync` call them, so the
  provider logic lives in one place.
- Clone identity comes from `ProjectStore.GetVoiceCloneProviderId` / `GetVoiceProviderId` (same as
  speak-batch). Fakes (`FakeVoiceClient`, `FakeVoiceCloneClient`) cover it in tests; no paid call
  happens outside an explicit job.
- Per-line audio path: `MediaRegistryService.RevoiceSegmentAudioRelativePath(scene, clip, seg, ext)`
  (new, sits alongside the existing single-line `RevoiceAudioRelativePath`) so multi-speaker clips
  get one file per line.

## Server orchestration (`FilmJobService.RunVoiceSubstitutionAsync`)

Tracked job, kind `voice-substitution`, started via `StartVoiceSubstitutionAsync` /
`POST /api/jobs/voice-substitution`, progress over `/hubs/jobs`, character-locked like speak-batch.

1. Resolve the clone voice context (error out cleanly if no clone / missing key).
2. Load the shot plan; build per-clip dialogue lines with
   `VoiceAlignmentStore.BuildDialogueLinesFromBlueprint` (optionally filtered to the target speaker /
   narrator).
3. Load any prior alignment to **reuse persisted timestamps** (skip re-detection on re-run).
4. For each clip, for each line: synthesize the cloned voice, write it to the per-segment revoice
   path, hand the client media URL off over SignalR (same pattern as speak-batch), and record the
   segment (character + text + audio path + reused/estimated timestamps).
5. Save `voice_alignment.json`.

The **timestamps themselves** are measured on the client (ffmpeg), then persisted via
`POST /api/projects/{id}/voice-alignment/timestamps`, which matches the posted windows to the known
lines server-side and rewrites the alignment.

## Client ffmpeg (`wwwroot/js/pagetomovie-ffmpeg.js` + `ClientVoiceSubstitutionService`)

- `detectSpeechSegmentsAsync(url)` — `silencedetect` → non-silent windows.
- `overlayVoiceSegmentsAsync(videoUrl, segments, {duckVolume})` — `filter_complex` that (a) ducks the
  **original** audio only inside each `[startSec,endSec]` window via a `volume='if(...)'` envelope so
  ambience/music/SFX survive elsewhere, (b) `adelay`s each cloned-voice clip to its start, (c)
  `amix`es them together. Video stream is copied.
- `_silenceDetectMemfsAsync` — the shared silencedetect runner. (It was referenced by the existing
  silence-trim `analyzeSilenceAsync` but was missing from the file; adding it also repairs that
  pre-existing gap.)
- `ClientVoiceSubstitutionService.ApplyAcrossMovieAsync(projectId)` orchestrates per clip: resolve
  clip URL (reusing `ClientVideoStitchService.ResolveClipUrlAsync`) → detect windows unless already
  detected → resolve each segment's local cloned-voice blob → overlay → return the final clip URL,
  and POST any new windows back for persistence.

## Multi-speaker clips and time-fitting

- **Multi-speaker / multiple lines per clip:** the model is a list of segments per clip. Association
  reads `audio_payload.speaker`/`dialogue` for the common single-line shape and also an
  `audio_payload.lines[]` / `dialogue_lines[]` array when a shot plan carries one. Matching maps
  detected windows onto the ordered lines.
- **TTS longer/shorter than the original window:** overlay places the cloned line at its `startSec`
  and ducks the original across the window. If the cloned line runs longer, it simply plays on over
  the (already ducked) original into the following gap; if shorter, the original stays ducked for the
  remainder of the window. Implemented now. **TODO (enhancement):** optionally `atempo`-fit the
  cloned line to the exact window length for tight lip-sync, and extend ducking to the cloned line's
  real length.

## Reused services (summary)

`IVoiceClient` / `IVoiceCloneClient`, `ProjectStore` voice-id lookups, `SupportedModelCatalog`
resolution, `FilmJobService` job/lock/progress plumbing, `MediaRegistryService` path conventions,
`MediaProxyTicketStore` handoff, `ClientVideoStitchService` URL resolution, `ClientMediaFolderService`
local blob resolution, and the existing ffmpeg.wasm harness.

## What is implemented vs. TODO

- **Implemented:** alignment model + store + persistence; blueprint→speaker association; window→line
  matching; per-segment cloned-voice TTS job (tracked, reusing voice services); persist-timestamps +
  read-alignment endpoints; client `detectSpeechSegmentsAsync` / `overlayVoiceSegmentsAsync` +
  `_silenceDetectMemfsAsync`; `ClientVoiceSubstitutionService` orchestration; unit tests for the
  model round-trip, association, and matching.
- **Specified / TODO:** a workflow UI surface (button + progress) to trigger the job and drive
  `ApplyAcrossMovieAsync`, saving overlaid clips back to the media folder; optional `atempo` window
  fitting; optional STT word-level timestamp source behind the existing `source` field.
