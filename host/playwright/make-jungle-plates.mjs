/**
 * Render mock book-plate images for The_Jungle_Book plate-rank eval.
 * Uses Playwright to screenshot simple HTML cards (no real book art).
 *
 * Usage:
 *   cd host/playwright
 *   npm install && npx playwright install chromium
 *   node make-jungle-plates.mjs
 *
 * Writes:
 *   projects/The_Jungle_Book/source/book_images/
 *   projects/The_Jungle_Book/assets/characters/
 */
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, "..", "..");
const PROJECT = path.join(ROOT, "projects", "The_Jungle_Book");
const CAST = path.join(PROJECT, "source", "cast_seeds.json");
const BOOK_IMG = path.join(PROJECT, "source", "book_images");
const ASSETS = path.join(PROJECT, "assets", "characters");

function ensureDir(d) {
  fs.mkdirSync(d, { recursive: true });
}

function slug(key) {
  return key
    .replace(/^Character_/i, "")
    .replace(/[^A-Za-z0-9]+/g, "_")
    .replace(/^_|_$/g, "")
    .toLowerCase();
}

function cardHtml(title, subtitle, accent) {
  return `<!DOCTYPE html>
<html><head><meta charset="utf-8"/>
<style>
  html, body { margin: 0; width: 640px; height: 480px; overflow: hidden; }
  body {
    font-family: Georgia, "Times New Roman", serif;
    background: linear-gradient(160deg, ${accent} 0%, #1a1a14 55%, #0d0d0a 100%);
    color: #f5f0e6;
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .card {
    width: 560px;
    height: 400px;
    border: 3px solid rgba(245,240,230,0.35);
    border-radius: 12px;
    padding: 32px;
    box-sizing: border-box;
    background: rgba(0,0,0,0.35);
    display: flex;
    flex-direction: column;
    justify-content: flex-end;
  }
  .badge {
    position: absolute;
    top: 28px;
    left: 48px;
    font-size: 12px;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    opacity: 0.7;
  }
  .title { font-size: 36px; margin: 0 0 12px; line-height: 1.15; }
  .sub { font-size: 15px; line-height: 1.4; opacity: 0.88; max-height: 7.5em; overflow: hidden; }
</style></head>
<body>
  <div class="badge">book plate · mock</div>
  <div class="card">
    <h1 class="title">${escapeHtml(title)}</h1>
    <p class="sub">${escapeHtml(subtitle)}</p>
  </div>
</body></html>`;
}

function textPageHtml(n) {
  return `<!DOCTYPE html>
<html><head><meta charset="utf-8"/>
<style>
  body { margin:0; width:640px; height:480px; background:#f4efe6; color:#222;
    font-family: Georgia, serif; padding:40px; box-sizing:border-box; }
  h1 { font-size:18px; margin:0 0 16px; }
  p { font-size:13px; line-height:1.55; }
</style></head>
<body>
  <h1>Text page ${n}</h1>
  <p>Dense storybook prose without a clear character portrait. Used as a negative plate for ranking.</p>
  <p>Lorem jungle law and the night and the moon over the Seeonee hills — text only.</p>
</body></html>`;
}

