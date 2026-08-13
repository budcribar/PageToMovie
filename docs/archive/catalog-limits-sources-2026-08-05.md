# Catalog API limits — sources (2026-08-05)

Filled missing `models_catalog.json` capability fields with researched provider values; soft `??` defaults in clients fail-fast when the catalog field is absent.

## Chat / vision tokens

| Model | maxInputTokens | maxOutputTokens | Source |
|-------|----------------|-----------------|--------|
| gpt-4o / gpt-4o-mini | 128000 | 16384 | OpenAI API tables |
| gpt-5.6-sol/terra/luna | 1050000 | 128000 | Published GPT-5.6 API tables |
| o3-mini | 200000 | 100000 | Artificial Analysis / OpenAI o-series |
| grok-4 / grok-4.20-reasoning | 256000 | 128000 | xAI docs (256k context); xAI default max_completion_tokens 128k |
| grok-4.5 | 500000 | 128000 | xAI (Grok) stated 500k context |
| claude-sonnet-5 / opus-5 | 1000000 | 128000 | Anthropic Claude 5 long-context / 128k output (product-cited) |
| gemini-2.5-flash / 3.x | 1000000 | 65536 | Google Gemini 2.5 Flash: 1,048,576 in / 65,536 out (rounded input to 1M) |

## Video

| Model | Notes | Source |
|-------|-------|--------|
| grok-imagine-video | duration 1–15s; extend 2–10s; maxPromptLength 4000 | xAI video docs |
| veo-3.1 | 4/6/8s; maxReferenceImages **3**; maxPromptLength 4000 | Vertex AI Veo 3.1 (up to 3 asset images) |
| fal-ai/wan-2.1 | 5–6s; maxPromptLength 800; maxReferenceImageDimension 2000 | fal Wan docs (related wan-2.5 prompt 800; image size range) |
| hunyuan-video | frame ladder 85/129; steps 30 | fal Hunyuan |

## Audio / voice

| Model | Notes | Source |
|-------|-------|--------|
| elevenlabs-music | max 600s; prompt ≤4100 | ElevenLabs Music API (`music_length_ms` 3000–600000) |
| suno / aimusicapi | maxAudio 240s | Common Suno API track cap (no single public hard max found) |
| fal stable-audio / musicgen / etc. | maxPromptLength 1000 | fal prompt budgets |

## Code fail-fast

- `AnthropicChatClient.ResolveMaxTokens` — requires catalog `maxOutputTokens`
- `FalVideoClient` — requires `maxPromptLength`; `maxReferenceImageDimension` when image present; frame/step only if catalog declares
- `GrokVideoClient` / `ClipVideoPromptBuilder` / `FilmJobService` — require `maxPromptLength`
- `FalVoiceCloneClient` — requires `maxPromptLength`
