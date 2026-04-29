// RC 보 일람표 analyzer (with VOID handling + reading order).
//
// Output: per symbol, "<symbol>_<size or VIOD>" sorted by reading order
// (top-to-bottom, left-to-right within each row band).

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
  text: t.text.trim(),
  x: t.position[0],
  y: t.position[1],
}));

// Beam type pattern. Allows:
//   - multi-letter prefixes up to 4 letters (ALLWG, AWG, HG, AG, SG, etc.)
//   - floor part with commas / ranges
//   - suffix variations like "-ESC", "-1", "-2"
//   - comma-joined complete symbols (where 2nd+ may inherit floor implicitly)
// Strip internal whitespace + uppercase before regex matching.
// Symbols like "ALL WG0" / "ALL b1" / "ALL RAWG1A" appear with mixed case
// and embedded spaces in some sheets — treat as case-insensitive
// space-free for matching.
const FLOOR_OPT = String.raw`(?:-?\d+(?:~-?\d+)?(?:,-?\d+(?:~-?\d+)?)*)?`;
const TYPE      = String.raw`[A-Z]{0,7}[GB]\d+[A-Z]?(?:-[A-Z0-9]+)?`;
const ONE_SYM   = `${FLOOR_OPT}${TYPE}`;
const symPattern = new RegExp(`^${ONE_SYM}(?:,${ONE_SYM})*$`);

// Normalize: strip spaces, uppercase. Match against normalized form;
// keep original (with spaces and case preserved) for display.
function normalize(s) {
  return s.replace(/\s+/g, "").toUpperCase();
}
function isSymbol(s) {
  return symPattern.test(normalize(s));
}

// Smart split: if a comma-separated chunk has no [GB] letter (floor
// continuation like "4,5"), join with previous. If a chunk starts with a
// letter (no floor digit), inherit the floor from the previous chunk.
function splitSymbols(s) {
  const parts = s.split(",").map(p => p.trim());
  const out = [];
  let buf = "";
  let lastFloor = "";
  for (const p of parts) {
    if (!/[GB]\d/.test(p)) {
      // Floor continuation chunk like "4" in "4,5HG1A"
      buf = buf ? buf + "," + p : p;
      continue;
    }
    // p has a [GB] letter — it's a complete-ish symbol
    let sym;
    if (buf) {
      sym = buf + "," + p;
      buf = "";
    } else {
      sym = p;
    }
    // If the symbol doesn't start with a digit, it has no floor —
    // inherit the floor from the previous symbol.
    if (!/^-?\d/.test(sym) && lastFloor) sym = lastFloor + sym;
    // Capture floor for next iteration
    const fmatch = sym.match(/^(-?\d+(?:~-?\d+)?(?:,-?\d+(?:~-?\d+)?)*)/);
    if (fmatch) lastFloor = fmatch[1];
    out.push(sym);
  }
  if (buf) out.push(buf);
  return out;
}
const sizePattern = /^(\d+)\s*[xX×]\s*(\d+)$/;

