# Multi-user project sharing

## Model

- **ACL** (`project-acl.json` in project dir): `owner`, `editors[]`, `viewers[]`, monotonic `rev`
- **Leases** (`leases/{resource}.json`): exclusive lock for `project`, `script`, or `scene:{n}` (HTTP **423** if held)
- **Presence**: in-memory heartbeats; SignalR hub `/hubs/project`
- **Rev**: optimistic concurrency token; clients can `GET/POST .../rev`

## HTTP

| Method | Path | Access |
|--------|------|--------|
| GET | `/api/projects/{id}/acl` | viewer+ |
| POST | `/api/projects/{id}/acl/editors` body `{ "userId" }` | owner |
| DELETE | `/api/projects/{id}/acl/editors/{userId}` | owner |
| POST | `/api/projects/{id}/acl/viewers` | owner |
| DELETE | `/api/projects/{id}/acl/viewers/{userId}` | owner |
| POST | `/api/projects/{id}/leases/{resourceKey}/acquire` | editor+ |
| POST | `.../release` `.../renew` `.../transfer` | holder / editor |
| GET | `.../leases/{resourceKey}` | viewer+ |
| GET | `.../presence` | viewer+ |
| GET/POST | `.../rev` | viewer+ / editor+ |

## SignalR

- Hub: `/hubs/project`
- `JoinProject` / `LeaveProject` / `Heartbeat`
- Events: `PresenceChanged`, `LeaseChanged`

## Legacy projects

If no `project-acl.json` exists, access falls back to the `owner` segment of `projectId` (`owner/name`).
