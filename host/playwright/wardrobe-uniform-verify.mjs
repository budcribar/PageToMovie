/**
 * Verify the shared wardrobe/uniform-lock feature end to end:
 *
 *   Three characters (Officer Reynolds / Hale / Briggs) point at one
 *   wardrobe_lock_tokens group (Wardrobe_PoliceOfficer). The FIRST officer generated
 *   should create the shared costume-only reference plate; the following two should
 *   REUSE that exact same file instead of each re-imagining "civil hat"/"badge" from
 *   scratch — that reuse is the whole point of the feature (see CharacterDesignService.
 *   EnsureWardrobeReferenceAsync + the "COSTUME REFERENCE ONLY" prompt clause in
 *   GrokImageClient.EditVariantsAsync).
 *
 * Runs against a throwaway project (never touches TellTaleHeartV7's real locked
 * assets) using FakeGrokImageClient, so it's free and fast. It checks the PLUMBING —
 * one shared file generated once and reused, correct job-log evidence, the character
 * lock flow, and that the real Blazor Characters page renders the result without
 * error — not visual fidelity (fakes return a 1x1 placeholder PNG, not a real
 * portrait), which needs a small real-API pass to confirm separately.
 *
 * Prereqs (see host/playwright/README.md Phase A — fakes):
 *   API on http://127.0.0.1:5088 with FilmStudio__UseFakes=true
 *   Web on http://localhost:5079
 *
 *   node wardrobe-uniform-verify.mjs
 */
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const WEB_URL = (process.env.WEB_URL || "http://localhost:5079").replace(/\/$/, "");
const API_URL = (process.env.API_URL || "http://127.0.0.1:5088").replace(/\/$/, "");
const WORKSPACE = path.resolve(__dirname, "..", "..");
const PROJECT =
  process.env.PROJECT_ID ||
  `WardrobeVerify_${new Date().toISOString().replace(/[:.]/g, "").slice(0, 15)}`;
const ARTIFACTS = path.join(
  __dirname,
  "artifacts",
  `wardrobe-verify-${new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19)}`
);

const HEADERS = {
  "Content-Type": "application/json",
  "X-FilmStudio-User": "pilot",
  "X-FilmStudio-Role": "admin",
};

function log(...a) {
  const line = `[${new Date().toISOString()}] ${a.join(" ")}`;
  console.log(line);
  fs.mkdirSync(ARTIFACTS, { recursive: true });
  fs.appendFileSync(path.join(ARTIFACTS, "run.log"), line + "\n");
}

