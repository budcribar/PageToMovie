/**
 * Generate 3 portrait variants for 10 Jungle Book characters and lock the first
 * that passes the style gate (simulates human pick-best).
 *
 * Requires API: http://127.0.0.1:5088 with real XAI (not fakes).
 *
 *   node jungle-cast-gen.mjs
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const API = (process.env.API_URL || "http://127.0.0.1:5088").replace(/\/$/, "");
const PROJECT = process.env.PROJECT_ID || "The_Jungle_Book";
const WORKSPACE = path.resolve(__dirname, "..", "..");
const ARTIFACTS = path.join(
  __dirname,
  "artifacts",
  `jungle-cast-${new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19)}`
);

const KEYS = [
  "Character_Mowgli",
  "Character_Baloo",
  "Character_Bagheera",
  "Character_Shere_Khan",
  "Character_Akela",
  "Character_Father_Wolf",
  "Character_Mother_Wolf",
  "Character_Tabaqui",
  "Character_Kaa",
  "Character_Narrator",
];

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
  const r = await fetch(`${API}${p}`, {
    method,
    headers: HEADERS,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    /* */
  }
  return { ok: r.ok, status: r.status, json, text };
}

async function waitJobsIdle(timeoutMs = 25 * 60_000) {
  const start = Date.now();
  let last = "";
  while (Date.now() - start < timeoutMs) {
    const j = await api("GET", `/api/jobs?projectId=${encodeURIComponent(PROJECT)}`);
    const jobs = j.json?.jobs || j.json?.Jobs || [];
    const active = jobs.find((x) => /queued|running/i.test(x.status || x.Status || ""));
    if (!active) {
      await new Promise((r) => setTimeout(r, 600));
      const j2 = await api("GET", `/api/jobs?projectId=${encodeURIComponent(PROJECT)}`);
      const jobs2 = j2.json?.jobs || j2.json?.Jobs || [];
      if (!jobs2.find((x) => /queued|running/i.test(x.status || x.Status || ""))) return;
      continue;
    }
    const msg = `${active.kind || active.Kind}|${active.message || active.Message || ""}`;
    if (msg !== last) {
      last = msg;
      log("job", active.status || active.Status, (active.message || active.Message || "").slice(0, 120));
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
  throw new Error("timeout waiting for jobs");
}

function charDir() {
  return path.join(WORKSPACE, "projects", PROJECT, "assets", "characters");
}

function listVariants(charKey) {
  const stem = String(charKey).toLowerCase();
  const dir = charDir();
  const found = [];
  for (let i = 1; i <= 3; i++) {
    const name = `${stem}_variant_0${i}.png`;
    const p = path.join(dir, name);
    if (fs.existsSync(p)) found.push({ index: i, path: p, bytes: fs.statSync(p).size });
  }
  return found;
}

function snapshot(charKey) {
  const dest = path.join(ARTIFACTS, "cast", charKey);
  fs.mkdirSync(dest, { recursive: true });
  const stem = String(charKey).toLowerCase();
  const dir = charDir();
  for (const f of fs.readdirSync(dir).filter((x) => x.startsWith(stem) && x.endsWith(".png"))) {
    fs.copyFileSync(path.join(dir, f), path.join(dest, f));
  }
}

async function pickAndLock(charKey) {
  const variants = listVariants(charKey);
  log("variants on disk", charKey, variants.map((v) => `v${v.index}=${v.bytes}B`).join(", ") || "none");
  if (variants.length === 0) throw new Error(`no variants for ${charKey}`);

  // Prefer middle variant as "human pick" if all lock; try 2, then 1, then 3
  const order = [2, 1, 3].filter((i) => variants.some((v) => v.index === i));
  for (const idx of order) {
    log("try lock", charKey, `variant ${idx}`);
    const lock = await api(
      "POST",
      `/api/projects/${encodeURIComponent(PROJECT)}/characters/${encodeURIComponent(charKey)}/lock-variant`,
      { index: idx }
    );
    if (lock.ok) {
      log("LOCKED", charKey, `variant ${idx}`);
      snapshot(charKey);
      return idx;
    }
    log("lock reject", charKey, idx, String(lock.status), (lock.text || "").slice(0, 200));
  }
  throw new Error(`could not lock any variant for ${charKey}`);
}

async function main() {
  log("API", API, "project", PROJECT);
  const health = await api("GET", "/health");
  if (!health.ok) throw new Error("API not healthy");

  const results = [];
  for (const key of KEYS) {
    log("===", key, "===");
    // clear mock plates that might confuse — gen will overwrite variants
    const gen = await api("POST", "/api/jobs/character-variants", {
      projectId: PROJECT,
      charKey: key,
      count: 3,
      seedMode: "none",
      includePreferred: false,
      includeLockedRef: false,
      maxRefs: 0,
      persistDescription: true,
    });
    if (!gen.ok) {
      log("gen fail", key, gen.status, (gen.text || "").slice(0, 300));
      // retry auto seed
      const gen2 = await api("POST", "/api/jobs/character-variants", {
        projectId: PROJECT,
        charKey: key,
        count: 3,
        seedMode: "auto",
        persistDescription: true,
      });
      if (!gen2.ok) {
        results.push({ key, ok: false, error: gen2.text?.slice(0, 200) });
        continue;
      }
    }
    try {
      await waitJobsIdle();
      const lockedIdx = await pickAndLock(key);
      results.push({ key, ok: true, lockedVariant: lockedIdx });
    } catch (e) {
      log("ERROR", key, e.message || e);
      results.push({ key, ok: false, error: String(e.message || e) });
    }
  }

  fs.writeFileSync(path.join(ARTIFACTS, "summary.json"), JSON.stringify({ project: PROJECT, results }, null, 2));
  log("DONE", results.filter((r) => r.ok).length, "/", results.length, "locked");
  console.log(JSON.stringify(results, null, 2));
}

try {
  await main();
} catch (e) {
  console.error(e);
  process.exit(1);
}