const symbols = [];
const sizes = [];
const voids = [];
const sheetTitles = []; // {name, x, y}
const titlePattern = /일람표[^"]*?-?\d/;

for (const t of texts) {
  if (isSymbol(t.text)) {
    for (const part of splitSymbols(normalize(t.text))) {
      symbols.push({ symbol: part, x: t.x, y: t.y });
    }
  } else if (sizePattern.test(t.text)) {
    const m = t.text.match(sizePattern);
    sizes.push({ B: parseInt(m[1]), D: parseInt(m[2]), x: t.x, y: t.y });
  } else if (/^VOID$/i.test(t.text)) {
    voids.push({ x: t.x, y: t.y });
  } else if (titlePattern.test(t.text) && /일람표/.test(t.text)) {
    // Strip leading "■" and whitespace
    const name = t.text.replace(/^■\s*/, "").trim();
    sheetTitles.push({ name, x: t.x, y: t.y });
  }
}

// Determine sheet bounds from grid layout.
// Each sheet's title sits at the TOP-LEFT of the sheet. The sheet extends:
//   - rightward up to the next title's X (or +SHEET_WIDTH default)
//   - downward up to the previous Y row's titles (or by SHEET_HEIGHT default)
const SHEET_WIDTH = 25230;
const SHEET_HEIGHT = 26200; // a hair under observed Y-gap to avoid bleeding into row below

function findSheet(x, y) {
  // A sheet "contains" (x, y) if:
  //   title.x <= x < title.x + SHEET_WIDTH
  //   title.y - SHEET_HEIGHT < y <= title.y
  for (const t of sheetTitles) {
    if (x >= t.x && x < t.x + SHEET_WIDTH
        && y > t.y - SHEET_HEIGHT && y <= t.y) {
      return t.name;
    }
  }
  return "(unknown)";
}

console.log(`Symbols found: ${symbols.length}`);
console.log(`Sizes found: ${sizes.length}`);
console.log(`VOIDs found: ${voids.length}`);

// Per-symbol "cell" search: each beam type's cell extends BELOW the symbol
// up to ROW_HEIGHT and ±CELL_HALF_WIDTH around it. Find the closest
// size or VOID inside that cell.
const ROW_HEIGHT = 5000;     // Allow up to ~5000u below symbol (HORIZONTAL sheets ~4500u offset)
const CELL_HALF_WIDTH = 1500; // Each cell contains 단부+중앙부 sub-columns within ±~1000

function inCell(s, c) {
  const dx = c.x - s.x;
  const dy = s.y - c.y; // positive = c is BELOW s
  return Math.abs(dx) < CELL_HALF_WIDTH && dy > 0 && dy < ROW_HEIGHT;
}

function dist(a, b) { return Math.hypot(a.x - b.x, a.y - b.y); }

const matches = [];
const unmatched = [];
for (const s of symbols) {
  const sizeIn = sizes.filter(c => inCell(s, c)).sort((a, b) => dist(a, s) - dist(b, s));
  const voidIn = voids.filter(c => inCell(s, c)).sort((a, b) => dist(a, s) - dist(b, s));

  let label = null;
  if (sizeIn.length && voidIn.length) {
    // Prefer the closer one
    label = dist(sizeIn[0], s) <= dist(voidIn[0], s)
      ? `${sizeIn[0].B} x ${sizeIn[0].D}` : "(VIOD)";
  } else if (sizeIn.length) {
    label = `${sizeIn[0].B} x ${sizeIn[0].D}`;
  } else if (voidIn.length) {
    label = "(VIOD)";
  }

  if (label) {
    matches.push({ symbol: s.symbol, label, x: s.x, y: s.y, sheet: findSheet(s.x, s.y) });
  } else {
    unmatched.push(s);
  }
}

// Group matches by sheet, then within each sheet sort by reading order.
const bySheet = new Map();
for (const m of matches) {
  const s = m.sheet;
  if (!bySheet.has(s)) bySheet.set(s, []);
  bySheet.get(s).push(m);
}

// Sort sheets by their title position (top→bottom, left→right).
const sheetOrder = [...sheetTitles].sort((a, b) => b.y - a.y || a.x - b.x);
const orderedSheetNames = sheetOrder.map(t => t.name);
// Append "(unknown)" at the end if any
if (bySheet.has("(unknown)") && !orderedSheetNames.includes("(unknown)")) {
  orderedSheetNames.push("(unknown)");
}

const ordered = [];
for (const sheetName of orderedSheetNames) {
  const arr = bySheet.get(sheetName);
  if (!arr) continue;

  // Dedupe within the sheet (same symbol appearing multiple times in one sheet)
  const seen = new Set();
  const dedup = [];
  for (const m of arr) {
    const k = `${m.symbol}|${m.label}`;
    if (seen.has(k)) continue;
    seen.add(k);
    dedup.push(m);
  }

  // Reading order within sheet
  dedup.sort((a, b) => b.y - a.y);
  const bands = [];
  let cur = [], curY = null;
  const Y_BAND = 500;
  for (const m of dedup) {
    if (curY === null || Math.abs(curY - m.y) > Y_BAND) {
      if (cur.length) bands.push(cur);
      cur = [m]; curY = m.y;
    } else {
      cur.push(m);
    }
  }
  if (cur.length) bands.push(cur);
  for (const band of bands) band.sort((a, b) => a.x - b.x);
  ordered.push(...bands.flat());
}

console.log(`\n=== Final list (${ordered.length} unique entries, reading order) ===`);
for (let i = 0; i < ordered.length; i++) {
  const m = ordered[i];
  console.log(`${String(i + 1).padStart(3)}.  ${m.symbol}_${m.label}\t<<SHEET>>${m.sheet}`);
}

if (unmatched.length > 0) {
  console.log(`\n=== Unmatched symbols (${unmatched.length}) ===`);
  for (const u of unmatched.slice(0, 20)) {
    console.log(`  ${u.symbol} @ [${Math.round(u.x)}, ${Math.round(u.y)}]`);
  }
  if (unmatched.length > 20) console.log(`  ... and ${unmatched.length - 20} more`);
}

const voidCount = ordered.filter(m => m.label === "(VIOD)").length;
console.log(`\n총 ${ordered.length}개 (사이즈: ${ordered.length - voidCount}, VOID: ${voidCount})`);
