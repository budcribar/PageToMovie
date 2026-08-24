# Media and timeline contract

This is the canonical contract for deciding whether media can be used by **Cut** and for deciding what appears when audio extends beyond the photographed picture.

## Valid media is role-specific

File size is not a media validator. Cut has no minimum-byte rule such as 50 KB. A nonempty file is only a candidate; the browser decoder and ffmpeg.wasm decide whether its streams are usable.

| Role | Required evidence | Not required |
|------|-------------------|--------------|
| Video take | Nonempty file, finite positive duration, positive decoded width and height, browser decodes the first frame, and ffmpeg.wasm decodes the selected in/out range during compose | An audio stream |
| Music / audio | Nonempty file, finite positive duration, and the browser decodes an audio stream | Video frames |

Consequences:

- A 192-byte placeholder is rejected because it cannot decode, not because it is below an invented size threshold.
- A silent video is a valid video take.
- An audio-only MP4 is valid audio, but it is not a valid video take.
- A file with a plausible header but corrupt media later in the selected range fails the ffmpeg.wasm compose check.
- Import errors should report the failed role (video or audio) and the decoder failure.

Film's `scene_SS_clip_CC.current.json` selects the take. If that pointer is absent, Cut may recover the highest numbered `_take_NN.mp4` from the same scene/clip slot only; it never borrows a previous scene's picture. Recovery selects a candidate, not proof of validity, so the same browser and compose decoding checks still apply.

## Continuous exported picture

An exported movie always has a picture for its complete output duration.

| Timeline condition | Exported picture |
|--------------------|------------------|
| Music starts before the first video frame | Black frames for the configured **Black intro** duration |
| Music overlaps the video | The normal picture |
| Music continues beyond the last video frame | The final video frame is frozen until the music ends |
| An intentional cut-to-black hold | Black frames for that hold |
| A missing Film slot | The existing missing-slot black placeholder/card |
| An ordinary clip or scene boundary | The next picture begins contiguously; no accidental black or silent gap |

Select the music block and set **Black intro** in its inspector to create a music-over-black opening. The first picture and its native voice are delayed by that amount; the music keeps its own timeline placement. The setting is saved in `cut.project.json`.

When a music track is present, Play waits for a composed movie containing the real mix. It does not start a native video-only shortcut that would misleadingly omit the music.

The output duration is:

```text
max(black intro + picture duration,
    music start + selected music duration / playback rate)
```

This means a song is not cut off merely because the picture ends first. Cut freezes the final frame for the remaining song duration. If a project deliberately needs black rather than a frozen frame at the end, add an explicit cut-to-black/card hold instead of relying on an empty timeline gap.

## Verification and caching

Validation happens in two stages:

1. Import quickly proves that the browser can decode the stream required by the file's role.
2. Compose makes ffmpeg.wasm decode the complete selected range. Only a successful compose is proof that the chosen range can be exported.

Cached scene segments and the final merge are keyed by render fingerprints. Trim, transition, title, music placement, fades, speed, volume, or Black intro changes invalidate the affected fingerprint. File byte length is not used as a proxy for validity or cache freshness.

### FFmpeg worker experiments

Cut defaults to one worker for each pool. Independent dirty scenes use the scene pool. Independent scene-body trims and uncached transition renders use a separate stitch pool. Ordered final concat and the final soundtrack mix each produce one dependent output and remain single-worker operations; avoiding that dependency would require an additional generation of video encoding or a codec-copy assumption that is not unconditionally valid.

```text
?ffmpegWorkers=1   safe baseline and default
?ffmpegWorkers=2   two scene workers
?ffmpegWorkers=3   three scene workers
?ffmpegWorkers=4   maximum supported experiment

?ffmpegStitchWorkers=1   safe stitch baseline and default
?ffmpegStitchWorkers=2   two body/transition workers
?ffmpegStitchWorkers=3   three body/transition workers
?ffmpegStitchWorkers=4   maximum supported stitch experiment
```

Each query parameter overrides its persisted browser setting. `PageToMovieCut.setFfmpegWorkerCount(n)` and `PageToMovieCut.setFfmpegStitchWorkerCount(n)` persist clamped values from 1 through 4. If either parallel pool fails, Cut terminates its extra workers, discards that pool's partial results, resets the primary FFmpeg instance, and retries the affected phase once through the one-worker path.

For repeatable benchmarks, add `ffmpegFresh=1`; this bypasses movie, picture, scene, and transition cache URLs for that compose without changing render fingerprints. `PageToMovieCut.getLastComposeMetrics()` reports requested/effective workers, dirty scenes, scene-preparation time, total time, and whether fallback occurred.

Mary19Test was benchmarked in the browser on August 24, 2026, with four dirty scenes and `ffmpegFresh=1`. Each row is one full compose through the same Play/export composition pipeline:

| Pool | Scene preparation | Total compose | Total improvement vs. 1 | Fallback |
| ---: | ---: | ---: | ---: | :---: |
| 1 | 2:34.2 | 7:45.6 | baseline | No |
| 2 | 1:40.2 | 6:49.8 | 12.0% | No |
| 3 | 1:17.7 | 6:28.3 | 16.6% | No |
| 4 | 1:17.8 | 6:31.2 | 16.0% | No |