function escapeHtml(s) {
  return String(s || "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

const ACCENTS = {
  wolf: "#4a5560",
  bear: "#6b4423",
  panther: "#1c1c28",
  tiger: "#a85a1a",
  human: "#5c6b7a",
  monkey: "#6a5a40",
  default: "#3d5a45",
};

function accentFor(key, desc) {
  const b = `${key} ${desc}`.toLowerCase();
  if (b.includes("wolf") || b.includes("akela")) return ACCENTS.wolf;
  if (b.includes("bear") || b.includes("baloo")) return ACCENTS.bear;
  if (b.includes("panther") || b.includes("bagheera")) return ACCENTS.panther;
  if (b.includes("tiger") || b.includes("shere")) return ACCENTS.tiger;
  if (b.includes("monkey") || b.includes("bandar")) return ACCENTS.monkey;
  if (b.includes("mowgli") || b.includes("human") || b.includes("messua") || b.includes("buldeo") || b.includes("narrator"))
    return ACCENTS.human;
  return ACCENTS.default;
}

async function shot(page, html, outPath) {
  await page.setContent(html, { waitUntil: "load" });
  await page.setViewportSize({ width: 640, height: 480 });
  await page.screenshot({ path: outPath, type: "png" });
}

async function main() {
  if (!fs.existsSync(CAST)) {
    console.error("Missing cast seeds:", CAST);
    process.exit(1);
  }
  ensureDir(BOOK_IMG);
  ensureDir(ASSETS);

  const seeds = JSON.parse(fs.readFileSync(CAST, "utf8")).character_seed_tokens || {};
  // Prefer major cast for plates; cap for speed
  const preferred = [
    "Character_Mowgli",
    "Character_Baloo",
    "Character_Bagheera",
    "Character_Shere_Khan",
    "Character_Akela",
    "Character_Father_Wolf",
    "Character_Mother_Wolf",
    "Character_Tabaqui",
    "Character_Kaa",
    "Character_Bandar_Log",
    "Character_Narrator",
    "Character_Messua",
    "Character_Buldeo",
  ];
  const keys = preferred.filter((k) => seeds[k]);
  for (const k of Object.keys(seeds)) {
    if (keys.length >= 16) break;
    if (!keys.includes(k)) keys.push(k);
  }

  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  const written = [];

  // Generic establishing pages
  for (let i = 1; i <= 3; i++) {
    const p = path.join(BOOK_IMG, `page_${String(i).padStart(2, "0")}_establishing.png`);
    await shot(page, cardHtml("Seeonee Hills", "Moon over teak and bamboo. Establishing landscape.", ACCENTS.default), p);
    written.push(p);
  }
  // Text-only decoys
  for (let i = 1; i <= 3; i++) {
    const p = path.join(BOOK_IMG, `text_page_${i}.png`);
    await shot(page, textPageHtml(i), p);
    written.push(p);
  }

  for (const key of keys) {
    const seed = seeds[key] || {};
    const name = (seed.canonical_given_name || key.replaceAll(/^Character_/g, "").replaceAll("_", " ")).trim();
    const desc = (seed.description || "").slice(0, 220);
    const s = slug(key);
    const accent = accentFor(key, desc);

    const bookPlate = path.join(BOOK_IMG, `plate_${s}.png`);
    await shot(page, cardHtml(name, desc, accent), bookPlate);
    written.push(bookPlate);

    // Product-style character asset names (ref + 2 variants)
    const ref = path.join(ASSETS, `character_${s}_ref.png`);
    const v1 = path.join(ASSETS, `character_${s}_variant_01.png`);
    const v2 = path.join(ASSETS, `character_${s}_variant_02.png`);
    await shot(page, cardHtml(name, desc, accent), ref);
    await shot(page, cardHtml(name + " · turn", desc, accent), v1);
    await shot(page, cardHtml(name + " · detail", desc, accent), v2);
    written.push(ref, v1, v2);
  }

  // Extra decoy asset not tied to a main hero
  await shot(
    page,
    cardHtml("Unknown figure", "Blurred background figure — wrong plate for ranking.", "#333"),
    path.join(ASSETS, "character_unknown_extra.png")
  );

  await browser.close();

  const manifest = {
    projectId: "The_Jungle_Book",
    generated: new Date().toISOString(),
    note: "Mock book plates via Playwright HTML screenshots (not real book art)",
    bookImages: fs.readdirSync(BOOK_IMG).filter((f) => f.endsWith(".png")),
    characterAssets: fs.readdirSync(ASSETS).filter((f) => f.endsWith(".png")),
    castKeys: keys,
  };
  fs.writeFileSync(path.join(BOOK_IMG, "mock_plates_manifest.json"), JSON.stringify(manifest, null, 2));
  console.log(`Wrote ${written.length} images`);
  console.log("book_images:", BOOK_IMG, manifest.bookImages.length);
  console.log("assets/characters:", ASSETS, manifest.characterAssets.length);
}

try {
  await main();
} catch (e) {
  console.error(e);
  process.exit(1);
}
