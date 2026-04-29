// Verify per-sheet: list all candidate symbols vs what we matched.
// Spot any sheets where the count is suspicious (unusually low or many
// orphans).

import fs from "fs";

const textsFile = process.env.TEXTS;

function loadJson(path) {
  const raw = fs.readFileSync(path, "utf8");
  const start = raw.indexOf("{");
  let depth = 0, end = -1;
  for (let i = start; i < raw.length; i++) {
    if (raw[i] === "{") depth++;
    else if (raw[i] === "}") { depth--; if (depth === 0) { end = i + 1; break; } }
  }
  return JSON.parse(raw.slice(start, end));
}

const texts = loadJson(textsFile).data.texts.map(t => ({
  text: t.text.trim(), x: t.position[0], y: t.position[1]
}));

// Find sheet titles
const titles = texts.filter(t => /일람표/.test(t.text) && /^■/.test(t.text)).map(t => ({
  name: t.text.replace(/^■\s*/, "").trim(),
  x: t.x, y: t.y,
}));

const SHEET_WIDTH = 25230;
const SHEET_HEIGHT = 26200;

// For each sheet, find ALL texts inside
function inSheetRegion(t, sheet) {
  return t.x >= sheet.x && t.x < sheet.x + SHEET_WIDTH
      && t.y > sheet.y - SHEET_HEIGHT && t.y <= sheet.y;
}

// Symbol candidates: anything that contains [GB] + digit, no Korean,
// length under 30, not a size or rebar spec.
const sizePattern = /^\d+\s*[xX×]\s*\d+$/;
function looksLikeSymbol(s) {
  if (sizePattern.test(s)) return false;
  if (/^VOID$/i.test(s)) return false;
  if (/일람표/.test(s)) return false;
  if (/[가-힣]/.test(s)) return false;
  if (/[xX×]/.test(s)) return false;
  if (/@/.test(s)) return false;
  if (s.length > 30) return false;
  return /[GBgb]\d/.test(s) && /^[-\d~,A-Za-z\s]+$/.test(s);
}

const FLOOR_OPT = String.raw`(?:-?\d+(?:~-?\d+)?(?:,-?\d+(?:~-?\d+)?)*)?`;
const TYPE      = String.raw`[A-Z]{0,7}[GB]\d+[A-Z]?(?:-[A-Z0-9]+)?`;
const ONE_SYM   = `${FLOOR_OPT}${TYPE}`;
const symPattern = new RegExp(`^${ONE_SYM}(?:,${ONE_SYM})*$`);

function normalize(s) { return s.replace(/\s+/g, "").toUpperCase(); }
const _origTest = symPattern.test.bind(symPattern);
symPattern.test = (s) => _origTest(normalize(s));

function splitSymbols(s) {
  const parts = s.split(",").map(p => p.trim());
  const out = [];
  let buf = "";
  for (const p of parts) {
    if (!/[GB]\d/.test(p)) { buf = buf ? buf + "," + p : p; continue; }
    let sym = buf ? buf + "," + p : p;
    buf = "";
    out.push(sym);
  }
  if (buf) out.push(buf);
  return out;
}

// Sort sheets by reading order (top→bottom, left→right)
titles.sort((a, b) => b.y - a.y || a.x - b.x);

console.log("=".repeat(95));
console.log(`${"Sheet".padEnd(40)} | candidate | regex-match | split-count | suspicious`);
console.log("=".repeat(95));

for (const sheet of titles) {
  const regionTexts = texts.filter(t => inSheetRegion(t, sheet));
  const candidates = regionTexts.filter(t => looksLikeSymbol(t.text));
  const matched = candidates.filter(t => symPattern.test(t.text));
  const splitCount = matched.reduce((sum, t) => sum + splitSymbols(t.text).length, 0);
  const orphans = candidates.filter(t => !symPattern.test(t.text));

  const susp = orphans.length > 0 ? `⚠️ ${orphans.length} unmatched: ${orphans.slice(0, 3).map(o => o.text).join(", ")}` : "";
  console.log(`${sheet.name.padEnd(40)} | ${String(candidates.length).padStart(9)} | ${String(matched.length).padStart(11)} | ${String(splitCount).padStart(11)} | ${susp}`);
}
