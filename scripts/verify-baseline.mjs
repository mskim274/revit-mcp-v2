#!/usr/bin/env node
// Compare current revit-mcp-v2 WebSocket responses against the
// pre-refactor baseline snapshot. Used to verify Phase 1 (npm
// package extraction) didn't introduce a regression.
//
// Usage: node scripts/verify-baseline.mjs
//
// Exits 0 on clean match, 1 on any diff.

import { readFileSync, writeFileSync, mkdirSync, readdirSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { join, basename, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const BASELINE = join(ROOT, "작업자료/2026-04-28/snapshots/baseline-pre-refactor");
const CURRENT = join(ROOT, "작업자료/2026-04-28/snapshots/post-phase1");
mkdirSync(CURRENT, { recursive: true });

const CASES = [
  ["01_ping", "ping"],
  ["02_get_levels", "get_levels"],
  ["03_get_grids", "get_grids"],
  ["04_get_all_categories", "get_all_categories"],
  ["05_query_walls_summary", "query_elements", '{"category":"Walls","summary_only":true}'],
  ["06_get_views_limit10", "get_views", '{"limit":10}'],
  ["07_query_walls_limit5", "query_elements", '{"category":"Walls","summary_only":false,"limit":5}'],
];

// Drop fields that legitimately drift between runs (timestamps, request id).
function stripVolatile(obj) {
  if (obj === null || typeof obj !== "object") return obj;
  if (Array.isArray(obj)) return obj.map(stripVolatile);
  const out = {};
  for (const [k, v] of Object.entries(obj)) {
    if (k === "id" || k === "timestamp") continue;
    out[k] = stripVolatile(v);
  }
  return out;
}

function runCase([label, command, params]) {
  const args = ["scripts/test-ws.js", command];
  if (params) args.push(params);
  const res = spawnSync("node", args, { cwd: ROOT, encoding: "utf8" });
  const text = res.stdout;
  writeFileSync(join(CURRENT, `${label}.json`), text);
  return text;
}

let failed = 0;
console.log("[verify] Re-running baseline commands against current build…");
for (const c of CASES) {
  const [label] = c;
  runCase(c);

  const oldRaw = readFileSync(join(BASELINE, `${label}.json`), "utf8");
  const newRaw = readFileSync(join(CURRENT, `${label}.json`), "utf8");
  let oldObj, newObj;
  try { oldObj = stripVolatile(JSON.parse(oldRaw)); }
  catch (e) { console.error(`[FAIL] ${label}: baseline JSON parse — ${e.message}`); failed++; continue; }
  try { newObj = stripVolatile(JSON.parse(newRaw)); }
  catch (e) { console.error(`[FAIL] ${label}: current JSON parse — ${e.message}`); failed++; continue; }

  const oldStr = JSON.stringify(oldObj, null, 2);
  const newStr = JSON.stringify(newObj, null, 2);
  if (oldStr === newStr) {
    console.log(`[ OK ] ${label}`);
  } else {
    console.error(`[FAIL] ${label}`);
    // Quick visual diff (first divergence)
    const a = oldStr.split("\n");
    const b = newStr.split("\n");
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      if (a[i] !== b[i]) {
        console.error(`  line ${i + 1}:`);
        console.error(`    - ${a[i] ?? "(missing)"}`);
        console.error(`    + ${b[i] ?? "(missing)"}`);
        if (i + 5 < Math.max(a.length, b.length)) break;
      }
    }
    failed++;
  }
}

if (failed) {
  console.error(`\n${failed} mismatch(es). Investigate before proceeding.`);
  process.exit(1);
}
console.log("\nAll baselines match.");
