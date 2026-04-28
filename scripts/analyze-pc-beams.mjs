// Analyzer v3: leaf-dim detection.
//
// Insight (from user, confirmed by section drawing):
//   - The schedule's section D is the **concrete trunk depth**, NOT the
//     envelope-with-slab-and-haunch.
//   - In dim chains, the envelope dim (e.g. 800) = sum of leaf dims
//     (200 slab + 100 haunch + 500 trunk). The schedule D is the leaf.
//   - Same idea sometimes applies to B: the actual beam width vs the slab
//     on top. For PC beams here B = 800 is the bottom (main concrete) width.
//
// Heuristic:
//   - For each region's vertical dims, classify as "outer" or "leaf":
//     * Outer = contains other smaller vertical dims in its xline range.
//     * Leaf  = does not contain any smaller dim.
//   - D = largest LEAF vertical dim.
//   - B (horizontal) = the horizontal dim at the LOWEST Y (typically the
//     bottom-of-section dim showing main concrete width). Verified to be
//     800 for all 4 beams in this drawing.

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

// 1. Find labels.
const labels = texts.filter(t => /^P[SC][GBC]\d/.test(t.text.trim())).map(t => ({
  text: t.text.trim().split(/\s|\(/)[0],
  full: t.text.trim(),
  x: t.position[0],
  y: t.position[1],
}));
const seen = new Set();
const uniqLabels = labels.filter(l => {
  const key = `${l.text}@${Math.round(l.x / 100)},${Math.round(l.y / 100)}`;
  if (seen.has(key)) return false;
  seen.add(key);
  return true;
}).filter(l => l.full.includes("L=")); // drop noise like "PSG41-"

// 2. Voronoi regions.
function dist(a, b) { return Math.hypot(a.x - b.x, a.y - b.y); }
function dimXY(d) { return { x: d.text_position[0], y: d.text_position[1] }; }

const groups = uniqLabels.map(l => ({ label: l, dims: [] }));
for (const d of dims) {
  const dp = dimXY(d);
  let best = -1, bestDist = Infinity;
  for (let i = 0; i < uniqLabels.length; i++) {
    const ds = dist(dp, uniqLabels[i]);
    if (ds < bestDist) { bestDist = ds; best = i; }
  }
  if (best >= 0) groups[best].dims.push(d);
}

// 3. Leaf vs Outer classification.
//
// For vertical dims at roughly the same X, dim A "contains" dim B if:
//   - |A.x - B.x| < clusterTol (same X column)
//   - A's Y range fully contains B's Y range
// A leaf dim is one that contains NO other dim.

function yRange(dim) {
  const ys = [dim.xline1[1], dim.xline2[1]].sort((a, b) => a - b);
  return { y1: ys[0], y2: ys[1], x: (dim.xline1[0] + dim.xline2[0]) / 2 };
}

function isLeaf(dim, allDims) {
  const r = yRange(dim);
  for (const other of allDims) {
    if (other === dim) continue;
    const o = yRange(other);
    // "containers" share roughly the same X (same dim chain column)
    if (Math.abs(o.x - r.x) > 50) continue;   // tight: only same dim chain (column)
    // We're checking if `dim` CONTAINS `other` (dim is outer)
    if (r.y1 - 1 <= o.y1 && r.y2 + 1 >= o.y2 && (r.y2 - r.y1) > (o.y2 - o.y1) + 1) {
      return false; // dim contains other → dim is outer
    }
  }
  return true;
}

function isLeafX(dim, allDims) {
  // Same idea but for horizontal dims, swap X/Y.
  const xs = [dim.xline1[0], dim.xline2[0]].sort((a, b) => a - b);
  const r = { x1: xs[0], x2: xs[1], y: (dim.xline1[1] + dim.xline2[1]) / 2 };
  for (const other of allDims) {
    if (other === dim) continue;
    const oxs = [other.xline1[0], other.xline2[0]].sort((a, b) => a - b);
    const o = { x1: oxs[0], x2: oxs[1], y: (other.xline1[1] + other.xline2[1]) / 2 };
    if (Math.abs(o.y - r.y) > 50) continue;
    if (r.x1 - 1 <= o.x1 && r.x2 + 1 >= o.x2 && (r.x2 - r.x1) > (o.x2 - o.x1) + 1) {
      return false;
    }
  }
  return true;
}

// 4. Per-label, find B and D using leaf rule.
const results = [];
for (const g of groups) {
  const horiz = g.dims.filter(d => d.orientation === "horizontal");
  const vert = g.dims.filter(d => d.orientation === "vertical");

  const horizLeaf = horiz.filter(d => isLeafX(d, horiz));
  const vertLeaf = vert.filter(d => isLeaf(d, vert));

  // B = max horizontal leaf
  const B = Math.max(0, ...horizLeaf.map(d => d.measurement));
  // D = max vertical leaf
  const D = Math.max(0, ...vertLeaf.map(d => d.measurement));

  // Parse L and LL from label
  const lengthMatch = g.label.full.match(/L=([\d.]+)m/);
  const llMatch = g.label.full.match(/LL=([\d.]+)kN/);

  results.push({
    type: g.label.text,
    B: Math.round(B),
    D: Math.round(D),
    L: lengthMatch ? `${lengthMatch[1]}m` : "?",
    LL: llMatch ? `${llMatch[1]}kN/m²` : "?",
    full: g.label.full,
    horiz_count: horiz.length,
    vert_count: vert.length,
    horiz_leaf_values: horizLeaf.map(d => Math.round(d.measurement)).sort((a, b) => b - a),
    vert_leaf_values: vertLeaf.map(d => Math.round(d.measurement)).sort((a, b) => b - a),
    horiz_outer_values: horiz.filter(d => !isLeafX(d, horiz)).map(d => Math.round(d.measurement)),
    vert_outer_values: vert.filter(d => !isLeaf(d, vert)).map(d => Math.round(d.measurement)),
  });
}

// 5. Print details + summary.
console.log(`=== Per-label leaf classification ===`);
for (const r of results) {
  console.log(`\n  ${r.type}`);
  console.log(`    horizontal — leaf: [${r.horiz_leaf_values.join(", ")}]   outer: [${r.horiz_outer_values.join(", ")}]`);
  console.log(`    vertical   — leaf: [${r.vert_leaf_values.join(", ")}]   outer: [${r.vert_outer_values.join(", ")}]`);
  console.log(`    >>> B = ${r.B},  D = ${r.D}`);
}

console.log(`\n${"=".repeat(76)}`);
console.log(`타입       | B × D       | L      | LL          | full label`);
console.log(`${"-".repeat(76)}`);
for (const r of results) {
  console.log(`${r.type.padEnd(10)} | ${String(r.B).padStart(4)} × ${String(r.D).padEnd(4)} | ${r.L.padEnd(6)} | ${r.LL.padEnd(11)} | ${r.full}`);
}
