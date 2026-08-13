import fs from "node:fs";
import path from "node:path";
const API = "http://127.0.0.1:5088";
const PROJECT = "The_Jungle_Book";
const KEYS = ["Character_Bagheera", "Character_Shere_Khan"];
const HEADERS = { "Content-Type": "application/json", "X-FilmStudio-User": "pilot", "X-FilmStudio-Role": "admin" };
async function api(method, p, body) {
  const r = await fetch(API + p, { method, headers: HEADERS, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let json = null; try { json = JSON.parse(text); } catch {}
  return { ok: r.ok, status: r.status, json, text };
}
async function waitJobsIdle(timeoutMs = 20 * 60_000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const j = await api("GET", `/api/jobs?projectId=${encodeURIComponent(PROJECT)}`);
    const jobs = j.json?.jobs || [];
    const active = jobs.find((x) => /queued|running/i.test(x.status || ""));
    if (!active) { await new Promise((r) => setTimeout(r, 500)); return; }
    console.log("job", active.status, (active.message || "").slice(0, 100));
    await new Promise((r) => setTimeout(r, 2000));
  }
  throw new Error("timeout");
}
const dir = path.resolve("../../projects/The_Jungle_Book/assets/characters");
for (const key of KEYS) {
  console.log("===", key);
  const gen = await api("POST", "/api/jobs/character-variants", {
    projectId: PROJECT, charKey: key, count: 3, seedMode: "none",
    includePreferred: false, includeLockedRef: false, maxRefs: 0, persistDescription: true,
  });
  console.log("gen", gen.status, gen.ok);
  if (!gen.ok) { console.log(gen.text.slice(0, 300)); continue; }
  await waitJobsIdle();
  const stem = key.toLowerCase();
  for (let i = 1; i <= 3; i++) {
    const p = path.join(dir, `${stem}_variant_0${i}.png`);
    console.log(p, fs.existsSync(p) ? fs.statSync(p).size : "missing");
  }
  for (const idx of [2, 1, 3]) {
    const lock = await api("POST", `/api/projects/${encodeURIComponent(PROJECT)}/characters/${encodeURIComponent(key)}/lock-variant`, { index: idx });
    console.log("lock", idx, lock.ok, lock.ok ? "OK" : lock.text.slice(0, 180));
    if (lock.ok) break;
  }
}
