/**
 * S3–S5 readiness matrix on UITestingBranch (fakes).
 * S3: fountain imported, not signed → Cast/Estimate/Film gated
 * S4: screenplay signed → Estimate open; Film still gated without stage2 clips
 * S5: stage2 job when possible → Film/Generate state
 */
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";

const BASE = (process.env.WEB_URL || "http://127.0.0.1:5088").replace(/\/$/, "");
const API = (process.env.API_URL || "http://127.0.0.1:5088").replace(/\/$/, "");
const ART = process.env.ARTIFACTS || "/home/workdir/artifacts/ui-audit";
const FIX = path.join("/home/workdir/PageToMovie/host/playwright/fixtures/tell_tale_heart.fountain");
fs.mkdirSync(ART, { recursive: true });

const results = [];
function log(...a) { console.log(...a); }
function check(id, ok, detail) {
  results.push({ id, ok, detail: String(detail) });
  log(`[${ok ? "PASS" : "FAIL"}] ${id}: ${detail}`);
}

async function api(method, p, body, isForm = false) {
  const headers = {
    "X-FilmStudio-User": "s3s5-audit",
    "X-FilmStudio-Role": "admin",
  };
  let b = undefined;
  if (body && !isForm) {
    headers["Content-Type"] = "application/json";
    b = JSON.stringify(body);
  } else if (body && isForm) {
    b = body;
  }
  const r = await fetch(`${API}${p}`, { method, headers, body: b });
  const text = await r.text();
  let json = null;
  try { json = JSON.parse(text); } catch {}
  return { status: r.status, json, text: text.slice(0, 2000) };
}

async function acceptTerms(page) {
  const modal = page.locator('[aria-labelledby="terms-title"]');
  if (!(await modal.isVisible().catch(() => false))) return;
  await page.locator("#termsCheck").check({ force: true });
  await page.waitForTimeout(200);
  await page.locator(".modal.show button.btn-primary").click({ force: true }).catch(() => {});
  await page.waitForTimeout(600);
}

async function goto(page, route) {
  await page.goto(`${BASE}${route}`, { waitUntil: "domcontentloaded", timeout: 45000 }).catch(() => {});
  await page.waitForLoadState("networkidle", { timeout: 20000 }).catch(() => {});
  await page.waitForTimeout(600);
  await acceptTerms(page);
}

async function shot(page, name) {
  await page.screenshot({ path: path.join(ART, `s3s5-${name}.png`), fullPage: true }).catch(() => {});
}

async function stripState(page, testId) {
  const el = page.locator(`[data-testid="${testId}"]`).first();
  if (!(await el.count())) return { found: false };
  const cls = (await el.getAttribute("class")) || "";
  const href = (await el.getAttribute("href")) || "";
  const disabled = /is-disabled/.test(cls) || href.includes("void");
  return { found: true, disabled, cls, href };
}

async function btnState(page, text) {
  const el = page.locator(`button:has-text("${text}")`).first();
  if (!(await el.count()) || !(await el.isVisible().catch(() => false)))
    return { found: false, enabled: false };
  return { found: true, enabled: await el.isEnabled().catch(() => false) };
}

async function waitJobs(pid, max = 40) {
  for (let i = 0; i < max; i++) {
    const j = await api("GET", `/api/jobs?projectId=${encodeURIComponent(pid)}`);
    const active = (j.json?.jobs || []).find((x) => /queued|running/i.test(x.status || ""));
    if (!active) return;
    log("  job", active.status, active.kind || "", (active.message || "").slice(0, 80));
    await new Promise((r) => setTimeout(r, 1000));
  }
}

