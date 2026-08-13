# Voice Capture — "Listen-and-Copy" clone sampling

**Status:** design agreed 2026-08-05; Phase 1 in progress.
**Owner:** budcribar.

## Why

Everything the dub fights downstream — stretch, calibration, placement — is compensating for
**one root problem: the voice sample is recorded at the wrong pace/expressiveness, so the clone
doesn't match the narration.** A better *sample* beats more downstream heuristics. This feature
captures a much better sample by having the user **imitate the real original narration** (listen
and copy), so their clone inherits the right pronunciation, emotion, and rhythm.

It also produces a high-value by-product: **STT-verified line↔window mappings** that feed straight
back into the dub's overlay timing (replacing the even-spread guessing).

## Approach

Instead of a synthetic karaoke (TTS reference + word-timed ball), the user **listens to the actual
extracted original dialogue and copies it**. The reference is exactly what we want to match, and
it sidesteps word-level alignment and TTS-tempo arguments.

Two clearly separated stages:

- **Once per book (offline, cached):** find and rank the best dialogue phrases, verify them with
  STT, and save the selection with the project.
- **Per user (fast, snappy):** load the saved phrases and run the capture loop.

## Pipeline

### Stage A — Phrase selection (once per book, cached)

1. **Detect** narrator dialogue windows across scenes (existing client silence detection).
2. **Extract** each window's audio segment (client ffmpeg trim → short clip).
3. **Verify with ElevenLabs Scribe (STT):** transcribe each segment; fuzzy-match the transcript to
   the blueprint's expected narrator line (word overlap ≥ ~70%, tunable). A match means the window
   genuinely contains that line. Triple duty:
   - filters out ambience / wrong-speaker false positives,
   - yields the **confident (line ↔ window)** mapping (→ overlay timing),
   - gives the **read-along text** shown to the user.
4. **Rank & select** — spend effort here (it's amortized): audio **dynamic range** + an **LLM pass**
   for expressiveness/variety + **phonetic/length spread**. Keep a ranked pool of **~8–12**
   (alternates for skips/awkward ones).
5. **Save** `assets/voice_capture/phrases.json` (see schema) — regenerable, computed once.

### Stage B — Capture loop (per user)

Load `phrases.json`, then for each phrase (directive UI only — no mechanism/why/expectations):

1. **Listen** — play the original segment.
2. **Record** — user copies while a **ball paces them to that window's own duration** (the pace we
   already calculate). Red-dot recording indicator; **auto-stop** at the end + a short tail.
3. **Score** — a **light, encouraging "how'd I do"** score, computed **locally, instantly**:
   **rhythm only** = duration closeness + energy-envelope correlation vs the original.
   *Not* words (easy for them, adds noise) and *never* timbre/pitch similarity (their different
   voice is the point — a good take would score low). Generous thresholds. Doubles as the take-ranker.
4. **Again / Next** — keep the best take (auto-highlighted), redo, or move on.

When enough good takes are kept (~6–8), **stitch** them (existing client concat) → **create the
voice clone** (existing clone call).

## `phrases.json` schema (per project)

```jsonc
{
  "schema_version": "voice_capture.v1",
  "generated_at_utc": "…",
  "phrases": [
    {
      "scene": 3,
      "clip": 2,
      "window_start_sec": 1.10,
      "window_end_sec": 4.85,
      "text": "He goes to sleep because they say tomorrow is another day.",
      "dynamic_range_db": 18.4,   // measured on the extracted segment
      "rank": 0
    }
  ]
}
```

## Scoring detail (rhythm-only, timbre-independent)

- **Duration closeness:** how close the take's length is to the original window.
- **Envelope correlation:** normalized cross-correlation (or DTW) of the RMS loudness envelopes —
  captures *where the emphasis/syllables land* and is independent of voice timbre.
- Combine into a single 0–100 (or stars), tuned generous. Absolutely **no pitch/timbre matching.**

## UI principles

