# Capability mapping validation tests

**Branch:** `UITestingBranch` · **Date:** 2026-08-05

## Tests

`host/PageToMovie.Tests/ProjectModelSelectionTests.cs` — **49 passed**

- Accept: video / image / planning (chat) / vision slots with correct catalog ids  
- Reject: wrong capability in each slot (e.g. chat model as `model_name`)  
- Sentinels: `none` / `disabled` / `auto` / empty → “no model selected”  
- `CapabilityFromApiKind` mapping for telemetry kinds  
- Planning prefers `planning_model_name` over `chat_model_name`  
- Disabled `hunyuan-video` rejected  

## Bug found and fixed

`ProjectModelSelection.Require` used:

```csharp
Find(id, capability) ?? Find(id)  // any capability
```

So a **Chat** model in the **Video** slot was accepted. That defeated Settings capability filters.

**Fix:** capability-scoped `Find` only; error message includes catalogued capability when mismatched.  
**Intentional exception:** `SupportedModelCatalog.Find` still allows **Chat↔Vision** overlap for shared ids (e.g. `grok-4.5`).

## Not covered by fakes

Different catalog video models still share one `FakeGrokVideoClient` under `UseFakes`; these tests validate **selection rules**, not provider behavior.