Pool 3 was the best scene-pool result for this project. Pool 4 did not improve scene preparation and was 2.9 seconds slower overall, so three scene workers is the recommended experimental setting for Mary19Test. One worker remains the product default because it has the lowest memory pressure and is the unconditional recovery path. These timings are machine- and project-specific; re-run the forced-fresh benchmark before changing the default globally.

The transition/stitch pool was then benchmarked with the scene pool fixed at 3. Mary19Test produced seven independent scene-body/transition tasks:

| Stitch pool | Stitch preparation | Final concat | Final mix | Total compose | Total improvement vs. stitch 1 | Fallback |
| ---: | ---: | ---: | ---: | ---: | ---: | :---: |
| 1 | 1:11.6 | 3:03.1 | 1:11.0 | 6:56.1 | baseline | No |
| 2 | 0:42.8 | 3:08.3 | 1:12.4 | 6:27.3 | 6.9% | No |
| 3 | 0:39.2 | 3:01.0 | 1:09.9 | 6:11.7 | 10.7% | No |
| 4 | 0:35.6 | 2:53.0 | 1:09.0 | 5:59.7 | 13.6% | No |

Four stitch workers produced the best Mary19Test result. Stitch preparation was 50.4% faster than the one-worker stitch baseline and the complete compose was 56.5 seconds faster. The recommended Mary19Test experiment is therefore `?ffmpegWorkers=3&ffmpegStitchWorkers=4&ffmpegFresh=1`. Concat and mix timings vary between runs and remain the dominant dependent work; their measured differences must not be attributed to the stitch pool. One stitch worker remains the default and automatic recovery path.

#### Combined concat and soundtrack experiment

`ffmpegCombined=1` replaces the normal final picture concat plus subsequent soundtrack mix with one FFmpeg command when a soundtrack exists and no reusable picture is available. The command concatenates the ordered pieces, preserves native clip audio when present, mixes the placed soundtrack, adds intro black or a frozen final frame when required, and encodes the final movie once. Cut verifies that the output duration reaches the unconditional expected duration and separately requires the browser to decode both its video and audio streams. A command failure, short result, missing stream, or decode failure resets FFmpeg and retries the proven two-pass concat-then-mix path.

The combined result cannot also be a reusable dry-picture cache: its audio already contains the soundtrack, and mapping a second dry output would require a second video encode. Cut therefore marks the picture as non-reusable so a later music-only edit performs a fresh composition rather than mixing the soundtrack twice. The experiment remains off by default while that remix-workflow tradeoff is evaluated.

Mary19Test was benchmarked with scene pool 3, stitch pool 4, and forced-fresh inputs. The one-pass result had a browser-decoded video stream, a browser-decoded audio stream, and a duration of 121.888 seconds:

| Final path | Final encode work | Total compose | Improvement | Fallback |
| --- | ---: | ---: | ---: | :---: |
| Picture concat, then mix | 2:53.0 + 1:09.0 | 5:59.7 | baseline | No |
| Combined concat and mix | 0:59.1 | 2:50.6 | 52.6% | No |

The combined pass saved 3:09.1. Its explicit benchmark URL is `?ffmpegWorkers=3&ffmpegStitchWorkers=4&ffmpegFresh=1&ffmpegCombined=1`. Browser stream validation added 0.109 seconds. The result supports continued use of the combined path for full renders, while the dry-picture cache tradeoff should be evaluated separately for workflows that repeatedly adjust only music settings.

#### Flattened clip pipeline

`ffmpegFlat=1` with the combined pass removes intermediate scene concat encodes. Dirty clips are prepared through one global configurable pool (`ffmpegClipWorkers=1` through `4`), scene-boundary transitions are rendered against the corresponding last and first prepared clips, and the resulting ordered pieces go directly into the combined concat-and-mix command. This preserves per-clip trims, text overlays, holds/credits, native audio, music, fades, scene transitions, intro black, and the frozen final frame. Layouts containing an inline card before a non-hold clip stay on the scene pipeline. Any clip-pool, transition, combined-command, duration, or browser stream-validation failure retries through the proven scene pipeline.

Mary19Test forced-fresh measurements used four transition workers:

| Clip workers | Clip preparation | Transition preparation | Combined pass | Total compose | Improvement vs. combined scene path | Fallback |
| ---: | ---: | ---: | ---: | ---: | ---: | :---: |
| Scene path (3 scene workers) | 1:15.9 | 0:34.1 | 0:59.1 | 2:50.6 | baseline | No |
| 3 | 0:37.6 | 0:06.8 | 0:57.5 | 1:43.3 | 39.4% | No |
| 4 | 0:31.8 | 0:07.0 | 0:58.7 | **1:38.8** | **42.1%** | No |

The maximum-speed Mary19Test URL is `?ffmpegClipWorkers=4&ffmpegStitchWorkers=4&ffmpegFresh=1&ffmpegCombined=1&ffmpegFlat=1`. Three clip workers are recommended when saving memory is more important than 4.5 seconds. Both flattened results had browser-decodable video and audio, completed without fallback, and produced 121.879-second output versus 121.888 seconds from the scene path.

For the editor's scope and controls, see [Cut 1.0](../host/PageToMovie.Cut/CUT-1.0.md). For running and testing Cut, see its [README](../host/PageToMovie.Cut/README.md).
