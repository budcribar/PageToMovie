# What breaks when we run a second instance

The API host currently runs as **one process**. Several singletons hold their state in that
process's memory, and each one is correct only because there is exactly one of them. None of them
fail loudly when a second instance appears — they degrade into wrong answers that look like
ordinary bugs, which is why this list exists before the scale-out rather than after it.

Read this as a checklist for the day we add instance #2, not as a list of current defects.

## The trap underneath all of it

Two instances only share what they *both* reach. On a per-machine volume (Fly volumes are
per-machine by default), `/data` is **not** shared — so the disk-backed items in the "safe" table
below are only safe if the deployment gives every instance the same storage. Check that first;
several fixes below assume it.

## In-process state — breaks at instance #2

| What | Where | What goes wrong |
|---|---|---|
| Job records | `JobStore` (`IJobStore`) | Jobs live in a dictionary. A client polling `/api/jobs/{id}` that lands on the other instance gets a 404, and `JobLostOnRestart` reads that as "the job is gone" and fails a snapshot for a job that is running fine. |
| Job progress events | `SignalRJobProgressSink` → in-process `IHubContext`, `AddSignalR()` with no backplane | A broadcast never crosses instances. The job completes, the browser hears nothing, and `ClientMediaFolderService` never saves the clip — the API host drops its own copy once `ClientMediaUrl` is published, so the bytes are lost. `JobHub` + `HubGroupRegistry` now log a warning when a finished job publishes to a group with no local listener, so this announces itself. |
| Media proxy tickets | `MediaProxyTicketStore` | Tokens are minted in one process's dictionary. `/api/media/proxy/{token}` on the other instance 404s, so the download fails outright. |
| Project / stage locks | `InMemoryLockService` | The guard that stops stage 1 and stage 2 running on one project at once. Two instances would each think they hold it. |
| Media save claims | `MediaSaveClaims` | Two windows on different instances are both granted, so both write the same file. Correctness survives — the loser verifies its own folder and re-saves — but the protection is gone. |
| Login throttling | `LoginRateLimiter` | The attempt cap becomes N times looser with N instances. |
| Concurrency caps | `ApiWorkerPool` (`MaxVideoInFlight`, `MaxVideoInFlightPerUser`) | Semaphores are per-process, so the real ceiling is N× the configured one. Credit accounting is DB-backed and still correct, but the burst rate against providers is not what the config says. |
| Admin metrics | `ServerMetricsService` | Each instance reports only its own traffic, so the admin page shows a fraction of reality. |

## Already safe

Backed by SQLite or the project directory rather than memory:

- `CreditService` → `UserDatabaseService` (money is not at risk)
- `MediaRegistryService` → SQLite `media_objects`
- `ProjectLeaseService` → `projects/{id}/leases/` on disk
- `ProjectStore` → the project directory

…subject to the shared-storage caveat above.

## Rough order to fix

1. **Sticky sessions first.** Routing a user's REST calls and their socket to one instance makes
   most of the table above stop mattering immediately, and buys time for the rest.
2. **A SignalR backplane** (Redis), because losing generated media is the most expensive failure
   here and the one the user cannot recover from.
3. **Shared job + ticket state**, so polling and media downloads stop depending on which instance
   answered.
4. **Move `MediaSaveClaims` onto `ProjectLeaseService`** — it is the same shape (TTL, holder-checked
   release) and already on disk. Kept separate for now only because it would put sub-second
   machinery into a store whose other entries are user-facing collaboration state.
