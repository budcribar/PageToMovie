# Checklist — GitHub project packages, history & video vault

Track progress toward plan-aligned **namespaced projects**, **GitHub-backed text history**, and optional **paid video backup**.  
Source of truth for product intent: `public-community-plan.md` (namespacing + `PageToMovie-Projects` / org `Projects`).

**Legend:** `[ ]` open · `[~]` partial · `[x]` done

---

## Step 1 — Namespaced project paths + Git package (no video)

- [x] Disk layout `projects/{username}/{projectSlug}/` when `ownerUserId` is set at create
- [x] Composite project id `username/projectSlug` (legacy flat `projects/{id}/` still resolves)
- [x] `ListProjects` discovers one- and two-level project folders
- [x] Fork lands under **fork owner** namespace when owner is known
- [x] On create: ensure Git repo + `.gitignore` excluding video/audio; initial commit
- [ ] Full dual-write migration tool for all legacy flat projects (optional; resolve-on-read works)
- [ ] Public routes `/projects/@alice/Buster` (UI/API polish)

**Exit:** Two users can both own `Buster` without collision; each has a local Git package without MP4s.

---

## Step 2 — Commit discipline (server Git)

- [x] `POST /api/projects/{id}/commit` (existing)
- [x] Commit stages tracked files only (video ignored via `.gitignore`)
- [x] Debounced auto-commit on screenplay/cast/config/clip save (nested-repo guard skips app-repo demos)
- [x] Stage-end auto-commit: book prepared · screenplay · cast · Stage 2 · film/music job finished
- [ ] In-app commit timeline UI

---

## Step 3 — Protected project messaging

- [ ] UI copy: recipe/package backed up to Git; clips need media folder or vault
- [x] Surface last commit hash + History / Save revision on Home project bar

---

## Step 4 — Push package to GitHub + history link

- [x] `GitOptions` (`PageToMovie:Git:*`) — repo URL, token, enabled flag, branch prefix
- [x] `ProjectGitRepositoryService.PushProjectAsync` — push current branch tip to remote
- [x] `POST /api/projects/{id}/push` (owner/admin); optional `commit` then push
- [x] Response includes **GitHub history URL** (branch commits)
- [x] Wire “Save revision” + “View on GitHub” buttons in Studio UI (Home, owner/admin)
- [ ] CI test against local bare remote (optional)

**Config (Railway):**

```text
PageToMovie__Git__Enabled=true
PageToMovie__Git__ProjectsRepoUrl=https://github.com/PageToMovie/Projects.git
PageToMovie__Git__Token=<PAT with contents:write on that repo>
PageToMovie__Git__DefaultBranchPrefix=proj/
```

Branch naming: `proj/{username}/{slug}` (slashes → safe branch path) so each project has its own history on one monorepo remote without subtree complexity.

---

## Step 5 — Visibility modes (plan #3)

- [~] `visibilityMode` on project exists (Private / Public / Open)
- [ ] Enforce gallery fork only when **Open**
- [ ] Public read-only: play, no fork

---

## Step 6 — Collab loop on namespaced + pushed packages

- [x] Fork / sync-origin / contribution review (server-side)
- [ ] After fork: initial commit + optional push under forker namespace
- [ ] After sync merge: commit + push
- [ ] “Propose to parent” / GitHub PR link (optional)

---

## Step 7 — Auto-commit (guarded)

- [ ] Detect project path not nested inside app `.git` (issue-26)
- [ ] Hook saves → commit (debounced)
- [ ] Optional auto-push

---

## Step 8 — Video vault (paid GB) using media SHAs

- [ ] Object storage backend (R2/S3/B2) + config
- [ ] Upload/restore by `MediaRegistry` SHA-256
- [ ] Billing SKU / quota (or manual admin grant first)
- [ ] Restore into client media folder
- [ ] **Not** default GitHub LFS for all users (optional power-user later)

---

## Step 9 — Optional user GitHub LFS mirror

- [ ] User PAT + their repo
- [ ] Opt-in mirror of selected clips

---

## Explicitly out of scope (default)

- MP4s in the main GitHub monorepo (plan: ignore video; YouTube + vault + local folder)
- Replacing LibGit2Sharp server merge with “merge only on GitHub”

---

## Progress log

| Date | Notes |
|------|--------|
| 2026-07-26 | Checklist created. **Step 1** (namespace + git package) and **Step 4** (GitHub push API) implemented in product code. |
| 2026-07-26 | Wired `POST /api/projects/{id}/push` (optional `commitFirst`), tests for branch/history URL + disabled push, fork/visibility tests updated for `owner/slug` ids. |
| 2026-07-26 | **Step 4 UI:** Home “Save revision” + “View on GitHub” (after successful push); short hash badge; friendly errors when backup not configured. |

*Last updated: 2026-07-26*
