// Analyzer (end-section preferred, longest dim, fallback to opposite side).
//
// Rules (user-specified):
//   1. For each PC beam type, look at 양단부 (LEFT view, X < label.X) first.
//   2. If 양단부 region has no dims, fall back to 내단부/중앙부 (RIGHT view).
//   3. B = the longest horizontal DIM in the chosen side.
//   4. D = the longest vertical DIM in the chosen side.

import fs from "fs";

const textsFile = process.env.TEXTS;
const dimsFile = process.env.DIMS;

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

const texts = loadJson(textsFile).data.texts;
const dims = loadJson(dimsFile).data.dimensions;

const labelRe = /^P[SC][GBC]\d/;
const rawLabels = texts.filter(t => labelRe.test(t.text.trim()));

const labelMap = new Map();
for (const t of rawLabels) {
  const key = t.text.trim().split(/[\s(]/)[0];
  if (!labelMap.has(key)) labelMap.set(key, []);
  labelMap.get(key).push({ x: t.position[0], y: t.position[1], full: t.text.trim() });
}

console.log(`=== Found ${labelMap.size} unique PC beam types in selection ===`);

function dist(ax, ay, bx, by) { return Math.hypot(ax - bx, ay - by); }

const RESULTS = [];

for (const [labelKey, occurrences] of labelMap.entries()) {
  occurrences.sort((a, b) => b.y - a.y);

  for (let occIdx = 0; occIdx < occurrences.length; occIdx++) {
    const lab = occurrences[occIdx];

    const ownedDims = dims.filter(d => {
      const dx = d.text_position[0];
      const dy = d.text_position[1];
      const myDist = dist(dx, dy, lab.x, lab.y);
      for (const otherOccs of labelMap.values()) {
        for (const other of otherOccs) {
          if (other === lab) continue;
          if (dist(dx, dy, other.x, other.y) < myDist) return false;
        }
      }
      return myDist < 4000;
    });

    if (ownedDims.length === 0) continue;

    // Split by X relative to label
    const leftDims = ownedDims.filter(d => d.text_position[0] < lab.x);
    const rightDims = ownedDims.filter(d => d.text_position[0] >= lab.x);

    // Pick 양단부 (left) first; fall back to 내단부/중앙부 (right) if no dims on left.
    let chosenSide, sideLabel;
    if (leftDims.length > 0 && leftDims.some(d => d.orientation === "horizontal")
                             && leftDims.some(d => d.orientation === "vertical")) {
      chosenSide = leftDims;
      sideLabel = "양단부";
    } else if (rightDims.length > 0) {
      chosenSide = rightDims;
      sideLabel = "내단부(fallback)";
    } else {
      chosenSide = ownedDims;
      sideLabel = "all-region";
    }

    const horiz = chosenSide.filter(d => d.orientation === "horizontal");
    const vert = chosenSide.filter(d => d.orientation === "vertical");

    const B = horiz.length ? Math.max(...horiz.map(d => d.measurement)) : 0;
    const D = vert.length ? Math.max(...vert.map(d => d.measurement)) : 0;

    RESULTS.push({
      type: labelKey,
      sheet_idx: occIdx + 1,
      total_occurrences: occurrences.length,
      label_pos: [Math.round(lab.x), Math.round(lab.y)],
      chosen_side: sideLabel,
      end_dims_count: chosenSide.length,
      end_horiz_count: horiz.length,
      end_vert_count: vert.length,
      left_count: leftDims.length,
      right_count: rightDims.length,
      B: Math.round(B),
      D: Math.round(D),
      full: lab.full,
    });
  }
}

console.log(`\n=== Per-occurrence detail ===`);
for (const r of RESULTS) {
  const fb = r.chosen_side !== "양단부" ? ` ⚠️ ${r.chosen_side}` : "";
  if (r.B === 0 || r.D === 0) {
    console.log(`  ${r.type.padEnd(10)} sheet#${r.sheet_idx}/${r.total_occurrences}  L${r.left_count}/R${r.right_count}  B×D incomplete (${r.B}×${r.D})${fb}`);
  } else {
    console.log(`  ${r.type.padEnd(10)} sheet#${r.sheet_idx}/${r.total_occurrences}  L${r.left_count}/R${r.right_count}  ${r.B}×${r.D}${fb}`);
  }
}

// Dedupe per type: prefer occurrence with most dims.
const bestByType = new Map();
for (const r of RESULTS) {
  if (r.B === 0 || r.D === 0) continue;
  const cur = bestByType.get(r.type);
  if (!cur || r.end_dims_count > cur.end_dims_count) bestByType.set(r.type, r);
}

console.log(`\n${"=".repeat(85)}`);
console.log(`타입         | B × D       | side          | L          | LL          | (occ | dims)`);
console.log(`${"-".repeat(85)}`);
const rows = Array.from(bestByType.values()).sort((a, b) => a.type.localeCompare(b.type));
for (const r of rows) {
  const lengthMatch = r.full.match(/L=([\d.]+)m/);
  const llMatch = r.full.match(/LL=([\d.]+)kN/);
  const L = lengthMatch ? `${lengthMatch[1]}m` : "?";
  const LL = llMatch ? `${llMatch[1]}kN/m²` : "?";
  console.log(`${r.type.padEnd(12)} | ${String(r.B).padStart(4)} × ${String(r.D).padEnd(4)} | ${r.chosen_side.padEnd(13)} | ${L.padEnd(10)} | ${LL.padEnd(11)} | (${r.total_occurrences}, ${r.end_dims_count})`);
}

console.log(`\nTotal unique beam types: ${bestByType.size}`);
const fallbackCount = rows.filter(r => r.chosen_side !== "양단부").length;
if (fallbackCount > 0) {
  console.log(`Fallback used for: ${fallbackCount} types (no 양단부 dims found)`);
}