Directive only. *Listen → Record → Again/Next.* No explanation of how it works, no expectation-
setting. (Matches the app's clean, jargon-free UI rule.)

## Phases / checklist

### Phase 1 — Extract + Scribe verify + confident mapping (also improves the dub today)
- [x] `ElevenLabsScribeClient` (STT via `POST /v1/speech-to-text`, `scribe_v1`) + DI + HttpClient.
- [x] Server endpoint `POST /api/transcribe`: audio segment → transcript (ElevenLabs key server-side).
- [x] Client JS `extractAudioSegmentAsync(videoUrl, startSec, endSec)` → mono 16 kHz WAV bytes.
- [x] Client `EngineApiClient.TranscribeSegmentAsync(bytes)` → transcript.
- [x] `VoiceCapturePhrases`/`VoiceCapturePhrase` model + `GET/POST /api/projects/{id}/voice-capture/phrases`
      (cached at `assets/voice_capture/phrases.json`) + client get/save methods.
- [x] Verification loop `ClientVoiceCaptureService.BuildPhrasesAsync`: per narrator-only scene, stitch →
      detect windows → extract each → Scribe transcribe → word-overlap match to the blueprint line
      (≥0.7 = confident) → rank confident by duration → **save phrases.json** (the once-per-book cache).
- [x] Wire confident (line↔window) pairs into the overlay: `ApplyAcrossMovieAsync` reads phrases.json and
      places any line matching a confident window at that verified window; else WPS/word-count fallback.
      Auto-builds the cache on the first dub if missing (once per book) — logs
      `[dub] scene NN: N line(s) placed from STT-verified windows`.
- [ ] *(refinement)* dynamic-range measurement per segment for ranking; LLM expressiveness/spread pass;
      dedicated "prepare phrases" trigger UI (currently auto-builds inside the dub).

### Phase 2 — Capture UI + ball + rhythm score
- [x] Playback extractor `extractAudioSegmentToUrlAsync(videoUrl, start, end)` → WAV blob URL for Listen.
- [x] Rhythm-match scorer `analyzeRhythmMatchAsync(originalUrl, takeUrl)` — Web Audio: normalized RMS
      envelope Pearson-correlation (shape) + duration closeness → 0–100. Timbre-independent, generous.
- [x] Phrase persistence save/load (done in Phase 1).
- [x] Capture page `VoiceCapture.razor` (route `/voice-capture`): loads top confident phrases → per
      phrase Listen (play original) → Record (`PageToMovieVoiceCapture.start/stop`) with a ball paced to
      the window duration + auto-stop → `analyzeRhythmMatchAsync` "how'd I do" (stars + label) → Re-record
      / Keep & next.
- [x] Finish: `concatAudioToBytesAsync` stitches kept takes → save + `UploadVoiceCloneSampleAsync` →
      `ApplyVoiceCloneAsync` (Phase 3 folded in here).

### Phase 3 — Stitch → clone
- [x] Done as the capture page's Finish step (`concatAudioToBytesAsync` → upload sample → apply clone).

### Follow-ups
- [x] Build phrases standalone from the capture page (no dub needed): `GET
      /api/projects/{id}/voice-capture/narrator-lines` (blueprint-derived) + a "Prepare phrases" button
      → `ClientVoiceCaptureService.BuildPhrasesAsync` now sources lines from that endpoint.
- [x] Capture UX: ready-set-go traffic light; the pacing ball became a **teleprompter** (words scroll
      past a fixed marker, light to its left, shown through the countdown); **Listen** plays + scrolls the
      original in sync.
- [x] **Per-word pacing:** persist Scribe per-word timestamps (`VoiceCapturePhrase.Words`) and drive the
      teleprompter scroll off them (`startWordTeleprompter` + `BuildWordTimeline`) so it copies the
      narrator's real rhythm (linger on stretched words) instead of an even glide. *Needs a one-time
      re-run of "Prepare phrases" per book to populate `Words` in existing caches.*
- [ ] Nav link to `/voice-capture` (currently reached by URL).
- [ ] Per-segment dynamic-range ranking + LLM expressiveness/spread selection for the phrase pool.
- [ ] Optional pitch-contour overlay (Smule-style) on the score.
- [ ] Live browser test: mic record + Web Audio score + audio concat + clone round-trip.

## Out of scope / v2
- Pitch-contour overlay (Smule-style visual), pitch-normalized intonation scoring.
- Vocal isolation (for projects with loud ambience).
- Book-text-level phrase cache reusable across adaptations (this cache is per-project).
- DTW auto-scoring refinements.

## Honest note (internal, never shown to the user)
Imitation improves clone **timbre/pronunciation/emotion**; the final output **pacing** is still
governed by TTS speed + our stretch. This feature makes the voice *sound* right; pacing stays the
combined lever.
