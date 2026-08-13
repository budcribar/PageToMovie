# UI test checklist

## P0 — Studio product flows
- [ ] Terms gate (show / Accept persists / blocked until accept)
- [ ] Create project
- [ ] Delete project
- [ ] Length / cost card (bind, Agree enablement, loading)
- [ ] Out-of-range / empty inputs
- [ ] Rapid double-submit busy-disable

## P1 — Scenes / history
- [ ] Scene delete → history → revert
- [ ] Clip edit guards
- [ ] Navigate away mid-async
- [ ] Characters & Scenes EnsureLoadedAsync

## P2 — Multi-user
- [x] Two contexts isolated localStorage (smoke)
- [x] Concurrent demo no pageerror (smoke)
- [ ] Dual login storageState A/B
- [ ] ACL invite + 403 for non-member
- [ ] Scene lease 423 / release / transfer
- [ ] Concurrent edit + reload

## P3 — Admin / catalog
- [ ] Admin gate for anonymous
- [ ] Lab mode admin-only
- [ ] Models scan-for-updates UI

## P4 — Hardening
- [ ] Full route matrix
- [ ] Console severity filters
- [ ] API ACL/lease probes when collab ships
- [ ] Fake-mode deep walk

## Already covered (smoke)
- [x] Anonymous home / demo / studio shell
- [x] Demo not forced to login
- [x] Optional signed-in chrome via PLAYWRIGHT_AUTH_STORAGE
- [x] Capabilities-off model picker block
