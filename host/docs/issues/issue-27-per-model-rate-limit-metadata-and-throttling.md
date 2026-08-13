# Issue 27 — No per-model rate-limit metadata; chat clients fire-and-catch-429 instead of throttling

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | open |
| Branch | (none yet) |
| Related files | `host/PageToMovie.Core/config/models_catalog.json`, chat clients, `docs/archive/automatic-model-selection-plan.md` |

## Problem

`SupportedModelCatalog`/`models_catalog.json` has no concept of a model's requests-per-minute or
tokens-per-minute ceiling. Every chat client just fires the request and lets the provider's 429
happen; nothing paces calls to stay under a known limit.

Confirmed live during a `ScreenplayBenchmark` run against `books/Nick_and_Me.txt` (277K chars,
comfortably within gpt-4o's real 128K-token context window and the product's own single-shot
budget ceiling): gpt-4o still failed with

```
Rate limit reached for gpt-4o in organization org-... on tokens per min (TPM):
Limit 30000, Used 15532, Requested 14655. Please try again in 374ms.
```

This is an account/org-tier throttle, not a model capability limit — gpt-4o-mini succeeded
immediately after on the same run. `CompleteWithOneRetryAsync` (`BookToFountainConverter.cs`)
retries once with no delay, so it doesn't reliably recover even though the API told us exactly how
long to wait (374ms). A worked-around instance (benchmark-only `PromptBudget` override forcing
gpt-4o onto the multi-chunk path) is in `ScreenplayBenchmark/Program.cs`
(`ResolveRateLimitSafeBudgetOverride`) — that's a stopgap for one tool, not a product fix.

## Suggested fix

1. Add optional `requestsPerMinute` / `tokensPerMinute` fields to model catalog entries (operator-
   maintained, since these are account-tier values that drift when a tier changes — not a fixed
   model property, so don't pretend otherwise in naming/docs).
2. Add a small shared rate limiter (token-bucket or sliding-window, keyed by model id) that
   `GrokChatClient`/`AnthropicChatClient`/`GeminiChatClient` (or centrally in
   `MultiProviderChatClient`) consult before firing a call — wait until capacity frees up instead
   of firing and catching a 429.
3. Where a provider returns a `Retry-After`/"try again in Xms" hint (as OpenAI did above), honor it
   directly rather than the current fixed no-delay single retry.

## Notes

This is a prerequisite for archived `automatic-model-selection-plan.md`'s Tier Policy Engine: automatic
model routing under a quality-tier abstraction needs real-time rate-limit awareness, not just
static per-tier pricing — otherwise the "pick a model, bill me" experience that plan describes can
still silently blow up mid-job exactly like this run did, just with no human watching to notice and
retry with a different model.

Held per user request (2026-07-30) — noted as backlog, not to be implemented yet.