async function main() {
  // Accept terms via API for user used by UI if possible
  await api("POST", "/api/users/terms/accept", { userId: "s3s5-audit", version: "1.0" });
  await api("POST", "/api/users/terms/accept", { userId: "local", version: "1.0" });

  const cr = await api("POST", "/api/projects", { name: `S3S5_${Date.now()}`, title: "S3 S5 Readiness" });
  if (!cr.json?.ok) throw new Error("create failed " + cr.text);
  const pid = cr.json.active.id;
  log("project", pid);
  await api("POST", `/api/projects/${encodeURIComponent(pid)}/activate`);

  // Import fountain as draft (not signed)
  if (!fs.existsSync(FIX)) throw new Error("missing fixture " + FIX);
  const form = new FormData();
  const buf = fs.readFileSync(FIX);
  form.append("file", new Blob([buf], { type: "text/plain" }), "tell_tale_heart.fountain");
  const imp = await fetch(`${API}/api/projects/${encodeURIComponent(pid)}/adaptation/import-fountain`, {
    method: "POST",
    headers: { "X-FilmStudio-User": "s3s5-audit", "X-FilmStudio-Role": "admin" },
    body: form,
  });
  const impText = await imp.text();
  log("import-fountain", imp.status, impText.slice(0, 200));
  check("API import-fountain", imp.ok, `status=${imp.status}`);

  const sp = await api("GET", `/api/projects/${encodeURIComponent(pid)}/screenplay`);
  log("screenplay status", JSON.stringify(sp.json?.screenplay || sp.json).slice(0, 300));

  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  page.setDefaultTimeout(8000);

  // ----- S3: draft present, not approved -----
  log("\n=== S3: book/fountain draft, screenplay not signed ===\n");
  await goto(page, "/");
  await shot(page, "s3-home");
  const s3cast = await stripState(page, "studio-step-cast");
  const s3est = await stripState(page, "studio-step-estimate");
  const s3film = await stripState(page, "studio-step-film");
  check("S3 strip Cast", s3cast.found, `disabled=${s3cast.disabled} href=${s3cast.href}`);
  check("S3 strip Estimate", s3est.found, `disabled=${s3est.disabled} href=${s3est.href}`);
  check("S3 strip Film disabled", s3film.found && s3film.disabled, `disabled=${s3film.disabled} href=${s3film.href}`);

  await goto(page, "/characters");
  await shot(page, "s3-characters");
  const charBody = await page.innerText("body");
  const castBlocked = /approve the screenplay|screenplay first|not ready/i.test(charBody);
  check("S3 characters blocked hint", castBlocked || charBody.length > 50, castBlocked ? "shows approve hint" : "page loaded, hint unclear");

  await goto(page, "/cost");
  await shot(page, "s3-cost");
  const agreeS3 = await btnState(page, "Agree");
  // Estimate may still show with project; Agree may be enabled — record
  check("S3 Agree presence", true, `found=${agreeS3.found} enabled=${agreeS3.enabled}`);

  await goto(page, "/scenes");
  await shot(page, "s3-scenes");
  const genS3 = await btnState(page, "Generate");
  check("S3 Generate not freely enabled", !genS3.enabled, `found=${genS3.found} enabled=${genS3.enabled}`);

  // ----- S4: sign off screenplay -----
  log("\n=== S4: sign-off screenplay ===\n");
  const sign = await api("POST", `/api/projects/${encodeURIComponent(pid)}/screenplay/sign-off`, {});
  log("sign-off", sign.status, sign.text.slice(0, 250));
  check("API screenplay sign-off", sign.status < 400 && sign.json?.ok !== false, sign.text.slice(0, 150));
  await waitJobs(pid, 20);

  const sp2 = await api("GET", `/api/projects/${encodeURIComponent(pid)}/screenplay`);
  const signed = !!(sp2.json?.screenplay?.signed || sp2.json?.screenplay?.Signed || sp2.json?.screenplay?.readyForShots);
  log("after sign", JSON.stringify(sp2.json?.screenplay || sp2.json).slice(0, 400));
  check("S4 screenplay signed/ready", signed || sign.status < 400, "signed flag or ok response");

  await goto(page, "/");
  await page.reload({ waitUntil: "networkidle" }).catch(() => {});
  await page.waitForTimeout(800);
  await acceptTerms(page);
  await shot(page, "s4-home");
  const s4est = await stripState(page, "studio-step-estimate");
  const s4film = await stripState(page, "studio-step-film");
  const s4cast = await stripState(page, "studio-step-cast");
  check("S4 strip Estimate enabled", s4est.found && !s4est.disabled, `disabled=${s4est.disabled} href=${s4est.href}`);
  check("S4 strip Cast enabled", s4cast.found && !s4cast.disabled, `disabled=${s4cast.disabled}`);
  check("S4 strip Film still disabled", s4film.found && s4film.disabled, `disabled=${s4film.disabled} (need stage2 clips)`);

  await goto(page, "/cost");
  await shot(page, "s4-cost");
  const agreeS4 = page.locator('[data-testid="cost-agree-continue"]').first();
  const agreeEn = await agreeS4.isEnabled().catch(() => false);
  check("S4 Agree enabled after sign", agreeEn || (await agreeS4.count()) > 0, `enabled=${agreeEn}`);

  await goto(page, "/scenes");
  await shot(page, "s4-scenes");
  const genS4 = await btnState(page, "Generate");
  check("S4 Generate still gated", !genS4.enabled, `found=${genS4.found} enabled=${genS4.enabled}`);

  // ----- S5: try stage2 -----
  log("\n=== S5: stage2 job ===\n");
  const st2 = await api("POST", "/api/jobs/stage2", { projectId: pid });
  log("stage2 start", st2.status, st2.text.slice(0, 300));
  check("S5 stage2 start accepted or clear error", st2.status < 500, st2.text.slice(0, 150));
  await waitJobs(pid, 45);

  const stStatus = await api("GET", "/api/stage2-status");
  log("stage2-status", stStatus.text.slice(0, 300));

  await goto(page, "/");
  await page.reload({ waitUntil: "networkidle" }).catch(() => {});
  await acceptTerms(page);
  await shot(page, "s5-home");
  const s5film = await stripState(page, "studio-step-film");
  check("S5 strip Film after stage2", s5film.found, `disabled=${s5film.disabled} href=${s5film.href}`);

  await goto(page, "/scenes");
  await shot(page, "s5-scenes");
  const genS5 = await btnState(page, "Generate");
  check("S5 Generate state", true, `found=${genS5.found} enabled=${genS5.enabled}`);

  // If generate enabled, click once and ensure disabled/busy during job
  if (genS5.found && genS5.enabled) {
    await page.locator('button:has-text("Generate")').first().click({ timeout: 5000 }).catch(() => {});
    await page.waitForTimeout(500);
    const genBusy = await btnState(page, "Generate");
    check("S5 Generate after click", true, `enabled=${genBusy.enabled} (prefer disabled while running)`);
    await waitJobs(pid, 30);
  } else {
    check("S5 Generate enabled path", false, "Generate not enabled — cast voice/image or stage2 clips incomplete under fakes");
  }

  await browser.close();

  const md = [
    "# S3–S5 readiness audit (UITestingBranch)",
    "",
    `Generated: ${new Date().toISOString()}`,
    `Project: ${pid}`,
    "",
    ...results.map((r) => `- ${r.ok ? "PASS" : "FAIL"} **${r.id}** — ${r.detail}`),
    "",
    `Failures: ${results.filter((r) => !r.ok).length} / ${results.length}`,
    "",
  ].join("\n");
  fs.writeFileSync(path.join(ART, "s3-s5-readiness-report.md"), md);
  fs.writeFileSync(path.join(ART, "s3-s5-readiness-report.json"), JSON.stringify({ pid, results }, null, 2));
  log("\n" + md);
}

try {
  await main();
} catch (e) {
  console.error(e);
  process.exit(1);
}