async function api(method, p, body) {
  const r = await fetch(`${API_URL}${p}`, {
    method,
    headers: HEADERS,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    /* not json */
  }
  return { ok: r.ok, status: r.status, json, text };
}

function jget(obj, ...names) {
  for (const n of names) {
    if (obj?.[n] !== undefined) return obj[n];
  }
  return undefined;
}

async function waitJobIdle(timeoutMs = 5 * 60_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const j = await api("GET", `/api/jobs?projectId=${encodeURIComponent(PROJECT)}`);
    const jobs = jget(j.json || {}, "jobs", "Jobs") || [];
    const active = jobs.some((x) => /queued|running/i.test(jget(x, "status", "Status") || ""));
    if (!active) return;
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error("timeout waiting for jobs to go idle");
}

function charDir() {
  return path.join(WORKSPACE, "projects", PROJECT, "assets", "characters");
}

function fileStat(name) {
  const p = path.join(charDir(), name);
  return fs.existsSync(p) ? fs.statSync(p) : null;
}

const results = { project: PROJECT, checks: [] };
function check(name, pass, detail) {
  results.checks.push({ name, pass: !!pass, detail: detail ?? "" });
  log(pass ? "PASS" : "FAIL", name, detail ?? "");
}

async function genVariants(charKey) {
  const gen = await api("POST", "/api/jobs/character-variants", {
    projectId: PROJECT,
    charKey,
    count: 3,
    seedMode: "none", // force no identity/book refs — isolates the costume-only-ref path
    includePreferred: false,
    includeLockedRef: false,
    maxRefs: 0,
    persistDescription: false,
  });
  if (!gen.ok) throw new Error(`generate ${charKey} failed: ${gen.status} ${gen.text}`);
  const jobId = jget(jget(gen.json, "job", "Job") || {}, "jobId", "JobId");
  await waitJobIdle();
  let jobLog = [];
  if (jobId) {
    const jd = await api("GET", `/api/jobs/${jobId}`);
    jobLog = jget(jget(jd.json, "job", "Job") || {}, "log", "Log") || [];
  }
  return jobLog;
}

function seedThrowawayProject() {
  // Fresh throwaway project — NEVER touches TellTaleHeartV7's real locked assets.
  // Seed with the migrated wardrobe_lock_tokens cast (copied from TellTaleHeartV7,
  // which already has all three officers pointing at Wardrobe_PoliceOfficer). Also copy
  // the signed screenplay + sign-off metadata verbatim (hash-based sign-off, so identical
  // bytes stay "signed") purely so the Characters UI's screenplay-approved gate opens —
  // the wardrobe feature itself doesn't touch the screenplay at all.
  const sourceDir = path.join(WORKSPACE, "projects", PROJECT, "source");
  fs.mkdirSync(sourceDir, { recursive: true });
  const fixtureSourceDir = path.join(WORKSPACE, "projects", "TellTaleHeartV7", "source");
  for (const name of ["cast_seeds.json", "screenplay.fountain", "screenplay_meta.json"]) {
    const src = path.join(fixtureSourceDir, name);
    if (!fs.existsSync(src)) throw new Error(`missing fixture: ${src}`);
    fs.copyFileSync(src, path.join(sourceDir, name));
  }
  log("seeded cast_seeds.json (wardrobe_lock_tokens) + signed screenplay from TellTaleHeartV7");
}

function checkOfficerJobLog(key, i, jobLog) {
  const generatedShared = jobLog.some((l) => /Generating shared uniform reference/i.test(l));
  const reusedShared = jobLog.some((l) => /shared costume ref/i.test(l));
  const modeWardrobeLocked = jobLog.some((l) => /mode=.*wardrobe_locked/i.test(l));
  if (i === 0) {
    check(`${key}: generated the shared uniform plate (first officer)`, generatedShared);
  } else {
    check(
      `${key}: reused the existing uniform plate, did NOT regenerate it`,
      !generatedShared && reusedShared
    );
  }
  check(`${key}: job mode reports wardrobe_locked`, modeWardrobeLocked, jobLog[jobLog.length - 1]);
}

function checkSharedWardrobeFile(key, i, wardrobeFile, wardrobeStatAfterFirst) {
  const wStat = fileStat(wardrobeFile);
  check(`${key}: shared wardrobe ref exists on disk`, !!wStat);
  if (i === 0) return wStat;
  if (wardrobeStatAfterFirst && wStat) {
    check(
      `${key}: shared wardrobe ref is byte-identical to the one from officer #1 (not regenerated)`,
      wStat.size === wardrobeStatAfterFirst.size && wStat.mtimeMs === wardrobeStatAfterFirst.mtimeMs,
      `size ${wStat.size} vs ${wardrobeStatAfterFirst.size}, mtime ${wStat.mtimeMs} vs ${wardrobeStatAfterFirst.mtimeMs}`
    );
  }
  return wardrobeStatAfterFirst;
}

async function generateAndLockOfficer(key, i, wardrobeFile, wardrobeStatAfterFirst) {
  log("=== generating", key, "===");
  const jobLog = await genVariants(key);
  fs.writeFileSync(path.join(ARTIFACTS, `${key}.joblog.txt`), jobLog.join("\n"));

  const variants = [1, 2, 3].map((n) => fileStat(`${key.toLowerCase()}_variant_0${n}.png`));
  check(`${key}: 3 variant files written`, variants.every(Boolean), variants.map((s) => s?.size).join(","));

  checkOfficerJobLog(key, i, jobLog);
  const nextStat = checkSharedWardrobeFile(key, i, wardrobeFile, wardrobeStatAfterFirst);

  const lock = await api(
    "POST",
    `/api/projects/${encodeURIComponent(PROJECT)}/characters/${encodeURIComponent(key)}/lock-variant`,
    { index: 1 }
  );
  check(`${key}: lock-variant succeeded`, lock.ok, (lock.text || "").slice(0, 200));
  return nextStat;
}

async function verifyCharactersUi(officers) {
  await api("POST", `/api/projects/${encodeURIComponent(PROJECT)}/activate`, {});
  const browser = await chromium.launch({ headless: process.env.HEADED !== "1" });
  try {
    const page = await browser.newPage();
    const consoleErrors = [];
    page.on("console", (m) => {
      if (m.type() === "error") consoleErrors.push(m.text());
    });
    page.on("pageerror", (e) => consoleErrors.push(String(e)));

    await page.goto(`${WEB_URL}/characters?admin=1`, { waitUntil: "networkidle" });
    await page.waitForTimeout(1500);
    await page.screenshot({ path: path.join(ARTIFACTS, "characters-page.png"), fullPage: true });
    check("Characters UI: loaded with no console/page errors", consoleErrors.length === 0, consoleErrors.join(" | "));

    const bodyText = (await page.textContent("body")) || "";
    for (const key of officers) {
      const label = key.replace("Character_Officer", "Officer ");
      check(`Characters UI: lists ${label}`, bodyText.includes(label) || bodyText.includes(key));
    }
  } finally {
    await browser.close();
  }
}

async function main() {
  log("API", API_URL, "WEB", WEB_URL, "project", PROJECT);
  const health = await api("GET", "/health");
  if (!health.ok)
    throw new Error(
      "API not healthy — start it with FilmStudio__UseFakes=true (see host/playwright/README.md Phase A)"
    );

  const created = await api("POST", "/api/projects", { name: PROJECT });
  if (!created.ok) throw new Error(`create project failed: ${created.status} ${created.text}`);
  log("created project", PROJECT);

  seedThrowawayProject();

  const officers = ["Character_OfficerReynolds", "Character_OfficerHale", "Character_OfficerBriggs"];
  const wardrobeFile = "wardrobe_policeofficer_ref.png";
  let wardrobeStatAfterFirst = null;

  for (let i = 0; i < officers.length; i++) {
    wardrobeStatAfterFirst = await generateAndLockOfficer(
      officers[i],
      i,
      wardrobeFile,
      wardrobeStatAfterFirst
    );
  }

  await verifyCharactersUi(officers);

  fs.writeFileSync(path.join(ARTIFACTS, "summary.json"), JSON.stringify(results, null, 2));
  const failed = results.checks.filter((c) => !c.pass);
  log("DONE", `${results.checks.length - failed.length}/${results.checks.length} checks passed`, "artifacts:", ARTIFACTS);
  console.log(JSON.stringify(results, null, 2));
  if (failed.length > 0) process.exit(1);
}

try {
  await main();
} catch (e) {
  console.error(e);
  process.exit(1);
}
